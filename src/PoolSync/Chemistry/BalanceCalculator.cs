using PoolSync.Configuration;

namespace PoolSync.Chemistry;

/// <summary>What the calculator needs to know about a pool beyond its readings.</summary>
public sealed record PoolProfile(
    double? VolumeLitres,
    bool Imperial,
    PoolSurface Surface,
    bool Swg,
    double? SaltMin,
    double? SaltMax,
    double? SaltTarget,
    bool TrackSalt,
    double? BorMin,
    double? BorMax,
    double? BorTarget,
    bool TrackBor,
    double? FcTargetOverride,
    int TempUnits)
{
    public const double LitresPerGallon = 3.78541;

    public static PoolProfile From(PoolSettings settings)
    {
        var imperial = settings.VolumeUnit == 0;
        double? litres = settings.Volume is > 0 and var v ? (imperial ? v * LitresPerGallon : v) : null;

        // Salt and borate are checked wherever the pool has a target for them.
        return new PoolProfile(
            litres,
            imperial,
            settings.Surface,
            settings.Swg,
            settings.SaltMin,
            settings.SaltMax,
            settings.SaltTarget,
            settings.SaltTarget is not null,
            settings.BorMin,
            settings.BorMax,
            settings.BorTarget,
            settings.BorTarget is not null,
            settings.FcTarget,
            settings.TempUnits);
    }
}

/// <summary>An ideal range and the value to dose towards when a reading falls outside it.</summary>
public sealed record BalanceTarget(
    string Key, string Label, double Min, double Max, double Target, double? Current, string Status);

/// <summary>One thing to add, or do, to bring a parameter back into range.</summary>
public sealed record Recommendation(
    string Key, string Label, double Current, double Target, string Action, string? Amount, string? Note);

public sealed record WaterBalance(
    IReadOnlyDictionary<string, SourcedReading> Water,
    int TempUnits,
    double? Csi,
    double? CsiAfter,
    double CsiMin,
    double CsiMax,
    IReadOnlyList<BalanceTarget> Targets,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<string> Notes);

/// <summary>
/// Compares the current water against Trouble Free Pool's recommended levels, which are what Pool
/// Math checks against, and works out how much of each product brings it back. Where the pool has its
/// own target in its settings (salt, borate, an FC override) that wins.
/// </summary>
public sealed class BalanceCalculator(BalanceOptions options)
{
    public const double CsiMin = -0.3;
    public const double CsiMax = 0.3;

    public WaterBalance Calculate(IReadOnlyDictionary<string, SourcedReading> water, PoolProfile pool)
    {
        double? Get(string key) => water.TryGetValue(key, out var r) ? r.Value : null;

        var targets = new List<BalanceTarget>();
        var recommendations = new List<Recommendation>();
        var notes = new List<string>();

        var ph = Get(PoolMathFields.Ph);
        var ta = Get(PoolMathFields.TotalAlkalinity);
        var ch = Get(PoolMathFields.CalciumHardness);
        var cya = Get(PoolMathFields.CyanuricAcid);
        var salt = Get(PoolMathFields.Salt);
        var bor = Get(PoolMathFields.Borate);
        var fc = Get(PoolMathFields.FreeChlorine);
        var tempC = Get(WaterReadings.WaterTempC);

        if (water.TryGetValue(WaterReadings.WaterTempC, out var temperature) && temperature.Note is not null)
        {
            notes.Add(
                $"The latest water temperature was {temperature.Note}. Add a test with the current " +
                "temperature to replace it.");
        }

        if (tempC > WaterReadings.ImplausibleCelsius)
        {
            notes.Add($"Water temperature reads {tempC:0.#} °C, which can't be right. Enter the current temperature below.");
            tempC = null;
        }

        // The values the water should end up at, for projecting CSI after every recommendation.
        var after = new Dictionary<string, double>(StringComparer.Ordinal);
        void Plan(string key, double value) => after[key] = value;

        // --- Free chlorine: a percentage of CYA, lower for a salt cell that tops it up all day.
        if (cya is { } c)
        {
            var target = pool.FcTargetOverride ?? Math.Round(c * (pool.Swg ? 0.075 : 0.125) * 2) / 2;
            var min = Math.Round(c * (pool.Swg ? 0.05 : 0.075), 1);
            // FC has no TFP upper limit short of SLAM level (40% of CYA), so that's the top of the range.
            targets.Add(Target(PoolMathFields.FreeChlorine, "FC", min, Math.Max(target, Math.Round(c * 0.4, 1)), target, fc));

            if (fc is { } f && f < target)
            {
                var ml = (target - f) * Litres(pool) / (options.ChlorinePercent * 10);
                recommendations.Add(new Recommendation(
                    PoolMathFields.FreeChlorine, "FC", f, target, "raise",
                    pool.VolumeLitres is null ? null : $"{Volume(ml, pool.Imperial)} of {options.ChlorinePercent:0.##}% liquid chlorine",
                    pool.Swg ? "Or raise the salt cell output; this is the one-off top-up." : null));
            }
        }

        // --- pH, dosed with the carbonate model so TA, CYA and borate buffering are accounted for.
        const double phMin = 7.6, phMax = 7.8, phTarget = 7.6;
        targets.Add(Target(PoolMathFields.Ph, "pH", phMin, phMax, phTarget, ph));

        if (ph is { } p && ta is { } t && tempC is { } tc && (p > phMax || p < phMin))
        {
            var model = (ch: ch ?? 0, cya: cya ?? 0, bor: bor ?? 0, salt: salt ?? 0);

            if (p > phMax)
            {
                var meqPerLitre = WaterChemistry.AcidDemand(p, phTarget, t, model.cya, model.bor, model.ch, model.salt, tc);
                var ml = meqPerLitre * Litres(pool) / AcidMeqPerMl(options.AcidPercent);
                recommendations.Add(new Recommendation(
                    PoolMathFields.Ph, "pH", p, phTarget, "lower",
                    pool.VolumeLitres is null ? null : $"{Volume(ml, pool.Imperial)} of {options.AcidPercent:0.##}% muriatic acid",
                    "Pour it in front of a return with the pump running, then retest pH after 30 minutes."));
                Plan(PoolMathFields.TotalAlkalinity, t - meqPerLitre * WaterChemistry.CaCO3PerMeq);
            }
            else
            {
                var mmolPerLitre = WaterChemistry.SodaAshDemand(p, phTarget, t, model.cya, model.bor, model.ch, model.salt, tc);
                var mg = mmolPerLitre * Litres(pool) * 105.99;
                recommendations.Add(new Recommendation(
                    PoolMathFields.Ph, "pH", p, phTarget, "raise",
                    pool.VolumeLitres is null ? null : $"{Mass(mg, pool.Imperial)} of soda ash (washing soda)",
                    "This raises TA as well. In a salt pool pH usually drifts up on its own; consider waiting."));
                Plan(PoolMathFields.TotalAlkalinity, t + 2 * mmolPerLitre * WaterChemistry.CaCO3PerMeq);
            }

            Plan(PoolMathFields.Ph, phTarget);
        }
        else if (ph is not null && (ph > phMax || ph < phMin) && tempC is null)
        {
            notes.Add("Enter the water temperature to get a pH dose; it changes how much acid or soda ash is needed.");
        }

        // --- Total alkalinity. Raised with baking soda; lowering it is a process, not a dose.
        const double taMin = 60, taMax = 80, taTarget = 70;
        targets.Add(Target(PoolMathFields.TotalAlkalinity, "TA", taMin, taMax, taTarget, ta));

        if (ta is { } alk && alk < taMin)
        {
            var mg = (taTarget - alk) / WaterChemistry.CaCO3PerMeq * Litres(pool) * 84.01;
            recommendations.Add(new Recommendation(
                PoolMathFields.TotalAlkalinity, "TA", alk, taTarget, "raise",
                pool.VolumeLitres is null ? null : $"{Mass(mg, pool.Imperial)} of baking soda", null));
            Plan(PoolMathFields.TotalAlkalinity, taTarget);
        }
        else if (ta is { } high && high > taMax)
        {
            recommendations.Add(new Recommendation(
                PoolMathFields.TotalAlkalinity, "TA", high, taTarget, "lower", null,
                "Lower pH to 7.2 with acid, then aerate (point returns up, run water features) to bring pH " +
                "back up without adding alkalinity. Repeat until TA is in range."));
        }

        // --- Calcium hardness, by surface.
        var (chMin, chMax, chTarget) = pool.Surface switch
        {
            PoolSurface.Vinyl => (50.0, 550.0, 50.0),
            // Hot tubs foam with much calcium, but need some to protect the heater.
            PoolSurface.Spa => (100.0, 250.0, 150.0),
            _ => (350.0, 550.0, 400.0),
        };
        targets.Add(Target(PoolMathFields.CalciumHardness, "CH", chMin, chMax, chTarget, ch));

        if (ch is { } hard && hard < chMin)
        {
            // Calcium chloride dihydrate, the usual "calcium increaser" flake.
            var mg = (chTarget - hard) / 100.09 * Litres(pool) * 147.01;
            recommendations.Add(new Recommendation(
                PoolMathFields.CalciumHardness, "CH", hard, chTarget, "raise",
                pool.VolumeLitres is null ? null : $"{Mass(mg, pool.Imperial)} of calcium chloride",
                "Dissolve it in a bucket of pool water first; it gets hot."));
            Plan(PoolMathFields.CalciumHardness, chTarget);
        }
        else if (ch is { } hardHigh && hardHigh > chMax)
        {
            recommendations.Add(Drain(PoolMathFields.CalciumHardness, "CH", hardHigh, chTarget));
            Plan(PoolMathFields.CalciumHardness, chTarget);
        }

        // --- CYA, higher for a salt cell.
        var (cyaMin, cyaMax, cyaTarget) =
            pool.Surface == PoolSurface.Spa ? (30.0, 50.0, 40.0)
            : pool.Swg ? (70.0, 80.0, 75.0)
            : (40.0, 50.0, 45.0);
        targets.Add(Target(PoolMathFields.CyanuricAcid, "CYA", cyaMin, cyaMax, cyaTarget, cya));

        if (cya is { } stab && stab < cyaMin)
        {
            var mg = (cyaTarget - stab) * Litres(pool);
            recommendations.Add(new Recommendation(
                PoolMathFields.CyanuricAcid, "CYA", stab, cyaTarget, "raise",
                pool.VolumeLitres is null ? null : $"{Stabilizer(mg, pool.Imperial)} of stabilizer (cyanuric acid)",
                "It dissolves slowly: put it in a sock in the skimmer basket and don't backwash for a week. " +
                "Raise FC to match the new CYA."));
            Plan(PoolMathFields.CyanuricAcid, cyaTarget);
        }
        else if (cya is { } stabHigh && stabHigh > cyaMax)
        {
            recommendations.Add(Drain(PoolMathFields.CyanuricAcid, "CYA", stabHigh, cyaTarget));
            Plan(PoolMathFields.CyanuricAcid, cyaTarget);
        }

        // --- Salt, only where the pool has a salt target.
        if (pool.SaltTarget is { } saltTarget && (pool.Swg || pool.TrackSalt))
        {
            var saltMin = pool.SaltMin ?? saltTarget - 200;
            var saltMax = pool.SaltMax ?? saltTarget + 600;
            targets.Add(Target(PoolMathFields.Salt, "Salt", saltMin, saltMax, saltTarget, salt));

            if (salt is { } s && s < saltMin)
            {
                var mg = (saltTarget - s) * Litres(pool);
                recommendations.Add(new Recommendation(
                    PoolMathFields.Salt, "Salt", s, saltTarget, "raise",
                    pool.VolumeLitres is null ? null : $"{Mass(mg, pool.Imperial)} of pool salt",
                    "Turn the salt cell off for 24 hours while it dissolves."));
                Plan(PoolMathFields.Salt, saltTarget);
            }
            else if (salt is { } sHigh && sHigh > saltMax)
            {
                recommendations.Add(Drain(PoolMathFields.Salt, "Salt", sHigh, saltTarget));
                Plan(PoolMathFields.Salt, saltTarget);
            }
        }

        // --- Borate, only where the pool has a borate target.
        if (pool.BorTarget is { } borTarget && borTarget > 0)
        {
            var borMin = pool.BorMin ?? Math.Max(0, borTarget - 10);
            var borMax = pool.BorMax ?? borTarget + 20;
            targets.Add(Target(PoolMathFields.Borate, "Borate", borMin, borMax, borTarget, bor));

            if (bor is null)
            {
                notes.Add("Enter a borate reading below to check it against the pool's borate target.");
            }
            else if (bor < borMin)
            {
                var mg = (borTarget - bor.Value) * Litres(pool) * 61.83 / 10.81;
                recommendations.Add(new Recommendation(
                    PoolMathFields.Borate, "Borate", bor.Value, borTarget, "raise",
                    pool.VolumeLitres is null ? null : $"{Mass(mg, pool.Imperial)} of boric acid",
                    "Add it slowly in front of a return; it lowers pH slightly as it mixes."));
                Plan(PoolMathFields.Borate, borTarget);
            }
            else if (bor > borMax)
            {
                recommendations.Add(Drain(PoolMathFields.Borate, "Borate", bor.Value, borTarget));
                Plan(PoolMathFields.Borate, borTarget);
            }
        }

        // --- CSI now, and once everything above has been done.
        double? Csi(Func<string, double?> value) =>
            value(PoolMathFields.Ph) is { } cp && value(PoolMathFields.TotalAlkalinity) is { } ct
            && value(PoolMathFields.CalciumHardness) is { } cc && tempC is { } t
                ? WaterChemistry.Csi(
                    cp, ct, cc,
                    value(PoolMathFields.CyanuricAcid) ?? 0,
                    value(PoolMathFields.Salt) ?? 0,
                    value(PoolMathFields.Borate) ?? 0,
                    t)
                : null;

        var csi = Csi(Get);
        var csiAfter = recommendations.Count == 0 ? csi : Csi(k => after.TryGetValue(k, out var v) ? v : Get(k));

        if (csi is null)
        {
            var missing = new[]
                {
                    (ph, "pH"), (ta, "TA"), (ch, "CH"), (tempC, "water temperature"),
                }
                .Where(x => x.Item1 is null)
                .Select(x => x.Item2)
                .ToList();

            if (missing.Count > 0)
            {
                notes.Add($"CSI needs {string.Join(", ", missing)}.");
            }
        }
        else if (csiAfter is { } projected && (projected < CsiMin || projected > CsiMax))
        {
            notes.Add(projected > CsiMax
                ? $"CSI would still be {projected:+0.00;-0.00} after these changes. Lowering TA towards {taMin:0} brings it down."
                : $"CSI would still be {projected:+0.00;-0.00} after these changes. Raising CH or TA brings it up.");
        }

        if (salt is null && (pool.Swg || pool.TrackSalt))
        {
            notes.Add("No salt reading; CSI is calculated as if there were no salt, which reads it slightly high.");
        }

        if (pool.VolumeLitres is null && recommendations.Count > 0)
        {
            notes.Add("This pool has no volume set, so amounts can't be calculated. Set it in the pool's settings.");
        }

        return new WaterBalance(
            water, pool.TempUnits, Round(csi), Round(csiAfter), CsiMin, CsiMax, targets, recommendations, notes);
    }

    private static BalanceTarget Target(string key, string label, double min, double max, double target, double? current) =>
        new(key, label, min, max, target, current,
            current is null ? "unknown" : current < min ? "low" : current > max ? "high" : "ok");

    private static Recommendation Drain(string key, string label, double current, double target)
    {
        var percent = Math.Ceiling((1 - target / current) * 100);
        return new Recommendation(
            key, label, current, target, "lower", $"Replace about {percent:0}% of the water",
            "Nothing removes it chemically; draining and refilling with fresh water dilutes it.");
    }

    private static double Litres(PoolProfile pool) => pool.VolumeLitres ?? 0;

    /// <summary>Milliequivalents of HCl per mL of muriatic acid at the given strength.</summary>
    private static double AcidMeqPerMl(double percent)
    {
        // Density rises with strength: about 1.07 g/mL at 14.5% and 1.16 at 31.45%.
        var density = 1 + 0.005 * percent;
        return percent / 100 * density * 1000 / 36.46;
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;

    public static string Volume(double ml, bool imperial)
    {
        if (!imperial)
        {
            return ml < 10 ? $"{ml:0.#} mL" : ml < 1000 ? $"{ml:0} mL" : $"{ml / 1000:0.##} L";
        }

        var flOz = ml / 29.5735;
        return flOz < 10 ? $"{flOz:0.#} fl oz"
            : flOz < 128 ? $"{flOz:0} fl oz"
            : $"{flOz / 128:0.##} gal ({flOz:0} fl oz)";
    }

    /// <summary>Granular cyanuric acid: about 2¼ dry cups to the pound.</summary>
    private const double StabilizerCupsPerPound = 2.25;

    /// <summary>Stabilizer is measured out by the cup as often as weighed, so show both.</summary>
    internal static string Stabilizer(double mg, bool imperial)
    {
        var pounds = mg / 1000 / 453.592;
        var cups = pounds * StabilizerCupsPerPound;
        var weight = imperial ? $"{pounds:0.#} lb" : Mass(mg, imperial: false);
        return $"{weight} ({cups:0.#} cups)";
    }

    public static string Mass(double mg, bool imperial)
    {
        var grams = mg / 1000;
        if (!imperial)
        {
            return grams < 10 ? $"{grams:0.#} g" : grams < 1000 ? $"{grams:0} g" : $"{grams / 1000:0.##} kg";
        }

        var oz = grams / 28.3495;
        return oz < 1 ? $"{oz:0.##} oz" : oz < 16 ? $"{oz:0.#} oz" : $"{oz / 16:0.#} lb";
    }
}
