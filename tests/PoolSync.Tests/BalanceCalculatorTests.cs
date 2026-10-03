using PoolSync.Chemistry;
using PoolSync.Configuration;
using Xunit;

namespace PoolSync.Tests;

public class BalanceCalculatorTests
{
    private const double Gallons25k = 25000 * PoolProfile.LitresPerGallon;

    private static readonly BalanceCalculator Calculator = new(new BalanceOptions());

    private static PoolProfile Pool(
        double? litres = Gallons25k,
        bool imperial = true,
        PoolSurface surface = PoolSurface.Plaster,
        bool swg = true,
        double? saltTarget = 3600,
        double? borTarget = 30) =>
        new(litres, imperial, surface, swg, 3600, 4500, saltTarget, true, null, null, borTarget, true, null, 0);

    private static Dictionary<string, SourcedReading> Water(
        double? fc = 5.5, double? ph = 7.7, double? ta = 70, double? cya = 75, double? ch = 400,
        double? salt = 3800, double? bor = 30, double? tempC = 28)
    {
        var water = new Dictionary<string, SourcedReading>(StringComparer.Ordinal);
        void Add(string key, double? value)
        {
            if (value is { } v)
            {
                water[key] = new SourcedReading(v, ReadingSource.PoolMath, null);
            }
        }

        Add(PoolMathFields.FreeChlorine, fc);
        Add(PoolMathFields.Ph, ph);
        Add(PoolMathFields.TotalAlkalinity, ta);
        Add(PoolMathFields.CyanuricAcid, cya);
        Add(PoolMathFields.CalciumHardness, ch);
        Add(PoolMathFields.Salt, salt);
        Add(PoolMathFields.Borate, bor);
        Add(WaterReadings.WaterTempC, tempC);
        return water;
    }

    [Fact]
    public void Balanced_water_needs_nothing_and_has_a_csi()
    {
        var balance = Calculator.Calculate(Water(), Pool());

        Assert.Empty(balance.Recommendations);
        Assert.NotNull(balance.Csi);
        Assert.All(balance.Targets, t => Assert.Equal("ok", t.Status));
    }

    [Fact]
    public void High_ph_gets_an_acid_dose_and_the_projected_csi_improves()
    {
        var balance = Calculator.Calculate(Water(ph: 8.29, ta: 105, ch: 200), Pool());

        var acid = Assert.Single(balance.Recommendations, r => r.Key == PoolMathFields.Ph);
        Assert.Equal("lower", acid.Action);
        Assert.Contains("muriatic acid", acid.Amount);
        Assert.Contains("fl oz", acid.Amount);
        Assert.True(balance.CsiAfter < balance.Csi);
    }

    [Fact]
    public void Salt_and_borate_dose_towards_the_targets_set_in_pool_math()
    {
        var balance = Calculator.Calculate(Water(salt: 2900, bor: 0), Pool());

        var salt = Assert.Single(balance.Recommendations, r => r.Key == PoolMathFields.Salt);
        Assert.Equal(3600, salt.Target);
        Assert.Equal("146 lb of pool salt", salt.Amount);

        var borate = Assert.Single(balance.Recommendations, r => r.Key == PoolMathFields.Borate);
        Assert.Equal(30, borate.Target);
        Assert.Equal("35.8 lb of boric acid", borate.Amount);
    }

    [Fact]
    public void Fc_target_follows_cya_and_is_lower_for_a_salt_cell()
    {
        var swg = Calculator.Calculate(Water(fc: 1, cya: 80), Pool(swg: true));
        var liquid = Calculator.Calculate(Water(fc: 1, cya: 80), Pool(swg: false));

        Assert.Equal(6, swg.Targets.Single(t => t.Key == PoolMathFields.FreeChlorine).Target);
        Assert.Equal(10, liquid.Targets.Single(t => t.Key == PoolMathFields.FreeChlorine).Target);
        Assert.Contains("liquid chlorine", swg.Recommendations.Single(r => r.Key == PoolMathFields.FreeChlorine).Amount);
    }

    [Fact]
    public void Too_much_cya_means_replacing_water()
    {
        var balance = Calculator.Calculate(Water(cya: 100), Pool());

        var cya = Assert.Single(balance.Recommendations, r => r.Key == PoolMathFields.CyanuricAcid);
        Assert.Equal("Replace about 25% of the water", cya.Amount);
    }

    [Fact]
    public void A_vinyl_pool_is_not_told_to_add_calcium()
    {
        var balance = Calculator.Calculate(Water(ch: 120), Pool(surface: PoolSurface.Vinyl));

        Assert.DoesNotContain(balance.Recommendations, r => r.Key == PoolMathFields.CalciumHardness);
    }

    [Fact]
    public void Without_a_temperature_there_is_no_csi_and_the_page_asks_for_one()
    {
        var balance = Calculator.Calculate(Water(tempC: null), Pool());

        Assert.Null(balance.Csi);
        Assert.Contains(balance.Notes, n => n.Contains("water temperature"));
    }

    [Fact]
    public void An_impossible_temperature_is_not_used()
    {
        var balance = Calculator.Calculate(Water(tempC: 81.92), Pool());

        Assert.Null(balance.Csi);
        Assert.Contains(balance.Notes, n => n.Contains("can't be right"));
    }

    [Fact]
    public void A_corrected_temperature_is_used_and_explained()
    {
        var water = Water();
        water[WaterReadings.WaterTempC] = new SourcedReading(27.7, ReadingSource.PoolMath, null, "saved in Pool Math as 81.92 °C; read as °F");

        var balance = Calculator.Calculate(water, Pool());

        Assert.NotNull(balance.Csi);
        Assert.Contains(balance.Notes, n => n.Contains("81.92 °C; read as °F"));
    }

    [Fact]
    public void Stabilizer_is_given_in_pounds_and_cups()
    {
        var balance = Calculator.Calculate(Water(cya: 22.1), Pool());

        var cya = Assert.Single(balance.Recommendations, r => r.Key == PoolMathFields.CyanuricAcid);
        Assert.Equal("11 lb (24.8 cups) of stabilizer (cyanuric acid)", cya.Amount);
    }

    [Fact]
    public void Without_a_volume_the_advice_has_no_amounts()
    {
        var balance = Calculator.Calculate(Water(salt: 2900), Pool(litres: null));

        Assert.Null(Assert.Single(balance.Recommendations).Amount);
        Assert.Contains(balance.Notes, n => n.Contains("no volume"));
    }

    [Fact]
    public void A_metric_pool_gets_metric_amounts()
    {
        var balance = Calculator.Calculate(Water(salt: 2900), Pool(litres: 50000, imperial: false));

        Assert.Equal("35 kg of pool salt", Assert.Single(balance.Recommendations).Amount);
    }

    [Fact]
    public void Missing_borate_asks_for_a_reading_when_the_pool_has_a_target()
    {
        var balance = Calculator.Calculate(Water(bor: null), Pool());

        Assert.Contains(balance.Notes, n => n.Contains("borate"));
        Assert.Equal("unknown", balance.Targets.Single(t => t.Key == PoolMathFields.Borate).Status);
    }

    [Fact]
    public void Small_doses_for_a_spa_keep_a_decimal()
    {
        Assert.Equal("0.3 fl oz", BalanceCalculator.Volume(9, imperial: true));
        Assert.Equal("0.8 oz", BalanceCalculator.Mass(22_680, imperial: true));
        Assert.Equal("0.03 oz", BalanceCalculator.Mass(850, imperial: true));
    }

    [Fact]
    public void A_spa_gets_spa_calcium_and_cya_targets()
    {
        var balance = Calculator.Calculate(Water(ch: 30, cya: 30), Pool(surface: PoolSurface.Spa, swg: true));

        var ch = balance.Targets.Single(t => t.Key == PoolMathFields.CalciumHardness);
        var cya = balance.Targets.Single(t => t.Key == PoolMathFields.CyanuricAcid);
        Assert.Equal((100, 250, 150), (ch.Min, ch.Max, ch.Target));
        Assert.Equal("ok", cya.Status);
    }
}
