namespace PoolSync.State;

public sealed class SyncState
{
    /// <summary>Keyed by LabCOM account id.</summary>
    public Dictionary<string, WaterBodyState> WaterBodies { get; set; } = new(StringComparer.Ordinal);

    public WaterBodyState For(string labComAccountId)
    {
        if (!WaterBodies.TryGetValue(labComAccountId, out var state))
        {
            state = new WaterBodyState();
            WaterBodies[labComAccountId] = state;
        }

        return state;
    }
}

public sealed class WaterBodyState
{
    /// <summary>High-water mark: measurements at or below this id have already been synced.</summary>
    public long LastMeasurementId { get; set; }

    /// <summary>Timestamp of the newest reading written to Pool Math.</summary>
    public DateTimeOffset? LastSessionTimestamp { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public int SessionsWritten { get; set; }

    /// <summary>Ids of the most recent logs written, kept for troubleshooting.</summary>
    public List<string> RecentLogIds { get; set; } = [];

    public void RecordLog(string logId)
    {
        RecentLogIds.Insert(0, logId);
        if (RecentLogIds.Count > 20)
        {
            RecentLogIds.RemoveRange(20, RecentLogIds.Count - 20);
        }
    }
}
