namespace PoolSync.Chemistry;

/// <summary>
/// Water balance arithmetic: the calcite saturation index and a carbonate model for working out how
/// much acid or soda ash moves pH to a target.
///
/// The CSI formula and the CYA and borate alkalinity corrections are the ones Trouble Free Pool
/// publishes, which is what Pool Math uses; the tests pin it against CSI values Pool Math reported for
/// real pools. The carbonate model is standard aqueous chemistry, so its doses are estimates in the
/// same way Pool Math's are: add part, circulate, retest.
/// </summary>
public static class WaterChemistry
{
    /// <summary>mg of CaCO3 per milliequivalent: converts alkalinity in ppm to meq/L.</summary>
    public const double CaCO3PerMeq = 50.04;

    /// <summary>
    /// Calcite saturation index. Inputs are in Pool Math's units: ppm (borate as ppm boron) and °C.
    /// Returns null when the water can't hold any carbonate alkalinity, where the index is undefined.
    /// </summary>
    public static double? Csi(
        double ph, double ta, double ch, double cya, double salt, double borate, double tempC)
    {
        var carbonateAlkalinity = CarbonateAlkalinity(ta, cya, borate, ph);
        if (ch <= 0 || carbonateAlkalinity <= 0)
        {
            return null;
        }

        var sqrtI = Math.Sqrt(IonicStrength(ta, ch, salt));

        return ph
            - 6.9395
            + Math.Log10(ch)
            + Math.Log10(carbonateAlkalinity)
            - 2.56 * sqrtI / (1 + 1.65 * sqrtI)
            - 1412.5 / (tempC + 273.15);
    }

    /// <summary>The part of TA that is carbonate, once CYA's and borate's share is taken out.</summary>
    public static double CarbonateAlkalinity(double ta, double cya, double borate, double ph) =>
        ta - CyaAlkalinity(cya, ph) - BorateAlkalinity(borate, ph);

    /// <summary>
    /// Ionic strength from the major ions. Salt meters read calcium chloride as salt too, so the
    /// chloride that came in with the calcium is subtracted to avoid counting it twice.
    /// </summary>
    public static double IonicStrength(double ta, double ch, double salt)
    {
        var extraNaCl = Math.Max(0, salt - 1.1678 * ch);
        return (1.5 * ch + ta) / 50045.0 + extraNaCl / 58440.0;
    }

    /// <summary>
    /// Strong acid needed to take the water from <paramref name="ph"/> to <paramref name="targetPh"/>,
    /// in meq per litre. Carbon dioxide is assumed to stay in the water while the acid mixes in, which
    /// is close enough over the time it takes to dose and retest.
    /// </summary>
    public static double AcidDemand(
        double ph, double targetPh, double ta, double cya, double borate, double ch, double salt, double tempC)
    {
        var model = Carbonate.From(ph, ta, cya, borate, ch, salt, tempC);
        return Math.Max(0, model.TotalAlkalinity(ph) - model.TotalAlkalinity(targetPh));
    }

    /// <summary>
    /// Sodium carbonate (soda ash) needed to raise pH to <paramref name="targetPh"/>, in mmol per litre.
    /// Each mole adds one mole of carbonate and two equivalents of alkalinity, so TA rises too.
    /// </summary>
    public static double SodaAshDemand(
        double ph, double targetPh, double ta, double cya, double borate, double ch, double salt, double tempC)
    {
        if (targetPh <= ph)
        {
            return 0;
        }

        var model = Carbonate.From(ph, ta, cya, borate, ch, salt, tempC);
        var alkalinity = model.TotalAlkalinity(ph);

        // pH after adding x rises with x, so bisect on the dose.
        double low = 0, high = 20;
        for (var i = 0; i < 60; i++)
        {
            var mid = (low + high) / 2;
            var reached = model.WithCarbon(mid).SolvePh(alkalinity + 2 * mid);
            if (reached < targetPh)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static double CyaAlkalinity(double cya, double ph) =>
        0.38772 * cya / (1 + Math.Pow(10, 6.83 - ph));

    private static double BorateAlkalinity(double borate, double ph) =>
        4.63 * borate / (1 + Math.Pow(10, 9.11 - ph));

    /// <summary>Carbonate, cyanurate and borate in equilibrium, for a fixed amount of each.</summary>
    private sealed record Carbonate(double Dic, double Cya, double Borate, double K1, double K2)
    {
        public static Carbonate From(
            double ph, double ta, double cya, double borate, double ch, double salt, double tempC)
        {
            // Apparent constants: a quadratic fit to the textbook pK values between 0 and 40 °C, then
            // a Davies activity correction, since pH meters and reagents report hydrogen activity.
            var ionic = IonicStrength(ta, ch, salt);
            var sqrtI = Math.Sqrt(ionic);
            var logGamma1 = -0.51 * (sqrtI / (1 + sqrtI) - 0.3 * ionic);

            var pK1 = 6.58 - 0.012867 * tempC + 0.0001467 * tempC * tempC + logGamma1;
            var pK2 = 10.63 - 0.014917 * tempC + 0.0001167 * tempC * tempC + 3 * logGamma1;

            var k1 = Math.Pow(10, -pK1);
            var k2 = Math.Pow(10, -pK2);

            var carbonateMeq = CarbonateAlkalinity(ta, cya, borate, ph) / CaCO3PerMeq;
            var dic = Math.Max(0, carbonateMeq / CarbonateEquivalents(ph, k1, k2));

            return new Carbonate(dic, cya, borate, k1, k2);
        }

        public Carbonate WithCarbon(double addedMmolPerLitre) => this with { Dic = Dic + addedMmolPerLitre };

        /// <summary>Total alkalinity in meq/L this water would show at the given pH.</summary>
        public double TotalAlkalinity(double ph) =>
            Dic * CarbonateEquivalents(ph, K1, K2)
            + (CyaAlkalinity(Cya, ph) + BorateAlkalinity(Borate, ph)) / CaCO3PerMeq;

        /// <summary>The pH at which this water carries the given alkalinity, in meq/L.</summary>
        public double SolvePh(double alkalinity)
        {
            double low = 4, high = 11;
            for (var i = 0; i < 60; i++)
            {
                var mid = (low + high) / 2;
                if (TotalAlkalinity(mid) < alkalinity)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            return (low + high) / 2;
        }

        /// <summary>Equivalents of alkalinity per mole of dissolved carbonate at this pH.</summary>
        private static double CarbonateEquivalents(double ph, double k1, double k2)
        {
            var h = Math.Pow(10, -ph);
            var denominator = h * h + k1 * h + k1 * k2;
            return (k1 * h + 2 * k1 * k2) / denominator;
        }
    }
}
