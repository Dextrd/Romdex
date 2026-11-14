using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Access;
using Romdex.Core.Data;
using DownloadState = Romdex.Core.Data.DownloadState;
using Romdex.Core.Downloads;
using Romdex.Core.Plugins;

namespace Romdex.Core.Api;

/// <summary>Downloads: what's arriving, finding a game through the plugins, and how each system files what arrives.</summary>
public static class DownloadEndpoints
{
    public static RouteGroupBuilder MapDownloadEndpoints(this RouteGroupBuilder api)
    {
        var downloads = api.MapGroup("/downloads").WithTags("Downloads");

        downloads.MapGet("", async (RomdexDbContext db, Client.DownloadState? state, int? skip, int? take, CancellationToken ct) =>
            {
                var items = db.Downloads.AsNoTracking();
                if (state is { } wanted)
                {
                    var stored = (DownloadState)(int)wanted;
                    items = items.Where(i => i.State == stored);
                }
                var total = await items.CountAsync(ct);
                var page = await items.OrderByDescending(i => i.CreatedAt).Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, 500))
                    .Include(i => i.Dumps).ToListAsync(ct);
                var waiting = await db.Downloads.CountAsync(i => i.State == DownloadState.NeedsReview, ct);
                return new DownloadsPage(await InfoAsync(db, page, ct), total, waiting);
            })
            .WithName("ListDownloads").WithSummary("What's arrived through the retrievers, newest first, with where each is.");

        downloads.MapGet("/{id:guid}", async (Guid id, RomdexDbContext db, CancellationToken ct) =>
                await db.Downloads.AsNoTracking().Include(i => i.Dumps).FirstOrDefaultAsync(i => i.Id == id, ct) is { } item
                    ? Results.Ok((await InfoAsync(db, [item], ct))[0])
                    : Results.NotFound())
            .WithName("GetDownload").WithSummary("One arrival, with its files' hashes and what they matched.");

        downloads.MapPost("/{id:guid}/accept", (Guid id, RomdexDbContext db, DownloadService service, CancellationToken ct) =>
                ActAsync(id, db, service.AcceptAsync, "Only an item held for review with whole dumps in it can be accepted.", ct))
            .RequireScope(Scopes.Download).WithName("AcceptDownload")
            .WithSummary("Files an item held for review, such as an alternative the system holds. Something that matched nothing can't be.");

        downloads.MapPost("/{id:guid}/reject", (Guid id, RomdexDbContext db, DownloadService service, CancellationToken ct) =>
                ActAsync(id, db, service.RejectAsync, "It's already filed, discarded or rejected, or is being worked on.", ct))
            .RequireScope(Scopes.Download).WithName("RejectDownload").WithSummary("Throws an arrival's staged files away.");

        downloads.MapPost("/{id:guid}/retry", (Guid id, RomdexDbContext db, DownloadService service, CancellationToken ct) =>
                ActAsync(id, db, service.RetryAsync, "Only an item held for review or that failed can be retried, and one whose files are gone needs its plugin.", ct))
            .RequireScope(Scopes.Download).WithName("RetryDownload")
            .WithSummary("Identifies an arrival again, such as after a catalogue update, or fetches it again if its files are gone.");

        downloads.MapPost("/check", (DownloadSignal signal) =>
            {
                signal.CheckNow();
                return Results.Accepted();
            })
            .RequireScope(Scopes.Download).WithName("CheckDownloads").WithSummary("Asks the plugins that hand over what arrived by themselves for anything new, now.");

        downloads.MapPost("/fetch", async (StartDownload body, RomdexDbContext db, DownloadService service, RetrievalTargets targets, CancellationToken ct) =>
            {
                if (await db.PluginInstances.FindAsync([body.PluginId], ct) is not { Type: Contracts.Plugins.PluginType.Retrieval, Endpoint: not null, Enabled: true } instance)
                    return Problem("pluginId", "That isn't a retriever that's set up and enabled.");
                if (!RetrievalPlugins.CapabilitiesOf(instance).Contains(RetrievalCapabilities.Search))
                    return Problem("pluginId", $"{instance.Name} doesn't search, so it can't fetch for a title.");
                if (await targets.ForAsync(body.TitleId, body.DumpId, ct) is not { } target)
                    return Problem("titleId", "There's no such title.");
                var item = await service.FetchAsync(instance, body.Candidate, target, body.TitleId, body.DumpId, ct);
                return Results.Ok((await InfoAsync(db, [item], ct))[0]);
            })
            .RequireScope(Scopes.Download).WithName("StartDownload")
            .WithSummary("Has a plugin stage a candidate it offered for a title; it's then identified and filed like anything else that arrives.");

        api.MapPost("/library/files/{id:guid}/download", async (Guid id, RomdexDbContext db, DownloadService service, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) is not { } file)
                    return Results.NotFound();
                if (await db.LibraryContents.AnyAsync(c => c.LibraryFileId == id && c.Matches.Any(), ct))
                    return Results.Problem("It matches the catalogue, so it's where it belongs. Only files that match nothing or can't be read go back.", statusCode: StatusCodes.Status409Conflict);
                try
                {
                    var item = await service.ReclaimAsync(file, ct);
                    return Results.Ok((await InfoAsync(db, [item], ct))[0]);
                }
                catch (Exception e) when (e is FilingWorkerException or HttpRequestException)
                {
                    return Results.Problem($"The igir worker couldn't move it: {e.Message}", statusCode: StatusCodes.Status409Conflict);
                }
            })
            .RequireScope(Scopes.Review).WithTags("Downloads").WithName("ReclaimLibraryFile")
            .WithSummary("Takes a file that matches nothing, or can't be read, out of the library and through Downloads again.");

        api.MapPost("/titles/{id:guid}/search", async (Guid id, SearchDownloads? body, RetrievalPlugins plugins, RetrievalTargets targets, CancellationToken ct) =>
            {
                if (await targets.ForAsync(id, body?.DumpId, ct) is not { } target)
                    return Results.NotFound();
                var searching = await plugins.WithAsync(RetrievalCapabilities.Search, ct);
                var answers = await Task.WhenAll(searching.Select(async p =>
                {
                    try
                    {
                        return (p.Instance, Candidates: await p.Plugin.SearchAsync(target, 10, ct), Error: (string?)null);
                    }
                    catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        return (p.Instance, Candidates: (IReadOnlyList<Candidate>)[], Error: e.Message);
                    }
                }));
                var offers = answers.SelectMany(a => a.Candidates.Select(c => new DownloadOffer(a.Instance.Id, a.Instance.Name, c))).ToList();
                var problems = answers.Where(a => a.Error is not null).Select(a => $"{a.Instance.Name}: {a.Error}").ToList();
                if (searching.Count == 0)
                    problems.Add("No retriever that searches is set up. Add one under Settings → Plugins.");
                return Results.Ok(new DownloadSearchResult(offers, problems));
            })
            .RequireScope(Scopes.Download).WithTags("Downloads").WithName("SearchDownloads")
            .WithSummary("Asks every retriever that searches for dumps of a title. Fetch one with POST /downloads/fetch.");

        api.MapGet("/systems/{id:guid}/filing", async (Guid id, RomdexDbContext db, Filing filing, CancellationToken ct) =>
                await db.Systems.FindAsync([id], ct) is { } system ? Results.Ok(await filing.GetAsync(system.Name, ct)) : Results.NotFound())
            .WithTags("Downloads").WithName("GetFiling").WithSummary("How a system's arrivals are filed: folder layout, storage format and alternatives.");

        api.MapGet("/systems/{id:guid}/filing/formats", async (Guid id, RomdexDbContext db, Filing filing, CancellationToken ct) =>
                await db.Systems.FindAsync([id], ct) is { } system ? Results.Ok(await filing.FormatsForAsync(system, ct)) : Results.NotFound())
            .WithTags("Downloads").WithName("ListStorageFormats").WithSummary("The storage formats that make sense for a system.");

        api.MapGet("/systems/{id:guid}/filing/adoption", async (Guid id, RomdexDbContext db, Adoption adoption, CancellationToken ct) =>
                await db.Systems.FindAsync([id], ct) is { } system ? Results.Ok(await adoption.ReportAsync(system, ct)) : Results.NotFound())
            .WithTags("Downloads").WithName("ReportAdoption")
            .WithSummary("Which of a system's games in the library aren't where its filing settings would put them, and where they'd go. Nothing is moved.");

        api.MapPut("/systems/{id:guid}/filing", async (Guid id, FilingSettings body, RomdexDbContext db, Filing filing, CancellationToken ct) =>
            {
                if (await db.Systems.FindAsync([id], ct) is not { } system)
                    return Results.NotFound();
                if (system.BiosForId is not null)
                    return Problem("id", "A BIOS DAT is filed as its system is: set that system's instead.");
                if (!(await filing.FormatsForAsync(system, ct)).Contains(body.Storage))
                    return Problem("storage", $"{body.Storage} isn't a format for {system.Name}.");
                await filing.SaveAsync(system.Name, body, ct);
                return Results.Ok(body);
            })
            .RequireScope(Scopes.Files).WithTags("Downloads").WithName("SetFiling")
            .WithSummary("Sets how a system's arrivals are filed. Files already in the library stay as they are.");

        return api;
    }

    private static async Task<IResult> ActAsync(Guid id, RomdexDbContext db, Func<Download, CancellationToken, Task<bool>> act, string refusal, CancellationToken ct)
    {
        if (await db.Downloads.Include(i => i.Dumps).FirstOrDefaultAsync(i => i.Id == id, ct) is not { } item)
            return Results.NotFound();
        if (!await act(item, ct))
            return Results.Problem(item.State == DownloadState.Failed && item.Message is { } why ? why : refusal, statusCode: StatusCodes.Status409Conflict);
        return Results.Ok((await InfoAsync(db, [item], ct))[0]);
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static async Task<List<DownloadInfo>> InfoAsync(RomdexDbContext db, IReadOnlyList<Download> items, CancellationToken ct)
    {
        var dumpIds = items.SelectMany(i => i.Dumps.Select(d => d.DumpId)).ToList();
        var dumps = await db.Dumps.AsNoTracking().Where(d => dumpIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, d.Regions, d.SystemId, System = db.Systems.First(s => s.Id == d.SystemId).Name })
            .ToDictionaryAsync(d => d.Id, ct);
        var titleIds = items.Where(i => i.TitleId is not null).Select(i => i.TitleId!.Value).ToList();
        var titles = await db.Titles.AsNoTracking().Where(t => titleIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return items.Select(item =>
        {
            var candidate = JsonSerializer.Deserialize<Candidate>(item.CandidateJson, RetrievalJson.Options);
            var files = item.FilesJson is null ? [] : JsonSerializer.Deserialize<List<StagedFileInfo>>(item.FilesJson, RomdexJson.Options) ?? [];
            return new DownloadInfo(item.Id, item.Source, item.Name, (Client.DownloadState)(int)item.State, item.Message,
                item.TitleId is { } t && titles.TryGetValue(t, out var name) ? new DumpRef(t, name, []) : null,
                item.Dumps.Where(d => dumps.ContainsKey(d.DumpId)).Select(d =>
                {
                    var dump = dumps[d.DumpId];
                    return new DownloadDumpInfo(new DumpRef(dump.Id, dump.Name, dump.Regions.Length == 0 ? [] : dump.Regions.Split(',')), dump.SystemId, dump.System,
                        d.Alternative, d.FiledPaths?.Split('|') ?? []);
                }).ToList(),
                files, candidate?.Notes, item.CreatedAt, item.UpdatedAt);
        }).ToList();
    }
}
