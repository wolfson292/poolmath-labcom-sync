using PoolSync.Chemistry;
using Xunit;

namespace PoolSync.Tests;

public class WaterChemistryTests
{
    // Overviews Pool Math returned for two real pools on 2026-10-03, with the CSI it calculated.
    [Theory]
    [InlineData(8.29, 105, 200, 22, 2900, 0, 81.92, 1.22)]
    [InlineData(8.19, 76, 500, 34.713, 4000, 0, 29.2, 0.59)]
    public void Csi_matches_what_pool_math_reports(
        double ph, double ta, double ch, double cya, double salt, double bor, double tempC, double expected)
    {
        var csi = WaterChemistry.Csi(ph, ta, ch, cya, salt, bor, tempC);

        Assert.NotNull(csi);
        Assert.InRange(csi.Value, expected - 0.02, expected + 0.02);
    }

    [Fact]
    public void Csi_is_undefined_when_cya_accounts_for_all_the_alkalinity()
    {
        Assert.Null(WaterChemistry.Csi(7.5, 10, 300, 100, 0, 0, 27));
    }

    [Fact]
    public void Borate_buffering_raises_the_acid_needed()
    {
        var without = WaterChemistry.AcidDemand(8.2, 7.5, 100, 30, 0, 300, 1000, 27);
        var with = WaterChemistry.AcidDemand(8.2, 7.5, 100, 30, 50, 300, 1000, 27);

        Assert.True(without > 0);
        Assert.True(with > without * 2, $"expected borates to at least double the demand: {without} vs {with}");
    }

    [Fact]
    public void No_acid_is_needed_to_reach_a_higher_ph()
    {
        Assert.Equal(0, WaterChemistry.AcidDemand(7.4, 7.6, 80, 30, 0, 300, 1000, 27));
    }

    [Fact]
    public void Acid_demand_is_in_the_range_closed_system_chemistry_predicts()
    {
        // 10,000 gal at TA 100, pH 8.2 -> 7.5: roughly half a litre of full-strength muriatic acid.
        var meqPerLitre = WaterChemistry.AcidDemand(8.2, 7.5, 100, 30, 0, 300, 1000, 27);
        var ml = meqPerLitre * 37854 / (0.3145 * 1.157 * 1000 / 36.46);

        Assert.InRange(ml, 350, 700);
    }

    [Fact]
    public void Soda_ash_demand_grows_with_the_rise_and_is_zero_for_none()
    {
        var small = WaterChemistry.SodaAshDemand(7.2, 7.4, 80, 30, 0, 300, 1000, 27);
        var large = WaterChemistry.SodaAshDemand(7.2, 7.6, 80, 30, 0, 300, 1000, 27);

        Assert.True(small > 0);
        Assert.True(large > small);
        Assert.Equal(0, WaterChemistry.SodaAshDemand(7.6, 7.4, 80, 30, 0, 300, 1000, 27));
    }
}
