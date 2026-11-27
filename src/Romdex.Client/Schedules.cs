namespace Romdex.Client;

/// <summary>Something that can run on a schedule. A window runs those ticked for it in this order.</summary>
public enum ScheduledTask
{
    /// <summary>Scan the library folder and match new and changed files.</summary>
    Scan,

    /// <summary>Read the DATs again, and what the catalogue plugins know.</summary>
    CatalogueUpdate,

    /// <summary>Look again for requests that ran out of offers, a few at a time, until all have been or the window closes.</summary>
    RetryFailed,

    /// <summary>Ask for what's on everyone's wishlists, a few at a time, until all have been or the window closes.</summary>
    Wishlist,
}

/// <summary>How often a schedule's window comes round.</summary>
public enum ScheduleRepeat { Daily, Weekly, Monthly }

/// <summary>A window for scheduled tasks to run in, and the tasks ticked for it.</summary>
public abstract record ScheduleWindow
{
    /// <summary>When the window opens.</summary>
    public TimeOnly From { get; init; } = new(1, 0);

    /// <summary>When it closes: the next day when it's earlier than <see cref="From"/>, as 22:00 to 04:00 is; a whole day when it's the same.</summary>
    public TimeOnly Until { get; init; } = new(6, 0);

    /// <summary>The tasks run in it, in <see cref="ScheduledTask"/>'s order. A task is in one schedule at most.</summary>
    public IReadOnlyList<ScheduledTask> Tasks { get; init; } = [];
}


/// <summary>Every day.</summary>
public sealed record DailySchedule : ScheduleWindow;

/// <summary>One day a week.</summary>
public sealed record WeeklySchedule : ScheduleWindow
{
    public DayOfWeek Day { get; init; } = DayOfWeek.Monday;
}

/// <summary>One day a month.</summary>
public sealed record MonthlySchedule : ScheduleWindow
{
    /// <summary>From 1 to 31. In a month shorter than that, it's the month's last day.</summary>
    public int Day { get; init; } = 1;
}

/// <summary>
/// When scheduled tasks run: a daily, a weekly and a monthly window, each with its own times and the tasks ticked for
/// it, all in one time zone; and how often the retrievers that hand over what arrives are asked.
/// </summary>
public sealed record ScheduleSettings
{
    /// <summary>The time zone the days and times are in, as a browser names it (IANA); UTC if it's unknown.</summary>
    public string TimeZone { get; init; } = "UTC";

    public DailySchedule Daily { get; init; } = new()
    {
        Tasks = [ScheduledTask.Scan, ScheduledTask.CatalogueUpdate],
    };

    public WeeklySchedule Weekly { get; init; } = new()
    {
        Tasks = [ScheduledTask.RetryFailed],
    };

    public MonthlySchedule Monthly { get; init; } = new();

    /// <summary>
    /// How often, in minutes, the retrievers that hand over what arrives by themselves, such as the inbox folder, are
    /// asked: 1 to 1440. All day, rather than in a window, as Melodex's playlist sync is.
    /// </summary>
    public int ArrivalCheckMinutes { get; init; } = 5;

    /// <summary>The most <see cref="ArrivalCheckMinutes"/> may be: a day.</summary>
    public const int MostArrivalCheckMinutes = 1440;

    /// <summary>
    /// How many fetches a window's retries keep going at once, from 1 to 20: as each finishes, the next is asked for in
    /// its place.
    /// </summary>
    public int DownloadsAtATime { get; init; } = 5;

    /// <summary>The most <see cref="DownloadsAtATime"/> may be.</summary>
    public const int MostDownloadsAtATime = 20;

    /// <summary>The schedule a repeat names.</summary>
    public ScheduleWindow Of(ScheduleRepeat repeat) => repeat switch
    {
        ScheduleRepeat.Daily => Daily,
        ScheduleRepeat.Weekly => Weekly,
        _ => Monthly,
    };

    /// <summary>The schedule a task is ticked in, if it's in one.</summary>
    public ScheduleRepeat? WhenRuns(ScheduledTask task) =>
        Enum.GetValues<ScheduleRepeat>().Cast<ScheduleRepeat?>().FirstOrDefault(r => Of(r!.Value).Tasks.Contains(task));
}

/// <summary>Where a step of a scheduled run stands.</summary>
public enum ScheduledStepState { Waiting, Running, Done, Failed, Skipped }

/// <summary>One task in a scheduled run.</summary>
/// <param name="Detail">What it did, or why it failed or was skipped.</param>
/// <param name="TaskId">Its task on the Activity page, for those that are tasks; the rest ask for fetches.</param>
public sealed record ScheduledStep(ScheduledTask Task, ScheduledStepState State, string? Detail = null, Guid? TaskId = null);

/// <summary>A window's run of its tasks.</summary>
/// <param name="Opens">When its window opened.</param>
/// <param name="Closes">When it closes.</param>
/// <param name="FinishedAt">When its last task finished; null while it's going.</param>
public sealed record ScheduleRunInfo(ScheduleRepeat Repeat, DateTime Opens, DateTime Closes, DateTime StartedAt, DateTime? FinishedAt,
    IReadOnlyList<ScheduledStep> Steps);

/// <summary>A schedule and where it stands.</summary>
/// <param name="Next">When its window next opens, while it has tasks.</param>
/// <param name="Last">Its last run, or the one going.</param>
public sealed record ScheduleWindowStatus(ScheduleRepeat Repeat, DateTime? Next, ScheduleRunInfo? Last);

/// <summary>The schedules, when each next runs and how each last went.</summary>
/// <param name="Running">The run going now, if one is: windows take turns, so one runs at a time.</param>
/// <param name="Unavailable">
/// Tasks that make no sense as things are, with why. They can't be ticked, and one ticked already is left out of runs
/// until it makes sense again. None of Romdex's are, for now.
/// </param>
public sealed record ScheduleStatus(ScheduleSettings Settings, IReadOnlyList<ScheduleWindowStatus> Windows, ScheduleRunInfo? Running,
    IReadOnlyDictionary<ScheduledTask, string> Unavailable);

/// <summary>What <c>POST /requests/retry-failed</c> asked for.</summary>
/// <param name="Asked">How many requests were queued, again or new.</param>
public sealed record AskedAgain(int Asked);
