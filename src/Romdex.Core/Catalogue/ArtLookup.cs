using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Contracts.Plugins.Metadata;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Catalogue;

/// <summary>A cover ready to serve.</summary>
public sealed record CachedCover(string Path, string ContentType, DateTime UpdatedAt);

/// <summary>
/// Covers and the names people know games by, found through the metadata plugins the first time each game is shown,
/// whether the library has it or not, and kept in the data folder's <c>art</c> folder. Pages only ever load covers
/// from the core, so the browser never contacts the sources. Each dump is looked up once, however many ask at once.
/// </summary>
public sealed class CoverCache(IServiceScopeFactory scopes, IHttpClientFactory clients, IOptions<RomdexOptions> options, ILogger<CoverCache> logger)
    : IDisposable
{
    public const string HttpClientName = "art";

    /// <summary>How long before a dump nothing was found for is looked up again, and one whose lookup failed.</summary>
    public static readonly TimeSpan NotFoundAgain = TimeSpan.FromDays(30);

    public static readonly TimeSpan FailedAgain = TimeSpan.FromDays(1);

    /// <summary>
    /// How many of a title's dumps are looked up for its cover: those the library has, then the one 1G1R prefers,
    /// then the rest. A source that knows one region of a game usually knows them all, so a few are enough.
    /// </summary>
    public const int MostDumps = 3;

    private readonly ConcurrentDictionary<Guid, Task<CachedCover?>> _titles = new();
    private readonly ConcurrentDictionary<Guid, Task<bool>> _dumps = new();
    private readonly CancellationTokenSource _stopping = new();

    private string Root => Path.Combine(options.Value.DataPath, "art");

    /// <summary>A title's cover, looking it up if it hasn't been. Null when no source has one.</summary>
    public async Task<CachedCover?> GetAsync(Guid titleId, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdexDbContext>();
            if (await CachedAsync(db, titleId, ct) is { } cached)
                return cached;
            if (!await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Metadata && p.Enabled, ct)
                || (await DueAsync(db, scope.ServiceProvider.GetRequiredService<LibraryViewBuilder>(), titleId, ct)).Count == 0)
                return null;
        }
        // Not cancelled with a request, so a page that moves on doesn't waste what was found.
        return await _titles.GetOrAdd(titleId, FindOnceAsync).WaitAsync(ct);
    }

    /// <summary>Looks up one dump, unless it was lately. True when a cover was found. Throws <see cref="MetadataUnavailableException"/> when a source is busy.</summary>
    public Task<bool> LookUpAsync(Guid dumpId, CancellationToken ct) => _dumps.GetOrAdd(dumpId, LookUpOnceAsync).WaitAsync(ct);

    /// <summary>
    /// Looks up the title's dumps that haven't been lately, whether or not it has a cover, for what else the plugins
    /// say about them, such as the IDs its details are found by. Gives up for now when a source is busy.
    /// </summary>
    public async Task LookUpTitleAsync(Guid titleId, CancellationToken ct)
    {
        IReadOnlyList<Guid> due;
        using (var scope = scopes.CreateScope())
            due = await DueAsync(scope.ServiceProvider.GetRequiredService<RomdexDbContext>(), scope.ServiceProvider.GetRequiredService<LibraryViewBuilder>(), titleId, ct);
        PluginHttp.Interactive.Value = true;
        try
        {
            foreach (var dumpId in due)
                await LookUpAsync(dumpId, ct);
        }
        catch (MetadataUnavailableException e)
        {
            logger.LogInformation("Couldn't look up a game for now: {Message}", e.Message);
        }
    }

    /// <summary>A title's best art: that of a dump the library has, with a cover, if there is one.</summary>
    public static async Task<DumpArt?> BestAsync(RomdexDbContext db, Guid titleId, CancellationToken ct) =>
        await db.DumpArt.AsNoTracking()
            .Join(db.Dumps.Where(d => d.TitleId == titleId), a => a.DumpId, d => d.Id, (a, d) => new { Art = a, d.Id })
            .OrderByDescending(x => x.Art.CoverFile != null)
            .ThenByDescending(x => db.LibraryMatches.Any(m => m.DumpFile!.DumpId == x.Id))
            .Select(x => x.Art)
            .FirstOrDefaultAsync(ct);

    /// <summary>Whether a dump's art was looked up lately enough not to ask again.</summary>
    public static System.Linq.Expressions.Expression<Func<DumpArt, bool>> Fresh(DateTime now) =>
        a => a.Error == null ? a.Name != null || a.CoverFile != null || a.LookedUpAt > now - NotFoundAgain : a.LookedUpAt > now - FailedAgain;

    private async Task<CachedCover?> CachedAsync(RomdexDbContext db, Guid titleId, CancellationToken ct)
    {
        if (await BestAsync(db, titleId, ct) is not { CoverFile: { } file, LookedUpAt: var at } || CoverPath(file) is not { } path)
            return null;
        return new CachedCover(path, ContentType(file), at);
    }

    // The title's dumps worth looking up that haven't been lately, best first.
    private static async Task<IReadOnlyList<Guid>> DueAsync(RomdexDbContext db, LibraryViewBuilder views, Guid titleId, CancellationToken ct)
    {
        var systemId = await db.Titles.Where(t => t.Id == titleId).Select(t => (Guid?)t.SystemId).FirstOrDefaultAsync(ct);
        if (systemId is null || await views.GetAsync(systemId.Value, ct) is not { } view || !view.ById.TryGetValue(titleId, out var title))
            return [];
        var candidates = title.Dumps
            .OrderByDescending(d => d.Status == DumpStatus.Good)
            .ThenByDescending(d => d.Release == title.Preferred.Release)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .Take(MostDumps).Select(d => d.Id).ToList();
        var done = await db.DumpArt.AsNoTracking().Where(a => candidates.Contains(a.DumpId)).Where(Fresh(DateTime.UtcNow)).Select(a => a.DumpId).ToListAsync(ct);
        return candidates.Except(done).ToList();
    }

    private async Task<CachedCover?> FindOnceAsync(Guid titleId)
    {
        try
        {
            await Task.Yield();
            var ct = _stopping.Token;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdexDbContext>();
            // Someone is waiting on these: they go ahead of background lookups under the sources' rate limits.
            PluginHttp.Interactive.Value = true;
            foreach (var dumpId in await DueAsync(db, scope.ServiceProvider.GetRequiredService<LibraryViewBuilder>(), titleId, ct))
                if (await LookUpAsync(dumpId, ct))
                    break;
            return await CachedAsync(db, titleId, ct);
        }
        catch (MetadataUnavailableException e)
        {
            logger.LogInformation("Couldn't look up a cover for now: {Message}", e.Message);
            return null;
        }
        finally
        {
            _titles.TryRemove(titleId, out _);
        }
    }

    private async Task<bool> LookUpOnceAsync(Guid dumpId)
    {
        try
        {
            await Task.Yield();
            var ct = _stopping.Token;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdexDbContext>();
            if (await db.DumpArt.AsNoTracking().Where(a => a.DumpId == dumpId).Where(Fresh(DateTime.UtcNow)).AnyAsync(ct))
                return false;
            var lookups = await scope.ServiceProvider.GetRequiredService<MetadataPlugins>().GetAllAsync(ct);
            var files = await db.DumpFiles.AsNoTracking().Where(f => f.DumpId == dumpId).OrderBy(f => f.Name).ToListAsync(ct);
            // A disc's first track says which disc it is; its sheet is only text.
            if (lookups.Count == 0 || (files.FirstOrDefault(f => !LibraryViewBuilder.IsSheet(f.Name)) ?? files.FirstOrDefault()) is not { } file)
                return false;

            var art = await db.DumpArt.FindAsync([dumpId], ct) ?? db.DumpArt.Add(new DumpArt { DumpId = dumpId }).Entity;
            art.LookedUpAt = DateTime.UtcNow;
            art.Error = null;
            var found = false;
            try
            {
                DumpMetadata? result = null;
                foreach (var (_, lookup) in lookups)
                    if ((result = await lookup.LookUpAsync(new IndexDumpFile(file.Name, file.Size, file.Crc32, file.Md5, file.Sha1), ct)) is not null)
                        break;
                art.Name = result?.Title;
                art.IdsJson = JsonSerializer.Serialize(result?.Ids ?? new Dictionary<string, string>());
                art.LinksJson = JsonSerializer.Serialize(result?.Links ?? new Dictionary<string, string>());
                art.Description = result?.Description;
                if (result?.CoverUrl is { } cover && await DownloadAsync(dumpId, cover, ct) is { } saved)
                    (art.CoverFile, found) = (saved, true);
            }
            catch (HttpRequestException e)
            {
                art.Error = e.Message;
            }
            await db.SaveChangesAsync(ct);
            return found;
        }
        finally
        {
            _dumps.TryRemove(dumpId, out _);
        }
    }

    // Saves a cover as {dumpId}.{ext} in the art folder; null when it isn't a picture.
    private async Task<string?> DownloadAsync(Guid dumpId, string url, CancellationToken ct)
    {
        using var response = await clients.CreateClient(HttpClientName).GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var extension = response.Content.Headers.ContentType?.MediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => null,
        };
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // Hasheous says JPEG for what are PNGs: the file's own first bytes are what count.
        extension = bytes.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]) ? ".png"
            : bytes.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]) ? ".jpg"
            : extension;
        if (extension is null)
            return null;
        Directory.CreateDirectory(Root);
        var name = dumpId.ToString("N") + extension;
        await File.WriteAllBytesAsync(Path.Combine(Root, name), bytes, ct);
        return name;
    }

    // Where a cover is kept, or null for a name that would leave the art folder.
    private string? CoverPath(string file) =>
        file == Path.GetFileName(file) && File.Exists(Path.Combine(Root, file)) ? Path.GetFullPath(Path.Combine(Root, file)) : null;

    private static string ContentType(string file) => Path.GetExtension(file) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}

/// <summary>
/// Looks up the games the library has ahead of time, so their covers are there before anyone opens them. One dump at
/// a time, each kept as it's done, so a lookup that stops, for a restart or a busy source, carries on where it left off
/// next time. Games the library hasn't are looked up when they're first shown (<see cref="CoverCache"/>).
/// </summary>
public sealed class ArtLookupTask(RomdexDbContext db, CoverCache covers, LibraryViewBuilder views, TaskFeed feed, ILogger<ArtLookupTask> logger)
    : ITaskHandler
{
    public const string TaskKind = "art-lookup";

    public string Kind => TaskKind;

    public string Title(string? payload) => "Looking up covers";

    /// <summary>Queues a lookup when a metadata plugin is on; there's nothing to do otherwise.</summary>
    public static async Task QueueIfUsefulAsync(RomdexDbContext db, TaskQueue queue, CancellationToken ct)
    {
        if (await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Metadata && p.Enabled, ct))
            await queue.EnqueueAsync(TaskKind, null, ct);
    }

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        if (!await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Metadata && p.Enabled, ct))
            return;

        // The dumps the library has whole, but not BIOS files, which have no covers.
        var owned = new List<Guid>();
        foreach (var view in await views.GetAllAsync(ct))
            owned.AddRange(view.Titles.SelectMany(t => t.Dumps).Where(d => d.Status == DumpStatus.Good).Select(d => d.Id));
        var done = await db.DumpArt.AsNoTracking().Where(CoverCache.Fresh(DateTime.UtcNow)).Select(a => a.DumpId).ToListAsync(ct);
        var todo = owned.Except(done).ToList();

        // Each game on Activity, by its dump's name.
        var rows = feed.Start(task);
        var names = new Dictionary<Guid, (string Name, Guid TitleId)>();
        foreach (var chunk in todo.Chunk(500))
            foreach (var dump in await db.Dumps.AsNoTracking().Where(d => chunk.Contains(d.Id)).Select(d => new { d.Id, d.Name, d.TitleId }).ToListAsync(ct))
                names[dump.Id] = (dump.Name, dump.TitleId);
        void Row(Guid id, TaskRowState state, string? detail = null)
        {
            if (names.TryGetValue(id, out var dump))
                rows.Put(id.ToString(), state, dump.Name, null, detail, $"titles/{dump.TitleId}");
        }
        foreach (var id in todo)
            Row(id, TaskRowState.Waiting);

        var found = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            progress.Report($"Looking up {i + 1:N0} of {todo.Count:N0}");
            Row(todo[i], TaskRowState.Working, "Looking up");
            try
            {
                if (await covers.LookUpAsync(todo[i], ct))
                {
                    found++;
                    Row(todo[i], TaskRowState.Done, "Found a cover");
                }
                else
                    Row(todo[i], TaskRowState.Skipped, "No cover found");
            }
            catch (MetadataUnavailableException e)
            {
                // Busy: this one and the rest wait for the next lookup.
                logger.LogInformation("Stopped looking up covers for now: {Message}", e.Message);
                for (var rest = i; rest < todo.Count; rest++)
                    Row(todo[rest], TaskRowState.Skipped, "Left for the next lookup: the metadata source is busy");
                break;
            }
        }
        progress.Report(todo.Count == 0 ? "Every game has been looked up" : $"Found {found:N0} cover{(found == 1 ? "" : "s")} for {todo.Count:N0} game{(todo.Count == 1 ? "" : "s")}");
    }
}
