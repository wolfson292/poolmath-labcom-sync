using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoolSync.Configuration;
using PoolSync.LabCom;
using PoolSync.Sync;
using Xunit;

namespace PoolSync.Tests;

public class ReadingMapperTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 29, 14, 0, 0, TimeSpan.Zero);

    private static ReadingMapper CreateMapper(
        MappingOptions? mapping = null,
        SyncOptions? sync = null) =>
        new(
            Options.Create(mapping ?? new MappingOptions()),
            Options.Create(sync ?? new SyncOptions()),
            NullLogger<ReadingMapper>.Instance);

    private static LabComMeasurement Measurement(
        long id, string parameter, string value, int minutesAfterBase, string? scenario = null) =>
        new()
        {
            Id = id,
            Parameter = parameter,
            Scenario = scenario,
            Value = value,
            Timestamp = Base.AddMinutes(minutesAfterBase),
            DeviceSerial = "PL-1234",
        };

    private static WaterBodyOptions WaterBody() =>
        new() { Name = "Pool", LabComAccountId = "1", PoolMathPoolId = "pool-guid" };

    [Fact]
    public void GroupIntoSessions_clusters_readings_taken_minutes_apart()
    {
        var mapper = CreateMapper();

        var sessions = mapper.GroupIntoSessions(
        [
            Measurement(1, "PL pH", "7.5", 0),
            Measurement(2, "PL Chlorine Free", "3.2", 4),
            Measurement(3, "PL T-Alka", "80", 9),
        ]);

        var session = Assert.Single(sessions);
        Assert.Equal(3, session.Measurements.Count);
        Assert.Equal(Base.AddMinutes(9), session.Timestamp);
        Assert.Equal(3, session.MaxMeasurementId);
    }

    [Fact]
    public void GroupIntoSessions_splits_when_the_gap_exceeds_the_window()
    {
        var mapper = CreateMapper(sync: new SyncOptions { SessionWindow = TimeSpan.FromMinutes(20) });

        var sessions = mapper.GroupIntoSessions(
        [
            Measurement(1, "PL pH", "7.5", 0),
            Measurement(2, "PL pH", "7.6", 45),
        ]);

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void GroupIntoSessions_chains_readings_across_a_long_run()
    {
        // Each gap is inside the window even though the run spans more than one window.
        var mapper = CreateMapper(sync: new SyncOptions { SessionWindow = TimeSpan.FromMinutes(20) });

        var sessions = mapper.GroupIntoSessions(
        [
            Measurement(1, "PL pH", "7.5", 0),
            Measurement(2, "PL Chlorine Free", "3.2", 15),
            Measurement(3, "PL T-Alka", "80", 30),
        ]);

        Assert.Single(sessions);
    }

    [Fact]
    public void ToTestLog_maps_parameters_onto_pool_math_fields()
    {
        var mapper = CreateMapper();
        var session = new TestSession(
        [
            Measurement(1, "PL pH", "7.5", 0),
            Measurement(2, "PL Chlorine Free", "3.2", 2),
            Measurement(3, "PL T-Alka", "80", 4),
            Measurement(4, "PL Cyanuric Acid", "60", 6),
        ]);

        var log = mapper.ToTestLog(session, WaterBody());

        Assert.NotNull(log);
        Assert.Equal("testlog", log!.Type);
        Assert.Equal("pool-guid", log.PoolId);
        Assert.Equal(7.5, log.Ph);
        Assert.Equal(3.2, log.Fc);
        Assert.Equal(80, log.Ta);
        Assert.Equal(60, log.Cya);
        Assert.Null(log.Ch);
    }

    [Fact]
    public void ToTestLog_matches_on_scenario_before_parameter_name()
    {
        var mapper = CreateMapper();
        var session = new TestSession(
        [
            // A renamed parameter still resolves through its scenario id.
            Measurement(1, "Some Renamed Parameter", "7.4", 0, scenario: "429-pH-PoolLab"),
        ]);

        var log = mapper.ToTestLog(session, WaterBody());

        Assert.Equal(7.4, log?.Ph);
    }

    [Fact]
    public void ToTestLog_derives_combined_chlorine_from_free_and_total()
    {
        var mapper = CreateMapper();
        var session = new TestSession(
        [
            Measurement(1, "PL Chlorine Free", "3.0", 0),
            Measurement(2, "PL Chlorine Total", "3.4", 2),
        ]);

        var log = mapper.ToTestLog(session, WaterBody());

        Assert.Equal(3.0, log?.Fc);
        Assert.Equal(0.4, log?.Cc);
    }

    [Fact]
    public void ToTestLog_clamps_negative_combined_chlorine_to_zero()
    {
        var mapper = CreateMapper();
        var session = new TestSession(
        [
            Measurement(1, "PL Chlorine Free", "3.4", 0),
            Measurement(2, "PL Chlorine Total", "3.0", 2),
        ]);

        Assert.Equal(0, mapper.ToTestLog(session, WaterBody())?.Cc);
    }

    [Fact]
    public void ToTestLog_takes_the_later_reading_when_a_parameter_is_retested()
    {
        var mapper = CreateMapper();
        var session = new TestSession(
        [
            Measurement(1, "PL pH", "7.2", 0),
            Measurement(2, "PL pH", "7.6", 5),
        ]);

        Assert.Equal(7.6, mapper.ToTestLog(session, WaterBody())?.Ph);
    }

    [Fact]
    public void ToTestLog_returns_null_when_nothing_maps()
    {
        var mapper = CreateMapper();
        var session = new TestSession([Measurement(1, "PL Urea", "1.2", 0)]);

        Assert.Null(mapper.ToTestLog(session, WaterBody()));
    }

    [Fact]
    public void ToTestLog_skips_non_numeric_values()
    {
        var mapper = CreateMapper();
        var session = new TestSession(
        [
            Measurement(1, "PL pH", "n/a", 0),
            Measurement(2, "PL Chlorine Free", "3.2", 2),
        ]);

        var log = mapper.ToTestLog(session, WaterBody());

        Assert.Null(log?.Ph);
        Assert.Equal(3.2, log?.Fc);
    }

    [Fact]
    public void ToTestLog_writes_the_timestamp_in_the_format_pool_math_uses()
    {
        var mapper = CreateMapper();
        var session = new TestSession([Measurement(1, "PL pH", "7.5", 0)]);

        Assert.Equal("2026-08-29T14:00:00.000Z", mapper.ToTestLog(session, WaterBody())?.LogTimestamp);
    }

    [Fact]
    public void ToTestLog_sets_water_temp_units_only_when_temperature_was_measured()
    {
        var mapper = CreateMapper();

        var withTemp = new TestSession([Measurement(1, "Water Temperature", "84", 0)]);
        var withoutTemp = new TestSession([Measurement(2, "PL pH", "7.5", 0)]);

        Assert.Equal(0, mapper.ToTestLog(withTemp, WaterBody())?.WaterTempUnits);
        Assert.Null(mapper.ToTestLog(withoutTemp, WaterBody())?.WaterTempUnits);
    }
}
