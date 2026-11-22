using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romdex.Core.Data;
using Romdex.Core.Library;

namespace Romdex.Core.Catalogue;

/// <summary>A title on its system.</summary>
public sealed record GameTitle(SystemView System, TitleView Title);

/// <summary>
/// Games across systems: titles that are one game, such as Sonic Heroes on the GameCube and the PlayStation 2. Titles
/// are one game when their names are the same but for case, accents and punctuation, or when a metadata plugin gave
/// their dumps the same IGDB ID, which joins a game released under another name. Within a system, Retool's clone
/// lists have already made a game's other names one title.
/// </summary>
public sealed class Games(RomdexDbContext db, LibraryViewBuilder views)
{
    /// <summary>Every title's game: its titles on every system, best first, keyed by each title's ID.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<GameTitle>>> GetAsync(CancellationToken ct)
    {
        var all = (await views.GetAllAsync(ct)).SelectMany(s => s.Titles.Select(t => new GameTitle(s, t))).ToList();
        var parent = all.ToDictionary(t => t.Title.Id, t => t.Title.Id);
        Guid Find(Guid id)
        {
            while (parent[id] != id)
                id = parent[id] = parent[parent[id]];
            return id;
        }
        void Join(IEnumerable<Guid> ids)
        {
            Guid? first = null;
            foreach (var id in ids)
                if (first is null)
                    first = Find(id);
                else if (Find(id) is var root && root != first)
                    parent[root] = first.Value;
        }

        foreach (var same in all.GroupBy(t => Key(t.Title.Name)))
            Join(same.Select(t => t.Title.Id));
        // An ID two titles of one system share is a mistake, such as a sequel's dump taken for the game: had they been
        // one game, the system's clone list would have made them one title. So it joins nothing.
        var systemOf = all.ToDictionary(t => t.Title.Id, t => t.System.Summary.Id);
        foreach (var same in (await IgdbIdsAsync(ct)).Where(i => systemOf.ContainsKey(i.TitleId)).GroupBy(i => i.Igdb))
        {
            var titles = same.Select(i => i.TitleId).Distinct().ToList();
            if (titles.DistinctBy(t => systemOf[t]).Count() == titles.Count)
                Join(titles);
        }

        var games = new Dictionary<Guid, IReadOnlyList<GameTitle>>();
        foreach (var game in all.GroupBy(t => Find(t.Title.Id)))
        {
            var titles = game.OrderBy(t => t.Title.Status).ThenBy(t => SystemNames.Short(t.System.Summary.Name), StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var title in titles)
                games[title.Title.Id] = titles;
        }
        return games;
    }

    /// <summary>
    /// The key names are the same on: lowercased, without accents or anything but letters and digits, with a leading
    /// "The" dropped and "&amp;" read as "and", so <c>Sonic &amp; Knuckles</c> is <c>Sonic and Knuckles</c>.
    /// </summary>
    public static string Key(string name)
    {
        var key = new StringBuilder(name.Length);
        foreach (var c in name.Replace("&", " and ").Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c))
                key.Append(char.ToLowerInvariant(c));
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                key.Append(' ');
        var words = key.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Length > 1 && words[0] == "the" ? words[1..] : words);
    }

    // What isn't a game's release proper, so doesn't show how widely it came out.
    private const DumpKinds NotARelease = DumpKinds.AddOn | DumpKinds.Bad | DumpKinds.Beta | DumpKinds.Proto | DumpKinds.Demo | DumpKinds.Debug
                                          | DumpKinds.Pirate | DumpKinds.Program | DumpKinds.Bios;

    /// <summary>
    /// How widely a game came out, standing in for how well known it is, as the DATs don't say: the regions it was
    /// released in, counted on each system. A game out in Japan, the USA and Europe on three systems is 9; a Japanese
    /// spin-off on one is 1. A World release counts as three. Search and a game's series put games in this order.
    /// </summary>
    public static int Reach(IReadOnlyList<GameTitle> titles) =>
        titles.Sum(t => t.Title.Dumps.Where(d => (d.Kinds & NotARelease) == 0)
            .SelectMany(d => d.Regions).Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(r => r.Equals("World", StringComparison.OrdinalIgnoreCase) ? 3 : 1));

    // Each title's IGDB IDs, from what the metadata plugins found for its dumps.
    private async Task<IEnumerable<(Guid TitleId, string Igdb)>> IgdbIdsAsync(CancellationToken ct)
    {
        var found = await db.DumpArt.AsNoTracking().Where(a => a.IdsJson.Contains("\"igdb\""))
            .Join(db.Dumps, a => a.DumpId, d => d.Id, (a, d) => new { d.TitleId, a.IdsJson })
            .ToListAsync(ct);
        return found.Select(f => (f.TitleId, Igdb: Igdb(f.IdsJson))).Where(f => f.Igdb is not null).Select(f => (f.TitleId, f.Igdb!)).Distinct();
    }

    private static string? Igdb(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)?.GetValueOrDefault("igdb");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
