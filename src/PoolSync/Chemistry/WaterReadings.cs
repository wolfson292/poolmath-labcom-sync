using PoolSync.Configuration;
using PoolSync.Storage;
using PoolSync.Sync;

namespace PoolSync.Chemistry;

/// <summary>
/// A single reading and where it came from, so the page can show how current it is. Note explains a
/// value that was corrected on the way in.
/// </summary>
public sealed record SourcedReading(double Value, string Source, DateTimeOffset? At, string? Note = null);

/// <summary>Display names for where a reading came from.</summary>
public static class ReadingSource
{
    public const string LabCom = "LabCOM";
    public const string PoolMath = "Pool Math";
    public const string Manual = "Manual";

    public static string For(string testSource) => testSource switch
    {
        TestSource.LabCom => LabCom,
        TestSource.Manual => Manual,
        TestSource.PoolMath => PoolMath,
        _ => testSource,
    };
}

/// <summary>
/// The current picture of a water body, built from its test history. A PoolLab measures only some
/// parameters, so CH, salt, borate and temperature usually come from older or hand-entered tests:
/// for each parameter the newest test that measured it wins.
///
/// Only tests feed this. Live controller sensors drift, so they are compared against tests rather
/// than ever standing in for one.
/// </summary>
public static class WaterReadings
{
    /// <summary>Key for water temperature, always held in °C here regardless of how it was entered.</summary>
    public const string WaterTempC = "waterTempC";

    /// <summary>Above this a °C reading can't be pool water; it's a Fahrenheit number saved with the wrong unit.</summary>
    public const double ImplausibleCelsius = 45;

    /// <summary>
    /// What any test could plausibly read: wide enough for a SLAM, narrow enough to catch a typo or a
    /// photometer's over-range marker (an FC of 1,000,000). A stored value outside these is skipped,
    /// so an older valid reading shows instead, and hand entry is refused outright.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Label, double Min, double Max)> Plausible =
        new Dictionary<string, (string, double, double)>(StringComparer.Ordinal)
        {
            [PoolMathFields.FreeChlorine] = ("FC", 0, 60),
            [PoolMathFields.CombinedChlorine] = ("CC", 0, 20),
            [PoolMathFields.Ph] = ("pH", 6, 9),
            [PoolMathFields.TotalAlkalinity] = ("TA", 0, 400),
            [PoolMathFields.CyanuricAcid] = ("CYA", 0, 300),
            [PoolMathFields.CalciumHardness] = ("CH", 0, 2000),
            [PoolMathFields.Salt] = ("Salt", 0, 10000),
            [PoolMathFields.Borate] = ("Borate", 0, 100),
            [PoolMathFields.Tds] = ("TDS", 0, 20000),
        };

    /// <param name="tests">The water body's tests, in any order.</param>
    /// <param name="pending">
    /// The latest LabCOM session when it hasn't been stored yet (still settling), so the page shows
    /// a test the moment it's taken.
    /// </param>
    public static Dictionary<string, SourcedReading> FromTests(
        IEnumerable<TestRecord> tests, LatestReadings? pending = null)
    {
        var readings = new Dictionary<string, SourcedReading>(StringComparer.Ordinal);

        void Offer(string key, double? value, string source, DateTimeOffset at, string? note = null)
        {
            if (value is not { } v || double.IsNaN(v)
                || (Plausible.TryGetValue(key, out var range) && (v < range.Min || v > range.Max)))
            {
                return;
            }

            // Strictly newer replaces, so on a tie the first offer stands: pending LabCOM readings are
            // offered first, then stored tests newest first.
            if (readings.TryGetValue(key, out var existing) && existing.At >= at)
            {
                return;
            }

            readings[key] = new SourcedReading(v, source, at, note);
        }

        if (pending is not null)
        {
            var at = pending.TakenAt;
            Offer(PoolMathFields.FreeChlorine, pending.Fc, ReadingSource.LabCom, at);
            Offer(PoolMathFields.CombinedChlorine, pending.Cc, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Ph, pending.Ph, ReadingSource.LabCom, at);
            Offer(PoolMathFields.TotalAlkalinity, pending.Ta, ReadingSource.LabCom, at);
            Offer(PoolMathFields.CyanuricAcid, pending.Cya, ReadingSource.LabCom, at);
            Offer(PoolMathFields.CalciumHardness, pending.Ch, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Salt, pending.Salt, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Borate, pending.Bor, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Tds, pending.Tds, ReadingSource.LabCom, at);
            var (tempC, note) = Temperature(pending.WaterTemp, pending.WaterTempUnits);
            Offer(WaterTempC, tempC, ReadingSource.LabCom, at, note);
        }

        foreach (var test in tests.OrderByDescending(t => t.TakenAt))
        {
            var source = ReadingSource.For(test.Source);
            var at = test.TakenAt;
            Offer(PoolMathFields.FreeChlorine, test.Fc, source, at);
            Offer(PoolMathFields.CombinedChlorine, test.Cc, source, at);
            Offer(PoolMathFields.Ph, test.Ph, source, at);
            Offer(PoolMathFields.TotalAlkalinity, test.Ta, source, at);
            Offer(PoolMathFields.CyanuricAcid, test.Cya, source, at);
            Offer(PoolMathFields.CalciumHardness, test.Ch, source, at);
            Offer(PoolMathFields.Salt, test.Salt, source, at);
            Offer(PoolMathFields.Borate, test.Bor, source, at);
            Offer(PoolMathFields.Tds, test.Tds, source, at);
            var (tempC, note) = Temperature(test.WaterTemp, test.WaterTempUnits);
            Offer(WaterTempC, tempC, source, at, note);
        }

        return readings;
    }

    /// <summary>
    /// A temperature in °C, catching the easy mistake of saving a Fahrenheit number with the unit set
    /// to Celsius: 81.9 °C is not pool water, but 81.9 °F is.
    /// </summary>
    public static (double? Celsius, string? Note) Temperature(double? value, int? units)
    {
        var celsius = ToCelsius(value, units);
        if (units == 1 && celsius > ImplausibleCelsius && ToCelsius(value, 0) is { } asFahrenheit and > 0)
        {
            return (asFahrenheit, $"saved as {value:0.##} °C; read as °F");
        }

        return (celsius, null);
    }

    /// <summary>Pool Math's temperature units: 0 = Fahrenheit, 1 = Celsius.</summary>
    public static double? ToCelsius(double? value, int? units) =>
        value is not { } v ? null
        : units == 1 ? v
        : (v - 32) * 5 / 9;
}
