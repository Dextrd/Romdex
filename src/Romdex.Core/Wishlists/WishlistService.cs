using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Plugins;

namespace Romdex.Core.Wishlists;

/// <summary>
/// Each user's wishlist, as Melodex's: games they'd like, kept for when something can fetch them. A game leaves it once
/// the library has it in every region you want, as Get would then have nothing to ask for; a dump wished for, once the
/// library has it.
/// </summary>
public sealed class WishlistService(RomdexDbContext db, LibraryViewBuilder views, RetrievalPlugins retrieval)
{
    public async Task<Wishlist> GetAsync(Guid userId, CancellationToken ct)
    {
        var entries = await db.WishlistEntries.Where(w => w.UserId == userId).OrderByDescending(w => w.AddedAt).ThenByDescending(w => w.Id).ToListAsync(ct);
        var titleIds = entries.Select(e => e.TitleId).ToList();
        var titles = await db.Titles.AsNoTracking().Where(t => titleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.SystemId, Owner = t.System!.BiosForId ?? t.SystemId, System = t.System.Name })
            .ToDictionaryAsync(t => t.Id, ct);
        var systems = new Dictionary<Guid, SystemView?>();
        foreach (var owner in titles.Values.Select(t => t.Owner).Distinct())
            systems[owner] = await views.GetAsync(owner, ct);

        var gone = new List<WishlistEntry>();
        var result = new List<WishlistItem>();
        foreach (var entry in entries)
        {
            if (!titles.TryGetValue(entry.TitleId, out var title) || systems[title.Owner]?.ById.GetValueOrDefault(entry.TitleId) is not { } view
                || Has(view, entry.DumpId))
            {
                gone.Add(entry);
                continue;
            }
            var dump = entry.DumpId is { } id ? view.Dumps.FirstOrDefault(d => d.Id == id) : null;
            // By the short name people call its system, where it has one, as the library shows it.
            var system = (title.SystemId == title.Owner ? systems[title.Owner] : await views.GetAsync(title.SystemId, ct))?.Summary;
            result.Add(new WishlistItem(entry.Id, entry.TitleId, dump?.Id, dump?.Name ?? view.Name, title.SystemId, system?.ShortName ?? title.System,
                Api.CatalogueEndpoints.CoverPath(entry.TitleId), view.Status, view.Fit, view.WantedMissing, entry.AddedAt));
        }
        if (gone.Count > 0)
        {
            db.WishlistEntries.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
        }
        return new Wishlist(await CanFetchAsync(ct), result);
    }

    // The library has what was wished for: the dump, or the game as well as your regions allow.
    private static bool Has(TitleView view, Guid? dumpId) => dumpId is { } id
        ? view.Dumps.FirstOrDefault(d => d.Id == id) is not { } dump || dump.Status == DumpStatus.Good
        : view.Fit == RegionFit.Best || view.Status == TitleStatus.Owned && view.WantedMissing is not { Count: > 0 };

    /// <summary>Whether a retriever that searches is on, so what's wished for can be got now.</summary>
    public async Task<bool> CanFetchAsync(CancellationToken ct) => (await retrieval.WithAsync(RetrievalCapabilities.Search, ct)).Count > 0;

    /// <summary>Puts a game, or one of its dumps, on the wishlist; once only. A problem when there's no such game.</summary>
    public async Task<string?> AddAsync(Guid userId, AddToWishlist add, CancellationToken ct)
    {
        if (!await db.Titles.AnyAsync(t => t.Id == add.TitleId, ct))
            return "There's no such game.";
        if (add.DumpId is { } dumpId && !await db.Dumps.AnyAsync(d => d.Id == dumpId && d.TitleId == add.TitleId, ct))
            return "That dump isn't one of the game's.";
        if (await db.WishlistEntries.FirstOrDefaultAsync(w => w.UserId == userId && w.TitleId == add.TitleId, ct) is { } already)
            already.DumpId = add.DumpId;
        else
            db.WishlistEntries.Add(new WishlistEntry { UserId = userId, TitleId = add.TitleId, DumpId = add.DumpId });
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Puts every game a system is missing on the wishlist, as the system's Missing list has them: those shown and not
    /// ignored that the library has no whole dump of. Null for no such system; else how many went on.
    /// </summary>
    public async Task<int?> AddMissingAsync(Guid userId, Guid systemId, CancellationToken ct)
    {
        if (await views.GetAsync(systemId, ct) is not { } view)
            return null;
        var missing = view.Titles.Where(t => t.Shown && !t.Ignored && t.Status != TitleStatus.Owned).Select(t => t.Id).ToList();
        var already = (await db.WishlistEntries.Where(w => w.UserId == userId).Select(w => w.TitleId).ToListAsync(ct)).ToHashSet();
        var added = missing.Where(id => !already.Contains(id)).ToList();
        db.WishlistEntries.AddRange(added.Select(id => new WishlistEntry { UserId = userId, TitleId = id }));
        await db.SaveChangesAsync(ct);
        return added.Count;
    }

    /// <summary>Takes the game off the wishlist; false if it wasn't on it.</summary>
    public async Task<bool> RemoveAsync(Guid userId, Guid titleId, CancellationToken ct) =>
        await db.WishlistEntries.Where(w => w.UserId == userId && w.TitleId == titleId).ExecuteDeleteAsync(ct) > 0;
}
