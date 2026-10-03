using Microsoft.Extensions.Options;
using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.PoolMath;
using PoolSync.State;

namespace PoolSync.Sync;

/// <summary>Outcome of a single sync run, shaped for the /sync endpoint's response.</summary>
public sealed record SyncRunResult(string Outcome, int LogsWritten, string? Error)
{
    public static SyncRunResult Busy() => new("busy", 0, null);

    public static SyncRunResult Succeeded(int logsWritten) => new("ok", logsWritten, null);

    public static SyncRunResult Failed(string error) => new("failed", 0, error);
}

/// <summary>Outcome of saving hand-entered readings, shaped for the /manual endpoint's response.</summary>
public sealed record ManualEntryResult(string Outcome, string? Error)
{
    public static ManualEntryResult Saved() => new("ok", null);

    public static ManualEntryResult Busy() => new("busy", null);

    public static ManualEntryResult Invalid(string error) => new("invalid", error);

    public static ManualEntryResult Failed(string error) => new("failed", error);
}

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
    SyncStatus status,
    ILogger<SyncRunner> logger)
{
    private readonly SyncOptions _sync = syncOptions.Value;
    private readonly PoolMathOptions _poolMath = poolMathOptions.Value;
    private readonly List<WaterBodyOptions> _waterBodies = waterBodies.Value;
    private readonly BalanceCalculator _balance = new(balanceOptions.Value);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long a manual entry waits for a scheduled run to finish before giving up.</summary>
    private static readonly TimeSpan ManualEntryWait = TimeSpan.FromSeconds(30);

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
        var pools = await PoolsAsync(poolMath, enabled, ct);
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
            "Sync complete: checked {WaterBodies} water body/bodies, wrote {Logs} test log(s).",
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

        var account = cloudAccount.Accounts.FirstOrDefault(
            a => a.Id.ToString() == waterBody.LabComAccountId);

        if (account is null)
        {
            logger.LogWarning(
                "No LabCOM account {AccountId} for water body {Name}. Available: {Available}.",
                waterBody.LabComAccountId,
                waterBody.Name,
                string.Join(", ", cloudAccount.Accounts.Select(a => $"{a.Id} ({a.DisplayName})")));
            status.RecordWaterBody(
                waterBody.Name, 0, null, null, shareUrl, Balance(waterBody, pool, null, bodyState.Manual), TempUnits(pool));
            return 0;
        }

        // The newest readings LabCOM holds, whether or not they are new to us. The status page
        // shows these, so it stays populated even when there is nothing left to sync.
        var latest = LatestReadingsFor(account, waterBody, mapper);

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
            status.RecordWaterBody(waterBody.Name, 0, bodyState.LastSessionTimestamp, latest, shareUrl,
            Balance(waterBody, pool, latest, bodyState.Manual), TempUnits(pool));
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
            status.RecordWaterBody(waterBody.Name, 0, bodyState.LastSessionTimestamp, latest, shareUrl,
            Balance(waterBody, pool, latest, bodyState.Manual), TempUnits(pool));
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
                await poolMath.PushTestLogsAsync([log], ct);
                written++;

                if (log.Id is not null)
                {
                    bodyState.RecordLog(log.Id);
                }
            }

            // Advance past sessions that produced no log too, so unmapped readings aren't
            // re-examined forever.
            bodyState.LastMeasurementId = session.MaxMeasurementId;
            bodyState.LastSessionTimestamp = session.Timestamp;
            bodyState.LastSyncedAt = now;
        }

        bodyState.SessionsWritten += written;

        logger.LogInformation(
            "{Name}: wrote {LogCount} test log(s) from {SessionCount} session(s); high-water mark now {Id}.",
            waterBody.Name,
            written,
            sessions.Count,
            bodyState.LastMeasurementId);

        status.RecordWaterBody(waterBody.Name, written, bodyState.LastSessionTimestamp, latest, shareUrl,
            Balance(waterBody, pool, latest, bodyState.Manual), TempUnits(pool));

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

    private static int TempUnits(PoolMathPool? pool) => pool?.WaterTempUnitDefault ?? 0;

    private WaterBalance Balance(
        WaterBodyOptions waterBody, PoolMathPool? pool, LatestReadings? latest, ManualReadings manual) =>
        _balance.Calculate(
            WaterReadings.Combine(pool?.Overview, latest, manual),
            PoolProfile.From(pool, waterBody));

    /// <summary>
    /// Saves hand-entered temperature, borate and/or calcium hardness: written to Pool Math as a test log of its own,
    /// and kept in the state file so the balance reflects it straight away. Then runs a sync so the
    /// status page picks up the change.
    /// </summary>
    public async Task<ManualEntryResult> RecordManualAsync(
        string waterBodyName, double? waterTemp, int? waterTempUnits, double? borate, double? ch, CancellationToken ct)
    {
        var waterBody = _waterBodies.FirstOrDefault(
            w => w.Enabled && string.Equals(w.Name, waterBodyName, StringComparison.OrdinalIgnoreCase));

        if (waterBody is null)
        {
            return ManualEntryResult.Invalid($"No water body named {waterBodyName}.");
        }

        if (waterTemp is null && borate is null && ch is null)
        {
            return ManualEntryResult.Invalid("Enter a temperature, borate or calcium hardness reading.");
        }

        var units = waterTempUnits is 1 ? 1 : 0;
        if (waterTemp is { } t && WaterReadings.ToCelsius(t, units) is < -2 or > 45)
        {
            return ManualEntryResult.Invalid($"{t} °{(units == 1 ? "C" : "F")} isn't a plausible water temperature.");
        }

        if (borate is < 0 or > 100)
        {
            return ManualEntryResult.Invalid("Borate should be between 0 and 100 ppm.");
        }

        if (ch is < 0 or > 2000)
        {
            return ManualEntryResult.Invalid("Calcium hardness should be between 0 and 2000 ppm.");
        }

        // Shares the sync gate: both read and save the state file, and a sync mid-way through would
        // overwrite this entry with the copy it loaded before it.
        if (!await _gate.WaitAsync(ManualEntryWait, ct))
        {
            return ManualEntryResult.Busy();
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var poolMath = scope.ServiceProvider.GetRequiredService<IPoolMathClient>();
            var store = scope.ServiceProvider.GetRequiredService<ISyncStateStore>();

            var now = DateTimeOffset.UtcNow;
            var log = new PoolMathTestLog
            {
                PoolId = waterBody.PoolMathPoolId,
                LogTimestamp = now.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"),
                WaterTemp = waterTemp,
                WaterTempUnits = waterTemp is null ? null : units,
                Bor = borate,
                Ch = ch,
            };

            await poolMath.PushTestLogsAsync([log], ct);

            var state = await store.LoadAsync(ct);
            var bodyState = state.For(waterBody.LabComAccountId);

            if (waterTemp is not null)
            {
                bodyState.Manual.WaterTemp = waterTemp;
                bodyState.Manual.WaterTempUnits = units;
                bodyState.Manual.WaterTempAt = now;
            }

            if (borate is not null)
            {
                bodyState.Manual.Bor = borate;
                bodyState.Manual.BorAt = now;
            }

            if (ch is not null)
            {
                bodyState.Manual.Ch = ch;
                bodyState.Manual.ChAt = now;
            }

            if (log.Id is not null)
            {
                bodyState.RecordLog(log.Id);
            }

            await store.SaveAsync(state, CancellationToken.None);

            logger.LogInformation(
                "{Name}: saved manual reading (temperature {Temp}, borate {Borate}, CH {Ch}).",
                waterBody.Name,
                waterTemp is null ? "unchanged" : $"{waterTemp} °{(units == 1 ? "C" : "F")}",
                borate?.ToString() ?? "unchanged",
                ch?.ToString() ?? "unchanged");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Name}: could not save manual reading.", waterBody.Name);
            return ManualEntryResult.Failed(ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        // Refresh the status page. A failure here is reported by the sync itself; the entry is saved.
        await RunAsync(ct);
        return ManualEntryResult.Saved();
    }

    private static LatestReadings? LatestReadingsFor(
        LabComAccount account, WaterBodyOptions waterBody, ReadingMapper mapper)
    {
        // Sessions come back oldest first, so the newest test run is the last one.
        var session = mapper.GroupIntoSessions(account.Measurements).LastOrDefault();
        if (session is null)
        {
            return null;
        }

        var log = mapper.ToTestLog(session, waterBody);
        return log is null ? null : LatestReadings.From(session.Timestamp, log);
    }
}
