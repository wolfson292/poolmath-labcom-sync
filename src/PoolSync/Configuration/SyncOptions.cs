using System.ComponentModel.DataAnnotations;

namespace PoolSync.Configuration;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>How often LabCOM is polled.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A PoolLab run produces one measurement per parameter, a few minutes apart. Readings from the
    /// same water body that fall within this window of each other become a single Pool Math test log.
    /// </summary>
    public TimeSpan SessionWindow { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A session is only pushed once its newest reading is this old, so a test still in progress
    /// isn't split across two Pool Math logs.
    /// </summary>
    public TimeSpan SessionSettleTime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>On first run, how far back to import. Later runs resume from stored state.</summary>
    public TimeSpan InitialBackfill { get; set; } = TimeSpan.FromDays(7);

    /// <summary>When true, sessions are logged but nothing is written to Pool Math.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Where sync state is persisted. Must be on a mounted volume to survive restarts.</summary>
    [Required]
    public string StatePath { get; set; } = "/data/state.json";
}
