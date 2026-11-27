using Romdex.Client;

namespace Romdex.Core.Library;

/// <summary>
/// When scheduled tasks run: each schedule's window, from its <see cref="ScheduleWindow.From"/> to its
/// <see cref="ScheduleWindow.Until"/> on the days it repeats, in the settings' time zone. A window that ends earlier in
/// the day than it starts runs past midnight, and belongs to the day it starts on; one that ends when it starts lasts a
/// day. A monthly one set for a day the month doesn't have is on its last day.
/// </summary>
public static class ScheduleWindows
{
    public static TimeZoneInfo Zone(string? id)
    {
        try
        {
            return id is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool Known(string? id)
    {
        try
        {
            return id is not null && TimeZoneInfo.FindSystemTimeZoneById(id) is not null;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The window of a schedule open at a moment, in UTC, or null when none is.</summary>
    public static (DateTime Start, DateTime End)? Current(ScheduleSettings settings, ScheduleRepeat repeat, DateTime utcNow)
    {
        var zone = Zone(settings.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        // Today's, or yesterday's if it runs past midnight.
        foreach (var day in new[] { today, today.AddDays(-1) })
            if (On(settings.Of(repeat), day, zone) is { } window && window.Start <= utcNow && utcNow < window.End)
                return window;
        return null;
    }

    /// <summary>When a schedule's next window opens, after a moment.</summary>
    public static DateTime? Next(ScheduleSettings settings, ScheduleRepeat repeat, DateTime utcNow)
    {
        var zone = Zone(settings.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        // A month and a bit covers the longest gap, between one month's last day and the next's.
        for (var i = 0; i <= 32; i++)
            if (On(settings.Of(repeat), today.AddDays(i), zone) is { } window && window.Start > utcNow)
                return window.Start;
        return null;
    }

    /// <summary>The window a task runs in that's open at a moment, if it's on a schedule and that's open.</summary>
    public static (DateTime Start, DateTime End)? For(ScheduleSettings settings, ScheduledTask task, DateTime utcNow) =>
        settings.WhenRuns(task) is { } repeat ? Current(settings, repeat, utcNow) : null;

    /// <summary>Whether a schedule's window opens on a day.</summary>
    public static bool RunsOn(ScheduleWindow window, DateOnly day) => window switch
    {
        WeeklySchedule weekly => day.DayOfWeek == weekly.Day,
        MonthlySchedule monthly => day.Day == Math.Min(Math.Clamp(monthly.Day, 1, 31), DateTime.DaysInMonth(day.Year, day.Month)),
        _ => true,
    };

    private static (DateTime Start, DateTime End)? On(ScheduleWindow window, DateOnly day, TimeZoneInfo zone)
    {
        if (!RunsOn(window, day))
            return null;
        var start = Utc(day.ToDateTime(window.From), zone);
        var endDay = window.Until > window.From ? day : day.AddDays(1);
        return (start, Utc(endDay.ToDateTime(window.Until), zone));
    }

    // A time the clocks skip, going forward, is taken as just after the jump.
    private static DateTime Utc(DateTime local, TimeZoneInfo zone)
    {
        for (var i = 0; zone.IsInvalidTime(local) && i < 4; i++)
            local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }
}
