using PoolSync.Configuration;

namespace PoolSync.Chemistry;

/// <summary>A product that can be added to the water.</summary>
/// <param name="Liquid">Measured by volume (normalised to mL) rather than weight (g).</param>
/// <param name="DefaultPercent">Strength, for products sold at more than one.</param>
public sealed record Chemical(string Name, bool Liquid, double? DefaultPercent);

/// <summary>A reading's value before and after an addition.</summary>
public sealed record Effect(string Key, string Label, double From, double To);

public sealed record EffectsResult(IReadOnlyList<Effect> Effects, double? CsiBefore, double? CsiAfter, string? Note);

/// <summary>
/// The products the logs and the effects preview know, with how each one changes the water. The
/// stoichiometry is standard; FC from hypochlorite is by trade percentage, as for the doses.
/// </summary>
public static class Chemicals
{
    public const string LiquidChlorine = "Liquid chlorine";
    public const string CalHypo = "Cal-hypo";
    public const string Dichlor = "Dichlor";
    public const string Trichlor = "Trichlor";
    public const string MuriaticAcid = "Muriatic acid";
    public const string BakingSoda = "Baking soda";
    public const string SodaAsh = "Soda ash";
    public const string Borax = "Borax";
    public const string BoricAcid = "Boric acid";
    public const string CalciumChloride = "Calcium chloride";
    public const string Stabilizer = "Stabilizer";
    public const string Salt = "Salt";
    public const string Other = "Other";

    public static readonly IReadOnlyList<Chemical> All =
    [
        new(LiquidChlorine, true, 10),
        new(MuriaticAcid, true, 31.45),
        new(BakingSoda, false, null),
        new(SodaAsh, false, null),
        new(CalciumChloride, false, null),
        new(Stabilizer, false, null),
        new(Salt, false, null),
        new(BoricAcid, false, null),
        new(Borax, false, null),
        new(CalHypo, false, 65),
        new(Dichlor, false, 56),
        new(Trichlor, false, 90),
        new(Other, false, null),
    ];

    public static Chemical? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Units each kind is entered in, with the factor to mL (liquids) or g (solids).</summary>
    public static readonly IReadOnlyDictionary<string, double> LiquidUnits = new Dictionary<string, double>
    {
        ["fl oz"] = 29.5735, ["cup"] = 236.588, ["gal"] = 3785.41, ["mL"] = 1, ["L"] = 1000,
    };

    public static readonly IReadOnlyDictionary<string, double> SolidUnits = new Dictionary<string, double>
    {
        ["oz"] = 28.3495, ["lb"] = 453.592, ["g"] = 1, ["kg"] = 1000,
    };

    /// <summary>The amount in mL or g, or null if the unit doesn't suit the product.</summary>
    public static double? Normalise(Chemical chemical, double amount, string unit)
    {
        if (chemical.Liquid)
        {
            return LiquidUnits.TryGetValue(unit, out var ml) ? amount * ml : null;
        }

        // Stabilizer is often measured by the cup: about 2¼ cups to the pound.
        if (chemical.Name == Stabilizer && unit == "cup")
        {
            return amount * 453.592 / 2.25;
        }

        return SolidUnits.TryGetValue(unit, out var g) ? amount * g : null;
    }

    /// <summary>How adding <paramref name="normalised"/> mL or g of a product changes the water.</summary>
    public static EffectsResult Effects(
        Chemical chemical, double normalised, double? percent,
        IReadOnlyDictionary<string, SourcedReading> water, double litres)
    {
        double? Get(string key) => water.TryGetValue(key, out var r) ? r.Value : null;

        var ph = Get(PoolMathFields.Ph);
        var ta = Get(PoolMathFields.TotalAlkalinity);
        var ch = Get(PoolMathFields.CalciumHardness);
        var cya = Get(PoolMathFields.CyanuricAcid);
        var salt = Get(PoolMathFields.Salt);
        var bor = Get(PoolMathFields.Borate);
        var fc = Get(PoolMathFields.FreeChlorine);
        var tempC = Get(WaterReadings.WaterTempC);
        var strength = percent ?? chemical.DefaultPercent ?? 100;

        // ppm changes, and charge/carbon/borate changes for the pH model.
        double dFc = 0, dCh = 0, dCya = 0, dSalt = 0, dBor = 0, dAlk = 0, dCarbon = 0;
        var mgPerLitre = normalised * 1000 / litres;

        switch (chemical.Name)
        {
            case LiquidChlorine:
                dFc = normalised * strength * 10 / litres;
                break;
            case CalHypo:
                dFc = mgPerLitre * strength / 100;
                dCh = dFc * 0.704;
                break;
            case Dichlor:
                dFc = mgPerLitre * strength / 100;
                dCya = dFc * 0.9;
                break;
            case Trichlor:
                dFc = mgPerLitre * strength / 100;
                dCya = dFc * 0.6;
                break;
            case MuriaticAcid:
                dAlk = -normalised * strength / 100 * (1 + 0.005 * strength) * 1000 / 36.46 / litres;
                break;
            case BakingSoda:
                dAlk = dCarbon = mgPerLitre / 84.01;
                break;
            case SodaAsh:
                dCarbon = mgPerLitre / 105.99;
                dAlk = 2 * dCarbon;
                break;
            case Borax:
                // Na2B4O7·10H2O: four borons and two equivalents of base per mole.
                dBor = mgPerLitre / 381.37 * 4 * 10.81;
                dAlk = mgPerLitre / 381.37 * 2;
                break;
            case BoricAcid:
                dBor = mgPerLitre / 61.83 * 10.81;
                break;
            case CalciumChloride:
                dCh = mgPerLitre / 147.01 * 100.09;
                break;
            case Stabilizer:
                dCya = mgPerLitre;
                break;
            case Salt:
                dSalt = mgPerLitre;
                break;
        }

        var effects = new List<Effect>();
        void Changed(string key, string label, double? from, double delta)
        {
            if (delta != 0)
            {
                effects.Add(new Effect(key, label, from ?? 0, Math.Round((from ?? 0) + delta, 2)));
            }
        }

        Changed(PoolMathFields.FreeChlorine, "FC", fc, dFc);
        Changed(PoolMathFields.CalciumHardness, "CH", ch, dCh);
        Changed(PoolMathFields.CyanuricAcid, "CYA", cya, dCya);
        Changed(PoolMathFields.Salt, "Salt", salt, dSalt);
        Changed(PoolMathFields.Borate, "Borate", bor, dBor);

        string? note = null;
        double? newPh = ph, newTa = ta;
        if (dAlk != 0 || dCarbon != 0 || dBor != 0 || dCya != 0)
        {
            if (ph is { } p && ta is { } t && tempC is { } tc)
            {
                (var phAfter, var taAfter) = WaterChemistry.After(
                    p, t, cya ?? 0, bor ?? 0, ch ?? 0, salt ?? 0, tc, dAlk, dCarbon, dBor, dCya);
                newPh = Math.Round(phAfter, 2);
                newTa = Math.Round(taAfter, 0);
                effects.Add(new Effect(PoolMathFields.Ph, "pH", p, newPh.Value));
                effects.Add(new Effect(PoolMathFields.TotalAlkalinity, "TA", t, newTa.Value));
            }
            else
            {
                note = "Needs pH, TA and water temperature to show the effect on pH.";
            }
        }

        double? Csi(double? phValue, double? taValue, double? chValue, double? cyaValue, double? saltValue, double? borValue) =>
            phValue is { } a && taValue is { } b && chValue is { } c && tempC is { } d
                ? WaterChemistry.Csi(a, b, c, cyaValue ?? 0, saltValue ?? 0, borValue ?? 0, d)
                : null;

        var before = Csi(ph, ta, ch, cya, salt, bor);
        var after = Csi(newPh, newTa, ch + dCh, cya + dCya, salt + dSalt, bor + dBor);

        return new EffectsResult(effects, Round(before), Round(after), note);
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 2) : null;
}
