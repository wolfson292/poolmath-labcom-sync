using System.Collections.Concurrent;

namespace PoolSync.Sync;

/// <summary>Last-run information, surfaced on /status for monitoring from the NUC.</summary>
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

    public void RecordWaterBody(string name, int sessionsWritten, DateTimeOffset? lastReading) =>
        _waterBodies[name] = new WaterBodyStatus(
            name,
            sessionsWritten,
            lastReading,
            DateTimeOffset.UtcNow);
}

public sealed record WaterBodyStatus(
    string Name,
    int SessionsWrittenThisRun,
    DateTimeOffset? LastReadingAt,
    DateTimeOffset CheckedAt);
