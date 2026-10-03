using PoolSync.Configuration;
using PoolSync.PoolMath;
using PoolSync.State;
using PoolSync.Sync;

namespace PoolSync.Chemistry;

/// <summary>
/// A single reading and where it came from, so the page can show how current it is. Note explains a
/// value that was corrected on the way in.
/// </summary>
public sealed record SourcedReading(double Value, string Source, DateTimeOffset? At, string? Note = null);

public static class ReadingSource
{
    public const string LabCom = "LabCOM";
    public const string PoolMath = "Pool Math";
    public const string Manual = "Manual";
}

/// <summary>
/// Assembles the best current picture of a water body from three places. A PoolLab measures only
/// some parameters, so CH and salt typically come from an older Pool Math entry, and temperature and
/// borate from what was typed in on the status page. For each parameter the newest value wins.
/// </summary>
public static class WaterReadings
{
    /// <summary>Key for water temperature, always held in °C here regardless of how it was entered.</summary>
    public const string WaterTempC = "waterTempC";

    public static Dictionary<string, SourcedReading> Combine(
        PoolMathOverview? overview,
        LatestReadings? labCom,
        ManualReadings? manual)
    {
        var readings = new Dictionary<string, SourcedReading>(StringComparer.Ordinal);

        void Offer(string key, double? value, string source, DateTimeOffset? at, string? note = null)
        {
            if (value is not { } v || double.IsNaN(v))
            {
                return;
            }

            // Strictly newer replaces: on a tie the earlier offer stands, and offers are made in order
            // of how directly the value was observed (typed in, then LabCOM, then Pool Math's copy).
            if (readings.TryGetValue(key, out var existing)
                && (existing.At ?? DateTimeOffset.MinValue) >= (at ?? DateTimeOffset.MinValue))
            {
                return;
            }

            readings[key] = new SourcedReading(v, source, at, note);
        }

        if (manual is not null)
        {
            Offer(WaterTempC, ToCelsius(manual.WaterTemp, manual.WaterTempUnits), ReadingSource.Manual, manual.WaterTempAt);
            Offer(PoolMathFields.Borate, manual.Bor, ReadingSource.Manual, manual.BorAt);
            Offer(PoolMathFields.CalciumHardness, manual.Ch, ReadingSource.Manual, manual.ChAt);
        }

        if (labCom is not null)
        {
            var at = labCom.TakenAt;
            Offer(PoolMathFields.FreeChlorine, labCom.Fc, ReadingSource.LabCom, at);
            Offer(PoolMathFields.CombinedChlorine, labCom.Cc, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Ph, labCom.Ph, ReadingSource.LabCom, at);
            Offer(PoolMathFields.TotalAlkalinity, labCom.Ta, ReadingSource.LabCom, at);
            Offer(PoolMathFields.CyanuricAcid, labCom.Cya, ReadingSource.LabCom, at);
            Offer(PoolMathFields.CalciumHardness, labCom.Ch, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Salt, labCom.Salt, ReadingSource.LabCom, at);
            Offer(PoolMathFields.Borate, labCom.Bor, ReadingSource.LabCom, at);
            Offer(WaterTempC, ToCelsius(labCom.WaterTemp, labCom.WaterTempUnits), ReadingSource.LabCom, at);
        }

        if (overview is not null)
        {
            Offer(PoolMathFields.FreeChlorine, overview.Fc, ReadingSource.PoolMath, overview.FcTs);
            Offer(PoolMathFields.CombinedChlorine, overview.Cc, ReadingSource.PoolMath, overview.CcTs);
            Offer(PoolMathFields.Ph, overview.Ph, ReadingSource.PoolMath, overview.PhTs);
            Offer(PoolMathFields.TotalAlkalinity, overview.Ta, ReadingSource.PoolMath, overview.TaTs);
            Offer(PoolMathFields.CyanuricAcid, overview.Cya, ReadingSource.PoolMath, overview.CyaTs);
            Offer(PoolMathFields.CalciumHardness, overview.Ch, ReadingSource.PoolMath, overview.ChTs);
            Offer(PoolMathFields.Salt, overview.Salt, ReadingSource.PoolMath, overview.SaltTs);
            Offer(PoolMathFields.Borate, overview.Bor, ReadingSource.PoolMath, overview.BorTs);
            var (tempC, tempNote) = PoolMathTemperature(overview.WaterTemp, overview.WaterTempUnits);
            Offer(WaterTempC, tempC, ReadingSource.PoolMath, overview.WaterTempTs, tempNote);
        }

        return readings;
    }

    /// <summary>Above this a °C reading can't be pool water; it's a Fahrenheit number saved with the wrong unit.</summary>
    public const double ImplausibleCelsius = 45;

    /// <summary>
    /// Reads a temperature from Pool Math, catching the easy mistake of saving a Fahrenheit number
    /// with the unit set to Celsius: 81.9 °C is not pool water, but 81.9 °F is.
    /// </summary>
    private static (double? Celsius, string? Note) PoolMathTemperature(double? value, int? units)
    {
        var celsius = ToCelsius(value, units);
        if (units == 1 && celsius > ImplausibleCelsius && ToCelsius(value, 0) is { } asFahrenheit and > 0)
        {
            return (asFahrenheit, $"saved in Pool Math as {value:0.##} °C; read as °F");
        }

        return (celsius, null);
    }

    /// <summary>Pool Math's temperature units: 0 = Fahrenheit, 1 = Celsius.</summary>
    public static double? ToCelsius(double? value, int? units) =>
        value is not { } v ? null
        : units == 1 ? v
        : (v - 32) * 5 / 9;
}
