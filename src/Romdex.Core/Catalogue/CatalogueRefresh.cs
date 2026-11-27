using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Contracts.Plugins.Index;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Catalogue;

/// <summary>
/// Brings the catalogue up to date: each index plugin that can fetches newer DATs, then every system whose DAT changed
/// is imported again, and the library is matched against the result. Where several list one system, the newest DAT
/// wins it. A system goes when no plugin lists it any more.
/// </summary>
public sealed class CatalogueRefreshTask(
    IndexPlugins plugins,
    CatalogueImporter importer,
    LibraryMatcher matcher,
    RomdexDbContext db,
    ILogger<CatalogueRefreshTask> logger) : ITaskHandler
{
    public const string TaskKind = "catalogue-refresh";

    public string Kind => TaskKind;

    public string Title(string? payload) => "Catalogue update";

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        // Where several catalogues list one system, the newest DAT wins it, whoever published it, and on the same date
        // the catalogue first in order. A catalogue that fails keeps the systems it had.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var held = new HashSet<string>(StringComparer.Ordinal);
        var listed = new List<(PluginInstance Instance, IIndexPlugin Plugin, int Order, IndexSystem System)>();
        var notes = new List<string>();
        int imported = 0, unchanged = 0;
        var all = await plugins.GetAllAsync(ct);
        for (var order = 0; order < all.Count; order++)
        {
            var (instance, plugin) = all[order];
            if (plugin is IDatFetcher fetcher)
            {
                progress.Report($"Fetching DATs: {instance.Name}");
                try
                {
                    var fetched = await fetcher.FetchAsync(ct);
                    if (fetched.Count > 0)
                        logger.LogInformation("{Plugin} fetched {Count} new DATs: {Systems}", instance.Name, fetched.Count, string.Join(", ", fetched.Select(s => s.Name)));
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "{Plugin} couldn't fetch new DATs", instance.Name);
                    notes.Add($"{instance.Name} couldn't fetch new DATs: {e.Message}");
                }
            }

            try
            {
                listed.AddRange((await plugin.ListSystemsAsync(ct)).Select(s => (instance, plugin, order, s)));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "{Plugin} couldn't list its systems", instance.Name);
                notes.Add($"{instance.Name} couldn't list its systems: {e.Message}");
                var had = await db.Systems.Where(s => s.PluginInstanceId == instance.Id).Select(s => s.Name).ToListAsync(ct);
                claimed.UnionWith(had);
                held.UnionWith(had.Select(DatVersions.SystemKey));
            }
        }

        var winners = new List<(PluginInstance Instance, IIndexPlugin Plugin, int Order, IndexSystem System)>();
        foreach (var same in listed.GroupBy(l => DatVersions.SystemKey(l.System.Name)).Where(g => !held.Contains(g.Key)))
        {
            var ranked = same.OrderByDescending(l => DatVersions.DateOf(l.System) ?? DateTimeOffset.MinValue).ThenBy(l => l.Order).ToList();
            winners.Add(ranked[0]);
            foreach (var older in ranked.Skip(1))
                logger.LogDebug("{System} {Version} from {Plugin} isn't used: {Winner} {WinnerVersion} from {WinnerPlugin} is newer or first",
                    older.System.Name, older.System.Version, older.Instance.Name, ranked[0].System.Name, ranked[0].System.Version, ranked[0].Instance.Name);
        }

        foreach (var (instance, plugin, _, system) in winners.OrderBy(w => w.Order))
        {
            if (!claimed.Add(system.Name))
                continue;
            var known = await db.Systems.AsNoTracking().FirstOrDefaultAsync(s => s.Name == system.Name, ct);
            if (known is not null && known.PluginInstanceId == instance.Id && known.PluginSystemId == system.Id && known.DatVersion == system.Version
                && known.CloneList == system.CloneList)
            {
                unchanged++;
                continue;
            }
            progress.Report($"Importing {system.Name} ({system.Version})");
            try
            {
                if (await plugin.GetSystemAsync(system.Id, ct) is not { } detail)
                    continue;
                var result = await importer.ImportAsync(instance.Id, detail, ct);
                imported++;
                logger.LogInformation("Imported {System} {Version} from {Plugin}: {Dumps} dumps in {Titles} titles ({Added} new, {Removed} gone)",
                    system.Name, system.Version, instance.Name, result.Dumps, result.Titles, result.Added, result.Removed);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "Couldn't import {System} from {Plugin}", system.Name, instance.Name);
                notes.Add($"{system.Name} couldn't be imported: {e.Message}");
            }
        }

        var gone = await db.Systems.Where(s => !claimed.Contains(s.Name)).ToListAsync(ct);
        foreach (var system in gone)
            logger.LogInformation("Removed {System}: no catalogue lists it any more, or a newer DAT of it took its place", system.Name);
        db.Systems.RemoveRange(gone);
        await db.SaveChangesAsync(ct);
        await importer.LinkBiosSystemsAsync(ct);

        progress.Report("Matching the library");
        await matcher.MatchAsync(ct);
        if (imported > 0)
            await db.OptimiseAsync(ct);
        var summary = $"{claimed.Count} systems: {imported} imported, {unchanged} unchanged" + (gone.Count > 0 ? $", {gone.Count} removed" : "");
        progress.Report(summary + string.Concat(notes.Select(n => "; " + n)));
    }
}
