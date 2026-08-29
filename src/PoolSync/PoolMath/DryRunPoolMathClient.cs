using System.Text.Json;

namespace PoolSync.PoolMath;

/// <summary>Logs what would be written instead of calling Pool Math. Used when Sync:DryRun is true.</summary>
public sealed class DryRunPoolMathClient(ILogger<DryRunPoolMathClient> logger) : IPoolMathClient
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public Task<IReadOnlyList<PoolMathPool>> ListPoolsAsync(CancellationToken ct)
    {
        logger.LogInformation("[dry run] Skipping pools/list; no Pool Math call made.");
        return Task.FromResult<IReadOnlyList<PoolMathPool>>([]);
    }

    public Task PushTestLogsAsync(IReadOnlyList<PoolMathTestLog> logs, CancellationToken ct)
    {
        foreach (var log in logs)
        {
            logger.LogInformation(
                "[dry run] Would write test log to pool {PoolId}:\n{Json}",
                log.PoolId,
                JsonSerializer.Serialize(log, Pretty));
        }

        return Task.CompletedTask;
    }
}
