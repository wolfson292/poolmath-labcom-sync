using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.Sync;
using Xunit;

namespace PoolSync.Tests;

/// <summary>
/// Mapping against the parameter and scenario names a real PoolLab actually reports, taken from a
/// live LabCOM account. These names differ from the ones in the PoolLab manual.
/// </summary>
public class PoolLabParameterMappingTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 29, 18, 31, 0, TimeSpan.Zero);

    private static ReadingMapper CreateMapper() =>
        new(
            Options.Create(new MappingOptions()),
            Options.Create(new SyncOptions()),
            NullLogger<ReadingMapper>.Instance);

    private static LabComMeasurement Measurement(
        long id, string parameter, string scenario, string value, int secondsAfterBase) =>
        new()
        {
            Id = id,
            Parameter = parameter,
            Scenario = scenario,
            Value = value,
            Timestamp = Base.AddSeconds(secondsAfterBase),
        };

    private static WaterBodyOptions WaterBody() =>
        new() { Name = "Spa", LabComAccountId = "1", PoolMathPoolId = "pool-guid" };

    /// <summary>A real test run: all three chlorines share the "8-CL" scenario, en dash included.</summary>
    private static TestSession RealSession() => new(
    [
        Measurement(455, "PL pH", "19-PH", "8.0391082763672", 0),
        Measurement(456, "PL Chlorine Free", "8–CL", "0.15999999642372", 96),
        Measurement(457, "PL Chlorine Total", "8–CL", "1.5", 284),
        Measurement(458, "PL Chlorine Combined", "8–CL", "1.3431500196457", 290),
        Measurement(459, "PL Alkalinity", "2-TA", "52.75804901123", 822),
    ]);

    [Fact]
    public void A_real_poollab_run_maps_every_reading()
    {
        var log = CreateMapper().ToTestLog(RealSession(), WaterBody());

        Assert.NotNull(log);
        Assert.Equal(8.0391082763672, log!.Ph);
        Assert.Equal(0.15999999642372, log.Fc);
        Assert.Equal(52.75804901123, log.Ta);
    }

    [Fact]
    public void Chlorine_readings_do_not_collapse_onto_one_field()
    {
        // Free, total and combined all report scenario "8-CL", so only the parameter name
        // distinguishes them. Free and combined must survive as separate values.
        var log = CreateMapper().ToTestLog(RealSession(), WaterBody());

        Assert.Equal(0.15999999642372, log!.Fc);
        Assert.Equal(1.3431500196457, log.Cc);
    }

    [Fact]
    public void A_measured_combined_chlorine_is_not_overwritten_by_the_derived_value()
    {
        // Deriving would give 1.5 - 0.16 = 1.34, close but not the device's own reading.
        var log = CreateMapper().ToTestLog(RealSession(), WaterBody());

        Assert.Equal(1.3431500196457, log!.Cc);
    }

    [Theory]
    [InlineData("12–CYA")] // as LabCOM sends it, with an en dash
    [InlineData("12-CYA")]      // as it is written in configuration
    public void Scenario_matching_tolerates_either_dash(string scenario)
    {
        var session = new TestSession([Measurement(1, "Renamed By LabCOM", scenario, "40", 0)]);

        Assert.Equal(40, CreateMapper().ToTestLog(session, WaterBody())?.Cya);
    }

    [Fact]
    public void The_manually_added_scenario_falls_through_to_the_parameter_name()
    {
        // "manually added" is shared across every parameter, so it must never resolve a field.
        var session = new TestSession(
        [
            Measurement(1, "PL Alkalinity", "manually added", "178", 0),
            Measurement(2, "Temperature", "manually added", "30.8", 60),
        ]);

        var log = CreateMapper().ToTestLog(session, WaterBody());

        Assert.Equal(178, log!.Ta);
        Assert.Equal(30.8, log.WaterTemp);
    }

    [Fact]
    public void Conductivity_is_left_unmapped_rather_than_guessed_as_salt()
    {
        // µS/cm converts to ppm only approximately; a wrong salt reading is worse than none.
        var session = new TestSession(
        [
            Measurement(1, "Conductivity (el.)", "manually added", "3600", 0),
        ]);

        Assert.Null(CreateMapper().ToTestLog(session, WaterBody()));
    }
}
