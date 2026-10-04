using System.Text.Json;
using PoolSync.Configuration;
using PoolSync.Storage;

namespace PoolSync.Controllers;

/// <summary>Something about the equipment worth knowing. Warnings are also sent as alerts.</summary>
public sealed record HealthIssue(string Key, string Severity, string Message)
{
    public const string Warning = "warning";
    public const string Info = "info";
}

/// <summary>A maintenance task's reminder state.</summary>
public sealed record Reminder(string Task, string Label, int Days, DateTimeOffset? LastDone, DateTimeOffset? DueAt, bool Overdue);

/// <summary>
/// The judgements made from controller samples and logs. Pure functions over plain data, so each
/// rule can be tested on its own.
/// </summary>
public static class EquipmentHealth
{
    /// <summary>
    /// Acid dosed since the last sample, from the controller's "dosed today" counter. The counter
    /// resets at midnight, so a drop means a new day and the new value is all dosed since.
    /// </summary>
    public static double? AcidDosed(double? previous, double current)
    {
        if (previous is not { } before)
        {
            return null;
        }

        var dosed = current >= before ? current - before : current;
        return dosed >= 0.05 ? dosed : null;
    }

    /// <summary>A rise in the tank level big enough to be a refill rather than rounding, in fl oz.</summary>
    public static double? AcidRefilled(double? previous, double current) =>
        previous is { } before && current - before >= 8 ? current - before : null;

    /// <summary>
    /// Filter pressure against what it reads when clean, at the same pump speed: pressure rises
    /// with speed, so only samples within 150 rpm of now are comparable. "Clean" is the low end of
    /// the readings since the filter was last backwashed or cleaned.
    /// </summary>
    public static (double? Baseline, HealthIssue? Issue) FilterPressure(
        IReadOnlyList<(double Psi, double Rpm)> sinceClean, double currentPsi, double currentRpm, double rise, string waterBody)
    {
        if (currentRpm < 500)
        {
            return (null, null);
        }

        var comparable = sinceClean
            .Where(s => Math.Abs(s.Rpm - currentRpm) <= 150 && s.Psi > 0)
            .Select(s => s.Psi)
            .Order()
            .ToList();

        if (comparable.Count < 12)
        {
            return (null, null);
        }

        var baseline = comparable[(int)(comparable.Count * 0.1)];
        var above = currentPsi - baseline;

        return above >= rise
            ? (baseline, new HealthIssue(
                $"{waterBody}:filter-psi",
                HealthIssue.Warning,
                $"{waterBody}: filter pressure is {currentPsi:0.#} psi, {above:0.#} above clean ({baseline:0.#} psi at " +
                $"{currentRpm:0} rpm). Backwash or clean the filter."))
            : (baseline, null);
    }

    /// <summary>
    /// A pump's power at a given speed follows the cube of the speed, so watts / rpm³ is constant
    /// while nothing changes. Drawing much more than usual means it's pushing against something (a
    /// full basket or filter, a closed valve); much less, that it's moving less water (air, lost prime).
    /// </summary>
    public static HealthIssue? PumpEfficiency(
        IReadOnlyList<(DateTimeOffset At, double Watts, double Rpm)> samples, DateTimeOffset now, string waterBody)
    {
        static double K((DateTimeOffset At, double Watts, double Rpm) s) => s.Watts / Math.Pow(s.Rpm / 1000, 3);

        var running = samples.Where(s => s.Rpm >= 800 && s.Watts > 20).ToList();
        var usual = running.Where(s => s.At < now.AddDays(-1)).Select(K).Order().ToList();
        var recent = running.Where(s => s.At >= now.AddHours(-2)).Select(K).Order().ToList();

        if (usual.Count < 48 || recent.Count < 4)
        {
            return null;
        }

        var change = recent[recent.Count / 2] / usual[usual.Count / 2] - 1;

        return change switch
        {
            >= 0.25 => new HealthIssue(
                $"{waterBody}:pump-high", HealthIssue.Warning,
                $"{waterBody}: the pump is drawing {change:P0} more power than usual for its speed. Check the pump " +
                "and skimmer baskets, the filter, and that no valve is closed."),
            <= -0.25 => new HealthIssue(
                $"{waterBody}:pump-low", HealthIssue.Warning,
                $"{waterBody}: the pump is drawing {-change:P0} less power than usual for its speed. It may be " +
                "moving less water: check for air in the pump basket or a lost prime."),
            _ => null,
        };
    }

    /// <summary>
    /// Hourly means from HA history. HA records a state only when it changes, so an hour with no
    /// change carries the value in force, which HA reports as the first point of the window.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Hour, double Value)> Hourly(
        IReadOnlyList<PoolSync.HomeAssistant.HaHistoryPoint> points, DateTimeOffset start, DateTimeOffset end)
    {
        var numeric = points.Where(p => p.Number is not null).OrderBy(p => p.At).ToList();
        var hours = new List<(DateTimeOffset, double)>();
        double? current = null;
        var index = 0;

        var hour = new DateTimeOffset(start.UtcDateTime.Year, start.UtcDateTime.Month, start.UtcDateTime.Day,
            start.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
        for (; hour < end; hour = hour.AddHours(1))
        {
            var inHour = new List<double>();
            while (index < numeric.Count && numeric[index].At < hour.AddHours(1))
            {
                if (numeric[index].At < hour)
                {
                    current = numeric[index].Number;
                }
                else
                {
                    inHour.Add(numeric[index].Number!.Value);
                    current = numeric[index].Number;
                }

                index++;
            }

            if (inHour.Count > 0)
            {
                hours.Add((hour, inHour.Average()));
            }
            else if (current is { } carried)
            {
                hours.Add((hour, carried));
            }
        }

        return hours;
    }

    /// <summary>Each task with a reminder: when it was last done and when it's next due.</summary>
    public static IReadOnlyList<Reminder> Reminders(
        IEnumerable<MaintenanceRecord> maintenance, PoolSettings settings, DateTimeOffset now)
    {
        var lastDone = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var entry in maintenance)
        {
            foreach (var task in Tasks(entry))
            {
                if (!lastDone.TryGetValue(task, out var at) || entry.At > at)
                {
                    lastDone[task] = entry.At;
                }
            }
        }

        var reminders = new List<Reminder>();
        foreach (var (task, days) in settings.ReminderDays)
        {
            if (days is not > 0 || !MaintenanceTasks.Labels.TryGetValue(task, out var label))
            {
                continue;
            }

            DateTimeOffset? last = lastDone.TryGetValue(task, out var done) ? done : null;
            DateTimeOffset? due = last?.AddDays(days.Value);
            reminders.Add(new Reminder(task, label, days.Value, last, due, due is { } d && d <= now));
        }

        return reminders.OrderBy(r => r.DueAt ?? DateTimeOffset.MaxValue).ToList();
    }

    /// <summary>The tasks a maintenance entry records as done.</summary>
    public static IEnumerable<string> Tasks(MaintenanceRecord entry)
    {
        using var document = JsonDocument.Parse(entry.Data);
        return document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.True && MaintenanceTasks.Labels.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();
    }

    /// <summary>Whether a sensor reading differs from a test by more than the pool's limit for it.</summary>
    public static double? DriftLimit(string role, PoolSettings settings) => role switch
    {
        SensorRole.Ph => settings.PhDriftLimit,
        SensorRole.Salt => settings.SaltDriftLimit,
        SensorRole.WaterTemp => settings.TempDriftLimitC,
        _ => null,
    };
}
