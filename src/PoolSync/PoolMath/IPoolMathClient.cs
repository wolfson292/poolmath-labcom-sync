namespace PoolSync.PoolMath;

public interface IPoolMathClient
{
    /// <summary>Lists the pools on the account, so pool ids can be matched to water bodies.</summary>
    Task<IReadOnlyList<PoolMathPool>> ListPoolsAsync(CancellationToken ct);

    /// <summary>
    /// Reads a pool from its public share page, or null if the page has no pool. Needs no sign-in, so
    /// it fills in for the account's pool list when that lacks a field.
    /// </summary>
    Task<PoolMathPool?> GetSharedPoolAsync(string shareCode, CancellationToken ct);

    /// <summary>Writes test logs. Implementations must be idempotent on the log's id.</summary>
    Task PushTestLogsAsync(IReadOnlyList<PoolMathTestLog> logs, CancellationToken ct);
}
