using PoolSync.Chemistry;
using PoolSync.Configuration;
using Xunit;

namespace PoolSync.Tests;

public class ChemicalEffectsTests
{
    private const double Litres10k = 10000 * PoolProfile.LitresPerGallon;

    private static Dictionary<string, SourcedReading> Water() => new()
    {
        [PoolMathFields.FreeChlorine] = new(3, "x", null),
        [PoolMathFields.Ph] = new(7.8, "x", null),
        [PoolMathFields.TotalAlkalinity] = new(80, "x", null),
        [PoolMathFields.CyanuricAcid] = new(40, "x", null),
        [PoolMathFields.CalciumHardness] = new(300, "x", null),
        [WaterReadings.WaterTempC] = new(28, "x", null),
    };

    private static Effect Effect(EffectsResult result, string key) => result.Effects.Single(e => e.Key == key);

    [Fact]
    public void A_gallon_of_ten_percent_chlorine_adds_about_ten_ppm_to_ten_thousand_gallons()
    {
        var chlorine = Chemicals.Find(Chemicals.LiquidChlorine)!;
        var result = Chemicals.Effects(chlorine, Chemicals.Normalise(chlorine, 1, "gal")!.Value, 10, Water(), Litres10k);

        Assert.Equal(13, Effect(result, PoolMathFields.FreeChlorine).To, precision: 0);
    }

    [Fact]
    public void Acid_lowers_ph_and_ta_and_the_csi()
    {
        var acid = Chemicals.Find(Chemicals.MuriaticAcid)!;
        var result = Chemicals.Effects(acid, Chemicals.Normalise(acid, 16, "fl oz")!.Value, 31.45, Water(), Litres10k);

        Assert.True(Effect(result, PoolMathFields.Ph).To < 7.8);
        Assert.True(Effect(result, PoolMathFields.TotalAlkalinity).To < 80);
        Assert.True(result.CsiAfter < result.CsiBefore);
    }

    [Fact]
    public void A_pound_and_a_half_of_baking_soda_raises_ta_about_ten_ppm_in_ten_thousand_gallons()
    {
        var soda = Chemicals.Find(Chemicals.BakingSoda)!;
        var result = Chemicals.Effects(soda, Chemicals.Normalise(soda, 1.5, "lb")!.Value, null, Water(), Litres10k);

        Assert.Equal(90.7, Effect(result, PoolMathFields.TotalAlkalinity).To, precision: 0);
    }

    [Fact]
    public void Stabilizer_by_the_cup_raises_cya()
    {
        var stabilizer = Chemicals.Find(Chemicals.Stabilizer)!;
        var grams = Chemicals.Normalise(stabilizer, 2.25, "cup")!.Value;

        Assert.Equal(453.592, grams, precision: 2);
        var result = Chemicals.Effects(stabilizer, grams, null, Water(), Litres10k);
        Assert.Equal(52, Effect(result, PoolMathFields.CyanuricAcid).To, precision: 0);
    }

    [Fact]
    public void Without_a_temperature_the_ph_effect_is_explained_not_guessed()
    {
        var water = Water();
        water.Remove(WaterReadings.WaterTempC);
        var acid = Chemicals.Find(Chemicals.MuriaticAcid)!;

        var result = Chemicals.Effects(acid, 500, 31.45, water, Litres10k);

        Assert.DoesNotContain(result.Effects, e => e.Key == PoolMathFields.Ph);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void A_unit_that_does_not_suit_the_product_is_refused()
    {
        Assert.Null(Chemicals.Normalise(Chemicals.Find(Chemicals.MuriaticAcid)!, 1, "lb"));
        Assert.Null(Chemicals.Normalise(Chemicals.Find(Chemicals.Salt)!, 1, "cup"));
    }
}
