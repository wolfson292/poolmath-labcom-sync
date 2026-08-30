using System.Text.Json;

namespace PoolSync.PoolMath;

/// <summary>
/// Suppresses writes while leaving reads intact: a dry run should still show what Pool Math holds,
/// it just must not change anything. Used when Sync:DryRun is true.
/// </summary>
public sealed class DryRunPoolMathClient(
    PoolMathClient inner,
    ILogger<DryRunPoolMathClient> logger) : IPoolMathClient
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public async Task<IReadOnlyList<PoolMathPool>> ListPoolsAsync(CancellationToken ct)
    {
        try
        {
            return await inner.ListPoolsAsync(ct);
        }
        catch (PoolMathException ex)
        {
            // Reading pools is a convenience here, not the job. Without credentials a dry run
            // should still report LabCOM readings rather than fail outright.
            logger.LogInformation("[dry run] Could not list Pool Math pools: {Message}", ex.Message);
            return [];
        }
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
