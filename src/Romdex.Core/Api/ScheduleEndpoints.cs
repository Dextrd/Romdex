using Romdex.Client;
using Romdex.Core.Access;
using Romdex.Core.Settings;
using Romdex.Core.Library;

namespace Romdex.Core.Api;

public static class ScheduleEndpoints
{
    public static RouteGroupBuilder MapScheduleEndpoints(this RouteGroupBuilder api)
    {
        var schedules = api.MapGroup("/schedules").WithTags("Schedules");

        schedules.MapGet("", (ScheduledTasks service, CancellationToken ct) => service.StatusAsync(ct))
            .WithName("GetSchedules")
            .WithSummary("When scheduled tasks run: the daily, weekly and monthly windows with the tasks ticked for each, their time zone, how often "
                         + "the retrievers that hand over what arrives are asked, when each window next opens and how its last run went.")
            .Produces<ScheduleStatus>();

        schedules.MapPut("", async (ScheduleSettings value, SettingsService s, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (!ScheduleWindows.Known(value.TimeZone))
                    errors["timeZone"] = ["Must be a time zone this server knows, such as Europe/London."];
                if (!Enum.IsDefined(value.Weekly.Day))
                    errors["weekly.day"] = ["Must be a day of the week."];
                if (value.Monthly.Day is < 1 or > 31)
                    errors["monthly.day"] = ["Must be between 1 and 31."];
                if (value.ArrivalCheckMinutes is < 1 or > ScheduleSettings.MostArrivalCheckMinutes)
                    errors["arrivalCheckMinutes"] = [$"Must be between 1 and {ScheduleSettings.MostArrivalCheckMinutes}."];
                if (value.DownloadsAtATime is < 1 or > ScheduleSettings.MostDownloadsAtATime)
                    errors["downloadsAtATime"] = [$"Must be between 1 and {ScheduleSettings.MostDownloadsAtATime}."];
                var twice = Enum.GetValues<ScheduleRepeat>().SelectMany(r => value.Of(r).Tasks.Distinct()).GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (twice.Count > 0)
                    errors["tasks"] = [$"A task can be on one schedule at most: {string.Join(", ", twice)} is on more than one."];
                if (value.Daily.Tasks.Concat(value.Weekly.Tasks).Concat(value.Monthly.Tasks).Any(t => !Enum.IsDefined(t)))
                    errors["tasks"] = ["That isn't a task that can be scheduled."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                await s.SaveScheduleAsync(value with
                {
                    Daily = value.Daily with { Tasks = value.Daily.Tasks.Distinct().Order().ToList() },
                    Weekly = value.Weekly with { Tasks = value.Weekly.Tasks.Distinct().Order().ToList() },
                    Monthly = value.Monthly with { Tasks = value.Monthly.Tasks.Distinct().Order().ToList() },
                }, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Settings).WithName("SaveSchedules")
            .WithSummary("Set when scheduled tasks run: each of the daily, weekly and monthly windows' times (one ending earlier than it starts runs "
                         + "past midnight), the weekly one's day, the monthly one's day of the month (in a shorter month, its last day), the tasks "
                         + "each runs (a task on one at most), the time zone, how many fetches retries keep going, and how often the retrievers "
                         + "that hand over what arrives are asked. A window runs its tasks once, in order: scan, catalogue update, then failed "
                         + "fetches, keeping a few going until done or the window closes.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        api.MapPost("/requests/retry-failed", async (ScheduledTasks tasks, CancellationToken ct) => new AskedAgain(await tasks.RetryFailedAsync(ct)))
            .RequireScope(Scopes.Download).WithTags("Downloads").WithName("RetryFailedRequests")
            .WithSummary("Retry failed: looks again, all at once, for every request that ran out of offers, passing over what was turned away.");

        return api;
    }
}
