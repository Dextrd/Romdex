using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Data;
using Romdex.Core.Library;
using DownloadState = Romdex.Core.Data.DownloadState;

namespace Romdex.Core.Downloads;

/// <summary>
/// Requests, as Melodex's: getting a game asks for each wanted region it came out in that the library doesn't have,
/// or, out in none, for the best dump there is. Each request tries the retrievers' offers in order, each attempt a
/// download, until one is filed. Another game is kept, to accept as what it is; a broken or failed attempt is thrown
/// away; either way the next offer is tried. Out of offers, the request waits under Review → Couldn't fetch.
/// </summary>
public sealed class RequestService(RomdexDbContext db, RetrievalSearch search, DownloadService downloads, LibraryViewBuilder views,
    Catalogue.Preferences preferences, ILogger<RequestService> logger)
{
    /// <summary>
    /// Gets a title: one request for each wanted region it's missing (regions one dump covers sharing one), or one for
    /// the best dump there is; with <paramref name="dumpId"/>, one for that dump. Each starts on its first offer.
    /// </summary>
    /// <returns>The requests, and why there are none, if there aren't; null for no such title.</returns>
    public async Task<(IReadOnlyList<Request> Requests, IReadOnlyList<string> Problems)?> StartAsync(Guid titleId, Guid? dumpId, IReadOnlyList<Guid>? replaces,
        CancellationToken ct)
    {
        var title = await db.Titles.AsNoTracking().Include(t => t.System).FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null)
            return null;
        List<IReadOnlyList<string>> plans;
        if (dumpId is not null)
            plans = [[]];
        else if (await PlanAsync(title, ct) is { } planned)
            plans = planned;
        else
            return ([], ["You have it in every region you want."]);

        var requests = new List<Request>();
        foreach (var regions in plans)
        {
            var request = new Request
            {
                TitleId = titleId,
                DumpId = dumpId,
                Regions = string.Join(',', regions),
                Replaces = replaces is { Count: > 0 } ? string.Join('|', replaces) : null,
                State = RequestState.Active,
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync(ct);
            await NextAsync(request, ct);
            requests.Add(request);
        }
        return (requests, []);
    }

    // The regions to ask for: each wanted region the title came out in and the library lacks, merged where one dump
    // covers several; or one ask for the best there is, when it came out in no wanted region. Null when it has the best.
    private async Task<List<IReadOnlyList<string>>?> PlanAsync(Title title, CancellationToken ct)
    {
        var owner = title.System!.BiosForId ?? title.SystemId;
        if ((await views.GetAsync(owner, ct))?.ById.GetValueOrDefault(title.Id) is not { } view)
            return [[]];
        if (view.Fit == RegionFit.Best)
            return null;
        if (view.WantedMissing is not { Count: > 0 } missing)
            return [[]];
        var (prefs, _) = await preferences.GetAsync((await db.Systems.AsNoTracking().FirstAsync(s => s.Id == owner, ct)).Name, ct);
        var releases = LibraryViewBuilder.Rank(view.Dumps.Where(d => !d.Hidden).GroupBy(d => d.Release).Select(g => g.ToList()), prefs).ToList();
        bool Covers(DumpView d, string region) => d.Regions.Any(r => r.Equals(region, StringComparison.OrdinalIgnoreCase)
            || r.Equals(SystemPreferences.World, StringComparison.OrdinalIgnoreCase));
        // Regions whose best release is the same, such as (USA, Europe), are one request.
        return missing
            .GroupBy(region => releases.FirstOrDefault(r => Covers(r[0], region))?[0].Release ?? region)
            .Select(g => (IReadOnlyList<string>)g.ToList())
            .ToList();
    }

    /// <summary>Tries a request's next offer, passing over what was turned away; out of offers, it's failed.</summary>
    private async Task NextAsync(Request request, CancellationToken ct)
    {
        var replaces = request.Replaces?.Split('|').Select(Guid.Parse).ToList();
        var regions = request.Regions.Length == 0 ? null : request.Regions.Split(',');
        var result = await search.FetchBestAsync(request.TitleId, request.DumpId, ct, replaces, regions, request.Id);
        if (result?.Item is not null)
        {
            request.State = RequestState.Active;
            request.Message = $"Fetching {result.Item.Name} from {result.Item.Source}";
        }
        else
        {
            var tried = await db.Downloads.AsNoTracking().Where(d => d.RequestId == request.Id).OrderBy(d => d.CreatedAt)
                .Select(d => new { d.Source, d.Name, d.Message }).ToListAsync(ct);
            var why = tried.Select(t => $"{t.Source}, {t.Name}: {t.Message}").Concat(result?.Problems ?? ["There's no such title any more."]);
            request.State = RequestState.Failed;
            request.Message = string.Join(" ", why.Select(w => w.TrimEnd('.') + "."));
            logger.LogWarning("Couldn't fetch {Title}{Regions}: {Why}", request.TitleId, request.Regions.Length > 0 ? $" ({request.Regions})" : "", request.Message);
        }
        request.UpdatedAt = DateTime.UtcNow;
        db.Update(request);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves requests on whose latest attempt is over: done once one is filed, else the next offer. Another game is kept
    /// to accept; anything else turned away is thrown away. A failed request one of whose attempts was accepted is done.
    /// </summary>
    public async Task AdvanceAsync(CancellationToken ct)
    {
        var open = await db.Requests.Include(r => r.Attempts).Where(r => r.State == RequestState.Active || r.State == RequestState.Failed).ToListAsync(ct);
        foreach (var request in open)
        {
            if (request.Attempts.FirstOrDefault(a => a.State == DownloadState.Filed) is { } filed)
            {
                await FinishAsync(request, RequestState.Done, filed.Message ?? $"Filed {filed.Name}", ct);
                continue;
            }
            if (request.State != RequestState.Active)
                continue;
            var latest = request.Attempts.OrderBy(a => a.CreatedAt).LastOrDefault();
            if (latest is null || latest.State is DownloadState.Fetching or DownloadState.Queued or DownloadState.Working)
                continue;
            // Already in the library: nothing more to get.
            if (latest.State == DownloadState.Discarded)
            {
                await FinishAsync(request, RequestState.Done, latest.Message ?? "It's in the library already", ct);
                continue;
            }
            // Another game is kept, to accept; anything else turned away is thrown away.
            if (latest.State == DownloadState.NeedsReview && latest.HeldFor != HoldReason.NotAskedFor)
                await downloads.RejectAsync(latest, ct);
            await NextAsync(request, ct);
        }
    }

    // Done or dismissed: what it held back, another game kept to accept, is thrown away.
    private async Task FinishAsync(Request request, RequestState state, string message, CancellationToken ct)
    {
        foreach (var held in request.Attempts.Where(a => a.State == DownloadState.NeedsReview).ToList())
            await downloads.RejectAsync(held, ct);
        request.State = state;
        request.Message = message;
        request.UpdatedAt = DateTime.UtcNow;
        db.Update(request);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Looks again for a request out of offers, passing over what was turned away. False if it isn't out of offers.</summary>
    public async Task<bool> TryAgainAsync(Guid id, CancellationToken ct)
    {
        if (await db.Requests.FirstOrDefaultAsync(r => r.Id == id, ct) is not { State: RequestState.Failed } request)
            return false;
        await NextAsync(request, ct);
        return true;
    }

    /// <summary>Dismisses a request out of offers, throwing away what it held back. False if it isn't out of offers.</summary>
    public async Task<bool> DismissAsync(Guid id, CancellationToken ct)
    {
        if (await db.Requests.Include(r => r.Attempts).FirstOrDefaultAsync(r => r.Id == id, ct) is not { State: RequestState.Failed } request)
            return false;
        await FinishAsync(request, RequestState.Dismissed, "Dismissed", ct);
        return true;
    }

    /// <summary>Requests as the API gives them, with their attempts, in the order given.</summary>
    public async Task<List<RequestInfo>> InfoAsync(IReadOnlyList<Request> requests, CancellationToken ct)
    {
        var ids = requests.Select(r => r.Id).ToList();
        var attempts = (await db.Downloads.AsNoTracking().Include(d => d.Dumps).Where(d => d.RequestId != null && ids.Contains(d.RequestId.Value))
            .OrderBy(d => d.CreatedAt).ToListAsync(ct)).ToLookup(d => d.RequestId!.Value);
        var infos = (await Api.DownloadEndpoints.InfoAsync(db, attempts.SelectMany(a => a).ToList(), ct)).ToDictionary(i => i.Id);
        var titleIds = requests.Select(r => r.TitleId).ToList();
        var titles = await db.Titles.AsNoTracking().Where(t => titleIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var dumpIds = requests.Where(r => r.DumpId is not null).Select(r => r.DumpId!.Value).ToList();
        var dumps = await db.Dumps.AsNoTracking().Where(d => dumpIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        return requests.Select(r => new RequestInfo(r.Id,
            new DumpRef(r.TitleId, titles.GetValueOrDefault(r.TitleId, "A game no DAT lists any more"), []),
            r.DumpId is { } d && dumps.TryGetValue(d, out var dump) ? new DumpRef(dump.Id, dump.Name, dump.Regions.Length == 0 ? [] : dump.Regions.Split(','), dump.TitleId) : null,
            r.Regions.Length == 0 ? [] : r.Regions.Split(','),
            r.State, r.Message, attempts[r.Id].Select(a => infos[a.Id]).ToList(), r.CreatedAt, r.UpdatedAt)).ToList();
    }
}
