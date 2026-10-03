using Microsoft.Extensions.Options;
using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.PoolMath;
using PoolSync.State;
using PoolSync.Storage;

namespace PoolSync.Sync;

/// <summary>Outcome of a single sync run, shaped for the /sync endpoint's response.</summary>
public sealed record SyncRunResult(string Outcome, int LogsWritten, string? Error)
{
    public static SyncRunResult Busy() => new("busy", 0, null);

    public static SyncRunResult Succeeded(int logsWritten) => new("ok", logsWritten, null);

    public static SyncRunResult Failed(string error) => new("failed", 0, error);
}

/// <summary>Outcome of saving a hand-entered test, shaped for the /tests endpoint's response.</summary>
public sealed record ManualEntryResult(string Outcome, string? Error, string? Id = null)
{
    public static ManualEntryResult Saved(string id) => new("ok", null, id);

    public static ManualEntryResult Invalid(string error) => new("invalid", error);

    public static ManualEntryResult Failed(string error) => new("failed", error);
}

/// <summary>A test typed in on the status page. Temperature units follow Pool Math: 0 = °F, 1 = °C.</summary>
public sealed record ManualTest(
    string WaterBody,
    DateTimeOffset? TakenAt,
    double? Fc,
    double? Cc,
    double? Ph,
    double? Ta,
    double? Cya,
    double? Ch,
    double? Salt,
    double? Bor,
    double? Tds,
    double? WaterTemp,
    int? WaterTempUnits,
    string? Notes);

/// <summary>
/// Performs one sync pass. Both the interval timer and the manual trigger go through here, and a
/// gate makes overlapping runs impossible: two concurrent passes would read the same high-water
/// mark and write the same readings to Pool Math twice.
/// </summary>
public sealed class SyncRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<SyncOptions> syncOptions,
    IOptions<PoolMathOptions> poolMathOptions,
    IOptions<BalanceOptions> balanceOptions,
    IOptions<List<WaterBodyOptions>> waterBodies,
    PoolDatabase database,
    SyncStatus status,
    ILogger<SyncRunner> logger)
{
    private readonly SyncOptions _sync = syncOptions.Value;
    private readonly PoolMathOptions _poolMath = poolMathOptions.Value;
    private readonly List<WaterBodyOptions> _waterBodies = waterBodies.Value;
    private readonly BalanceCalculator _balance = new(balanceOptions.Value);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs a sync unless one is already in progress, in which case the caller is told it's busy
    /// rather than queued — a manual trigger during a scheduled run wants an answer, not a wait.
    ///
    /// <paramref name="skipSettleTime"/> writes sessions as soon as they exist instead of waiting
    /// out <see cref="SyncOptions.SessionSettleTime"/>. The manual trigger sets it: whoever presses
    /// the button has just finished a test and wants it in Pool Math now, not in half an hour.
    /// </summary>
    public async Task<SyncRunResult> RunAsync(CancellationToken ct, bool skipSettleTime = false)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return SyncRunResult.Busy();
        }

        try
        {
            var written = await RunOnceAsync(skipSettleTime, ct);
            return SyncRunResult.Succeeded(written);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a failed run kill the caller: LabCOM and Pool Math both have transient
            // outages, and the high-water mark means the same readings are simply retried later.
            status.RunFailed(ex);

            if (IsTransientNetworkFailure(ex))
            {
                // A dropped connection or timeout is expected occasionally and recovers on its
                // own. Logging the full trace for one buries the failures that need attention.
                logger.LogWarning(
                    "Sync run failed to reach a remote service ({Message}); retrying in {Interval}.",
                    ex.Message,
                    _sync.Interval);
            }
            else
            {
                logger.LogError(ex, "Sync run failed; retrying in {Interval}.", _sync.Interval);
            }

            return SyncRunResult.Failed(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Network faults that resolve themselves: connection drops, timeouts, and the timeout the
    /// resilience pipeline raises once its own budget is spent.
    /// </summary>
    private static bool IsTransientNetworkFailure(Exception exception) =>
        exception switch
        {
            HttpRequestException or TaskCanceledException or OperationCanceledException
                or System.IO.IOException or System.Net.Sockets.SocketException => true,

            // Raised by the standard resilience handler. Matched by name so this doesn't take a
            // direct dependency on Polly, which arrives only transitively.
            _ when exception.GetType().FullName == "Polly.Timeout.TimeoutRejectedException" => true,

            { InnerException: { } inner } => IsTransientNetworkFailure(inner),

            _ => false,
        };

    private async Task<int> RunOnceAsync(bool skipSettleTime, CancellationToken ct)
    {
        status.RunStarted();

        using var scope = scopeFactory.CreateScope();
        var labCom = scope.ServiceProvider.GetRequiredService<LabComClient>();
        var poolMath = scope.ServiceProvider.GetRequiredService<IPoolMathClient>();
        var mapper = scope.ServiceProvider.GetRequiredService<ReadingMapper>();
        var store = scope.ServiceProvider.GetRequiredService<ISyncStateStore>();

        var state = await store.LoadAsync(ct);
        var cloudAccount = await labCom.GetCloudAccountAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var enabled = _waterBodies.Where(w => w.Enabled).ToList();
        var pools = _poolMath.Enabled
            ? await PoolsAsync(poolMath, enabled, ct)
            : new Dictionary<string, PoolMathPool>(StringComparer.Ordinal);
        var written = 0;

        try
        {
            foreach (var waterBody in enabled)
            {
                written += await SyncWaterBodyAsync(
                    waterBody, cloudAccount, state, mapper, poolMath, pools, now, skipSettleTime, ct);
            }
        }
        finally
        {
            // Save even when a water body threw, so sessions already written to Pool Math are not
            // written again on the next run. Not passing ct: progress must be persisted even when
            // the run is being cancelled, and this is a small local file.
            if (!_sync.DryRun)
            {
                await store.SaveAsync(state, CancellationToken.None);
            }
        }

        // A quiet run is the normal case, so say so explicitly: without this the logs are silent
        // between syncs and a healthy service looks the same as a stalled one.
        logger.LogInformation(
            "Sync complete: checked {WaterBodies} water body/bodies, stored {Logs} new test(s).",
            enabled.Count,
            written);

        status.RunSucceeded();
        return written;
    }

    /// <summary>Returns the number of test logs written for this water body.</summary>
    private async Task<int> SyncWaterBodyAsync(
        WaterBodyOptions waterBody,
        CloudAccount cloudAccount,
        SyncState state,
        ReadingMapper mapper,
        IPoolMathClient poolMath,
        IReadOnlyDictionary<string, PoolMathPool> pools,
        DateTimeOffset now,
        bool skipSettleTime,
        CancellationToken ct)
    {
        var pool = pools.GetValueOrDefault(waterBody.PoolMathPoolId);
        var shareUrl = ShareUrl(pool);
        var bodyState = state.For(waterBody.LabComAccountId);

        await MigrateManualReadingsAsync(waterBody, bodyState, ct);

        var account = cloudAccount.Accounts.FirstOrDefault(
            a => a.Id.ToString() == waterBody.LabComAccountId);

        if (account is null)
        {
            logger.LogWarning(
                "No LabCOM account {AccountId} for water body {Name}. Available: {Available}.",
                waterBody.LabComAccountId,
                waterBody.Name,
                string.Join(", ", cloudAccount.Accounts.Select(a => $"{a.Id} ({a.DisplayName})")));
            await RecordStatusAsync(waterBody, pool, 0, null, null, null, shareUrl, ct);
            return 0;
        }

        // The newest readings LabCOM holds, whether or not they are new to us. The status page
        // shows these, so it stays populated even when there is nothing left to sync.
        var (latest, latestMaxId) = LatestReadingsFor(account, waterBody, mapper);

        // The first run has no high-water mark, so the backfill window bounds the import instead.
        DateTimeOffset? cutoff = bodyState.LastMeasurementId == 0
            ? now - _sync.InitialBackfill
            : null;

        var candidates = account.Measurements
            .Where(m => m.Id > bodyState.LastMeasurementId)
            .Where(m => cutoff is null || m.Timestamp >= cutoff)
            .ToList();

        if (candidates.Count == 0)
        {
            logger.LogDebug("{Name}: no new LabCOM measurements.", waterBody.Name);
            await RecordStatusAsync(
                waterBody, pool, 0, bodyState.LastSessionTimestamp, latest, Pending(latest, latestMaxId, bodyState), shareUrl, ct);
            return 0;
        }

        // A session still in progress would otherwise be split across two Pool Math logs.
        var sessions = mapper.GroupIntoSessions(candidates)
            .Where(s => skipSettleTime || now - s.Timestamp >= _sync.SessionSettleTime)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (sessions.Count == 0)
        {
            logger.LogInformation(
                "{Name}: {Count} new measurement(s) still settling; leaving them for the next run.",
                waterBody.Name,
                candidates.Count);
            await RecordStatusAsync(
                waterBody, pool, 0, bodyState.LastSessionTimestamp, latest, Pending(latest, latestMaxId, bodyState), shareUrl, ct);
            return 0;
        }

        var written = 0;

        // One session at a time, advancing the high-water mark after each. Writing a whole batch
        // before advancing would mean a failure partway through left logs in Pool Math that the
        // next run wrote again, because the mark had not moved past them.
        foreach (var session in sessions)
        {
            var log = mapper.ToTestLog(session, waterBody);

            if (log is null)
            {
                logger.LogInformation(
                    "{Name}: session at {Timestamp} had no readings Pool Math tracks; skipping it.",
                    waterBody.Name,
                    session.Timestamp);
            }
            else
            {
                // The local history is the record; Pool Math only gets a copy when asked to. Stored
                // first, so a Pool Math failure can't lose the test.
                await database.InsertTestAsync(ToTest(log, waterBody, session), ct);

                if (_poolMath.Enabled && _poolMath.WriteLogs)
                {
                    await poolMath.PushTestLogsAsync([log], ct);

                    if (log.Id is not null)
                    {
                        bodyState.RecordLog(log.Id);
                    }
                }

                written++;
            }

            // Advance past sessions that produced no log too, so unmapped readings aren't
            // re-examined forever.
            bodyState.LastMeasurementId = session.MaxMeasurementId;
            bodyState.LastSessionTimestamp = session.Timestamp;
            bodyState.LastSyncedAt = now;
        }

        bodyState.SessionsWritten += written;

        logger.LogInformation(
            "{Name}: stored {LogCount} test(s) from {SessionCount} session(s); high-water mark now {Id}.",
            waterBody.Name,
            written,
            sessions.Count,
            bodyState.LastMeasurementId);

        await RecordStatusAsync(
            waterBody, pool, written, bodyState.LastSessionTimestamp, latest, Pending(latest, latestMaxId, bodyState), shareUrl, ct);

        return written;
    }

    /// <summary>
    /// The account's pools by id, for share links, settings and the current water summary. A pool
    /// whose list entry carries no overview is filled in from its public share page.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, PoolMathPool>> PoolsAsync(
        IPoolMathClient poolMath, IReadOnlyList<WaterBodyOptions> enabled, CancellationToken ct)
    {
        IReadOnlyList<PoolMathPool> pools;
        try
        {
            pools = await poolMath.ListPoolsAsync(ct);
        }
        catch (Exception ex) when (ex is PoolMathException or HttpRequestException)
        {
            // Pool details only feed the status page. Losing them must not fail a sync that would
            // otherwise work.
            logger.LogWarning("Could not read Pool Math pools: {Message}", ex.Message);
            return new Dictionary<string, PoolMathPool>(StringComparer.Ordinal);
        }

        var wanted = enabled.Select(w => w.PoolMathPoolId).ToHashSet(StringComparer.Ordinal);
        var byId = new Dictionary<string, PoolMathPool>(StringComparer.Ordinal);

        foreach (var pool in pools.Where(p => wanted.Contains(p.Id)))
        {
            byId[pool.Id] = pool;

            if (pool.Overview is not null || pool.ShareCodeOrNull is not { } code)
            {
                continue;
            }

            try
            {
                pool.Overview = (await poolMath.GetSharedPoolAsync(code, ct))?.Overview;
            }
            catch (Exception ex) when (ex is PoolMathException or HttpRequestException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Could not read the Pool Math share page for {Pool}: {Message}", pool.Name, ex.Message);
            }
        }

        return byId;
    }

    private string? ShareUrl(PoolMathPool? pool) =>
        pool?.ShareCodeOrNull is { } code
            ? _poolMath.ShareUrlTemplate.Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal)
            : null;

    /// <summary>
    /// The pool's settings: the service's own copy once it has one, otherwise seeded from Pool Math
    /// (and saved, so they survive Pool Math going away), otherwise defaults.
    /// </summary>
    public async Task<PoolSettings> SettingsAsync(
        WaterBodyOptions waterBody, PoolMathPool? pool, CancellationToken ct)
    {
        if (await database.SettingsAsync(waterBody.Name, ct) is { } saved)
        {
            return saved;
        }

        if (pool is null)
        {
            return PoolSettings.Defaults(waterBody);
        }

        var seeded = PoolSettings.FromPoolMath(pool, waterBody);
        await database.SaveSettingsAsync(waterBody.Name, seeded, ct);
        logger.LogInformation("{Name}: copied pool settings from Pool Math.", waterBody.Name);
        return seeded;
    }

    private async Task RecordStatusAsync(
        WaterBodyOptions waterBody,
        PoolMathPool? pool,
        int written,
        DateTimeOffset? lastSyncedReading,
        LatestReadings? latest,
        LatestReadings? pending,
        string? shareUrl,
        CancellationToken ct)
    {
        var settings = await SettingsAsync(waterBody, pool, ct);
        var tests = await database.TestsAsync(waterBody.Name, limit: null, ct);
        var balance = _balance.Calculate(WaterReadings.FromTests(tests, pending), PoolProfile.From(settings));

        status.RecordWaterBody(
            waterBody.Name, written, lastSyncedReading, latest, shareUrl, balance, settings.TempUnits, settings);
    }

    /// <summary>The latest LabCOM session, if it hasn't been stored yet because it's still settling.</summary>
    private static LatestReadings? Pending(LatestReadings? latest, long latestMaxId, WaterBodyState bodyState) =>
        latest is not null && latestMaxId > bodyState.LastMeasurementId ? latest : null;

    private static TestRecord ToTest(PoolMathTestLog log, WaterBodyOptions waterBody, TestSession session) => new()
    {
        WaterBody = waterBody.Name,
        TakenAt = session.Timestamp,
        Source = TestSource.LabCom,
        Fc = log.Fc,
        Cc = log.Cc,
        Ph = log.Ph,
        Ta = log.Ta,
        Cya = log.Cya,
        Ch = log.Ch,
        Salt = log.Salt,
        Bor = log.Bor,
        Tds = log.Tds,
        WaterTemp = log.WaterTemp,
        WaterTempUnits = log.WaterTempUnits,
        Notes = log.Notes,
        ExternalId = $"labcom:{waterBody.LabComAccountId}:{session.MaxMeasurementId}",
    };

    /// <summary>
    /// Moves temperature, borate and CH typed in before the database existed out of the state file
    /// and into the test history. Idempotent: the external ids are fixed, so a repeat stores nothing.
    /// </summary>
    private async Task MigrateManualReadingsAsync(
        WaterBodyOptions waterBody, WaterBodyState bodyState, CancellationToken ct)
    {
        var manual = bodyState.Manual;
        var key = $"state-manual:{waterBody.LabComAccountId}";

        if (manual.WaterTemp is not null && manual.WaterTempAt is { } tempAt)
        {
            await database.InsertTestAsync(new TestRecord
            {
                WaterBody = waterBody.Name, TakenAt = tempAt, Source = TestSource.Manual,
                WaterTemp = manual.WaterTemp, WaterTempUnits = manual.WaterTempUnits, ExternalId = key + ":temp",
            }, ct);
        }

        if (manual.Bor is not null && manual.BorAt is { } borAt)
        {
            await database.InsertTestAsync(new TestRecord
            {
                WaterBody = waterBody.Name, TakenAt = borAt, Source = TestSource.Manual,
                Bor = manual.Bor, ExternalId = key + ":bor",
            }, ct);
        }

        if (manual.Ch is not null && manual.ChAt is { } chAt)
        {
            await database.InsertTestAsync(new TestRecord
            {
                WaterBody = waterBody.Name, TakenAt = chAt, Source = TestSource.Manual,
                Ch = manual.Ch, ExternalId = key + ":ch",
            }, ct);
        }

        bodyState.Manual = new ManualReadings();
    }

    private static readonly (string Field, Func<ManualTest, double?> Value)[] ManualFields =
    [
        (PoolMathFields.FreeChlorine, t => t.Fc),
        (PoolMathFields.CombinedChlorine, t => t.Cc),
        (PoolMathFields.Ph, t => t.Ph),
        (PoolMathFields.TotalAlkalinity, t => t.Ta),
        (PoolMathFields.CyanuricAcid, t => t.Cya),
        (PoolMathFields.CalciumHardness, t => t.Ch),
        (PoolMathFields.Salt, t => t.Salt),
        (PoolMathFields.Borate, t => t.Bor),
        (PoolMathFields.Tds, t => t.Tds),
    ];

    /// <summary>
    /// Saves a hand-entered test to the history (and to Pool Math when writing there is on), then
    /// runs a sync so the status page reflects it.
    /// </summary>
    public async Task<ManualEntryResult> RecordManualTestAsync(ManualTest entry, CancellationToken ct)
    {
        var waterBody = _waterBodies.FirstOrDefault(
            w => w.Enabled && string.Equals(w.Name, entry.WaterBody, StringComparison.OrdinalIgnoreCase));

        if (waterBody is null)
        {
            return ManualEntryResult.Invalid($"No water body named {entry.WaterBody}.");
        }

        foreach (var (field, value) in ManualFields)
        {
            var (label, min, max) = WaterReadings.Plausible[field];
            if (value(entry) is { } v && (v < min || v > max))
            {
                return ManualEntryResult.Invalid($"{label} {v} is outside {min}–{max}.");
            }
        }

        var units = entry.WaterTempUnits is 1 ? 1 : 0;
        if (entry.WaterTemp is { } t && WaterReadings.ToCelsius(t, units) is < -2 or > 45)
        {
            return ManualEntryResult.Invalid($"{t} °{(units == 1 ? "C" : "F")} isn't a plausible water temperature.");
        }

        var now = DateTimeOffset.UtcNow;
        var takenAt = entry.TakenAt ?? now;
        if (takenAt > now.AddMinutes(5))
        {
            return ManualEntryResult.Invalid("The test time is in the future.");
        }

        var test = new TestRecord
        {
            WaterBody = waterBody.Name,
            TakenAt = takenAt,
            Source = TestSource.Manual,
            Fc = entry.Fc,
            Cc = entry.Cc,
            Ph = entry.Ph,
            Ta = entry.Ta,
            Cya = entry.Cya,
            Ch = entry.Ch,
            Salt = entry.Salt,
            Bor = entry.Bor,
            Tds = entry.Tds,
            WaterTemp = entry.WaterTemp,
            WaterTempUnits = entry.WaterTemp is null ? null : units,
            Notes = string.IsNullOrWhiteSpace(entry.Notes) ? null : entry.Notes.Trim(),
        };

        if (!test.HasAnyReading)
        {
            return ManualEntryResult.Invalid("Enter at least one reading.");
        }

        try
        {
            await database.InsertTestAsync(test, ct);

            if (_poolMath.Enabled && _poolMath.WriteLogs)
            {
                using var scope = scopeFactory.CreateScope();
                var poolMath = scope.ServiceProvider.GetRequiredService<IPoolMathClient>();
                await poolMath.PushTestLogsAsync([ToPoolMathLog(test, waterBody)], ct);
            }

            logger.LogInformation("{Name}: saved a manual test taken at {TakenAt}.", waterBody.Name, takenAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Name}: could not save manual test.", waterBody.Name);
            return ManualEntryResult.Failed(ex.Message);
        }

        // Refresh the status page. If a sync is already running it is skipped; the next one shows it.
        await RunAsync(ct);
        return ManualEntryResult.Saved(test.Id);
    }

    private static PoolMathTestLog ToPoolMathLog(TestRecord test, WaterBodyOptions waterBody) => new()
    {
        PoolId = waterBody.PoolMathPoolId,
        LogTimestamp = test.TakenAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"),
        Fc = test.Fc,
        Cc = test.Cc,
        Ph = test.Ph,
        Ta = test.Ta,
        Cya = test.Cya,
        Ch = test.Ch,
        Salt = test.Salt,
        Bor = test.Bor,
        Tds = test.Tds,
        WaterTemp = test.WaterTemp,
        WaterTempUnits = test.WaterTempUnits,
        Notes = test.Notes,
    };

    private static (LatestReadings? Latest, long MaxMeasurementId) LatestReadingsFor(
        LabComAccount account, WaterBodyOptions waterBody, ReadingMapper mapper)
    {
        // Sessions come back oldest first, so the newest test run is the last one.
        var session = mapper.GroupIntoSessions(account.Measurements).LastOrDefault();
        if (session is null)
        {
            return (null, 0);
        }

        var log = mapper.ToTestLog(session, waterBody);
        return (log is null ? null : LatestReadings.From(session.Timestamp, log), session.MaxMeasurementId);
    }
}
