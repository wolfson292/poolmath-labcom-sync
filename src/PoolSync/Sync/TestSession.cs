using PoolSync.LabCom;

namespace PoolSync.Sync;

/// <summary>One PoolLab test run: the measurements taken close together for a single water body.</summary>
public sealed record TestSession(IReadOnlyList<LabComMeasurement> Measurements)
{
    public DateTimeOffset Timestamp => Measurements.Max(m => m.Timestamp);

    public long MaxMeasurementId => Measurements.Max(m => m.Id);

    public string? DeviceSerial =>
        Measurements.Select(m => m.DeviceSerial).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
}
