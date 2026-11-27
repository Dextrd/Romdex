using Romdex.Client;

namespace Romdex.Web;

/// <summary>
/// The schedules, read once and kept for the pages that say when their tasks run, and how tasks and schedules are put
/// in words. Saving them on their tab keeps the copy here up to date.
/// </summary>
public sealed class Schedules(RomdexClient api)
{
    private Task<ScheduleSettings?>? _loading;

    /// <summary>The schedules, or null if they couldn't be read.</summary>
    public Task<ScheduleSettings?> GetAsync() => _loading ??= LoadAsync();

    public void Saved(ScheduleSettings settings) => _loading = Task.FromResult<ScheduleSettings?>(settings);

    private async Task<ScheduleSettings?> LoadAsync()
    {
        try
        {
            return (await api.GetSchedulesAsync()).Settings;
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
            _loading = null;
            return null;
        }
    }

    public static string Name(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => "Scan the library",
        ScheduledTask.CatalogueUpdate => "Update the catalogue",
        _ => "Retry failed fetches",
    };

    public static string Hint(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => "Hashes new and changed files in the library and matches them against the DATs.",
        ScheduledTask.CatalogueUpdate => "Reads the DATs again, and what the catalogue plugins know, such as a new No-Intro or Redump release.",
        _ => "Looks again for what you asked for that ran out of offers, a few at a time, passing over what was turned away. Any not looked for when the window closes wait for the next.",
    };

    /// <summary>Where it can be run now, by hand. (Retry failed moves to Activity → Fetches when that's brought over from Melodex.)</summary>
    public static (string Where, string Href) RunNow(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan or ScheduledTask.CatalogueUpdate => ("the Scan menu", ""),
        _ => ("Review → Couldn't fetch", "review/couldnt-fetch"),
    };

    public static string Name(ScheduleRepeat repeat) => repeat switch
    {
        ScheduleRepeat.Daily => "Daily",
        ScheduleRepeat.Weekly => "Weekly",
        _ => "Monthly",
    };

    /// <summary>When a schedule's window comes round, as "every day, 01:00–06:00".</summary>
    public static string When(ScheduleSettings settings, ScheduleRepeat repeat)
    {
        var window = settings.Of(repeat);
        var times = $"{window.From:HH:mm}–{window.Until:HH:mm}{(window.Until <= window.From ? " the next day" : "")}";
        return window switch
        {
            WeeklySchedule weekly => $"every {weekly.Day}, {times}",
            MonthlySchedule monthly => $"on the {Ordinal(monthly.Day)} of each month{(monthly.Day > 28 ? " (or its last day)" : "")}, {times}",
            _ => $"every day, {times}",
        };
    }

    public static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
