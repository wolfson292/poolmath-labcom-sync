using PoolSync.Chemistry;
using PoolSync.Configuration;
using PoolSync.Weather;
using Xunit;

namespace PoolSync.Tests;

public class DilutionTests
{
    private static readonly DateTimeOffset Tested = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);
    private const double Litres25k = 25000 * PoolProfile.LitresPerGallon;

    private static Dictionary<string, SourcedReading> Water() => new()
    {
        [PoolMathFields.CyanuricAcid] = new(70, ReadingSource.LabCom, Tested),
        [PoolMathFields.Salt] = new(3600, ReadingSource.Manual, Tested.AddDays(10)),
        [PoolMathFields.Ph] = new(7.6, ReadingSource.LabCom, Tested),
    };

    private static Dictionary<DateOnly, WaterDay> Days(params (DateOnly Day, double Rain, double Evaporation)[] days) =>
        days.ToDictionary(d => d.Day, d => new WaterDay(d.Rain, d.Evaporation));

    [Fact]
    public void Rain_after_each_test_dilutes_its_reading()
    {
        // 50.8 mm (2 in) on Sep 5, 25.4 mm (1 in) on Sep 20, no evaporation; the salt test was Sep 11.
        var rain = Days((new(2026, 9, 5), 50.8, 0), (new(2026, 9, 20), 25.4, 0));
        var settings = new PoolSettings { Volume = 25000, SurfaceArea = 600 };

        var result = Dilution.Estimate(Water(), rain, settings, Litres25k)!;
        var cya = result.Readings.Single(r => r.Key == PoolMathFields.CyanuricAcid);
        var salt = result.Readings.Single(r => r.Key == PoolMathFields.Salt);

        // 3 in on 600 ft² is 1,122 gal: about 4.4% of 25,000 gal.
        Assert.Equal(3, cya.RainInches, precision: 2);
        Assert.InRange(cya.Percent, 4.2, 4.6);
        Assert.Equal(1, salt.RainInches, precision: 2);
        Assert.InRange(salt.Estimate, 3540, 3552); // 1 in on 600 ft² is 374 gal: 1.5%.
        Assert.DoesNotContain(result.Readings, r => r.Key == PoolMathFields.Ph);
    }

    [Fact]
    public void Rain_on_the_test_day_itself_is_not_counted()
    {
        var rain = Days((new(2026, 9, 1), 100, 0));

        var cya = Dilution.Estimate(Water(), rain, new PoolSettings { SurfaceArea = 600 }, Litres25k)!
            .Readings.Single(r => r.Key == PoolMathFields.CyanuricAcid);

        Assert.Equal(0, cya.Percent);
    }

    [Fact]
    public void A_spa_without_a_surface_area_counts_as_covered()
    {
        Assert.Null(Dilution.Estimate(Water(), Days(),
            new PoolSettings { Surface = PoolSurface.Spa }, 800));
    }

    [Fact]
    public void Without_a_set_area_it_is_estimated_from_volume()
    {
        var (m2, estimated) = Dilution.SurfaceArea(new PoolSettings(), Litres25k);

        Assert.True(estimated);
        Assert.InRange(m2!.Value, 60, 66);
    }

    [Fact]
    public void Evaporation_soaks_up_rain_before_any_overflows()
    {
        // 1 in of rain after the level has dropped 1 in to evaporation: nothing overflows.
        var (rain, overflow) = Dilution.Overflow([new WaterDay(0, 25.4), new WaterDay(25.4, 0)]);

        Assert.Equal(25.4, rain);
        Assert.Equal(0, overflow);
    }

    [Fact]
    public void Only_rain_above_the_normal_level_overflows()
    {
        // Down 0.5 in, then 2 in of rain: 1.5 in overflows.
        var (_, overflow) = Dilution.Overflow([new WaterDay(0, 12.7), new WaterDay(50.8, 0)]);

        Assert.Equal(38.1, overflow, precision: 6);
    }

    [Fact]
    public void A_low_pool_is_topped_up_with_fresh_water_and_loses_nothing()
    {
        // Down 3 in (past the 2 in top-up point), topped back to normal; the next 1 in of rain overflows.
        var (_, overflow) = Dilution.Overflow([new WaterDay(0, 76.2), new WaterDay(25.4, 0)]);

        Assert.Equal(25.4, overflow, precision: 6);
    }
}



public class FcFromOrpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // A probe reading 650 mV at FC/CYA 0.1 and 60 mV more per tenfold FC/CYA.
    private static double Orp(double ratio) => 650 + 60 * Math.Log10(ratio / 0.1);

    [Fact]
    public void A_clean_set_of_tests_fits_and_reads_back()
    {
        var points = new[] { 0.05, 0.08, 0.12, 0.2 }.Select(r => (r, Orp(r))).ToList();

        var fit = FcFromOrp.Fit(points)!;

        Assert.Equal(60, fit.B, precision: 3);
        Assert.Equal(6, FcFromOrp.FcAt(fit, Orp(0.1), 60), precision: 3);
    }

    [Fact]
    public void Too_few_or_too_similar_tests_give_no_fit()
    {
        Assert.Null(FcFromOrp.Fit([(0.1, 650.0), (0.12, 655.0)]));
        Assert.Null(FcFromOrp.Fit([(0.1, 650.0), (0.105, 651.0), (0.11, 652.0)]));
    }

    [Fact]
    public void A_probe_that_does_not_track_chlorine_gives_no_fit()
    {
        Assert.Null(FcFromOrp.Fit([(0.05, 700.0), (0.1, 650.0), (0.2, 600.0)]));
    }

    [Fact]
    public void Falling_orp_predicts_when_fc_reaches_the_minimum()
    {
        var fit = FcFromOrp.Fit(new[] { 0.05, 0.08, 0.12, 0.2 }.Select(r => (r, Orp(r))).ToList());
        // FC falling 0.5 ppm/h from 6 ppm over 4 hours, at CYA 60.
        var recent = Enumerable.Range(0, 17)
            .Select(i => (Now.AddHours(-4 + i * 0.25), Orp((6 - 2 + (16 - i) * 0.125) / 60)))
            .ToList();

        var prediction = FcFromOrp.Predict(fit, 4, 60, minimum: 3, recent);

        Assert.Equal(4, prediction.Estimate!.Value, precision: 1);
        Assert.InRange(prediction.PerHour!.Value, -0.6, -0.4);
        Assert.InRange(prediction.HoursToMinimum!.Value, 1.6, 2.4);
    }

    [Fact]
    public void Without_a_fit_it_says_how_many_more_tests_are_needed()
    {
        var prediction = FcFromOrp.Predict(null, 1, 60, 3, []);

        Assert.Null(prediction.Estimate);
        Assert.Contains("2 more", prediction.Note);
    }
}
