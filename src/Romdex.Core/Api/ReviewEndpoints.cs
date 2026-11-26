using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Access;
using Romdex.Core.Data;
using Romdex.Core.Downloads;
using Romdex.Core.Library;

namespace Romdex.Core.Api;

/// <summary>
/// Review: what the DATs don't explain, a list for each of its tabs, and the decisions made there. Arrivals that wait
/// are listed under the tab their reason belongs to; they're accepted, rejected and retried under /downloads.
/// </summary>
public static class ReviewEndpoints
{
    public static RouteGroupBuilder MapReviewEndpoints(this RouteGroupBuilder api)
    {
        var review = api.MapGroup("/review").WithTags("Review");

        review.MapGet("", (ReviewService service, CancellationToken ct) => service.SummarizeAsync(ct))
            .WithName("GetReviewSummary").WithSummary("How much waits under each of Review's tabs, and how many files wait for a tool to read them.");

        review.MapGet("/bad-dumps", (ReviewService service, int? skip, int? take, string? sort, bool? descending, CancellationToken ct) => service.BadDumpsAsync(skip ?? 0, take ?? 100, ct, sort, descending ?? false))
            .WithName("ListBadDumps").WithSummary("Files, and files inside archives, named as a dump's file is but with other hashes: likely bad or modified copies of it.");

        review.MapGet("/matching", (ReviewService service, int? skip, int? take, string? sort, bool? descending, CancellationToken ct) => service.MatchingAsync(skip ?? 0, take ?? 100, ct, sort, descending ?? false))
            .WithName("ListUnmatched").WithSummary("Files, and files inside archives, that match nothing and are named as nothing is, with arrivals that match nothing.");

        review.MapGet("/outdated", (ReviewService service, int? skip, int? take, string? sort, bool? descending, CancellationToken ct) => service.OutdatedAsync(skip ?? 0, take ?? 100, ct, sort, descending ?? false))
            .WithName("ListOutdated").WithSummary("Files that matched a dump until a new DAT changed it, with the match they had.");

        review.MapGet("/corrupted", (ReviewService service, int? skip, int? take, string? sort, bool? descending, CancellationToken ct) => service.CorruptedAsync(skip ?? 0, take ?? 100, ct, sort, descending ?? false))
            .WithName("ListCorrupted").WithSummary("Files that couldn't be read, other than for want of a tool, with the dump each one's name suggests, and arrivals none of whose files could be.");

        review.MapGet("/incomplete", (ReviewService service, int? skip, int? take, string? sort, bool? descending, CancellationToken ct) => service.IncompletePageAsync(skip ?? 0, take ?? 100, ct, sort, descending ?? false))
            .WithName("ListIncomplete").WithSummary("Dumps the library has some files of but not all, with which are missing, and incomplete arrivals.");

        review.MapGet("/couldnt-fetch", (ReviewService service, int? skip, int? take, string? sort, bool? descending, CancellationToken ct) => service.CouldntFetchAsync(skip ?? 0, take ?? 50, ct, sort, descending ?? sort != "name"))
            .WithName("ListCouldntFetch").WithSummary("Arrivals that aren't what was asked for, and fetches that failed, newest first.");

        review.MapGet("/set-aside", (ReviewService service, bool? hidden, int? skip, int? take, CancellationToken ct) =>
                service.SetAsideAsync(hidden ?? false, skip ?? 0, take ?? 100, ct))
            .WithName("ListSetAside").WithSummary("Files someone deferred, or with hidden=true, hid, newest first.");

        review.MapPost("/files/{id:guid}/defer", (Guid id, ReviewService service, CancellationToken ct) => DoAsync(() => service.DeferAsync(id, ct)))
            .RequireScope(Scopes.Review).WithName("DeferFile")
            .WithSummary("Takes a file off Review for now. It's still matched each scan, so a DAT that adds it later finds it.");

        review.MapPost("/files/{id:guid}/hide", (Guid id, ReviewService service, CancellationToken ct) => DoAsync(() => service.HideAsync(id, ct)))
            .RequireScope(Scopes.Review).WithName("HideFile")
            .WithSummary("Hides a file: it stays in the library folder, but it's never matched or listed again until it's put back.");

        review.MapPost("/files/{id:guid}/put-back", (Guid id, ReviewService service, CancellationToken ct) => DoAsync(() => service.PutBackAsync(id, ct)))
            .RequireScope(Scopes.Review).WithName("PutBackFile").WithSummary("Puts a deferred or hidden file back: it's matched again, and listed if it still matches nothing.");

        review.MapPost("/files/{id:guid}/delete", async (Guid id, ReviewService service, RomdexDbContext db, CancellationToken ct) =>
                await DoAsync(async () => (await DownloadEndpoints.InfoAsync(db, [await service.DeleteFileAsync(id, ct)], ct))[0]))
            .RequireScope(Scopes.Review).WithName("DeleteFile")
            .WithSummary("Deletes a file that matches nothing, or can't be read, from the library. It's left on Downloads as rejected.");

        review.MapGet("/links", (ReviewService service, Guid? titleId, CancellationToken ct) => service.LinksAsync(titleId, ct))
            .WithName("ListLinks").WithSummary("Links people made between files and the catalogue: all of them, or a title's.");

        review.MapPost("/links", (LinkFile body, ReviewService service, CancellationToken ct) => DoAsync(() => service.LinkAsync(body, ct)))
            .RequireScope(Scopes.Review).WithName("LinkFile")
            .WithSummary("Says what a file is: a dump, which it then counts as while the DATs match its bytes to nothing, or a version of a game no DAT lists.");

        review.MapDelete("/links/{id:guid}", (Guid id, ReviewService service, CancellationToken ct) => DoAsync(() => service.UnlinkAsync(id, ct)))
            .RequireScope(Scopes.Review).WithName("UnlinkFile").WithSummary("Takes a link away: its file is back under Review if the DATs match it to nothing.");

        review.MapPost("/dumps/{id:guid}/keep", (Guid id, ReviewService service, CancellationToken ct) => DoAsync(() => service.KeepIncompleteAsync(id, ct)))
            .RequireScope(Scopes.Review).WithName("KeepIncomplete")
            .WithSummary("Keeps an incomplete dump as it is: it counts as whole while only the files it's missing now are.");

        review.MapDelete("/dumps/{id:guid}/keep", (Guid id, ReviewService service, CancellationToken ct) => DoAsync(() => service.UnkeepIncompleteAsync(id, ct)))
            .RequireScope(Scopes.Review).WithName("UnkeepIncomplete").WithSummary("Stops keeping a dump as it is: it's incomplete again.");

        review.MapPost("/dumps/{id:guid}/delete", async (Guid id, ReviewService service, RomdexDbContext db, CancellationToken ct) =>
                await DoAsync(async () => await DownloadEndpoints.InfoAsync(db, await service.DeleteIncompleteAsync(id, ct), ct)))
            .RequireScope(Scopes.Review).WithName("DeleteIncomplete")
            .WithSummary("Deletes what the library has of an incomplete dump, if those files hold nothing else.");

        return api;
    }

    private static async Task<IResult> DoAsync(Func<Task> action) =>
        await DoAsync(async () => { await action(); return (object?)null; });

    private static async Task<IResult> DoAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            return result is null ? Results.NoContent() : Results.Ok(result);
        }
        catch (ReviewException e) when (e.NotFound)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ReviewException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception e) when (e is FilingWorkerException or HttpRequestException)
        {
            return Results.Problem($"The igir worker couldn't take it out of the library: {e.Message}", statusCode: StatusCodes.Status409Conflict);
        }
    }
}
