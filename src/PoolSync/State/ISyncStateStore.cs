namespace PoolSync.State;

public interface ISyncStateStore
{
    Task<SyncState> LoadAsync(CancellationToken ct);

    Task SaveAsync(SyncState state, CancellationToken ct);
}
