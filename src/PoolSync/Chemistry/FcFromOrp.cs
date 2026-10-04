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
    string? Note);

/// <summary>
/// Estimates free chlorine between tests from an ORP probe. ORP responds to the active, unbound
/// chlorine, which for a given pH is proportional to FC / CYA, and roughly logarithmically: so each
/// pool's ORP is fitted against log(FC / CYA) at the moments it was tested. Probes differ and drift,
/// which is why the fit is per pool and uses only the last few months of tests.
/// </summary>
public static class FcFromOrp
{
    public const int MinimumPoints = 3;

    /// <summary>The fit, or null if the points can't support one: too few, too close together, or too scattered.</summary>
    public static OrpCalibration? Fit(IReadOnlyList<(double Ratio, double Orp)> points)
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

        // ORP must rise with chlorine; a flat or inverted fit means the probe isn't tracking it.
        return b > 0 && r2 >= 0.5 ? new OrpCalibration(a, b, r2, usable.Count) : null;
    }

    public static double FcAt(OrpCalibration calibration, double orp, double cya) =>
        Math.Clamp(cya * Math.Pow(10, (orp - calibration.A) / calibration.B), 0, 60);

    /// <param name="recentOrp">The last few hours of ORP samples, oldest first; the last is current.</param>
    public static FcPrediction Predict(
        OrpCalibration? calibration, int calibrationPoints, double? cya, double minimum,
        IReadOnlyList<(DateTimeOffset At, double Orp)> recentOrp)
    {
        if (calibration is null)
        {
            var needed = Math.Max(1, MinimumPoints - calibrationPoints);
            return new FcPrediction(null, null, minimum, null, calibrationPoints, null,
                calibrationPoints < MinimumPoints
                    ? $"Needs {needed} more FC test(s) while the ORP probe is reporting to estimate FC between tests."
                    : "The FC tests so far don't line up with ORP well enough to estimate from it; more tests at different FC levels will help.");
        }

        if (cya is not > 0 || recentOrp.Count == 0)
        {
            return new FcPrediction(null, null, minimum, null, calibrationPoints, calibration.R2,
                cya is not > 0 ? "Needs a CYA test to estimate FC from ORP." : "No ORP reading right now.");
        }

        var series = recentOrp.Select(s => (s.At, Fc: FcAt(calibration, s.Orp, cya.Value))).ToList();
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
