using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Contracts.Plugins.Index;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Catalogue;

/// <summary>
/// Looks up the games the library has with the hash-lookup plugins, such as Hasheous, for their names and covers, and
/// keeps the covers in the data folder. One dump at a time, each kept as it's done, so a lookup that stops, for a
/// restart or a busy source, carries on where it left off next time.
/// </summary>
public sealed class ArtLookupTask(
    RomdexDbContext db,
    IndexPlugins plugins,
    LibraryViewBuilder views,
    LibraryViews cache,
    IHttpClientFactory clients,
    IOptions<RomdexOptions> options,
    ILogger<ArtLookupTask> logger) : ITaskHandler
{
    public const string TaskKind = "art-lookup";
    public const string HttpClientName = "art";

    /// <summary>How long before a dump nothing was found for is looked up again, and one whose lookup failed.</summary>
    public static readonly TimeSpan NotFoundAgain = TimeSpan.FromDays(30);

    public static readonly TimeSpan FailedAgain = TimeSpan.FromDays(1);

    public string Kind => TaskKind;

    public string Title(string? payload) => "Looking up covers";

    /// <summary>Queues a lookup when a hash-lookup plugin is on; there's nothing to do otherwise.</summary>
    public static async Task QueueIfUsefulAsync(RomdexDbContext db, PluginCatalog catalog, TaskQueue queue, CancellationToken ct)
    {
        if (await LookupsOnAsync(db, catalog, ct))
            await queue.EnqueueAsync(TaskKind, null, ct);
    }

    private static async Task<bool> LookupsOnAsync(RomdexDbContext db, PluginCatalog catalog, CancellationToken ct) =>
        (await db.PluginInstances.Where(p => p.Type == Contracts.Plugins.PluginType.Index && p.Enabled).Select(p => p.PluginId).ToListAsync(ct))
        .Any(id => catalog.Find(id)?.Manifest.Capabilities.Contains(IndexCapabilities.HashLookup) == true);

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var lookups = (await plugins.GetAllAsync(ct)).Where(p => p.Plugin is IHashLookup).Select(p => (p.Instance.Name, Lookup: (IHashLookup)p.Plugin)).ToList();
        if (lookups.Count == 0)
            return;

        // The dumps the library has whole, but not BIOS files, which have no covers.
        var owned = new List<Guid>();
        foreach (var view in await views.GetAllAsync(ct))
            owned.AddRange(view.Titles.SelectMany(t => t.Dumps).Where(d => d.Status == DumpStatus.Good).Select(d => d.Id));
        var now = DateTime.UtcNow;
        var done = await db.DumpArt.AsNoTracking()
            .Where(a => a.Error == null ? a.Name != null || a.CoverFile != null || a.LookedUpAt > now - NotFoundAgain : a.LookedUpAt > now - FailedAgain)
            .Select(a => a.DumpId).ToListAsync(ct);
        var todo = owned.Except(done).ToList();

        var found = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            progress.Report($"Looking up {i + 1:N0} of {todo.Count:N0}");
            var files = await db.DumpFiles.AsNoTracking().Where(f => f.DumpId == todo[i]).OrderBy(f => f.Name).ToListAsync(ct);
            // A disc's first track says which disc it is; its sheet is only text.
            var file = files.FirstOrDefault(f => !LibraryViewBuilder.IsSheet(f.Name)) ?? files.FirstOrDefault();
            if (file is null)
                continue;
            var art = await db.DumpArt.FindAsync([todo[i]], ct) ?? db.DumpArt.Add(new DumpArt { DumpId = todo[i] }).Entity;
            art.LookedUpAt = DateTime.UtcNow;
            art.Error = null;
            try
            {
                HashLookupResult? result = null;
                foreach (var (_, lookup) in lookups)
                    if ((result = await lookup.LookUpAsync(new IndexDumpFile(file.Name, file.Size, file.Crc32, file.Md5, file.Sha1), ct)) is not null)
                        break;
                art.Name = result?.Title;
                art.IdsJson = JsonSerializer.Serialize(result?.Ids ?? new Dictionary<string, string>());
                if (result?.CoverUrl is { } cover && await DownloadAsync(todo[i], cover, ct) is { } saved)
                {
                    art.CoverFile = saved;
                    found++;
                }
            }
            catch (IndexUnavailableException e)
            {
                // Busy: this one and the rest wait for the next lookup.
                db.ChangeTracker.Clear();
                logger.LogInformation("Stopped looking up covers for now: {Message}", e.Message);
                break;
            }
            catch (HttpRequestException e)
            {
                art.Error = e.Message;
            }
            await db.SaveChangesAsync(ct);
        }
        if (found > 0)
            cache.Invalidate();
        progress.Report(todo.Count == 0 ? "Every game has been looked up" : $"Found {found:N0} cover{(found == 1 ? "" : "s")} for {todo.Count:N0} game{(todo.Count == 1 ? "" : "s")}");
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
        var folder = Path.Combine(options.Value.DataPath, "art");
        Directory.CreateDirectory(folder);
        var name = dumpId.ToString("N") + extension;
        await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, ct);
        return name;
    }

    /// <summary>A title's best art: that of a dump the library has, with a cover, if there is one.</summary>
    public static async Task<DumpArt?> BestAsync(RomdexDbContext db, Guid titleId, CancellationToken ct) =>
        await db.DumpArt.AsNoTracking()
            .Join(db.Dumps.Where(d => d.TitleId == titleId), a => a.DumpId, d => d.Id, (a, d) => new { Art = a, d.Id })
            .OrderByDescending(x => x.Art.CoverFile != null)
            .ThenByDescending(x => db.LibraryMatches.Any(m => m.DumpFile!.DumpId == x.Id))
            .Select(x => x.Art)
            .FirstOrDefaultAsync(ct);

    /// <summary>Where a cover is kept, or null for a name that would leave the art folder.</summary>
    public static string? CoverPath(RomdexOptions options, string file) =>
        file == Path.GetFileName(file) && File.Exists(Path.Combine(options.DataPath, "art", file)) ? Path.GetFullPath(Path.Combine(options.DataPath, "art", file)) : null;

    public static string ContentType(string file) => Path.GetExtension(file) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
