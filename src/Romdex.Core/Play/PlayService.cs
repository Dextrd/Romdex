using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Core.Apps;
using Romdex.Core.Catalogue;
using Romdex.Core.Data;
using Romdex.Core.Library;

namespace Romdex.Core.Play;

/// <summary>What the player loads as the game: one library file as it is, or several sent together as one zip.</summary>
/// <param name="Name">The name the player gets it by. EmulatorJS goes by its extension.</param>
/// <param name="Files">Library-relative paths, by the DAT's names for them when they're zipped together.</param>
public sealed record GameSource(string Name, IReadOnlyList<(string Name, string Path)> Files, bool Zipped);

/// <summary>A BIOS for the player: a library file, or a file inside an archive in the library.</summary>
/// <param name="Name">The name the emulator looks for it by.</param>
public sealed record BiosSource(string Name, string Path, string? Entry);

/// <summary>A dump that plays: the emulator, and what it loads.</summary>
public sealed record PlayPlan(Dump Dump, Emulator Emulator, GameSource Game, BiosSource? Bios);

/// <summary>
/// Playing games in the browser: whether a dump plays, and the signed links the player page loads it and its BIOS by.
/// EmulatorJS is GPL, so it runs from a container of its own and is only ever loaded by the browser.
/// </summary>
public sealed class PlayService(RomdexDbContext db, LibraryViewBuilder views, StreamSigner signer, AppLinks apps, EmulatorCheck emulatorCheck,
    IOptions<RomdexOptions> options)
{
    /// <summary>What a signature covers: everything the player of one dump loads.</summary>
    public static string SignedPath(Guid dumpId) => $"play/{dumpId}";

    /// <summary>Where the player page is, relative to the app's base like other API links.</summary>
    public static string PlayerPath(Guid dumpId) => $"api/v1/play/{dumpId}/player";

    public async Task<PlayInfo?> GetAsync(Guid dumpId, CancellationToken ct)
    {
        if (await db.Dumps.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dumpId, ct) is not { } dump)
            return null;
        var app = await apps.LaunchingAppAsync(ct);
        var (plan, unplayable) = await ResolveAsync(dump, ct);
        var url = plan is null ? null : $"{PlayerPath(dumpId)}?{Query(dumpId)}";
        return new PlayInfo(dump.Id, dump.TitleId, dump.Name, url, unplayable, plan?.Bios?.Name, app);
    }

    /// <summary>The signature's query string, for the links the player page uses.</summary>
    public string Query(Guid dumpId)
    {
        var path = SignedPath(dumpId);
        return signer.Sign(path)[(path.Length + 1)..];
    }

    public bool Verify(Guid dumpId, long? exp, string? sig) => signer.Verify(SignedPath(dumpId), exp ?? 0, sig);

    /// <summary>The game and BIOS to load, or why it can't play.</summary>
    public async Task<(PlayPlan? Plan, string? Unplayable)> ResolveAsync(Dump dump, CancellationToken ct)
    {
        var system = await db.Systems.AsNoTracking().FirstAsync(s => s.Id == dump.SystemId, ct);
        if ((dump.Kinds & DumpKinds.Bios) != 0 || system.BiosForId is not null)
            return (null, "It's a BIOS, which games use rather than play.");
        if (Emulators.For(system.Name) is not { } emulator)
            return (null, $"The browser's emulator doesn't play {system.Name} games.");
        if (await views.GetAsync(system.Id, ct) is not { } view
            || view.Titles.SelectMany(t => t.Dumps).FirstOrDefault(d => d.Id == dump.Id) is not { Status: DumpStatus.Good })
            return (null, "The library doesn't have all of it.");
        if (string.IsNullOrWhiteSpace(options.Value.EmulatorUrl))
            return (null, "The emulator isn't installed: add the emulatorjs container to Romdex's stack.");
        if (!await emulatorCheck.IsUpAsync(ct))
            return (null, $"The emulator isn't answering at {options.Value.EmulatorUrl}. Is the emulatorjs container running?");

        var game = await GameAsync(dump, ct);
        if (game is null)
            return (null, "The emulator can't read the way it's stored.");

        BiosSource? bios = null;
        if (emulator.Bios is { } need)
        {
            bios = await BiosAsync(view, need, Split(dump.Regions), ct);
            if (bios is null && need.Required)
                return (null, $"It needs a BIOS, which the library doesn't have: {string.Join(" or ", need.Names)}.");
        }
        return (new PlayPlan(dump, emulator, game, bios), null);
    }

    /// <summary>What a system's emulator needs, for its page: the BIOS it looks for, and whether the library has one.</summary>
    public async Task<BiosNeed?> BiosNeedAsync(SystemView view, CancellationToken ct)
    {
        if (Emulators.For(view.Summary.Name)?.Bios is not { } need)
            return null;
        var ready = await BiosAsync(view, need, [], ct) is not null;
        return new BiosNeed(need.Required, need.Names, ready);
    }

    private async Task<GameSource?> GameAsync(Dump dump, CancellationToken ct)
    {
        var places = await db.LibraryMatches.AsNoTracking().Where(m => m.DumpFile!.DumpId == dump.Id)
            .Select(m => new { m.DumpFile!.Name, m.Content!.File!.Path, m.Content.File.Container })
            .ToListAsync(ct);
        var names = places.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count();
        if (names == 0)
            return null;

        // One file holding the whole dump, such as a zip or a CHD, goes as it is.
        var whole = places.GroupBy(p => p.Path)
            .Where(g => g.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == names && (names == 1 || g.First().Container != Container.Raw))
            .OrderBy(g => g.First().Container == Container.Raw ? 0 : 1).FirstOrDefault();
        if (whole is not null)
            return whole.First().Container == Container.Rvz ? null : new GameSource(Path.GetFileName(whole.Key), [("", whole.Key)], false);

        // A disc in several files, such as a cue sheet and its tracks, goes as one zip, by the DAT's names, which the
        // sheet refers to them by.
        var files = places.GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(p => p.Container).First())
            .ToList();
        if (files.Any(f => f.Container != Container.Raw))
            return null;
        return new GameSource($"{dump.Name}.zip", files.Select(f => (f.Name, f.Path)).ToList(), true);
    }

    // The library's BIOS for the emulator: one the core knows by its MD5, of one of the game's regions if there is.
    private async Task<BiosSource?> BiosAsync(SystemView view, EmulatorBios need, IReadOnlyList<string> regions, CancellationToken ct)
    {
        var owned = view.Bios.Where(b => b.Status == DumpStatus.Good).Select(b => b.Id).ToList();
        var known = need.Files.Keys.ToList();
        var places = await db.LibraryMatches.AsNoTracking()
            .Where(m => owned.Contains(m.DumpFile!.DumpId) && known.Contains(m.DumpFile.Md5!))
            .Select(m => new { m.DumpFile!.DumpId, m.DumpFile.Md5, m.Content!.File!.Path, m.Content.Entry, m.Content.File.Container })
            .ToListAsync(ct);
        var regionsOf = view.Bios.ToDictionary(b => b.Id, b => b.Regions);
        var best = places.Where(p => p.Container is Container.Raw or Container.Zip or Container.SevenZip)
            .OrderBy(p => regionsOf[p.DumpId].Any(regions.Contains) ? 0 : 1)
            .ThenBy(p => known.IndexOf(p.Md5!))
            .FirstOrDefault();
        return best is null ? null : new BiosSource(need.Files[best.Md5!], best.Path, best.Entry);
    }

    private static IReadOnlyList<string> Split(string list) => list.Length == 0 ? [] : list.Split(',');
}
