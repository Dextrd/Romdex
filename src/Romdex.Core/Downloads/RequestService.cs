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
/// Only so many fetch at once (Settings → Fetching); the rest wait their turn in a queue that can be reordered, and any
/// can be paused, resumed or cancelled, as Melodex's.
/// </summary>
public sealed class RequestService(RomdexDbContext db, RetrievalSearch search, DownloadService downloads, LibraryViewBuilder views,
    Catalogue.Preferences preferences, Settings.SettingsService settings, ILogger<RequestService> logger)
{
    /// <summary>States a request is over in: nothing more happens to it unless it's tried again.</summary>
    public static readonly RequestState[] Finished = [RequestState.Done, RequestState.Failed, RequestState.Dismissed];

    // One change to the queue at a time, whichever request or the watcher makes it, so none starts twice.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static async Task<T> OneAtATimeAsync<T>(Func<Task<T>> change, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await change();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task OneAtATimeAsync(Func<Task> change, CancellationToken ct) => OneAtATimeAsync(async () =>
    {
        await change();
        return true;
    }, ct);
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
                State = RequestState.Queued,
                Message = "Waiting its turn",
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync(ct);
            requests.Add(request);
        }
        // Those with room start on their first offer straight away; the rest wait their turn.
        await OneAtATimeAsync(() => PumpCoreAsync(ct), ct);
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
    public Task AdvanceAsync(CancellationToken ct) => OneAtATimeAsync(() => AdvanceCoreAsync(ct), ct);

    private async Task AdvanceCoreAsync(CancellationToken ct)
    {
        // Queued ones too: one paused while its attempt was being filed is done once it's in.
        var open = await db.Requests.Include(r => r.Attempts).Where(r => r.State != RequestState.Done && r.State != RequestState.Dismissed).ToListAsync(ct);
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
        await PumpCoreAsync(ct);
    }

    /// <summary>
    /// Starts queued requests, in queue order and leaving out paused ones, while fewer than Settings → Fetching allows
    /// are fetching. One whose attempt is still going, paused while it was filed, picks up from there.
    /// </summary>
    public Task PumpAsync(CancellationToken ct) => OneAtATimeAsync(() => PumpCoreAsync(ct), ct);

    private async Task PumpCoreAsync(CancellationToken ct)
    {
        var room = (await settings.GetFetchAsync(ct)).ParallelFetches - await db.Requests.CountAsync(r => r.State == RequestState.Active, ct);
        if (room <= 0)
            return;
        var next = await db.Requests.Include(r => r.Attempts).Where(r => r.State == RequestState.Queued && !r.Paused)
            .OrderBy(r => r.QueueOrder).ThenBy(r => r.CreatedAt).Take(room).ToListAsync(ct);
        foreach (var request in next)
        {
            if (request.Attempts.Any(a => a.State is DownloadState.Fetching or DownloadState.Queued or DownloadState.Working))
            {
                request.State = RequestState.Active;
                request.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                continue;
            }
            await NextAsync(request, ct);
        }
    }

    /// <summary>
    /// Holds an unfinished request back until resumed. An offer it was fetching is stopped and forgotten, to fetch again
    /// from the start when resumed; one being filed carries on. False if it's finished.
    /// </summary>
    public Task<bool> PauseAsync(Request request, CancellationToken ct) => OneAtATimeAsync(async () =>
    {
        await db.Entry(request).ReloadAsync(ct);
        return await PauseCoreAsync(request, ct);
    }, ct);

    private async Task<bool> PauseCoreAsync(Request request, CancellationToken ct)
    {
        if (Finished.Contains(request.State))
            return false;
        await db.Entry(request).Collection(r => r.Attempts).LoadAsync(ct);
        foreach (var attempt in request.Attempts.Where(a => a.State is DownloadState.Fetching or DownloadState.Queued).ToList())
            if (await downloads.RejectAsync(attempt, ct))
                db.Downloads.Remove(attempt);
        request.Paused = true;
        request.State = RequestState.Queued;
        request.Message = "Paused";
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await PumpCoreAsync(ct);
        return true;
    }

    /// <summary>Lets a paused request go again, in its place in the queue. False if it wasn't paused.</summary>
    public Task<bool> ResumeAsync(Request request, CancellationToken ct) => OneAtATimeAsync(async () =>
    {
        await db.Entry(request).ReloadAsync(ct);
        return await ResumeCoreAsync(request, ct);
    }, ct);

    private async Task<bool> ResumeCoreAsync(Request request, CancellationToken ct)
    {
        if (!request.Paused)
            return false;
        request.Paused = false;
        if (request.Message == "Paused")
            request.Message = "Waiting its turn";
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await PumpCoreAsync(ct);
        return true;
    }

    /// <summary>Moves an unfinished request to just before another in the queue, or to the end when there's none.</summary>
    public Task<bool> MoveAsync(Request request, Guid? before, CancellationToken ct) => OneAtATimeAsync(async () =>
    {
        await db.Entry(request).ReloadAsync(ct);
        return await MoveCoreAsync(request, before, ct);
    }, ct);

    private async Task<bool> MoveCoreAsync(Request request, Guid? before, CancellationToken ct)
    {
        if (Finished.Contains(request.State))
            return false;
        var queue = await db.Requests.Where(r => r.Id != request.Id && (r.State == RequestState.Queued || r.State == RequestState.Active))
            .OrderBy(r => r.QueueOrder).ThenBy(r => r.CreatedAt).Select(r => new { r.Id, r.QueueOrder }).ToListAsync(ct);
        var index = before is { } id ? queue.FindIndex(r => r.Id == id) : queue.Count;
        if (index < 0)
            return false;
        request.QueueOrder = QueueOrders.Between(index > 0 ? queue[index - 1].QueueOrder : null, index < queue.Count ? queue[index].QueueOrder : null);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Cancels an unfinished request: what it's fetching is stopped and thrown away, and it's dismissed. One out of
    /// offers is dismissed as <see cref="DismissAsync"/> does. False if it's done or dismissed already.
    /// </summary>
    public Task<bool> CancelAsync(Request request, CancellationToken ct) => OneAtATimeAsync(async () =>
    {
        await db.Entry(request).ReloadAsync(ct);
        return await CancelCoreAsync(request, ct);
    }, ct);

    private async Task<bool> CancelCoreAsync(Request request, CancellationToken ct)
    {
        if (request.State == RequestState.Failed)
            return await DismissCoreAsync(request.Id, ct);
        if (Finished.Contains(request.State))
            return false;
        await db.Entry(request).Collection(r => r.Attempts).LoadAsync(ct);
        foreach (var attempt in request.Attempts.Where(a => a.State is DownloadState.NeedsReview or DownloadState.Failed or DownloadState.Queued or DownloadState.Fetching).ToList())
            await downloads.RejectAsync(attempt, ct);
        await FinishAsync(request, RequestState.Dismissed, "Cancelled", ct);
        await PumpCoreAsync(ct);
        return true;
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
    public Task<bool> TryAgainAsync(Guid id, CancellationToken ct) => OneAtATimeAsync(() => TryAgainCoreAsync(id, ct), ct);

    private async Task<bool> TryAgainCoreAsync(Guid id, CancellationToken ct)
    {
        if (await db.Requests.FirstOrDefaultAsync(r => r.Id == id, ct) is not { State: RequestState.Failed } request)
            return false;
        // At the back of the queue, starting now if there's room.
        request.State = RequestState.Queued;
        request.Paused = false;
        request.Message = "Waiting its turn";
        request.QueueOrder = QueueOrders.Now();
        await db.SaveChangesAsync(ct);
        await PumpCoreAsync(ct);
        return true;
    }

    /// <summary>Dismisses a request out of offers, throwing away what it held back. False if it isn't out of offers.</summary>
    public Task<bool> DismissAsync(Guid id, CancellationToken ct) => OneAtATimeAsync(() => DismissCoreAsync(id, ct), ct);

    private async Task<bool> DismissCoreAsync(Guid id, CancellationToken ct)
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
            r.State, r.Message, attempts[r.Id].Select(a => infos[a.Id]).ToList(), r.CreatedAt, r.UpdatedAt, r.Paused, r.QueueOrder)).ToList();
    }
}
