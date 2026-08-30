using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.PoolMath;
using PoolSync.State;

namespace PoolSync.Sync;

/// <summary>Polls LabCOM on an interval and writes any settled test sessions to Pool Math.</summary>
public sealed class SyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SyncOptions> syncOptions,
    IOptions<List<WaterBodyOptions>> waterBodies,
    SyncStatus status,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private readonly SyncOptions _sync = syncOptions.Value;
    private readonly List<WaterBodyOptions> _waterBodies = waterBodies.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Starting sync for {Count} water body/bodies every {Interval}. Dry run: {DryRun}.",
            _waterBodies.Count(w => w.Enabled),
            _sync.Interval,
            _sync.DryRun);

        using var timer = new PeriodicTimer(_sync.Interval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Keep the loop alive: LabCOM and Pool Math both have transient outages, and the
                // high-water mark means a failed run simply retries the same readings next tick.
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
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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

    private async Task RunOnceAsync(CancellationToken ct)
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
        var written = 0;

        foreach (var waterBody in enabled)
        {
            written += await SyncWaterBodyAsync(
                waterBody, cloudAccount, state, mapper, poolMath, now, ct);
        }

        // A dry run has to stay side-effect free. Persisting the high-water mark here would mean
        // that switching to live silently skipped every session the dry run had already "written".
        if (!_sync.DryRun)
        {
            await store.SaveAsync(state, ct);
        }

        // A quiet run is the normal case, so say so explicitly: without this the logs are silent
        // between syncs and a healthy service looks the same as a stalled one.
        logger.LogInformation(
            "Sync complete: checked {WaterBodies} water body/bodies, wrote {Logs} test log(s).",
            enabled.Count,
            written);

        status.RunSucceeded();
    }

    /// <summary>Returns the number of test logs written for this water body.</summary>
    private async Task<int> SyncWaterBodyAsync(
        WaterBodyOptions waterBody,
        CloudAccount cloudAccount,
        SyncState state,
        ReadingMapper mapper,
        IPoolMathClient poolMath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var account = cloudAccount.Accounts.FirstOrDefault(
            a => a.Id.ToString() == waterBody.LabComAccountId);

        if (account is null)
        {
            logger.LogWarning(
                "No LabCOM account {AccountId} for water body {Name}. Available: {Available}.",
                waterBody.LabComAccountId,
                waterBody.Name,
                string.Join(", ", cloudAccount.Accounts.Select(a => $"{a.Id} ({a.DisplayName})")));
            return 0;
        }

        var bodyState = state.For(waterBody.LabComAccountId);

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
            status.RecordWaterBody(waterBody.Name, 0, bodyState.LastSessionTimestamp);
            return 0;
        }

        // A session still in progress would otherwise be split across two Pool Math logs.
        var sessions = mapper.GroupIntoSessions(candidates)
            .Where(s => now - s.Timestamp >= _sync.SessionSettleTime)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (sessions.Count == 0)
        {
            logger.LogInformation(
                "{Name}: {Count} new measurement(s) still settling; leaving them for the next run.",
                waterBody.Name,
                candidates.Count);
            status.RecordWaterBody(waterBody.Name, 0, bodyState.LastSessionTimestamp);
            return 0;
        }

        var logs = new List<PoolMathTestLog>();
        foreach (var session in sessions)
        {
            var log = mapper.ToTestLog(session, waterBody);
            if (log is null)
            {
                logger.LogInformation(
                    "{Name}: session at {Timestamp} had no readings Pool Math tracks; skipping it.",
                    waterBody.Name,
                    session.Timestamp);
                continue;
            }

            logs.Add(log);
        }

        if (logs.Count > 0)
        {
            await poolMath.PushTestLogsAsync(logs, ct);
        }

        // Advance past every settled session, including ones that produced no log, so unmapped
        // readings aren't re-examined forever.
        bodyState.LastMeasurementId = sessions.Max(s => s.MaxMeasurementId);
        bodyState.LastSessionTimestamp = sessions.Max(s => s.Timestamp);
        bodyState.LastSyncedAt = now;
        bodyState.SessionsWritten += logs.Count;

        foreach (var log in logs.Where(l => l.Id is not null))
        {
            bodyState.RecordLog(log.Id!);
        }

        logger.LogInformation(
            "{Name}: wrote {LogCount} test log(s) from {SessionCount} session(s); high-water mark now {Id}.",
            waterBody.Name,
            logs.Count,
            sessions.Count,
            bodyState.LastMeasurementId);

        status.RecordWaterBody(waterBody.Name, logs.Count, bodyState.LastSessionTimestamp);

        return logs.Count;
    }
}
