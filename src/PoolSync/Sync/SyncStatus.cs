using System.Collections.Concurrent;
using PoolSync.PoolMath;

namespace PoolSync.Sync;

/// <summary>Last-run information, surfaced on /status and rendered by the root page.</summary>
public sealed class SyncStatus
{
    private readonly ConcurrentDictionary<string, WaterBodyStatus> _waterBodies = new(StringComparer.Ordinal);

    public DateTimeOffset? LastRunStartedAt { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public IReadOnlyDictionary<string, WaterBodyStatus> WaterBodies => _waterBodies;

    /// <summary>Healthy until a run has failed repeatedly; a single blip shouldn't restart the container.</summary>
    public bool IsHealthy => ConsecutiveFailures < 3;

    public void RunStarted() => LastRunStartedAt = DateTimeOffset.UtcNow;

    public void RunSucceeded()
    {
        LastSuccessAt = DateTimeOffset.UtcNow;
        ConsecutiveFailures = 0;
        LastError = null;
    }

    public void RunFailed(Exception exception)
    {
        LastError = exception.Message;
        LastErrorAt = DateTimeOffset.UtcNow;
        ConsecutiveFailures++;
    }

    public void RecordWaterBody(
        string name,
        int sessionsWritten,
        DateTimeOffset? lastSyncedReading,
        LatestReadings? latest) =>
        _waterBodies[name] = new WaterBodyStatus(
            name,
            sessionsWritten,
            lastSyncedReading,
            DateTimeOffset.UtcNow,
            latest);
}

public sealed record WaterBodyStatus(
    string Name,
    int SessionsWrittenThisRun,
    DateTimeOffset? LastSyncedReadingAt,
    DateTimeOffset CheckedAt,
    LatestReadings? Latest);

/// <summary>
/// The most recent test run LabCOM holds for a water body, mapped onto Pool Math's parameters.
/// This is what LabCOM reports, not necessarily what has been synced.
/// </summary>
public sealed record LatestReadings(
    DateTimeOffset TakenAt,
    double? Fc,
    double? Cc,
    double? Ph,
    double? Ta,
    double? Cya,
    double? Ch,
    double? Salt,
    double? Bor,
    double? Tds,
    double? WaterTemp,
    int? WaterTempUnits)
{
    public static LatestReadings From(DateTimeOffset takenAt, PoolMathTestLog log) => new(
        takenAt,
        log.Fc,
        log.Cc,
        log.Ph,
        log.Ta,
        log.Cya,
        log.Ch,
        log.Salt,
        log.Bor,
        log.Tds,
        log.WaterTemp,
        log.WaterTempUnits);
}
