using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Core.Catalogue;
using Romdex.Core.Data;
using Romdex.Core.Downloads;
using Romdex.Core.Settings;
using Romdex.Core.Tasks;

namespace Romdex.Core.Library;

/// <summary>
/// Scheduled tasks, as Melodex's (#112): when a schedule's window opens, its ticked tasks run, in
/// <see cref="ScheduledTask"/>'s order. Those that are tasks are queued together, so the task queue runs them one after
/// another; once they've all finished, requests that ran out of offers are looked for again, keeping a few going and
/// asking for the next as each finishes, until all have been and finished or the window closes. Each window runs once,
/// and the schedules take turns: one whose window opens while another's run is going waits for it, if its window's
/// still open.
/// </summary>
public sealed class ScheduledTasks(
    RomdexDbContext db,
    SettingsService settings,
    TaskQueue queue,
    RequestService requests,
    ILogger<ScheduledTasks> logger)
{
    private const string Closed = "The window closed before it started";

    /// <summary>Those that are tasks on the Activity page, by their kind.</summary>
    private static string? KindOf(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => LibraryScanTask.TaskKind,
        ScheduledTask.CatalogueUpdate => CatalogueRefreshTask.TaskKind,
        _ => null,
    };

    public static bool IsTask(ScheduledTask task) => KindOf(task) is not null;

    /// <summary>
    /// Moves the run that's going along, or starts the first schedule whose window is open and hasn't run. Run every few
    /// seconds; nothing runs before the first user is made at setup.
    /// </summary>
    public async Task TickAsync(DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(ct))
            return;
        var runs = await settings.GetScheduleRunsAsync(ct);
        var run = runs.Current;
        var downloads = runs.Downloads;
        if (run is null)
        {
            downloads = new ScheduleDownloads();
            var schedule = await settings.GetScheduleAsync(ct);
            var unavailable = await UnavailableAsync(ct);
            foreach (var repeat in Enum.GetValues<ScheduleRepeat>())
            {
                var tasks = schedule.Of(repeat).Tasks.Distinct().Where(t => !unavailable.ContainsKey(t)).Order().ToList();
                if (tasks.Count == 0 || ScheduleWindows.Current(schedule, repeat, utcNow) is not { } open
                    || runs.Last.GetValueOrDefault(repeat)?.Opens == open.Start)
                    continue;
                run = new ScheduleRunInfo(repeat, open.Start, open.End, utcNow, null,
                    tasks.Select(t => new ScheduledStep(t, ScheduledStepState.Waiting)).ToList());
                logger.LogInformation("The {Repeat} scheduled tasks are starting: {Tasks}", repeat, string.Join(", ", tasks));
                break;
            }
            if (run is null)
                return;
        }

        (run, downloads) = await AdvanceAsync(run, downloads, utcNow, ct);
        var finished = run.FinishedAt is not null;
        if (finished)
            logger.LogInformation("The {Repeat} scheduled tasks are done: {Steps}", run.Repeat,
                string.Join("; ", run.Steps.Select(s => $"{s.Task} {s.State}" + (s.Detail is { } d ? $" ({d})" : ""))));
        var last = new Dictionary<ScheduleRepeat, ScheduleRunInfo>(runs.Last) { [run.Repeat] = run };
        await settings.SaveScheduleRunsAsync(new ScheduleRuns
        {
            Current = finished ? null : run, Last = last, Downloads = finished ? new ScheduleDownloads() : downloads,
        }, ct);
    }

    /// <summary>Tasks that make no sense as things are, with why. None of Romdex's, for now.</summary>
    public Task<Dictionary<ScheduledTask, string>> UnavailableAsync(CancellationToken ct) => Task.FromResult(new Dictionary<ScheduledTask, string>());

    // Each step as far as it can go now.
    private async Task<(ScheduleRunInfo, ScheduleDownloads)> AdvanceAsync(ScheduleRunInfo run, ScheduleDownloads downloads, DateTime utcNow, CancellationToken ct)
    {
        var steps = run.Steps.ToList();
        var open = utcNow < run.Closes;

        // The tasks, all queued at once: the queue runs one at a time, in the order they're asked for.
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] is { State: ScheduledStepState.Waiting } waiting && KindOf(waiting.Task) is { } kind)
                steps[i] = open ? await QueueAsync(waiting, kind, ct) : waiting with { State = ScheduledStepState.Skipped, Detail = Closed };
        var ids = steps.Select(s => s.TaskId).OfType<Guid>().ToList();
        var tasks = await db.BackgroundTasks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] is { State: ScheduledStepState.Running, TaskId: { } id })
                steps[i] = tasks.GetValueOrDefault(id) is { } task ? Finished(steps[i], task) : steps[i] with { State = ScheduledStepState.Skipped, Detail = "Its task is gone" };

        // The rest wait for the tasks, then go in turn.
        if (!steps.Any(s => IsTask(s.Task) && s.State == ScheduledStepState.Running))
            for (var i = 0; i < steps.Count; i++)
            {
                if (IsTask(steps[i].Task) || steps[i].State is not (ScheduledStepState.Waiting or ScheduledStepState.Running))
                    continue;
                if (steps[i].State == ScheduledStepState.Waiting && !open)
                {
                    steps[i] = steps[i] with { State = ScheduledStepState.Skipped, Detail = Closed };
                    continue;
                }
                (steps[i], downloads) = await AskAsync(steps[i], downloads, open, ct);
                if (steps[i].State == ScheduledStepState.Running)
                    break;
                // Each starts afresh.
                downloads = new ScheduleDownloads();
            }

        var done = steps.All(s => s.State is ScheduledStepState.Done or ScheduledStepState.Failed or ScheduledStepState.Skipped);
        return (run with { Steps = steps, FinishedAt = done ? utcNow : null }, downloads);
    }

    private async Task<ScheduledStep> QueueAsync(ScheduledStep step, string kind, CancellationToken ct)
    {
        var task = await queue.EnqueueAsync(kind, null, ct);
        return step with { State = ScheduledStepState.Running, TaskId = task.Id };
    }

    private static ScheduledStep Finished(ScheduledStep step, BackgroundTask task) => task.Status switch
    {
        BackgroundTaskStatus.Succeeded => step with { State = ScheduledStepState.Done, Detail = task.Progress },
        BackgroundTaskStatus.Failed => step with { State = ScheduledStepState.Failed, Detail = task.Error },
        BackgroundTaskStatus.Cancelled => step with { State = ScheduledStepState.Skipped, Detail = "Cancelled" },
        _ => step with { Detail = task.Progress },
    };

    private async Task<(ScheduledStep, ScheduleDownloads)> AskAsync(ScheduledStep step, ScheduleDownloads downloads, bool open, CancellationToken ct)
    {
        var atATime = Math.Clamp((await settings.GetScheduleAsync(ct)).DownloadsAtATime, 1, ScheduleSettings.MostDownloadsAtATime);
        return step.Task switch
        {
            ScheduledTask.RetryFailed => await RetryingAsync(step, downloads, atATime, open, ct),
            _ => (step with { State = ScheduledStepState.Skipped }, downloads),
        };
    }

    // Those asked for that haven't finished.
    private Task<int> GoingAsync(IReadOnlyList<Guid> asked, CancellationToken ct) =>
        db.Requests.CountAsync(r => asked.Contains(r.Id) && r.State == RequestState.Active, ct);

    private IQueryable<Request> Failed() => db.Requests.Where(r => r.State == RequestState.Failed);

    // Requests out of offers looked for again a few at a time, each once. A request looks for its next offer at once,
    // so none waits to start: when the window closes, what's going carries on and the rest wait for the next window.
    private async Task<(ScheduledStep, ScheduleDownloads)> RetryingAsync(ScheduledStep step, ScheduleDownloads downloads, int atATime, bool open,
        CancellationToken ct)
    {
        var asked = downloads.Asked.ToList();
        var tried = downloads.Tried.ToHashSet();
        if (!open)
            return (step with
            {
                State = ScheduledStepState.Done,
                Detail = $"Looked again for {LibraryMatcher.Plural(asked.Count, "fetch", "fetches")} before the window closed",
            }, new ScheduleDownloads());

        var untried = (await Failed().OrderBy(r => r.UpdatedAt).Select(r => r.Id).ToListAsync(ct)).Where(id => !tried.Contains(id.ToString())).ToList();
        var places = atATime - await GoingAsync(asked, ct);
        var taken = Math.Clamp(places, 0, untried.Count);
        foreach (var id in untried.Take(taken))
        {
            tried.Add(id.ToString());
            if (await requests.TryAgainAsync(id, ct))
                asked.Add(id);
        }
        if (taken > 0)
            logger.LogInformation("Looked again for {Count} failed fetches on a schedule", taken);
        return (await RollingAsync(step, asked, untried.Count - taken, "Looked again for", "fetch", "fetches", "No fetches had failed", ct),
            new ScheduleDownloads(asked, tried.ToList()));
    }

    // Going while there's more to ask for or what's been asked for hasn't finished; done once neither.
    private async Task<ScheduledStep> RollingAsync(ScheduledStep step, IReadOnlyList<Guid> asked, int left, string verb, string one, string many,
        string nothing, CancellationToken ct)
    {
        var going = await GoingAsync(asked, ct);
        if (left > 0 || going > 0)
            return step with { State = ScheduledStepState.Running, Detail = $"{verb} {LibraryMatcher.Plural(asked.Count, one, many)} so far, {going} going" };
        return step with { State = ScheduledStepState.Done, Detail = asked.Count == 0 ? nothing : $"{verb} {LibraryMatcher.Plural(asked.Count, one, many)}" };
    }

    /// <summary>Retry failed: looks again, all at once, for every request that ran out of offers. Returns how many.</summary>
    public async Task<int> RetryFailedAsync(CancellationToken ct)
    {
        var failed = await Failed().OrderBy(r => r.UpdatedAt).Select(r => r.Id).ToListAsync(ct);
        var again = 0;
        foreach (var id in failed)
            if (await requests.TryAgainAsync(id, ct))
                again++;
        if (again > 0)
            logger.LogInformation("Looked again for {Count} failed fetches", again);
        return again;
    }

    /// <summary>The schedules, when each next runs and how each last went.</summary>
    public async Task<ScheduleStatus> StatusAsync(CancellationToken ct)
    {
        var schedule = await settings.GetScheduleAsync(ct);
        var runs = await settings.GetScheduleRunsAsync(ct);
        var unavailable = await UnavailableAsync(ct);
        var now = DateTime.UtcNow;
        var windows = Enum.GetValues<ScheduleRepeat>().Select(r => new ScheduleWindowStatus(r,
            schedule.Of(r).Tasks.Any(t => !unavailable.ContainsKey(t)) ? ScheduleWindows.Next(schedule, r, now) : null, runs.Last.GetValueOrDefault(r))).ToList();
        return new ScheduleStatus(schedule, windows, runs.Current, unavailable);
    }
}

/// <summary>Runs scheduled tasks as their windows open, every few seconds. Off in tests, which move them along themselves.</summary>
public sealed class ScheduleRunner(IServiceScopeFactory scopes, IOptions<RomdexOptions> options, ILogger<ScheduleRunner> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScheduledTasks)
            return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ScheduledTasks>().TickAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't run scheduled tasks: {Message}", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
