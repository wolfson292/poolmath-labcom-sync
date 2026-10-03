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

    /// <summary>Readings typed in on the status page, for parameters a PoolLab doesn't measure.</summary>
    public ManualReadings Manual { get; set; } = new();

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

/// <summary>
/// The last temperature, borate and calcium hardness entered by hand. Each is also written to Pool Math as it's saved;
/// keeping them here means the balance on the status page reflects them even in a dry run.
/// </summary>
public sealed class ManualReadings
{
    public double? WaterTemp { get; set; }

    /// <summary>0 = Fahrenheit, 1 = Celsius.</summary>
    public int? WaterTempUnits { get; set; }

    public DateTimeOffset? WaterTempAt { get; set; }

    /// <summary>Borate as ppm boron, Pool Math's unit.</summary>
    public double? Bor { get; set; }

    public DateTimeOffset? BorAt { get; set; }

    /// <summary>Calcium hardness, ppm as CaCO3.</summary>
    public double? Ch { get; set; }

    public DateTimeOffset? ChAt { get; set; }
}
