using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.PoolMath;
using PoolSync.State;
using PoolSync.Sync;
using Xunit;

namespace PoolSync.Tests;

public class WaterReadingsTests
{
    private static readonly DateTimeOffset Old = new(2024, 2, 17, 19, 22, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Recent = new(2026, 10, 3, 17, 29, 0, TimeSpan.Zero);

    private static LatestReadings LabCom(DateTimeOffset at, double? ph = null, double? ch = null) =>
        new(at, null, null, ph, null, null, ch, null, null, null, null, null);

    [Fact]
    public void The_newest_value_of_each_parameter_wins()
    {
        var overview = new PoolMathOverview { Ph = 7.4, PhTs = Old, Ch = 200, ChTs = Old };

        var readings = WaterReadings.Combine(overview, LabCom(Recent, ph: 8.1), manual: null);

        Assert.Equal(new SourcedReading(8.1, ReadingSource.LabCom, Recent), readings[PoolMathFields.Ph]);
        Assert.Equal(new SourcedReading(200, ReadingSource.PoolMath, Old), readings[PoolMathFields.CalciumHardness]);
    }

    [Fact]
    public void An_older_labcom_reading_does_not_replace_a_newer_pool_math_one()
    {
        var overview = new PoolMathOverview { Ph = 7.4, PhTs = Recent };

        var readings = WaterReadings.Combine(overview, LabCom(Old, ph: 8.1), manual: null);

        Assert.Equal(ReadingSource.PoolMath, readings[PoolMathFields.Ph].Source);
    }

    [Fact]
    public void Manual_temperature_is_converted_to_celsius()
    {
        var manual = new ManualReadings { WaterTemp = 84.2, WaterTempUnits = 0, WaterTempAt = Recent };

        var readings = WaterReadings.Combine(overview: null, labCom: null, manual);

        Assert.Equal(29, readings[WaterReadings.WaterTempC].Value, precision: 1);
        Assert.Equal(ReadingSource.Manual, readings[WaterReadings.WaterTempC].Source);
    }

    [Fact]
    public void A_manual_entry_beats_its_own_copy_in_pool_math()
    {
        // Saving a manual reading writes a Pool Math log with the same timestamp.
        var manual = new ManualReadings { Bor = 30, BorAt = Recent };
        var overview = new PoolMathOverview { Bor = 30, BorTs = Recent };

        var readings = WaterReadings.Combine(overview, labCom: null, manual);

        Assert.Equal(ReadingSource.Manual, readings[PoolMathFields.Borate].Source);
    }
}
