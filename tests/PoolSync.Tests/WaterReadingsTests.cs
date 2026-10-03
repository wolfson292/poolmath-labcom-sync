using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.Storage;
using PoolSync.Sync;
using Xunit;

namespace PoolSync.Tests;

public class WaterReadingsTests
{
    private static readonly DateTimeOffset Old = new(2024, 2, 17, 19, 22, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Recent = new(2026, 10, 3, 17, 29, 0, TimeSpan.Zero);

    private static TestRecord Test(DateTimeOffset at, string source = TestSource.PoolMath) =>
        new() { WaterBody = "Pool", TakenAt = at, Source = source };

    private static LatestReadings Pending(DateTimeOffset at, double? ph = null) =>
        new(at, null, null, ph, null, null, null, null, null, null, null, null);

    [Fact]
    public void The_newest_test_that_measured_each_parameter_wins()
    {
        var tests = new[]
        {
            Test(Old) with { Ph = 7.4, Ch = 200 },
            Test(Recent, TestSource.LabCom) with { Ph = 8.1 },
        };

        var readings = WaterReadings.FromTests(tests);

        Assert.Equal(new SourcedReading(8.1, ReadingSource.LabCom, Recent), readings[PoolMathFields.Ph]);
        Assert.Equal(new SourcedReading(200, ReadingSource.PoolMath, Old), readings[PoolMathFields.CalciumHardness]);
    }

    [Fact]
    public void Order_of_the_input_does_not_matter()
    {
        var tests = new[] { Test(Recent) with { Ph = 8.1 }, Test(Old) with { Ph = 7.4 } };

        Assert.Equal(8.1, WaterReadings.FromTests(tests)[PoolMathFields.Ph].Value);
    }

    [Fact]
    public void A_pending_labcom_session_shows_before_it_is_stored()
    {
        var readings = WaterReadings.FromTests([Test(Old) with { Ph = 7.4 }], Pending(Recent, ph: 8.1));

        Assert.Equal(ReadingSource.LabCom, readings[PoolMathFields.Ph].Source);
        Assert.Equal(8.1, readings[PoolMathFields.Ph].Value);
    }

    [Fact]
    public void Manual_temperature_is_converted_to_celsius()
    {
        var test = Test(Recent, TestSource.Manual) with { WaterTemp = 84.2, WaterTempUnits = 0 };

        var reading = WaterReadings.FromTests([test])[WaterReadings.WaterTempC];

        Assert.Equal(29, reading.Value, precision: 1);
        Assert.Equal(ReadingSource.Manual, reading.Source);
    }

    [Fact]
    public void A_fahrenheit_number_saved_as_celsius_is_read_as_fahrenheit()
    {
        var test = Test(Recent) with { WaterTemp = 81.92, WaterTempUnits = 1 };

        var reading = WaterReadings.FromTests([test])[WaterReadings.WaterTempC];

        Assert.Equal(27.7, reading.Value, precision: 1);
        Assert.Equal("saved as 81.92 °C; read as °F", reading.Note);
    }

    [Fact]
    public void A_real_celsius_temperature_is_left_alone()
    {
        var test = Test(Recent) with { WaterTemp = 29.2, WaterTempUnits = 1 };

        var reading = WaterReadings.FromTests([test])[WaterReadings.WaterTempC];

        Assert.Equal(29.2, reading.Value);
        Assert.Null(reading.Note);
    }

    [Fact]
    public void An_implausible_stored_value_is_skipped_for_an_older_good_one()
    {
        var tests = new[]
        {
            Test(Old) with { Fc = 4 },
            Test(Recent) with { Fc = 1000000 },
        };

        var reading = WaterReadings.FromTests(tests)[PoolMathFields.FreeChlorine];

        Assert.Equal(4, reading.Value);
        Assert.Equal(Old, reading.At);
    }
}
