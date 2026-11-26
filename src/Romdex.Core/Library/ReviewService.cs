using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Data;
using Romdex.Core.Downloads;
using DownloadState = Romdex.Core.Data.DownloadState;

namespace Romdex.Core.Library;

/// <summary>Why Review refused something, for the API to say.</summary>
public sealed class ReviewException(string message, bool notFound = false) : Exception(message)
{
    public bool NotFound { get; } = notFound;
}

/// <summary>
/// Review: what the DATs don't explain, sorted by what can be done about it, and the decisions people make there. A
/// library file, or a file inside one, that matches nothing is outdated if a DAT matched its bytes before, a bad dump if
/// a dump file has its name, and otherwise waits under Matching. Files someone hid or deferred are under none of them.
/// </summary>
public sealed class ReviewService(RomdexDbContext db, LibraryMatcher matcher, LibraryViews cache, LibraryViewBuilder views, DownloadService downloads)
{
    private sealed record Content(Guid Id, Guid FileId, string Path, string? Entry, long Size, string Crc32, string Sha1, string? HeaderlessSha1, DateTime ModifiedAt)
    {
        public string Name => System.IO.Path.GetFileName(Entry ?? Path);
    }

    private sealed record Sorted(List<ReviewFile> BadDumps, List<ReviewFile> Outdated, List<ReviewFile> Matching);

    // ---- What's under each tab ----

    public async Task<ReviewSummary> SummarizeAsync(CancellationToken ct)
    {
        var sorted = await SortAsync(ct);
        var (corrupted, noTool) = await UnreadableAsync(ct);
        var held = await db.Downloads.AsNoTracking().Where(d => d.State == DownloadState.NeedsReview).Select(d => d.HeldFor).ToListAsync(ct);
        var failed = await db.Downloads.CountAsync(d => d.State == DownloadState.Failed, ct);
        var incomplete = (await IncompleteAsync(ct)).Count;
        var deferred = await db.LibraryFiles.CountAsync(f => f.DeferredAt != null && f.HiddenAt == null, ct);
        var hidden = await db.LibraryFiles.CountAsync(f => f.HiddenAt != null, ct);
        return new ReviewSummary(
            sorted.BadDumps.Count,
            sorted.Matching.Count + held.Count(h => h == HoldReason.Unmatched),
            sorted.Outdated.Count,
            corrupted.Count + held.Count(h => h == HoldReason.Unreadable),
            incomplete + held.Count(h => h == HoldReason.Incomplete),
            failed + held.Count(h => h is HoldReason.NotAskedFor or HoldReason.Alternative or null),
            deferred,
            hidden,
            noTool.Count == 0 ? null : new ToolNotice(noTool.Count, noTool[0].Error!));
    }

    // Each tab's list sorts by: for files, path (the default), name, size or when it last changed; for dumps, name
    // (the default), system or how many files are missing; for fetches, when (newest first by default) or name.

    public async Task<ReviewFilePage> BadDumpsAsync(int skip, int take, CancellationToken ct, string? sort = null, bool descending = false) =>
        Page(Sort((await SortAsync(ct)).BadDumps, sort, descending), skip, take, []);

    public async Task<ReviewFilePage> OutdatedAsync(int skip, int take, CancellationToken ct, string? sort = null, bool descending = false) =>
        Page(Sort((await SortAsync(ct)).Outdated, sort, descending), skip, take, []);

    public async Task<ReviewFilePage> MatchingAsync(int skip, int take, CancellationToken ct, string? sort = null, bool descending = false) =>
        Page(Sort((await SortAsync(ct)).Matching, sort, descending), skip, take, await HeldAsync(d => d.HeldFor == HoldReason.Unmatched, ct));

    public async Task<ReviewFilePage> CorruptedAsync(int skip, int take, CancellationToken ct, string? sort = null, bool descending = false) =>
        Page(Sort((await UnreadableAsync(ct)).Corrupted, sort, descending), skip, take, await HeldAsync(d => d.HeldFor == HoldReason.Unreadable, ct));

    private static List<ReviewFile> Sort(List<ReviewFile> files, string? sort, bool descending)
    {
        IEnumerable<ReviewFile> ordered = sort switch
        {
            "name" => files.OrderBy(f => Path.GetFileName(f.Entry ?? f.Path), StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Path, StringComparer.Ordinal),
            "size" => files.OrderBy(f => f.Size).ThenBy(f => f.Path, StringComparer.Ordinal),
            "modified" => files.OrderBy(f => f.ModifiedAt).ThenBy(f => f.Path, StringComparer.Ordinal),
            _ => files.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Entry, StringComparer.Ordinal),
        };
        return (descending ? ordered.Reverse() : ordered).ToList();
    }

    /// <summary>Files someone deferred, or with <paramref name="hidden"/>, hid: one a file, newest decision first.</summary>
    public async Task<ReviewFilePage> SetAsideAsync(bool hidden, int skip, int take, CancellationToken ct)
    {
        var files = db.LibraryFiles.AsNoTracking().Where(f => hidden ? f.HiddenAt != null : f.DeferredAt != null && f.HiddenAt == null);
        var total = await files.CountAsync(ct);
        var page = await files.OrderByDescending(f => hidden ? f.HiddenAt : f.DeferredAt).ThenBy(f => f.Path)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        return new ReviewFilePage(page.Select(f => new ReviewFile(f.Id, f.Path, null, f.Size, null, null, f.ModifiedAt, null, null, f.Error, f.Failure, f.DeferredAt, f.HiddenAt)).ToList(),
            total, []);
    }

    public async Task<ReviewDumpPage> IncompletePageAsync(int skip, int take, CancellationToken ct, string? sort = null, bool descending = false)
    {
        IEnumerable<ReviewDump> ordered = sort switch
        {
            "system" => (await IncompleteAsync(ct)).OrderBy(d => d.System, StringComparer.Ordinal).ThenBy(d => d.Dump.Name, StringComparer.Ordinal),
            "missing" => (await IncompleteAsync(ct)).OrderBy(d => d.Missing.Count).ThenBy(d => d.Dump.Name, StringComparer.Ordinal),
            _ => (await IncompleteAsync(ct)).OrderBy(d => d.Dump.Name, StringComparer.Ordinal),
        };
        var all = (descending ? ordered.Reverse() : ordered).ToList();
        return new ReviewDumpPage(all.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 1000)).ToList(), all.Count,
            await HeldAsync(d => d.HeldFor == HoldReason.Incomplete, ct));
    }

    /// <summary>Arrivals that aren't what was asked for, and fetches that failed, newest first.</summary>
    public async Task<DownloadsPage> CouldntFetchAsync(int skip, int take, CancellationToken ct, string? sort = null, bool descending = true)
    {
        var items = db.Downloads.AsNoTracking().Where(d => d.State == DownloadState.Failed || d.State == DownloadState.NeedsReview
            && (d.HeldFor == HoldReason.NotAskedFor || d.HeldFor == HoldReason.Alternative || d.HeldFor == null));
        var total = await items.CountAsync(ct);
        var ordered = sort == "name"
            ? descending ? items.OrderByDescending(d => d.Name) : items.OrderBy(d => d.Name)
            : descending ? items.OrderByDescending(d => d.UpdatedAt) : items.OrderBy(d => d.UpdatedAt);
        var page = await ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).Include(d => d.Dumps).ToListAsync(ct);
        return new DownloadsPage(await Api.DownloadEndpoints.InfoAsync(db, page, ct), total, total);
    }

    private static ReviewFilePage Page(List<ReviewFile> all, int skip, int take, IReadOnlyList<DownloadInfo> arrivals) =>
        new(all.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 1000)).ToList(), all.Count, arrivals);

    private async Task<List<DownloadInfo>> HeldAsync(System.Linq.Expressions.Expression<Func<Download, bool>> held, CancellationToken ct)
    {
        var items = await db.Downloads.AsNoTracking().Where(d => d.State == DownloadState.NeedsReview).Where(held)
            .OrderByDescending(d => d.UpdatedAt).Take(200).Include(d => d.Dumps).ToListAsync(ct);
        return await Api.DownloadEndpoints.InfoAsync(db, items, ct);
    }

    private async Task<List<Content>> UnmatchedAsync(CancellationToken ct) =>
        await db.LibraryContents.AsNoTracking()
            .Where(c => !c.Matches.Any() && c.File!.HiddenAt == null && c.File.DeferredAt == null)
            // Versions of a game someone linked, which have no match of their own.
            .Where(c => !db.ManualMatches.Any(m => m.TitleId != null && (m.Sha1 == c.Sha1 || m.Sha1 == c.HeaderlessSha1)))
            .OrderBy(c => c.File!.Path).ThenBy(c => c.Entry)
            .Select(c => new Content(c.Id, c.LibraryFileId, c.File!.Path, c.Entry, c.Size, c.Crc32, c.Sha1, c.HeaderlessSha1, c.File.ModifiedAt))
            .ToListAsync(ct);

    private async Task<Sorted> SortAsync(CancellationToken ct)
    {
        var contents = await UnmatchedAsync(ct);
        var shas = contents.Select(c => c.Sha1).Concat(contents.Where(c => c.HeaderlessSha1 is not null).Select(c => c.HeaderlessSha1!)).Distinct().ToList();
        var past = (await db.PastMatches.AsNoTracking().Where(p => shas.Contains(p.Sha1)).ToListAsync(ct))
            .GroupBy(p => p.Sha1).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.LastMatchedAt).First());
        var still = await StillListedAsync(past.Values, ct);
        var lookalikes = await LookalikesAsync(contents.Select(c => c.Name), ct);

        var sorted = new Sorted([], [], []);
        foreach (var c in contents)
        {
            var was = past.GetValueOrDefault(c.Sha1) ?? (c.HeaderlessSha1 is { } headerless ? past.GetValueOrDefault(headerless) : null);
            var looks = lookalikes.GetValueOrDefault(c.Name);
            var file = new ReviewFile(c.FileId, c.Path, c.Entry, c.Size, c.Crc32, c.Sha1, c.ModifiedAt, looks,
                was is null ? null : new PastMatchInfo(was.SystemName, was.DumpName, was.FileName, was.DatVersion, was.LastMatchedAt, still.GetValueOrDefault((was.SystemName, was.DumpName))),
                null, null, null, null);
            (was is not null ? sorted.Outdated : looks is not null ? sorted.BadDumps : sorted.Matching).Add(file);
        }
        return sorted;
    }

    // The dumps past matches named that the DATs still list, by system and name.
    private async Task<Dictionary<(string, string), DumpRef>> StillListedAsync(IEnumerable<PastMatch> past, CancellationToken ct)
    {
        var names = past.Select(p => p.DumpName).Distinct().ToList();
        if (names.Count == 0)
            return [];
        var dumps = await db.Dumps.AsNoTracking().Where(d => names.Contains(d.Name))
            .Select(d => new { d.Id, d.Name, d.Regions, d.TitleId, System = db.Systems.First(s => s.Id == d.SystemId).Name }).ToListAsync(ct);
        return dumps.GroupBy(d => (d.System, d.Name)).ToDictionary(g => g.Key, g => Ref(g.First().Id, g.First().Name, g.First().Regions, g.First().TitleId));
    }

    // Dumps with a file of each name, which a file of that name but other hashes likely is a bad copy of.
    private async Task<Dictionary<string, DumpRef>> LookalikesAsync(IEnumerable<string> fileNames, CancellationToken ct)
    {
        var names = fileNames.Distinct().ToList();
        if (names.Count == 0)
            return new(StringComparer.OrdinalIgnoreCase);
        return (await db.DumpFiles.AsNoTracking().Where(f => names.Contains(f.Name))
                .Select(f => new { f.Name, f.Dump!.Id, DumpName = f.Dump.Name, f.Dump.Regions, f.Dump.TitleId }).ToListAsync(ct))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Ref(g.First().Id, g.First().DumpName, g.First().Regions, g.First().TitleId), StringComparer.OrdinalIgnoreCase);
    }

    // Files that couldn't be read: those that are broken or can't be got at, and those waiting for a tool.
    private async Task<(List<ReviewFile> Corrupted, List<ReviewFile> NoTool)> UnreadableAsync(CancellationToken ct)
    {
        var files = await db.LibraryFiles.AsNoTracking().Where(f => f.Error != null && f.HiddenAt == null && f.DeferredAt == null)
            .OrderBy(f => f.Path).ToListAsync(ct);
        // What a damaged file's name suggests it was, as a dump's name or one of its files' names.
        var stems = files.Select(f => Path.GetFileNameWithoutExtension(f.Path)).Distinct().ToList();
        var byDumpName = (await db.Dumps.AsNoTracking().Where(d => stems.Contains(d.Name)).Select(d => new { d.Id, d.Name, d.Regions, d.TitleId }).ToListAsync(ct))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Ref(g.First().Id, g.First().Name, g.First().Regions, g.First().TitleId), StringComparer.OrdinalIgnoreCase);
        var byFileName = await LookalikesAsync(files.Select(f => Path.GetFileName(f.Path)), ct);

        var corrupted = new List<ReviewFile>();
        var noTool = new List<ReviewFile>();
        foreach (var f in files)
        {
            var looks = byDumpName.GetValueOrDefault(Path.GetFileNameWithoutExtension(f.Path)) ?? byFileName.GetValueOrDefault(Path.GetFileName(f.Path));
            var file = new ReviewFile(f.Id, f.Path, null, f.Size, null, null, f.ModifiedAt, looks, null, f.Error, f.Failure, null, null);
            (f.Failure == ReadFailure.NoTool ? noTool : corrupted).Add(file);
        }
        return (corrupted, noTool);
    }

    private async Task<List<ReviewDump>> IncompleteAsync(CancellationToken ct)
    {
        var incomplete = new List<(DumpView Dump, SystemView System)>();
        foreach (var system in await views.GetAllAsync(ct))
            incomplete.AddRange(system.Titles.SelectMany(t => t.Dumps).Concat(system.Bios).Where(d => d.Status == DumpStatus.Incomplete).Select(d => (d, system)));
        if (incomplete.Count == 0)
            return [];
        var ids = incomplete.Select(d => d.Dump.Id).ToList();
        var files = await db.DumpFiles.AsNoTracking().Where(f => ids.Contains(f.DumpId)).Select(f => new { f.Id, f.DumpId, f.Name }).ToListAsync(ct);
        var fileIds = files.Select(f => f.Id).ToList();
        var places = await db.LibraryMatches.AsNoTracking().Where(m => fileIds.Contains(m.DumpFileId))
            .Select(m => new { m.DumpFileId, m.Content!.LibraryFileId, m.Content.File!.Path, m.Content.Entry, m.Content.File.Container }).ToListAsync(ct);
        var titleIds = incomplete.Select(d => d.Dump.TitleId).Distinct().ToList();
        var titles = await db.Titles.AsNoTracking().Where(t => titleIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var placesByFile = places.ToLookup(p => p.DumpFileId);
        return incomplete.Select(x =>
        {
            var own = files.Where(f => f.DumpId == x.Dump.Id).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var fromChd = own.Any(f => placesByFile[f.Id].Any(p => p.Container == Container.Chd));
            var missing = own.Where(f => !placesByFile[f.Id].Any() && !(fromChd && LibraryViewBuilder.IsSheet(f.Name))).Select(f => f.Name).ToList();
            var have = own.SelectMany(f => placesByFile[f.Id]).Select(p => new LibraryPlace(p.LibraryFileId, p.Path, p.Entry)).Distinct().ToList();
            return new ReviewDump(x.Dump.Ref with { TitleId = x.Dump.TitleId }, x.Dump.TitleId, titles.GetValueOrDefault(x.Dump.TitleId, x.Dump.Name), x.System.Summary.Id, x.System.Summary.Name, missing, have);
        }).OrderBy(d => d.System, StringComparer.Ordinal).ThenBy(d => d.Dump.Name, StringComparer.Ordinal).ToList();
    }

    // ---- Decisions ----

    /// <summary>Takes a file off Review for now: it's still matched each time, so a DAT that adds it later finds it.</summary>
    public async Task DeferAsync(Guid fileId, CancellationToken ct)
    {
        var file = await UnderReviewAsync(fileId, ct);
        file.DeferredAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hides a file: it stays in the folder, but it's never matched or listed again until it's put back.</summary>
    public async Task HideAsync(Guid fileId, CancellationToken ct)
    {
        var file = await UnderReviewAsync(fileId, ct);
        file.HiddenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await matcher.MatchAsync(ct);
    }

    /// <summary>Puts a deferred or hidden file back: it's matched again, and listed if it still matches nothing.</summary>
    public async Task PutBackAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.LibraryFiles.FindAsync([fileId], ct) ?? throw new ReviewException("There's no such library file.", notFound: true);
        var wasHidden = file.HiddenAt is not null;
        file.DeferredAt = null;
        file.HiddenAt = null;
        await db.SaveChangesAsync(ct);
        if (wasHidden)
            await matcher.MatchAsync(ct);
    }

    /// <summary>Deletes a file under Review from the library, such as a bad dump or a damaged archive.</summary>
    public async Task<Download> DeleteFileAsync(Guid fileId, CancellationToken ct)
    {
        var file = await UnderReviewAsync(fileId, ct);
        var item = await downloads.DeleteFromLibraryAsync(file, ct);
        await matcher.MatchAsync(ct);
        return item;
    }

    /// <summary>Links some bytes to a dump, or to a game as a version no DAT lists.</summary>
    public async Task<ManualMatchInfo> LinkAsync(LinkFile body, CancellationToken ct)
    {
        var contents = await db.LibraryContents.Include(c => c.Matches).Where(c => c.LibraryFileId == body.FileId).ToListAsync(ct);
        if (contents.Count == 0)
            throw new ReviewException("There's no such library file, or nothing in it could be read.", notFound: true);
        var content = contents.FirstOrDefault(c => c.Entry == body.Entry) ?? (body.Entry is null && contents.Count == 1 ? contents[0] : null)
            ?? throw new ReviewException($"The file has no entry {body.Entry}.", notFound: true);
        if (content.Matches.Any(m => m.ManualMatchId is null))
            throw new ReviewException("It already matches the catalogue by its hashes.");
        var name = Path.GetFileName(content.Entry ?? (await db.LibraryFiles.FindAsync([body.FileId], ct))!.Path);

        DumpFile? dumpFile = null;
        Title? title = null;
        if (body.DumpFileId is { } dumpFileId)
            dumpFile = await db.DumpFiles.Include(f => f.Dump).FirstOrDefaultAsync(f => f.Id == dumpFileId, ct) ?? throw new ReviewException("There's no such dump file.");
        else if (body.DumpId is { } dumpId)
        {
            var files = await db.DumpFiles.Include(f => f.Dump).Where(f => f.DumpId == dumpId).ToListAsync(ct);
            if (files.Count == 0)
                throw new ReviewException("There's no such dump.");
            dumpFile = files.Count == 1 ? files[0] : files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ReviewException($"{files[0].Dump!.Name} has {files.Count} files and none is named {name}: say which it is.");
        }
        else if (body.TitleId is { } titleId)
        {
            title = await db.Titles.FindAsync([titleId], ct) ?? throw new ReviewException("There's no such title.");
            if (string.IsNullOrWhiteSpace(body.Name))
                throw new ReviewException("A version no DAT lists needs a name.");
        }
        else
            throw new ReviewException("Say what it is: a dump, a dump file, or a title and a name.");

        var reason = await db.PastMatches.AnyAsync(p => p.Sha1 == content.Sha1 || p.Sha1 == content.HeaderlessSha1, ct) ? ManualMatchReason.KeptOutdated
            : await db.DumpFiles.AnyAsync(f => f.Name == name, ct) ? ManualMatchReason.Approved
            : ManualMatchReason.Searched;
        // One link a set of bytes: a new one replaces the old.
        await db.ManualMatches.Where(m => m.Sha1 == content.Sha1).ExecuteDeleteAsync(ct);
        var link = new ManualMatch
        {
            Sha1 = content.Sha1,
            DumpFileId = dumpFile?.Id,
            TitleId = title?.Id,
            Name = dumpFile?.Dump!.Name ?? body.Name!.Trim(),
            Reason = reason,
        };
        db.ManualMatches.Add(link);
        await db.SaveChangesAsync(ct);
        await matcher.MatchAsync(ct);
        return Info(link, dumpFile?.Dump);
    }

    public async Task UnlinkAsync(Guid linkId, CancellationToken ct)
    {
        if (await db.ManualMatches.Where(m => m.Id == linkId).ExecuteDeleteAsync(ct) == 0)
            throw new ReviewException("There's no such link.", notFound: true);
        await matcher.MatchAsync(ct);
    }

    /// <summary>Links people made: to a title's dumps or as versions of it, with <paramref name="titleId"/>; else all of them.</summary>
    public async Task<IReadOnlyList<ManualMatchInfo>> LinksAsync(Guid? titleId, CancellationToken ct)
    {
        var links = db.ManualMatches.AsNoTracking().Include(m => m.DumpFile).ThenInclude(f => f!.Dump).AsQueryable();
        if (titleId is { } id)
            links = links.Where(m => m.TitleId == id || m.DumpFile!.Dump!.TitleId == id);
        return (await links.OrderBy(m => m.CreatedAt).ToListAsync(ct)).Select(m => Info(m, m.DumpFile?.Dump)).ToList();
    }

    /// <summary>Keeps an incomplete dump as it is: it counts as whole while only the files it's missing now are.</summary>
    public async Task KeepIncompleteAsync(Guid dumpId, CancellationToken ct)
    {
        var dump = (await IncompleteAsync(ct)).FirstOrDefault(d => d.Dump.Id == dumpId)
            ?? throw new ReviewException("It isn't an incomplete dump: the library has all of it, or none.");
        await db.KeptIncompletes.Where(k => k.DumpId == dumpId).ExecuteDeleteAsync(ct);
        db.KeptIncompletes.Add(new KeptIncomplete { DumpId = dumpId, Missing = string.Join('|', dump.Missing) });
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }

    public async Task UnkeepIncompleteAsync(Guid dumpId, CancellationToken ct)
    {
        if (await db.KeptIncompletes.Where(k => k.DumpId == dumpId).ExecuteDeleteAsync(ct) == 0)
            throw new ReviewException("That dump isn't kept as it is.", notFound: true);
        cache.Invalidate();
    }

    /// <summary>
    /// Deletes what the library has of an incomplete dump. Only files that hold nothing but this dump go: an archive
    /// with another game in it, or a track another region's whole dump uses too, is refused.
    /// </summary>
    public async Task<IReadOnlyList<Download>> DeleteIncompleteAsync(Guid dumpId, CancellationToken ct)
    {
        var dump = (await IncompleteAsync(ct)).FirstOrDefault(d => d.Dump.Id == dumpId)
            ?? throw new ReviewException("It isn't an incomplete dump: the library has all of it, or none.");
        var dumpFileIds = await db.DumpFiles.Where(f => f.DumpId == dumpId).Select(f => f.Id).ToListAsync(ct);
        var fileIds = dump.Have.Select(p => p.FileId).Distinct().ToList();
        var shared = await db.LibraryMatches.AsNoTracking()
            .Where(m => fileIds.Contains(m.Content!.LibraryFileId) && !dumpFileIds.Contains(m.DumpFileId))
            .Select(m => m.Content!.File!.Path).Distinct().ToListAsync(ct);
        if (shared.Count > 0)
            throw new ReviewException($"{string.Join(", ", shared)} {(shared.Count == 1 ? "holds" : "hold")} other dumps too, so nothing was deleted.");
        var deleted = new List<Download>();
        foreach (var id in fileIds)
            if (await db.LibraryFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) is { } file)
                deleted.Add(await downloads.DeleteFromLibraryAsync(file, ct));
        await matcher.MatchAsync(ct);
        return deleted;
    }

    // A file Review deals with: one that couldn't be read, or holds something the DATs match to nothing.
    private async Task<LibraryFile> UnderReviewAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.LibraryFiles.FindAsync([fileId], ct) ?? throw new ReviewException("There's no such library file.", notFound: true);
        if (file.Error is null && await db.LibraryContents.AnyAsync(c => c.LibraryFileId == fileId && c.Matches.Any(m => m.ManualMatchId == null), ct))
            throw new ReviewException("It matches the catalogue, so there's nothing to review. Only files that match nothing, or can't be read, can be.");
        return file;
    }

    private static ManualMatchInfo Info(ManualMatch link, Dump? dump) =>
        new(link.Id, link.Sha1, link.Name, link.Reason, dump is null ? null : Ref(dump.Id, dump.Name, dump.Regions, dump.TitleId), link.TitleId, link.CreatedAt);

    private static DumpRef Ref(Guid id, string name, string regions, Guid titleId) => new(id, name, regions.Length == 0 ? [] : regions.Split(','), titleId);
}
