namespace PoolSync.Chemistry;

/// <summary>A pool's fitted relationship ORP = A + B · log10(FC / CYA), from tests paired with its ORP probe.</summary>
public sealed record OrpCalibration(double A, double B, double R2, int Points);

/// <summary>Free chlorine estimated from live ORP, and where it's heading.</summary>
public sealed record FcPrediction(
    double? Estimate,
    double? PerHour,
    double Minimum,
    double? HoursToMinimum,
    int CalibrationPoints,
    double? R2,
    string? Note,
    bool ProbeSuspect = false);

/// <summary>
/// Estimates free chlorine between tests from an ORP probe. ORP responds, roughly logarithmically,
/// to the active chlorine: hypochlorous acid not bound to CYA. That is proportional to FC / CYA and
/// falls as pH rises past HOCl's pKa of 7.53, so each pool's ORP is fitted against
/// log(FC / CYA / (1 + 10^(pH − 7.53))) at the moments it was tested. Probes differ and drift,
/// which is why the fit is per pool and uses only the last few months of tests.
/// </summary>
public static class FcFromOrp
{
    public const int MinimumPoints = 3;

    private const double HoclPka = 7.53;

    /// <summary>The share of free chlorine that's hypochlorous acid at this pH, relative to pH 7.53.</summary>
    public static double ActiveFactor(double ph) => 1 / (1 + Math.Pow(10, ph - HoclPka));

    /// <summary>The quantity ORP is fitted against: FC/CYA scaled by how active chlorine is at that pH.</summary>
    public static double Active(double fc, double cya, double ph) => fc / cya * ActiveFactor(ph);

    /// <summary>The fit, or null if the points can't support one: too few, too close together, or too scattered.</summary>
    public static OrpCalibration? Fit(IReadOnlyList<(double Ratio, double Orp)> points)
    {
        var line = Line(points);
        return line is { B: > 0, R2: >= 0.5 } ? line : null;
    }

    /// <summary>
    /// The least-squares line whatever its quality, or null when there are too few points or they
    /// cover too narrow a range to say anything.
    /// </summary>
    public static OrpCalibration? Line(IReadOnlyList<(double Ratio, double Orp)> points)
    {
        var usable = points.Where(p => p.Ratio > 0).Select(p => (X: Math.Log10(p.Ratio), Y: p.Orp)).ToList();
        if (usable.Count < MinimumPoints || usable.Max(p => p.X) - usable.Min(p => p.X) < Math.Log10(1.5))
        {
            return null;
        }

        var meanX = usable.Average(p => p.X);
        var meanY = usable.Average(p => p.Y);
        var sxx = usable.Sum(p => (p.X - meanX) * (p.X - meanX));
        var sxy = usable.Sum(p => (p.X - meanX) * (p.Y - meanY));
        var syy = usable.Sum(p => (p.Y - meanY) * (p.Y - meanY));
        if (sxx == 0 || syy == 0)
        {
            return null;
        }

        var b = sxy / sxx;
        var a = meanY - b * meanX;
        var r2 = sxy * sxy / (sxx * syy);
        return new OrpCalibration(a, b, r2, usable.Count);
    }

    public static double FcAt(OrpCalibration calibration, double orp, double cya, double ph) =>
        Math.Clamp(cya * Math.Pow(10, (orp - calibration.A) / calibration.B) / ActiveFactor(ph), 0, 60);

    /// <param name="recentOrp">The last few hours of ORP samples, oldest first; the last is current.</param>
    /// <param name="ph">Current pH, from the probe or the latest test.</param>
    /// <param name="line">The raw line through the points, used to explain a rejected fit.</param>
    public static FcPrediction Predict(
        OrpCalibration? calibration, int calibrationPoints, double? cya, double minimum,
        IReadOnlyList<(DateTimeOffset At, double Orp)> recentOrp, double ph = HoclPka, OrpCalibration? line = null)
    {
        if (calibration is null)
        {
            var needed = Math.Max(1, MinimumPoints - calibrationPoints);
            var note = calibrationPoints < MinimumPoints
                ? $"Needs {needed} more FC test(s) while the ORP probe is reporting to estimate FC between tests."
                : line is null
                    ? "The FC tests so far are too close together to calibrate ORP; tests at different FC levels will help."
                    : line.B <= 0
                        ? $"ORP isn't following FC: across {line.Points} tests it read higher when there was less chlorine. " +
                          "The ORP probe probably needs cleaning or calibrating."
                        : $"ORP only loosely follows FC across {line.Points} tests (R² {line.R2:0.00}). Cleaning or " +
                          "calibrating the ORP probe, or more tests, would help.";
            return new FcPrediction(
                null, null, minimum, null, calibrationPoints, line is null ? null : Math.Round(line.R2, 2), note,
                calibrationPoints >= MinimumPoints && line is { B: <= 0 });
        }

        if (cya is not > 0 || recentOrp.Count == 0)
        {
            return new FcPrediction(null, null, minimum, null, calibrationPoints, calibration.R2,
                cya is not > 0 ? "Needs a CYA test to estimate FC from ORP." : "No ORP reading right now.");
        }

        var series = recentOrp.Select(s => (s.At, Fc: FcAt(calibration, s.Orp, cya.Value, ph))).ToList();
        var estimate = Math.Round(series[^1].Fc, 1);

        double? perHour = null;
        if (series.Count >= 4 && (series[^1].At - series[0].At).TotalHours >= 1)
        {
            var t0 = series[0].At;
            var xs = series.Select(s => (s.At - t0).TotalHours).ToList();
            var ys = series.Select(s => s.Fc).ToList();
            var mx = xs.Average();
            var my = ys.Average();
            var sxx = xs.Sum(x => (x - mx) * (x - mx));
            perHour = sxx > 0 ? Math.Round(xs.Zip(ys).Sum(p => (p.First - mx) * (p.Second - my)) / sxx, 2) : null;
        }

        double? hours = perHour is < 0 && estimate > minimum ? Math.Round((estimate - minimum) / -perHour.Value, 1) : null;
        return new FcPrediction(estimate, perHour, minimum, hours, calibrationPoints, Math.Round(calibration.R2, 2), null);
    }
}
