using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Romdex.Client;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>
/// The catalogue and the library together, through the API: DATs imported, files hashed and matched, titles owned,
/// partial, bad and missing, 1G1R, ignoring, and the igir-shaped report. Every dump is made up.
/// </summary>
public sealed class LibraryTests : IDisposable
{
    private const string Handheld = "Test - Handheld";
    private const string Console = "Test - Console";
    private const string Disc = "Test - Disc";

    private readonly MadeUpDats _dats = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public LibraryTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Romdex:DataPath", _dats.Data)
            .UseSetting("Romdex:LibraryPath", _dats.Roms)
            .UseSetting("Romdex:DatPath", _dats.Dats)
            .UseSetting("Romdex:CatalogueRefreshHours", "0"));
        _api = new RomdexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dats.Dispose();
    }

    [Fact]
    public async Task Owning_any_region_owns_the_title_and_1G1R_points_at_the_preferred_one()
    {
        _dats.Dump(Handheld, "Hopper Quest (USA)", [".gb"]);
        _dats.Dump(Handheld, "Hopper Quest (Europe)", [".gb"], cloneOf: "Hopper Quest (USA)");
        var japan = _dats.Dump(Handheld, "Hopper Quest (Japan) (Rev 1)", [".gb"], cloneOf: "Hopper Quest (USA)");
        _dats.Dump(Handheld, "Sky Puzzle (World)", [".gb"]);
        _dats.Dump(Handheld, "Sky Puzzle (World) (Demo)", [".gb"]);
        _dats.Dump(Handheld, "Tilt Demo Disk (USA) (Demo)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/hopper.gb", japan[0].Data);
        await RefreshAndScanAsync();

        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal(Handheld, system.Name);
        Assert.Equal("20261101-000000", system.DatVersion);
        // The demo-only title is left out by default.
        Assert.Equal(2, system.Titles);
        Assert.Equal(1, system.Owned);
        Assert.Equal(1, system.Missing);

        var titles = (await _api.ListTitlesAsync(system.Id)).Items;
        var hopper = Assert.Single(titles, t => t.Name == "Hopper Quest");
        Assert.Equal(TitleStatus.Owned, hopper.Status);
        Assert.Equal("Hopper Quest (Europe)", hopper.Preferred.Name);
        Assert.Equal("Hopper Quest (Japan) (Rev 1)", hopper.Have!.Name);
        Assert.Equal(TitleStatus.Missing, Assert.Single(titles, t => t.Name == "Sky Puzzle").Status);

        // Japan first, and the Japanese release is both preferred and owned.
        await _api.SetSystemPreferencesAsync(system.Id, SystemPreferences.Default with { Regions = ["Japan", "USA"] });
        hopper = Assert.Single((await _api.ListTitlesAsync(system.Id)).Items, t => t.Name == "Hopper Quest");
        Assert.Equal("Hopper Quest (Japan) (Rev 1)", hopper.Preferred.Name);

        // Someone can prefer another dump of a title, and let 1G1R pick again.
        var detail = (await _api.GetTitleAsync(hopper.Id))!;
        Assert.Equal(3, detail.Dumps.Count);
        var usa = detail.Dumps.Single(d => d.Name == "Hopper Quest (USA)");
        detail = await _api.ChoosePreferredAsync(hopper.Id, usa.Id);
        Assert.True(detail.Dumps.Single(d => d.Id == usa.Id).Preferred);
        Assert.Equal(DumpStatus.Good, detail.Dumps.Single(d => d.Name.Contains("Japan")).Status);
        Assert.Equal("Handheld/hopper.gb", Assert.Single(detail.Dumps.Single(d => d.Name.Contains("Japan")).Files[0].Library).Path);
        detail = await _api.ChoosePreferredAsync(hopper.Id, null);
        Assert.Null(detail.ChosenDumpId);

        // Ignored, a missing title stops counting.
        var sky = Assert.Single((await _api.ListTitlesAsync(system.Id, TitleStatus.Missing)).Items);
        await _api.IgnoreTitleAsync(sky.Id);
        system = (await _api.GetSystemAsync(system.Id))!;
        Assert.Equal(0, system.Missing);
        Assert.Equal(1, system.Ignored);
        Assert.Equal(1.0, system.Completeness);
        Assert.Single((await _api.ListTitlesAsync(system.Id, ignored: true)).Items);

        // What the library has, whatever was ignored.
        await _api.IgnoreTitleAsync(hopper.Id);
        Assert.Equal("Hopper Quest", Assert.Single((await _api.ListTitlesAsync(system.Id, have: true)).Items).Name);
        Assert.Equal("Hopper Quest", Assert.Single((await _api.ListAllTitlesAsync(have: true)).Items).Name);
    }

    [Fact]
    public async Task Zips_and_headered_files_are_matched_by_what_they_hold()
    {
        var zipped = _dats.Dump(Handheld, "Zip Racer (Europe)", [".gb"]);
        var nes = _dats.Dump(Console, "Header Hero (USA)", [".nes"], size: 16384);
        _dats.WriteDats();
        _dats.Zip("Handheld/Zip Racer (Europe).zip", zipped[0]);
        // An iNES header in front of what the DAT lists.
        var header = new byte[16];
        "NES\x1A"u8.CopyTo(header);
        _dats.Put("Console/Header Hero (USA).nes", [.. header, .. nes[0].Data]);
        await RefreshAndScanAsync();

        foreach (var system in await _api.ListSystemsAsync())
            Assert.Equal(1, system.Owned);

        // Every system's games together, in one list.
        var games = await _api.ListAllTitlesAsync(TitleStatus.Owned);
        Assert.Equal(["Header Hero", "Zip Racer"], games.Items.Select(t => t.Name));
        Assert.Equal(2, games.Items.Select(t => t.SystemId).Distinct().Count());
        Assert.Single((await _api.ListAllTitlesAsync(q: "zip")).Items);

        // Search finds any game the DATs list, words in any order, the library's or not.
        _dats.Dump(Console, "Racer Zip Deluxe (Japan)", [".nes"]);
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());
        var found = await _api.SearchCatalogueAsync("racer zip");
        Assert.Equal(["Racer Zip Deluxe", "Zip Racer"], found.Items.Select(t => t.Name));
        Assert.Equal("Racer Zip Deluxe", Assert.Single((await _api.SearchCatalogueAsync("zip", have: false)).Items).Name);
        Assert.Equal("Zip Racer", Assert.Single((await _api.SearchCatalogueAsync("zip", have: true)).Items).Name);
        Assert.Equal(0, (await _api.SearchCatalogueAsync("zip", systemId: found.Items[0].Titles[0].SystemId, have: true)).Total);
    }

    [Fact]
    public async Task A_game_on_several_systems_is_found_once_and_owned_if_its_had_on_any()
    {
        _dats.Dump(Handheld, "Sky Puzzle (World)", [".gb"]);
        var console = _dats.Dump(Console, "Sky Puzzle! (USA)", [".nes"]);
        _dats.Dump(Disc, "The Sky Puzzle (Europe)", [".bin"]);
        _dats.Dump(Handheld, "Sky Puzzle Deluxe (World)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Console/sky.nes", console[0].Data);
        await RefreshAndScanAsync();

        // One game, named and pictured as the one the library has, with its title on each system.
        var found = await _api.SearchCatalogueAsync("sky puzzle");
        Assert.Equal(["Sky Puzzle!", "Sky Puzzle Deluxe"], found.Items.Select(g => g.Name));
        var game = found.Items[0];
        Assert.Equal(TitleStatus.Owned, game.Status);
        Assert.Equal(3, game.Titles.Count);
        Assert.Equal(game.Id, game.Titles[0].Id);
        Assert.Equal([TitleStatus.Missing, TitleStatus.Missing], game.Titles.Skip(1).Select(t => t.Status));
        Assert.Equal("Sky Puzzle Deluxe", Assert.Single((await _api.SearchCatalogueAsync("sky", have: false)).Items).Name);

        // Narrowed to a system it's missing on, it's still the one game.
        var handheld = (await _api.ListSystemsAsync()).Single(s => s.Name == Handheld);
        Assert.Equal(TitleStatus.Owned, (await _api.SearchCatalogueAsync("sky puzzle", handheld.Id)).Items[0].Status);

        // Its page on any system lists all three.
        var detail = (await _api.GetTitleAsync(game.Titles[1].Id))!;
        Assert.Equal(game.Titles.Select(t => t.Id).Order(), detail.Versions!.Select(v => v.Id).Order());
    }

    [Fact]
    public async Task Search_puts_the_game_named_first_then_the_most_widely_released()
    {
        _dats.Dump(Handheld, "Kart Pocket (Japan)", [".gb"]);
        _dats.Dump(Handheld, "Kart Pocket (Japan) (Beta)", [".gb"]);
        _dats.Dump(Console, "Super Kart (USA)", [".nes"]);
        _dats.Dump(Console, "Super Kart (Europe)", [".nes"]);
        _dats.Dump(Handheld, "Super Kart (World)", [".gb"]);
        _dats.Dump(Console, "Kart (Japan)", [".nes"]);
        _dats.Dump(Console, "Racing Pack (USA) (Kart)", [".nes"]);
        _dats.Dump(Console, "Fastest Kart, The (USA)", [".nes"]);
        _dats.WriteDats();
        await RefreshAndScanAsync();

        // Named just that first; then by how widely each came out, not by which starts with the word; then those only a
        // dump's name has. Names are as the game is written, and sort by the word after an article.
        var found = await _api.SearchCatalogueAsync("kart");
        Assert.Equal(["Kart", "Super Kart", "The Fastest Kart", "Kart Pocket", "Racing Pack"], found.Items.Select(g => g.Name));
        Assert.Equal("The Fastest Kart", Assert.Single((await _api.SearchCatalogueAsync("the fastest kart")).Items).Name);
    }

    [Fact]
    public async Task A_disc_is_owned_only_with_every_track_and_a_release_only_with_every_disc()
    {
        var whole = _dats.Dump(Disc, "Track Star (USA)", [".cue", ".bin", ".bin"]);
        var broken = _dats.Dump(Disc, "Half Track (Europe)", [".cue", ".bin", ".bin"]);
        var first = _dats.Dump(Disc, "Two Discs (USA) (Disc 1)", [".cue", ".bin"]);
        _dats.Dump(Disc, "Two Discs (USA) (Disc 2)", [".cue", ".bin"]);
        _dats.WriteDats();
        foreach (var (name, data) in whole)
            _dats.Put($"Disc/Track Star (USA)/{name}", data);
        // Track 2 is missing.
        _dats.Put($"Disc/Half Track (Europe)/{broken[0].Name}", broken[0].Data);
        _dats.Put($"Disc/Half Track (Europe)/{broken[1].Name}", broken[1].Data);
        foreach (var (name, data) in first)
            _dats.Put($"Disc/Two Discs (USA)/{name}", data);
        await RefreshAndScanAsync();

        var system = Assert.Single(await _api.ListSystemsAsync());
        var titles = (await _api.ListTitlesAsync(system.Id)).Items.ToDictionary(t => t.Name, t => t.Status);
        Assert.Equal(TitleStatus.Owned, titles["Track Star"]);
        Assert.Equal(TitleStatus.Bad, titles["Half Track"]);
        Assert.Equal(TitleStatus.Partial, titles["Two Discs"]);
        Assert.Equal((1, 1, 1), (system.Owned, system.Bad, system.Partial));

        // The report has the shape of igir's, one row a dump.
        var report = (await _api.GetSystemReportAsync(system.Id)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("DAT Name,Game Name,Status,ROM Files", report[0]);
        Assert.Contains(report, l => l.StartsWith("Test - Disc,Track Star (USA),FOUND,"));
        Assert.Contains(report, l => l.StartsWith("Test - Disc,Half Track (Europe),INCOMPLETE,"));
        Assert.Contains(report, l => l.StartsWith("Test - Disc,Two Discs (USA) (Disc 2),MISSING,"));
    }

    [Fact]
    public async Task Files_that_match_nothing_are_listed_and_one_with_a_dumps_name_is_a_bad_dump()
    {
        var real = _dats.Dump(Handheld, "Name Only (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/Name Only (USA).gb", [.. real[0].Data[..^1], 0]);
        _dats.Put("Handheld/homebrew.gb", [1, 2, 3]);
        _dats.Zip("Handheld/broken.zip");
        File.WriteAllBytes(Path.Combine(_dats.Roms, "Handheld", "damaged.zip"), [80, 75, 3, 4, 1, 2]);
        await RefreshAndScanAsync();

        var unknown = await _api.ListUnknownAsync();
        Assert.Contains(unknown.Items, u => u.Path == "Handheld/damaged.zip" && u.Error is not null);
        Assert.Contains(unknown.Items, u => u.Path == "Handheld/homebrew.gb" && u.LooksLike is null && u.Sha1 is not null);
        Assert.Equal("Name Only (USA)", Assert.Single(unknown.Items, u => u.Path == "Handheld/Name Only (USA).gb").LooksLike!.Name);
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal(TitleStatus.Bad, Assert.Single((await _api.ListTitlesAsync(system.Id)).Items).Status);

        var status = await _api.GetLibraryStatusAsync();
        Assert.Equal(4, status.Files);
        Assert.Equal(1, status.Unreadable);
    }

    [Fact]
    public async Task A_rescan_hashes_only_what_changed_and_a_new_DAT_keeps_whats_ignored()
    {
        var files = _dats.Dump(Handheld, "Cache Runner (USA)", [".gb"]);
        _dats.Dump(Handheld, "Left Out (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/cache.gb", files[0].Data);
        await RefreshAndScanAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        var leftOut = Assert.Single((await _api.ListTitlesAsync(system.Id, TitleStatus.Missing)).Items);
        await _api.IgnoreTitleAsync(leftOut.Id);

        var again = await RunAsync(await _api.ScanLibraryAsync());
        Assert.Contains("0 hashed", again.Progress);

        // A new version of the DAT: same entries, so the same IDs, and the ignore stays.
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());
        system = (await _api.GetSystemAsync(system.Id))!;
        Assert.Equal("20261108-000000", system.DatVersion);
        Assert.Equal(1, system.Ignored);
        Assert.Equal(1, system.Owned);
    }

    [Fact]
    public async Task Titles_a_metadata_plugin_found_one_game_for_are_one_unless_two_are_on_one_system()
    {
        _dats.Dump(Handheld, "Kite Story (World)", [".gb"]);
        _dats.Dump(Console, "Tako no Monogatari (Japan)", [".nes"]);
        _dats.Dump(Disc, "Kite Story (Europe)", [".bin"]);
        _dats.Dump(Disc, "Kite Story 2 (Europe)", [".bin"]);
        _dats.Dump(Handheld, "Owl Night (World)", [".gb"]);
        _dats.Dump(Console, "Fukurou no Yoru (Japan)", [".nes"]);
        _dats.WriteDats();
        await RefreshAndScanAsync();

        // As Hasheous would: the same IGDB game for the Japanese release, and, wrongly, for a sequel's dump.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.RomdexDbContext>();
            foreach (var (name, igdb) in new[] { ("Owl Night (World)", "1"), ("Fukurou no Yoru (Japan)", "1"), ("Kite Story (World)", "2"),
                         ("Tako no Monogatari (Japan)", "2"), ("Kite Story (Europe)", "2"), ("Kite Story 2 (Europe)", "2") })
                db.DumpArt.Add(new Data.DumpArt { DumpId = db.Dumps.Single(d => d.Name == name).Id, IdsJson = $$"""{"igdb":"{{igdb}}"}""" });
            await db.SaveChangesAsync();
        }

        var owl = Assert.Single((await _api.SearchCatalogueAsync("owl")).Items);
        Assert.Equal(["Fukurou no Yoru", "Owl Night"], owl.Titles.Select(t => t.Name).Order());
        // Kite Story 2 and Kite Story, both on the disc system, share an ID, so it joins nothing: the names still do.
        var kite = (await _api.SearchCatalogueAsync("kite")).Items;
        Assert.Equal(["Kite Story", "Kite Story 2"], kite.Select(g => g.Name));
        Assert.Equal(2, kite[0].Titles.Count);
        Assert.Single(Assert.Single((await _api.SearchCatalogueAsync("tako")).Items).Titles);
        // Found by its other name, a game is named as it was searched for.
        Assert.Equal("Fukurou no Yoru", Assert.Single((await _api.SearchCatalogueAsync("fukurou")).Items).Name);
    }

    [Fact]
    public async Task Updates_and_DLC_join_their_game_and_dont_make_it_owned()
    {
        _dats.Dump(Handheld, "Hopper Quest (USA)", [".gb"]);
        _dats.Dump(Handheld, "Hopper Quest (Japan)", [".gb"]);
        var update = _dats.Dump(Handheld, "Hopper Quest (USA) (Update)", [".gb"]);
        var expansion = _dats.Dump(Handheld, "Hopper Quest - Sky Levels (USA) (DLC)", [".gb"]);
        _dats.Dump(Handheld, "Hopper Quest (Japan) (Theme)", [".gb"]);
        // DLC for a game the DAT doesn't have is a title of its own.
        _dats.Dump(Handheld, "Kite Story - Wind Pack (USA) (DLC)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/update.gb", update[0].Data);
        _dats.Put("Handheld/expansion.gb", expansion[0].Data);
        await RefreshAndScanAsync();

        var system = Assert.Single(await _api.ListSystemsAsync());
        var titles = (await _api.ListTitlesAsync(system.Id)).Items;
        Assert.Equal(["Hopper Quest", "Kite Story: Wind Pack"], titles.Select(t => t.Name));
        var hopper = titles[0];
        // Its update and DLC are in the library, but the game isn't.
        Assert.Equal(TitleStatus.Missing, hopper.Status);
        Assert.Equal("Hopper Quest (USA)", hopper.Preferred.Name);
        var detail = (await _api.GetTitleAsync(hopper.Id))!;
        Assert.Equal(5, detail.Dumps.Count);
        Assert.Equal(["Hopper Quest (USA) (Update)"], detail.Dumps.Where(d => d.Kinds.Contains(DumpKind.Update)).Select(d => d.Name));
        Assert.Equal(["Hopper Quest (Japan) (Theme)", "Hopper Quest - Sky Levels (USA) (DLC)"],
            detail.Dumps.Where(d => d.Kinds.Contains(DumpKind.Dlc)).Select(d => d.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Where_catalogues_list_one_system_the_newest_DAT_wins_it()
    {
        _dats.Dump(Console, "Header Hero (USA)", [".nes"]);
        _dats.WriteDats("20261101-000000");
        // A second catalogue, later in order, with the same system made on the 14th, as libretro writes it.
        var more = Path.Combine(_dats.Root, "more");
        Directory.CreateDirectory(more);
        void Libretro(string version) => File.WriteAllText(Path.Combine(more, "console.dat"), $$"""
            clrmamepro (
            	name "{{Console}}"
            	version "{{version}}"
            )
            game (
            	name "Newer Game (USA)"
            	rom ( name "Newer Game (USA).nes" size 1 crc 00000001 )
            )
            """);
        Libretro("2026.11.14");
        await _api.AddPluginAsync(new AddPlugin("romdex.index.dat-folder", Name: "More DATs", Settings: JsonSerializer.SerializeToElement(new { folder = more })));

        await RunAsync(await _api.RefreshCatalogueAsync());
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal("2026.11.14", system.DatVersion);
        Assert.Equal(["Newer Game"], (await _api.ListTitlesAsync(system.Id)).Items.Select(t => t.Name));

        // A newer one dropped in wins it back; on the same day, the catalogue first in order does.
        _dats.WriteDats("20261120-000000");
        Libretro("2026.11.20");
        await RunAsync(await _api.RefreshCatalogueAsync());
        system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal("20261120-000000", system.DatVersion);
        Assert.Equal(["Header Hero"], (await _api.ListTitlesAsync(system.Id)).Items.Select(t => t.Name));
    }

    [Fact]
    public async Task Catalogues_systems_are_ticked_from_their_lists()
    {
        var libretro = await _api.AddPluginAsync(new AddPlugin("romdex.index.libretro-dats",
            Settings: JsonSerializer.SerializeToElement(new { systems = new[] { "Sega - Game Gear", "Atari - Lynx" } })));
        Assert.Equal(["Sega - Game Gear", "Atari - Lynx"], libretro.Settings.GetProperty("systems").EnumerateArray().Select(s => s.GetString()));
        await _api.AddPluginAsync(new AddPlugin("romdex.index.redump", Settings: JsonSerializer.SerializeToElement(new { systems = new[] { "PS2", "WIIU" } })));

        // Only what's on the list, each once, as a list.
        foreach (var systems in new object[] { new[] { "Nintendo - Nothing" }, new[] { "Atari - Lynx", "Atari - Lynx" }, "Atari - Lynx" })
        {
            var error = await Assert.ThrowsAsync<RomdexApiException>(() => _api.AddPluginAsync(new AddPlugin("romdex.index.libretro-dats",
                Settings: JsonSerializer.SerializeToElement(new { systems }))));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        }
    }

    private async Task RefreshAndScanAsync()
    {
        await RunAsync(await _api.RefreshCatalogueAsync());
        await RunAsync(await _api.ScanLibraryAsync());
    }

    private async Task<TaskInfo> RunAsync(TaskInfo task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, $"{task.Kind}: {task.State} {task.Error} {task.Progress}");
        return task;
    }
}
