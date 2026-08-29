namespace PoolSync.PoolMath;

public interface IPoolMathClient
{
    /// <summary>Lists the pools on the account, so pool ids can be matched to water bodies.</summary>
    Task<IReadOnlyList<PoolMathPool>> ListPoolsAsync(CancellationToken ct);

    /// <summary>Writes test logs. Implementations must be idempotent on the log's id.</summary>
    Task PushTestLogsAsync(IReadOnlyList<PoolMathTestLog> logs, CancellationToken ct);
}
