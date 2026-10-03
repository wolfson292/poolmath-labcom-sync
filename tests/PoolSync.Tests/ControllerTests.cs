using System.Text.Json;
using PoolSync.Configuration;
using PoolSync.Controllers;
using PoolSync.HomeAssistant;
using PoolSync.Storage;
using Xunit;

namespace PoolSync.Tests;

public class ControllerSensorsTests
{
    private static HaState State(string id, string state, string? unit = null, string? attributes = null) => new()
    {
        EntityId = id,
        State = state,
        Attributes = JsonDocument.Parse(attributes ?? (unit is null ? "{}" : $$"""{"unit_of_measurement":"{{unit}}"}""")).RootElement,
    };

    // Entity ids as the real controllers report them.
    private static readonly HaState[] Allaire =
    [
        State("sensor.pool_antenna_ph_adjusted", "7.53", "pH"),
        State("sensor.pool_antenna_ph_estimate", "7.51", "pH"),
        State("sensor.pool_antenna_phmv_raw", "-22.9", "mV"),
        State("sensor.pool_antenna_ph_delta_10m", "-0.008", "pH"),
        State("sensor.pool_antenna_orpmv", "629.4", "mV"),
        State("sensor.pool_antenna_orpmv_raw", "629.5", "mV"),
        State("sensor.pool_antenna_pool_temperature", "82.5", "°F"),
        State("sensor.pool_antenna_pool_temperature_raw", "82.4", "°F"),
        State("sensor.pool_antenna_swg_water_temp", "88", "°F"),
        State("sensor.pool_antenna_swg_salt_ppm", "3450", "ppm"),
        State("sensor.pool_antenna_pump_psi", "6.59", "psi"),
        State("sensor.pool_antenna_pump_input_power", "214", "W"),
        State("sensor.pool_antenna_pump_speed", "2000", "rpm"),
        State("sensor.pool_antenna_swg_set_percent", "99", "%"),
        State("number.pool_antenna_acid_dosed_today", "12", "oz"),
        State("number.pool_antenna_acid_tank", "403.78", "oz"),
        State("binary_sensor.pool_antenna_acid_dose_fault", "on"),
        State("binary_sensor.pool_antenna_swg_low_salt", "off"),
        State("sensor.green_other_ph", "7.0", "pH"),
    ];

    [Fact]
    public void The_allaire_controller_resolves_to_the_preferred_entities()
    {
        var readings = ControllerSensors.Resolve(Allaire, "pool_antenna").ToDictionary(r => r.Role, r => r.Entity);

        Assert.Equal("sensor.pool_antenna_ph_adjusted", readings[SensorRole.Ph]);
        Assert.Equal("sensor.pool_antenna_orpmv", readings[SensorRole.Orp]);
        Assert.Equal("sensor.pool_antenna_pool_temperature", readings[SensorRole.WaterTemp]);
        Assert.Equal("sensor.pool_antenna_swg_salt_ppm", readings[SensorRole.Salt]);
        Assert.Equal("sensor.pool_antenna_pump_psi", readings[SensorRole.FilterPsi]);
        Assert.Equal("sensor.pool_antenna_pump_input_power", readings[SensorRole.PumpWatts]);
        Assert.Equal("sensor.pool_antenna_pump_speed", readings[SensorRole.PumpRpm]);
        Assert.Equal("number.pool_antenna_acid_dosed_today", readings[SensorRole.AcidDosedToday]);
        Assert.Equal("number.pool_antenna_acid_tank", readings[SensorRole.AcidTank]);
    }

    [Fact]
    public void Water_temperature_is_converted_to_celsius()
    {
        var temp = ControllerSensors.Resolve(Allaire, "pool_antenna").Single(r => r.Role == SensorRole.WaterTemp);

        Assert.Equal(28.06, temp.Value, precision: 2);
    }

    [Fact]
    public void Only_active_fault_flags_are_reported()
    {
        var fault = Assert.Single(ControllerSensors.Faults(Allaire, "pool_antenna"));

        Assert.Equal("binary_sensor.pool_antenna_acid_dose_fault", fault.Entity);
    }

    [Fact]
    public void A_spa_takes_its_temperature_from_the_heater()
    {
        HaState[] spa =
        [
            State("climate.spa_controller_heater", "heat", attributes: """{"current_temperature":102,"temperature":101}"""),
            State("sensor.pool_spa_controller_swg_salinity", "47.5", "%"),
        ];

        var readings = ControllerSensors.Resolve(spa, "spa_controller");

        var temp = Assert.Single(readings);
        Assert.Equal(SensorRole.WaterTemp, temp.Role);
        Assert.Equal(38.9, temp.Value, precision: 1);
    }

    [Fact]
    public void The_green_controller_prefers_salt_cell_output_over_its_setting()
    {
        HaState[] green =
        [
            State("sensor.pool_pool_controller_swg_output", "25", "%"),
            State("sensor.pool_controller_swg_set_percent", "25", "%"),
            State("sensor.pool_controller_swg_salt_ppm", "3850", "ppm"),
            State("sensor.pool_controller_swg_water_temp", "83", "°F"),
            State("sensor.pool_pool_controller_pump_rpm", "2338", "rpm"),
            State("sensor.pool_pool_controller_pump_rpm_sniffed", "2338", "rpm"),
            State("sensor.pool_pool_controller_pump_watts", "727", "W"),
        ];

        var readings = ControllerSensors.Resolve(green, "pool_controller").ToDictionary(r => r.Role, r => r.Entity);

        Assert.Equal("sensor.pool_pool_controller_swg_output", readings[SensorRole.SwgPercent]);
        Assert.Equal("sensor.pool_pool_controller_pump_rpm", readings[SensorRole.PumpRpm]);
        Assert.Equal("sensor.pool_controller_swg_water_temp", readings[SensorRole.WaterTemp]);
    }
}

public class EquipmentHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 12.0, null)]
    [InlineData(8.0, 12.0, 4.0)]
    [InlineData(12.0, 12.0, null)]
    [InlineData(12.0, 2.0, 2.0)]
    public void Acid_doses_come_from_the_daily_counter(double? previous, double current, double? expected)
    {
        Assert.Equal(expected, EquipmentHealth.AcidDosed(previous, current));
    }

    [Fact]
    public void A_tank_refill_is_a_rise_of_more_than_rounding()
    {
        Assert.Equal(400, EquipmentHealth.AcidRefilled(3.78, 403.78)!.Value, precision: 2);
        Assert.Null(EquipmentHealth.AcidRefilled(403.78, 403.0));
    }

    [Fact]
    public void Filter_pressure_well_above_clean_at_the_same_speed_is_flagged()
    {
        var clean = Enumerable.Range(0, 20).Select(i => (Psi: 6.5 + i * 0.05, Rpm: 2000.0))
            .Concat(Enumerable.Range(0, 20).Select(i => (Psi: 14.0, Rpm: 3000.0)))
            .ToList();

        var (baseline, issue) = EquipmentHealth.FilterPressure(clean, 15.0, 2000, 8, "Allaire");

        Assert.Equal(6.6, baseline!.Value, precision: 1);
        Assert.NotNull(issue);
        Assert.Contains("Backwash", issue.Message);
    }

    [Fact]
    public void Filter_pressure_needs_enough_samples_at_that_speed()
    {
        var few = Enumerable.Range(0, 5).Select(_ => (Psi: 6.5, Rpm: 2000.0)).ToList();

        Assert.Equal((null, null), EquipmentHealth.FilterPressure(few, 20, 2000, 8, "Allaire"));
    }

    [Fact]
    public void A_pump_drawing_much_more_power_for_its_speed_is_flagged()
    {
        var usual = Enumerable.Range(0, 60).Select(i => (Now.AddDays(-2).AddMinutes(i * 15), 214.0, 2000.0));
        var recent = Enumerable.Range(0, 6).Select(i => (Now.AddMinutes(-i * 15), 300.0, 2000.0));

        var issue = EquipmentHealth.PumpEfficiency(usual.Concat(recent).ToList(), Now, "Allaire");

        Assert.Equal("Allaire:pump-high", issue?.Key);
    }

    [Fact]
    public void Normal_pump_power_is_not_flagged()
    {
        var samples = Enumerable.Range(0, 200).Select(i => (Now.AddMinutes(-i * 15), 214.0, 2000.0)).ToList();

        Assert.Null(EquipmentHealth.PumpEfficiency(samples, Now, "Allaire"));
    }

    [Fact]
    public void Reminders_are_due_from_the_last_time_each_task_was_done()
    {
        MaintenanceRecord[] log =
        [
            new() { WaterBody = "Allaire", At = Now.AddDays(-10), Source = "manual", Data = """{"brushed":true}""" },
            new() { WaterBody = "Allaire", At = Now.AddDays(-3), Source = "manual", Data = """{"vacuumed":true,"pressure":9}""" },
        ];
        var settings = new PoolSettings();

        var reminders = EquipmentHealth.Reminders(log, settings, Now).ToDictionary(r => r.Task);

        Assert.True(reminders[MaintenanceTasks.Brushed].Overdue);
        Assert.False(reminders[MaintenanceTasks.Vacuumed].Overdue);
        Assert.Null(reminders[MaintenanceTasks.CleanedFilter].LastDone);
        Assert.False(reminders[MaintenanceTasks.CleanedFilter].Overdue);
    }

    [Fact]
    public void A_salt_pool_gets_a_cell_cleaning_reminder_and_others_do_not()
    {
        Assert.True(new PoolSettings { Swg = true }.WithCellReminder().ReminderDays.ContainsKey(MaintenanceTasks.CleanedCell));
        Assert.False(new PoolSettings { Swg = false }.WithCellReminder().ReminderDays.ContainsKey(MaintenanceTasks.CleanedCell));
    }
}
