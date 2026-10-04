using PoolSync.Configuration;
using PoolSync.Weather;

namespace PoolSync.Chemistry;

/// <summary>A reading's tested value and what rain overflowing the pool since has probably diluted it to.</summary>
public sealed record DilutedReading(
    string Key, string Label, double Tested, DateTimeOffset TestedAt, double RainInches, double OverflowInches,
    double Percent, double Estimate);

public sealed record DilutionResult(IReadOnlyList<DilutedReading> Readings, bool AreaEstimated, double SurfaceAreaM2);

/// <summary>
/// Estimates how rain has diluted what a PoolLab doesn't retest often: CYA, CH, salt and borate.
///
/// Only water that leaves the pool takes chemicals with it, and in a hot climate evaporation takes
/// out about as much as rain puts in. So the level is followed day by day from the test: rain raises
/// it, evaporation (Open-Meteo's reference ET₀) lowers it, anything above normal overflows (or is
/// pumped down) and carries dissolved chemicals out, and a drop of 2 in below normal is assumed to
/// be topped up with fresh water, which adds water but removes nothing. With the pool mixed, an
/// overflow of volume O leaves exp(-O / V) of what was there. Tests remain the record.
/// </summary>
public static class Dilution
{
    private const double SquareFeetToM2 = 0.092903;

    /// <summary>How far the level is let fall before it's assumed topped back up, in mm.</summary>
    private const double TopUpMm = 50.8;

    private static readonly (string Key, string Label)[] Fields =
    [
        (PoolMathFields.CyanuricAcid, "CYA"),
        (PoolMathFields.CalciumHardness, "CH"),
        (PoolMathFields.Salt, "Salt"),
        (PoolMathFields.Borate, "Borate"),
    ];

    /// <summary>The pool's surface in m²: as set, or estimated from volume at an average depth of 5 ft.</summary>
    public static (double? M2, bool Estimated) SurfaceArea(PoolSettings settings, double? litres)
    {
        if (settings.SurfaceArea is > 0 and var area)
        {
            return (settings.VolumeUnit == 0 ? area * SquareFeetToM2 : area, false);
        }

        // A hot tub is covered when it isn't in use; without a set area, assume rain stays out.
        if (settings.Surface == PoolSurface.Spa || litres is not > 0)
        {
            return (null, false);
        }

        return (litres.Value / 1000 / 1.5, true);
    }

    public static DilutionResult? Estimate(
        IReadOnlyDictionary<string, SourcedReading> water,
        IReadOnlyDictionary<DateOnly, WaterDay> days,
        PoolSettings settings,
        double? litres)
    {
        var (areaM2, estimated) = SurfaceArea(settings, litres);
        if (areaM2 is not { } area || litres is not > 0)
        {
            return null;
        }

        var readings = new List<DilutedReading>();
        foreach (var (key, label) in Fields)
        {
            if (!water.TryGetValue(key, out var reading) || reading.At is not { } at)
            {
                continue;
            }

            // Whole days after the test; the test day's rain may have fallen before it.
            var testDay = DateOnly.FromDateTime(at.UtcDateTime);
            var (rainMm, overflowMm) = Overflow(days.Where(d => d.Key > testDay).OrderBy(d => d.Key).Select(d => d.Value));
            var fraction = 1 - Math.Exp(-(overflowMm * area) / litres.Value);

            readings.Add(new DilutedReading(
                key, label, reading.Value, at, Math.Round(rainMm / 25.4, 2), Math.Round(overflowMm / 25.4, 2),
                Math.Round(fraction * 100, 1), Math.Round(reading.Value * (1 - fraction), 1)));
        }

        return new DilutionResult(readings, estimated, Math.Round(area, 1));
    }

    /// <summary>Total rain, and how much of it overflowed, following the level from normal at the test.</summary>
    public static (double RainMm, double OverflowMm) Overflow(IEnumerable<WaterDay> days)
    {
        double level = 0, rain = 0, overflow = 0;
        foreach (var day in days)
        {
            rain += day.RainMm;
            level += day.RainMm - day.EvaporationMm;
            if (level > 0)
            {
                overflow += level;
                level = 0;
            }
            else if (level < -TopUpMm)
            {
                level = 0;
            }
        }

        return (rain, overflow);
    }

    /// <summary>The oldest test date that matters, so rainfall is only fetched that far back.</summary>
    public static DateOnly? Since(IReadOnlyDictionary<string, SourcedReading> water) =>
        Fields
            .Select(f => water.TryGetValue(f.Key, out var r) ? r.At : null)
            .OfType<DateTimeOffset>()
            .Select(at => (DateOnly?)DateOnly.FromDateTime(at.UtcDateTime))
            .Min();
}
