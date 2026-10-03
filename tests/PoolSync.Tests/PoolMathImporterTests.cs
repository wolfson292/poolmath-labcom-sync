using System.Text.Json;
using PoolSync.Import;
using PoolSync.PoolMath;
using PoolSync.Storage;
using Xunit;

namespace PoolSync.Tests;

public class PoolMathImporterTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 17, 33, 38, TimeSpan.Zero);

    // Shapes taken from a real /timeline/list response.
    private static PoolMathTimelineEntry Parse(string json) =>
        JsonSerializer.Deserialize<PoolMathTimelineEntry>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void A_test_log_keeps_its_readings_and_weather()
    {
        var entry = Parse("""
            {"type":"testlog","id":"380154ed","fc":5.8,"cc":0.0,"cya":22.0,"ch":null,"ph":8.29,"ta":105.0,
             "bor":0.0,"waterTemp":81.92,"waterTempUnits":1,"poolId":"p","logTimestamp":"2026-10-03T17:33:38Z",
             "weather":{"desc":"Mostly cloudy","temp":31.0}}
            """);

        var test = PoolMathImporter.ToTest(entry, "Allaire", At, "poolmath:380154ed", fromLabCom: false);

        Assert.Equal(TestSource.PoolMath, test.Source);
        Assert.Equal(8.29, test.Ph);
        Assert.Null(test.Ch);
        Assert.Equal(81.92, test.WaterTemp);
        Assert.Contains("Mostly cloudy", test.Weather);
    }

    [Fact]
    public void A_unit_without_a_temperature_is_dropped()
    {
        var entry = Parse("""{"type":"testlog","id":"x","ph":8.19,"waterTemp":null,"waterTempUnits":1}""");

        Assert.Null(PoolMathImporter.ToTest(entry, "Green Dream", At, "poolmath:x", false).WaterTempUnits);
    }

    [Fact]
    public void A_log_this_service_wrote_is_labelled_as_labcom()
    {
        var entry = Parse("""{"type":"testlog","id":"0ba2b72e","ph":9.0}""");

        Assert.Equal(TestSource.LabCom, PoolMathImporter.ToTest(entry, "Allaire", At, "poolmath:0ba2b72e", true).Source);
    }

    [Fact]
    public void A_chemical_log_keeps_its_codes_and_known_units()
    {
        var entry = Parse("""
            {"type":"chemlog","id":"c","chemical":0,"amount":5.0,"unit":5,"percent":10.5,"normalizedAmount":18927.05}
            """);

        var addition = PoolMathImporter.ToAddition(entry, "SPA", At, "poolmath:c");

        Assert.Equal(0, addition.ChemicalCode);
        Assert.Equal("gal", addition.Unit);
        Assert.Equal(10.5, addition.Percent);
        Assert.Equal(18927.05, addition.Normalized);
    }

    [Fact]
    public void An_unknown_unit_code_is_kept_as_a_code()
    {
        var entry = Parse("""{"type":"chemlog","id":"c","chemical":26,"amount":2.0,"unit":6}""");

        var addition = PoolMathImporter.ToAddition(entry, "SPA", At, "poolmath:c");

        Assert.Null(addition.Unit);
        Assert.Equal(6, addition.UnitCode);
    }

    [Fact]
    public void A_maintenance_log_keeps_only_what_was_recorded()
    {
        var entry = Parse("""
            {"type":"maintlog","id":"m","backwashed":false,"cleanedFilter":true,"pressure":null,"flowRate":null}
            """);

        var record = PoolMathImporter.ToMaintenance(entry, "Allaire", At, "poolmath:m");

        Assert.Equal("""{"cleanedFilter":true}""", record.Data);
    }
}
