using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romdex.Client;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Settings;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>Scheduled tasks, as Melodex's: the daily, weekly and monthly windows, and a window's run of what's ticked for it.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly MadeUpDats _dats = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public ScheduleTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Romdex:DataPath", _dats.Data)
            .UseSetting("Romdex:LibraryPath", _dats.Roms)
            .UseSetting("Romdex:DatPath", _dats.Dats)
            .UseSetting("Romdex:ScheduledTasks", "false"));
        _api = new RomdexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dats.Dispose();
    }

    [Fact]
    public async Task A_new_install_scans_and_updates_the_catalogue_daily_and_retries_weekly()
    {
        var status = await _api.GetSchedulesAsync();
        Assert.Equal([ScheduledTask.Scan, ScheduledTask.CatalogueUpdate], status.Settings.Daily.Tasks);
        Assert.Equal([ScheduledTask.RetryFailed], status.Settings.Weekly.Tasks);
        Assert.Empty(status.Settings.Monthly.Tasks);
        Assert.Equal((5, 5), (status.Settings.DownloadsAtATime, status.Settings.ArrivalCheckMinutes));
        Assert.NotNull(status.Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Next);
        Assert.Null(status.Windows.Single(w => w.Repeat == ScheduleRepeat.Monthly).Next);
        Assert.Null(status.Running);

        // A task is on one schedule at most, and each is kept in order.
        await _api.SaveSchedulesAsync(status.Settings with
        {
            Weekly = status.Settings.Weekly with { Tasks = [] },
            Monthly = new MonthlySchedule { Day = 31, From = new(22, 0), Until = new(4, 0), Tasks = [ScheduledTask.RetryFailed, ScheduledTask.RetryFailed] },
        });
        var saved = (await _api.GetSchedulesAsync()).Settings;
        Assert.Equal([ScheduledTask.RetryFailed], saved.Monthly.Tasks);
        var twice = await Assert.ThrowsAsync<RomdexApiException>(() => _api.SaveSchedulesAsync(saved with { Weekly = saved.Weekly with { Tasks = [ScheduledTask.RetryFailed] } }));
        Assert.Contains("one schedule at most", twice.Message);
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SaveSchedulesAsync(saved with { TimeZone = "Nowhere/Atlantis" }));
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SaveSchedulesAsync(saved with { ArrivalCheckMinutes = 0 }));
    }

    [Fact]
    public async Task A_window_runs_its_tasks_once_in_order()
    {
        _dats.Dump("Test - Handheld", "Hopper Quest (USA)", [".gb"]);
        _dats.WriteDats();
        var settings = (await _api.GetSchedulesAsync()).Settings;
        var now = DateTime.UtcNow;
        // A window open now, in UTC, scanning, updating the catalogue, then looking again for failed fetches.
        await _api.SaveSchedulesAsync(settings with
        {
            TimeZone = "UTC",
            Daily = new DailySchedule
            {
                From = TimeOnly.FromDateTime(now.AddHours(-1)), Until = TimeOnly.FromDateTime(now.AddHours(1)),
                Tasks = [ScheduledTask.RetryFailed, ScheduledTask.CatalogueUpdate, ScheduledTask.Scan],
            },
            Weekly = settings.Weekly with { Tasks = [] },
        });

        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ScheduledTasks>();
        await tasks.TickAsync(now, default);
        var running = (await _api.GetSchedulesAsync()).Running!;
        Assert.Equal(ScheduleRepeat.Daily, running.Repeat);
        Assert.Equal([ScheduledTask.Scan, ScheduledTask.CatalogueUpdate, ScheduledTask.RetryFailed], running.Steps.Select(s => s.Task));
        // The tasks are queued at once, and a quick one may have finished already.
        Assert.Contains(running.Steps[0].State, new[] { ScheduledStepState.Running, ScheduledStepState.Done });
        Assert.Contains(running.Steps[1].State, new[] { ScheduledStepState.Running, ScheduledStepState.Done });
        Assert.NotNull(running.Steps[0].TaskId);
        Assert.Equal(ScheduledStepState.Waiting, running.Steps[2].State);

        // Once the tasks have finished, the fetches go.
        ScheduleRunInfo? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (last is null && DateTime.UtcNow < deadline)
        {
            await tasks.TickAsync(now, default);
            var status = await _api.GetSchedulesAsync();
            last = status.Running is null ? status.Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Last : null;
            if (last is null)
                await Task.Delay(200);
        }
        Assert.NotNull(last?.FinishedAt);
        Assert.Equal(ScheduledStepState.Done, last.Steps[0].State);
        Assert.Equal(ScheduledStepState.Done, last.Steps[1].State);
        Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal((ScheduledStepState.Done, "No fetches had failed"), (last.Steps[2].State, last.Steps[2].Detail));

        // The same window doesn't run again.
        await tasks.TickAsync(now.AddMinutes(1), default);
        Assert.Null((await _api.GetSchedulesAsync()).Running);
    }

    [Fact]
    public async Task Failed_fetches_are_looked_for_again_a_few_at_a_time_until_the_window_closes()
    {
        _dats.Dump("Test - Handheld", "Hopper Quest (USA)", [".gb"]);
        _dats.WriteDats();
        await RunAsync(await _api.RefreshCatalogueAsync());
        var settings = (await _api.GetSchedulesAsync()).Settings;
        var now = DateTime.UtcNow;
        await _api.SaveSchedulesAsync(settings with
        {
            TimeZone = "UTC", DownloadsAtATime = 2,
            Daily = new DailySchedule { From = TimeOnly.FromDateTime(now.AddHours(-1)), Until = TimeOnly.FromDateTime(now.AddHours(1)), Tasks = [ScheduledTask.RetryFailed] },
            Weekly = settings.Weekly with { Tasks = [] },
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdexDbContext>();
        var title = await db.Titles.SingleAsync();
        // With no retriever set up, each one looked for again fails again at once.
        var failed = Enumerable.Range(0, 5).Select(i => new Request
        {
            TitleId = title.Id, State = RequestState.Failed, Message = "Nothing was offered.", UpdatedAt = now.AddMinutes(i),
        }).ToList();
        db.Requests.AddRange(failed);
        await db.SaveChangesAsync();
        var tasks = scope.ServiceProvider.GetRequiredService<ScheduledTasks>();

        // Two at a time, the longest failed first, each once.
        await tasks.TickAsync(now, default);
        var step = (await _api.GetSchedulesAsync()).Running!.Steps.Single();
        Assert.Equal((ScheduledStepState.Running, "Looked again for 2 fetches so far, 0 going"), (step.State, step.Detail));
        await tasks.TickAsync(now, default);
        await tasks.TickAsync(now, default);
        step = (await _api.GetSchedulesAsync()).Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Last!.Steps.Single();
        Assert.Equal((ScheduledStepState.Done, "Looked again for 5 fetches"), (step.State, step.Detail));
        Assert.Equal(5, await db.Requests.CountAsync(r => r.State == RequestState.Failed));
    }

    [Fact]
    public async Task What_hasnt_started_when_the_window_closes_is_skipped()
    {
        var settings = (await _api.GetSchedulesAsync()).Settings;
        var now = DateTime.UtcNow;
        await _api.SaveSchedulesAsync(settings with
        {
            TimeZone = "UTC",
            Daily = settings.Daily with { Tasks = [] },
            Weekly = new WeeklySchedule { Day = now.DayOfWeek, From = new(0, 0), Until = new(0, 0), Tasks = [ScheduledTask.RetryFailed] },
        });
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ScheduledTasks>();
        var store = scope.ServiceProvider.GetRequiredService<SettingsService>();
        // A run left going, its window since closed: the server was off, say.
        var opens = now.Date;
        await store.SaveScheduleRunsAsync(new ScheduleRuns
        {
            Current = new ScheduleRunInfo(ScheduleRepeat.Weekly, opens, opens.AddDays(1), opens, null, [new ScheduledStep(ScheduledTask.RetryFailed, ScheduledStepState.Waiting)]),
        }, default);

        await tasks.TickAsync(opens.AddDays(1).AddMinutes(1), default);
        var last = (await _api.GetSchedulesAsync()).Windows.Single(w => w.Repeat == ScheduleRepeat.Weekly).Last!;
        Assert.Equal(ScheduledStepState.Skipped, last.Steps.Single().State);
        Assert.NotNull(last.FinishedAt);
    }

    private async Task<TaskInfo> RunAsync(TaskInfo task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, $"{task.Kind}: {task.State} {task.Error} {task.Progress}");
        return task;
    }
}
