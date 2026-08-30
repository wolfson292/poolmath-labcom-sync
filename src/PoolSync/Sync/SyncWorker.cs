using Microsoft.Extensions.Options;
using PoolSync.Configuration;

namespace PoolSync.Sync;

/// <summary>Runs a sync on the configured interval. The work itself lives in <see cref="SyncRunner"/>.</summary>
public sealed class SyncWorker(
    SyncRunner runner,
    IOptions<SyncOptions> syncOptions,
    IOptions<List<WaterBodyOptions>> waterBodies,
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
                // RunAsync handles and logs its own failures, so the loop only has to survive
                // shutdown. A run skipped because a manual sync is in flight is fine: the next
                // tick picks up anything that run did not.
                await runner.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
