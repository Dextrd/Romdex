using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Romdex.Client;
using Romdex.Contracts.Plugins;
using Romdex.Core.Api;
using Romdex.Core.Apps;
using Romdex.Core.Catalogue;
using Romdex.Core.Plugins;
using Romdex.Core.Play;
using Romdex.Core.Tests.Support;
using Romdex.Plugins.EmulatorJs;

namespace Romdex.Core.Tests;

/// <summary>
/// Playing games the library has in the browser, their covers, and opening them in RomM. EmulatorJS, Hasheous and RomM
/// are stand-ins; every dump is made up.
/// </summary>
public sealed class PlayTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string PlayStation = "Sony - PlayStation";
    private const string GameCube = "Nintendo - GameCube";
    private const string TestDisc = "Test - Disc With A Known BIOS";

    private readonly MadeUpDats _dats = new();
    private readonly FakeServices _services = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public PlayTests()
    {
        AppSync.Every = TimeSpan.FromMilliseconds(50);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Romdex:DataPath", _dats.Data)
            .UseSetting("Romdex:LibraryPath", _dats.Roms)
            .UseSetting("Romdex:DatPath", _dats.Dats)
            .UseSetting("Romdex:ScheduledTasks", "false")
            .UseSetting("Romdex:DownloadCheckMinutes", "0")
            .UseSetting("Romdex:EmulatorUrl", "http://emulatorjs:8080")
            .ConfigureServices(services =>
            {
                services.Configure<PluginHttpOptions>(o =>
                {
                    o.PrimaryHandler = () => _services;
                    o.RateLimit = false;
                });
                services.AddHttpClient(PlayEndpoints.PlayerFilesClient).ConfigurePrimaryHttpMessageHandler(() => _services);
                services.AddHttpClient(CoverCache.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _services);
            }));
        _api = new RomdexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dats.Dispose();
    }

    [Fact]
    public async Task Games_the_library_has_play_through_signed_links()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        var harbor = _dats.Dump(PlayStation, "Harbor Lights (USA)", [".cue", ".bin", ".bin"]);
        var bios = _dats.Dump(PlayStation, "[BIOS] PlayStation (USA) (v4.1)", [".bin"]);
        var lights = _dats.Dump(TestDisc, "Harbor Lights (USA)", [".cue", ".bin", ".bin"]);
        // In a DAT of BIOS files of its own, as Redump's are, which doesn't mark it as one.
        var testBios = _dats.Dump(TestDisc + " - BIOS Images", "Test Disc (USA) (v1.0)", [".bin"]);
        Cores.Register(TestDisc, new EmulatorCore("psx", new CoreBios(false, new Dictionary<string, string> { [Md5(testBios[0].Data)] = "scph5501.bin" })));
        var garden = _dats.Dump(GameCube, "Glass Garden (USA)", [".iso"]);
        _dats.Dump(GameBoy, "Moss Maze (Japan)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        foreach (var (name, data) in harbor)
            _dats.Put($"psx/Harbor Lights (USA)/{name}", data);
        _dats.Zip("psx/bios.zip", bios[0]);
        foreach (var (name, data) in lights)
            _dats.Put($"test/Harbor Lights (USA)/{name}", data);
        _dats.Zip("test/bios.zip", testBios[0]);
        _dats.Put("ngc/Glass Garden (USA).iso", garden[0].Data);
        await SetUpAsync();

        // EmulatorJS is the player, set up from the emulatorjs container's address Romdex was given.
        var player = Assert.Single(await _api.ListPluginsAsync(), p => p.Type == PluginType.Player);
        Assert.Equal(EmulatorJsFactory.PluginId, player.PluginId);

        // A cartridge goes as its file, through a link that needs no sign-in, only its signature.
        var pondPlay = await PlayAsync("Pixel Pond");
        Assert.NotNull(pondPlay.PlayerUrl);
        Assert.Null(pondPlay.Bios);
        Assert.Null(pondPlay.App);
        using var anyone = _factory.CreateClient();
        var page = await anyone.GetStringAsync(pondPlay.PlayerUrl);
        Assert.Contains("EJS_core = \"gb\"", page);
        Assert.Contains("EJS_disableDatabases = true", page);
        Assert.Contains($"EJS_pathtodata = \"../../../../players/{player.Id}/data/\"", page);
        Assert.Contains($"href=\"../../../../titles/{pondPlay.TitleId}\"", page);
        Assert.Contains("\"loader.js\"", page);
        var gameUrl = Between(page, "EJS_gameUrl = \"", "\"");
        Assert.Equal(pond[0].Data, await anyone.GetByteArrayAsync(Under(pondPlay.PlayerUrl!, gameUrl)));
        // The player asks its size first, to see whether the copy it kept is still the one.
        using (var head = await anyone.SendAsync(new HttpRequestMessage(HttpMethod.Head, Under(pondPlay.PlayerUrl!, gameUrl))))
            Assert.Equal(pond[0].Data.Length, head.Content.Headers.ContentLength);
        var forged = pondPlay.PlayerUrl!.Replace("sig=", "sig=x");
        Assert.Equal(HttpStatusCode.Forbidden, (await anyone.GetAsync(forged)).StatusCode);

        // A disc in several files goes as one zip, by the DAT's names. A BIOS no core knows by its MD5 isn't used, as
        // this made-up one isn't, whatever its DAT says: the core plays with its own stand-in.
        var harborPlay = await PlayAsync("Harbor Lights", PlayStation);
        Assert.Null(harborPlay.Bios);
        page = await anyone.GetStringAsync(harborPlay.PlayerUrl);
        using (var zip = new ZipArchive(new MemoryStream(await anyone.GetByteArrayAsync(Under(harborPlay.PlayerUrl!, Between(page, "EJS_gameUrl = \"", "\""))))))
            Assert.Equal(harbor.Select(f => f.Name).Order(), zip.Entries.Select(e => e.FullName).Order());
        Assert.Contains("EJS_biosUrl = \"\"", page);
        var system = (await _api.ListSystemsAsync()).Single(s => s.Name == PlayStation);
        var need = (await _api.ListBiosAsync(system.Id)).Needed;
        Assert.NotNull(need);
        Assert.False(need.Ready);
        Assert.False(need.Required);
        Assert.Contains("scph5501.bin", need.Files);

        // One it knows goes with the game, out of its zip, by the name the core looks for.
        var lightsPlay = await PlayAsync("Harbor Lights", TestDisc);
        Assert.Equal("scph5501.bin", lightsPlay.Bios);
        page = await anyone.GetStringAsync(lightsPlay.PlayerUrl);
        Assert.Equal(testBios[0].Data, await anyone.GetByteArrayAsync(Under(lightsPlay.PlayerUrl!, Between(page, "EJS_biosUrl = \"", "\""))));

        // The GameCube isn't the browser's to play, and nothing plays what the library hasn't got.
        Assert.Equal($"No player plays {GameCube} games.", (await PlayAsync("Glass Garden")).Unplayable);
        Assert.Equal("The library doesn't have all of it.", (await PlayAsync("Moss Maze")).Unplayable);

        // EmulatorJS's own files come from its container, through the core.
        Assert.Equal("// loader", await anyone.GetStringAsync($"players/{player.Id}/data/loader.js"));

        // Turned off, there's no player, and nothing plays.
        await _api.UpdatePluginAsync(player.Id, new UpdatePlugin(Enabled: false));
        Assert.Equal("No player is set up: add one, such as EmulatorJS, under Settings → Plugins.", (await PlayAsync("Pixel Pond")).Unplayable);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"players/{player.Id}/data/loader.js")).StatusCode);
    }

    [Fact]
    public async Task Covers_come_from_metadata_plugins_and_stay()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        _services.Hasheous[Md5(pond[0].Data)] = "Pixel Pond Deluxe";

        var hasheous = await _api.AddPluginAsync(new AddPlugin("romdex.metadata.hasheous"));
        Assert.Equal(PluginType.Metadata, hasheous.Type);
        var title = (await _api.ListAllTitlesAsync(have: true)).Items.Single();

        Assert.Equal(FakeServices.Png, await CoverAsync(title.Cover!));
        Assert.Equal("Pixel Pond Deluxe", (await _api.GetTitleAsync(title.Id))!.KnownAs);
        Assert.Equal(1, _services.Lookups);

        // Looked up once: another scan doesn't ask again.
        await RunAsync(await _api.ScanLibraryAsync());
        Assert.Equal(1, _services.Lookups);
    }

    [Fact]
    public async Task Games_the_library_hasnt_get_covers_the_first_time_theyre_shown()
    {
        var kite = _dats.Dump(GameBoy, "Paper Kite (USA)", [".gb"]);
        _dats.Dump(GameBoy, "Night Owl (USA)", [".gb"]);
        _dats.WriteDats();
        await SetUpAsync();
        var kiteCover = Assert.Single((await _api.SearchCatalogueAsync("kite")).Items).Cover!;
        var owlCover = Assert.Single((await _api.SearchCatalogueAsync("owl")).Items).Cover!;

        // Every game has a cover's place, but with no plugin for covers there's none, at once.
        var client = _factory.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(kiteCover)).StatusCode);
        Assert.Equal(0, _services.Lookups);

        _services.Hasheous[Md5(kite[0].Data)] = "Paper Kite";
        await _api.AddPluginAsync(new AddPlugin("romdex.metadata.hasheous"));
        Assert.Equal(FakeServices.Png, await CoverAsync(kiteCover));
        Assert.Equal(1, _services.Lookups);

        // One no source knows is looked up once, which the page hears about with the rest it's waiting on.
        var status = await WaitAsync(async () => await (await client.PostAsJsonAsync("api/v1/titles/covers/status", new[] { "/" + owlCover, "/" + kiteCover }))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>(), s => s!["/" + owlCover] != "pending");
        Assert.Equal("none", status!["/" + owlCover]);
        Assert.Equal("ready", status["/" + kiteCover]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(owlCover)).StatusCode);
        Assert.Equal(2, _services.Lookups);
    }

    [Fact]
    public async Task A_games_page_has_what_wikipedia_knows_of_the_game_hasheous_found()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        // More of its series: one on another system, and one on two.
        _dats.Dump(GameBoy, "Pixel Pond 2 (USA)", [".gb"]);
        _dats.Dump(PlayStation, "Pixel Pond 2 (USA)", [".cue", ".bin"]);
        _dats.Dump(PlayStation, "Lily Leap (USA)", [".cue", ".bin"]);
        _dats.Dump(GameBoy, "Frog Racer (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        var title = (await _api.ListAllTitlesAsync(have: true)).Items.Single();

        // Without a plugin for it there's nothing to say.
        var info = (await _api.GetGameInfoAsync(title.Id))!;
        Assert.Null(info.Summary);
        Assert.Empty(info.Links);

        _services.Hasheous[Md5(pond[0].Data)] = "Pixel Pond";
        await _api.AddPluginAsync(new AddPlugin("romdex.metadata.hasheous"));
        // Hasheous alone: its description, and where else the game is known.
        info = (await _api.GetGameInfoAsync(title.Id))!;
        Assert.Equal("Pixel Pond\n\nA pond of pixels.", info.Summary);
        Assert.StartsWith("Hasheous", info.SummarySource);
        Assert.Equal(["IGDB", "Wikipedia"], info.Links.Select(l => l.Site));

        // With Wikipedia, what it is: shown as it's found, until there's no more to come.
        await _api.AddPluginAsync(new AddPlugin("romdex.metadata.wikipedia"));
        info = await WaitAsync(async () => (await _api.GetGameInfoSoFarAsync(title.Id))!, i => !i.Loading);
        Assert.Equal("Pixel Pond is a 1998 puzzle game.\n\nIt was well liked.", info.Summary);
        Assert.Equal(("Wikipedia", "https://en.wikipedia.org/wiki/Pixel_Pond"), (info.SummarySource, info.SummaryUrl));
        Assert.Equal([new ReleaseDate("Japan", "1998", null), new ReleaseDate("United States", "1998-06-01", null)], info.Released);
        Assert.Equal(["Lilypad Soft"], info.Developers);
        // A statement marked wrong is left out.
        Assert.Empty(info.Publishers);
        Assert.Equal(["puzzle video game"], info.Genres);
        Assert.Equal(["Rana Croak"], Assert.Single(info.Credits, c => c.Role == "Composer").Names);
        Assert.Equal([("Metacritic", "81/100"), ("Famitsu", "32/40"), ("IGN", "8.5/10 · 7/10"), ("Frog Monthly", "4/5")],
            info.Scores.Select(s => (s.Reviewer, s.Score)));
        // Ratings by their boards' marks; one the page has no mark for is left out.
        Assert.Equal([new AgeRating("ESRB", "e", "Everyone"), new AgeRating("PEGI", "7", "PEGI 7")], info.Ratings);
        Assert.Empty(info.Images);
        // The rest of its series that the catalogue has, the most widely released first (Pixel Pond 2 is on two
        // systems), each once on every system it's on; then the series that one's part of, without those already listed.
        Assert.Equal([("Pond Tales", ["Pixel Pond 2", "Lily Leap"]), ("Pond Universe", ["Frog Racer"])],
            info.InSeries.Select(s => (s.Name, s.Games.Select(g => g.Name).ToArray())).ToArray());
        Assert.Equal(2, info.InSeries[0].Games[0].Titles.Count);

        // Kept: the page shown again doesn't ask Wikipedia again.
        var asked = _services.WikipediaLookups;
        await _api.GetGameInfoAsync(title.Id);
        Assert.Equal(asked, _services.WikipediaLookups);
    }

    [Fact]
    public async Task A_games_page_has_libretros_pictures_of_the_dump_the_library_has()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.Dump(GameBoy, "Pixel Pond (Europe)", [".gb"], cloneOf: "Pixel Pond (USA)");
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        await _api.AddPluginAsync(new AddPlugin("romdex.metadata.libretro-thumbnails"));
        var title = (await _api.ListAllTitlesAsync(have: true)).Items.Single();

        // libretro has the title screen of both, and play of only the European one.
        var info = (await _api.GetGameInfoAsync(title.Id))!;
        Assert.Equal(["title", "screenshot"], info.Images.Select(i => i.Kind));
        Assert.All(info.Images, i => Assert.Equal("libretro thumbnails", i.Source));
        using var client = _factory.CreateSignedInClient();
        foreach (var image in info.Images)
            Assert.Equal(FakeServices.Png, await client.GetByteArrayAsync(image.Url));
        Assert.Equal(["/Nintendo - Game Boy/Named_Titles/Pixel Pond (USA).png", "/Nintendo - Game Boy/Named_Snaps/Pixel Pond (Europe).png"], _services.Pictures);

        // Kept, and the lists of what libretro has read once.
        Assert.Equal(FakeServices.Png, await client.GetByteArrayAsync(info.Images[0].Url));
        Assert.Equal(2, _services.Pictures.Count);
        Assert.Equal(2, _services.LibretroLists);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/v1/titles/{title.Id}/images/2")).StatusCode);
    }

    [Fact]
    public async Task TheGamesDB_finds_games_by_hash_with_your_key()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        _services.TheGamesDb = Md5(pond[0].Data);
        await _api.AddPluginAsync(new AddPlugin("romdex.metadata.thegamesdb", Settings: JsonSerializer.SerializeToElement(new { apiKey = "tgdb-test" })));
        var title = (await _api.ListAllTitlesAsync(have: true)).Items.Single();

        // Its cover and name, by the dump's hash, once it's been looked up: a cover asked for just as the lookup
        // finishes can miss it.
        await WaitAsync(async () => (await _api.GetTitleAsync(title.Id))!.KnownAs, k => k is not null);
        Assert.Equal(FakeServices.Png, await CoverAsync(title.Cover!));
        Assert.Equal("Pixel Pond Deluxe", (await _api.GetTitleAsync(title.Id))!.KnownAs);

        // What it is, with the names of what it gives by ID, and its title screen before its screenshots.
        var info = (await _api.GetGameInfoAsync(title.Id))!;
        Assert.Equal(("A pond of pixels.", "TheGamesDB", "https://thegamesdb.net/game.php?id=53"), (info.Summary, info.SummarySource, info.SummaryUrl));
        Assert.Equal([new ReleaseDate(null, "1998-06-01", null)], info.Released);
        Assert.Equal(["Lilypad Soft"], info.Developers);
        Assert.Equal(["Frog Press"], info.Publishers);
        Assert.Equal(["Puzzle"], info.Genres);
        Assert.Equal(["Up to 2 players", "Co-op"], info.Modes);
        Assert.Equal([new AgeRating("ESRB", "e10", "Everyone 10+")], info.Ratings);
        Assert.Equal(["title", "screenshot", "screenshot"], info.Images.Select(i => i.Kind));
        Assert.All(info.Images, i => Assert.Equal("TheGamesDB", i.Source));
        using var client = _factory.CreateSignedInClient();
        foreach (var image in info.Images)
            Assert.Equal(FakeServices.Png, await client.GetByteArrayAsync(image.Url));
        Assert.Equal(["/images/original/boxart/front/53-1.jpg", "/images/original/titlescreen/53-1.jpg", "/images/original/screenshots/53-1.jpg",
            "/images/original/screenshots/53-2.jpg"], _services.Pictures);

        // Every call carries the key.
        Assert.NotEmpty(_services.TheGamesDbKeys);
        Assert.All(_services.TheGamesDbKeys, k => Assert.Equal("tgdb-test", k));
    }

    [Fact]
    public async Task ScreenScraper_finds_games_by_hash_with_a_developer_id_and_your_account()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        _services.ScreenScraper = Md5(pond[0].Data);
        await _api.AddPluginAsync(new AddPlugin("romdex.metadata.screenscraper",
            Settings: JsonSerializer.SerializeToElement(new { devId = "dev", devPassword = "devpass", username = "frog", password = "ribbit" })));
        var title = (await _api.ListAllTitlesAsync(have: true)).Items.Single();

        // Its cover and its name as ScreenScraper has it, by the dump's hashes.
        Assert.Equal(FakeServices.Png, await CoverAsync(title.Cover!));
        Assert.Equal("Pixel Pond Deluxe", (await _api.GetTitleAsync(title.Id))!.KnownAs);

        // What it is, in English, with the ratings the page has marks for.
        var info = (await _api.GetGameInfoAsync(title.Id))!;
        Assert.Equal(("A pond of pixels.", "ScreenScraper"), (info.Summary, info.SummarySource));
        Assert.Equal([new ReleaseDate("Japan", "1998-03-01", null), new ReleaseDate("North America", "1998-06-01", null)], info.Released);
        Assert.Equal(["Lilypad Soft"], info.Developers);
        Assert.Equal(["Frog Press"], info.Publishers);
        Assert.Equal(["Puzzle"], info.Genres);
        Assert.Equal(["1-2 players"], info.Modes);
        Assert.Equal([new AgeRating("PEGI", "3", "PEGI 3"), new AgeRating("ESRB", "e", "Everyone")], info.Ratings);
        // Of the region games are most often known by.
        Assert.Equal(["title", "screenshot"], info.Images.Select(i => i.Kind));
        using var client = _factory.CreateSignedInClient();
        foreach (var image in info.Images)
            Assert.Equal(FakeServices.Png, await client.GetByteArrayAsync(image.Url));
        Assert.Equal(["box-2D us", "sstitle wor", "ss us"], _services.Pictures);

        // Every call says who's asking.
        Assert.NotEmpty(_services.ScreenScraperLogins);
        Assert.All(_services.ScreenScraperLogins, l => Assert.Equal("dev devpass Romdex frog ribbit json", l));
    }

    [Fact]
    public async Task Games_open_in_romm_once_it_has_found_them()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        await _api.AddPluginAsync(new AddPlugin("romdex.connector.romm",
            Settings: JsonSerializer.SerializeToElement(new { url = "http://romm:8080", token = "rmm_test", publicUrl = "https://romm.example" })));

        var play = await PlayAsync("Pixel Pond");
        Assert.Equal("RomM", play.App);

        // RomM hasn't scanned it yet: it's asked to, and then has it.
        var link = await _api.OpenInAppAsync(play.DumpId);
        Assert.Null(link.Url);
        Assert.Contains("asked to scan", link.Waiting);
        Assert.Equal(["gb"], _services.Scanned);
        link = await _api.OpenInAppAsync(play.DumpId);
        Assert.Equal("https://romm.example/rom/7", link.Url);
        Assert.Equal("Bearer rmm_test", _services.RommAuthorization);
    }

    private async Task SetUpAsync()
    {
        await RunAsync(await _api.RefreshCatalogueAsync());
        await RunAsync(await _api.ScanLibraryAsync());
    }

    private async Task<PlayInfo> PlayAsync(string name, string? system = null)
    {
        var systems = (await _api.ListSystemsAsync()).ToDictionary(s => s.Id, s => s.Name);
        var title = (await _api.ListAllTitlesAsync(q: name)).Items.Single(t => system is null || systems[t.SystemId] == system);
        var detail = (await _api.GetTitleAsync(title.Id))!;
        return (await _api.GetPlayAsync(detail.Dumps[0].Id))!;
    }

    // A link on the player page, which is relative to the page.
    private static string Under(string playerUrl, string relative) =>
        playerUrl[..playerUrl.LastIndexOf('/')] + "/" + relative.Replace("\\u0026", "&");

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }

    // A cover, once it's been looked up: until then it's a 503.
    private async Task<byte[]> CoverAsync(string cover)
    {
        var client = _factory.CreateSignedInClient();
        var response = await WaitAsync(() => client.GetAsync(cover), r => r.StatusCode != HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string Md5(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data));

    private static async Task<T> WaitAsync<T>(Func<Task<T>> get, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await get();
            if (done(value))
                return value;
            Assert.True(DateTime.UtcNow < deadline, $"Still waiting: {value}");
            await Task.Delay(100);
        }
    }

    private async Task RunAsync(TaskInfo task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, $"{task.Kind}: {task.State} {task.Error} {task.Progress}");
    }

    /// <summary>EmulatorJS's container, Hasheous and RomM, as far as Romdex uses them.</summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        /// <summary>Game names Hasheous knows, by MD5.</summary>
        public Dictionary<string, string> Hasheous { get; } = [];

        public int Lookups { get; private set; }
        public int WikipediaLookups { get; private set; }
        public int LibretroLists { get; private set; }
        public List<string> Pictures { get; } = [];

        /// <summary>The MD5 of the one game TheGamesDB knows, and ScreenScraper.</summary>
        public string? TheGamesDb { get; set; }
        public string? ScreenScraper { get; set; }

        public List<string> TheGamesDbKeys { get; } = [];
        public List<string> ScreenScraperLogins { get; } = [];

        private const string WikiText = """
            '''Pixel Pond''' is a game.
            {{Video game reviews
            | MC = 81/100<ref name="mc">{{cite web |url=https://example.com |title=Pixel Pond}}</ref>
            | Fam = 32/40
            | IGN = 8.5/10<br />7/10
            | rev1 = Frog Monthly | rev1Score = {{Rating|4|5}} 4/5
            | width = 30em
            }}
            """;
        public List<string> Scanned { get; } = [];
        public string? RommAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var path = url.AbsolutePath;
            switch (url.Host)
            {
                case "emulatorjs":
                    return path switch
                    {
                        "/data/version.json" => Json("""{"version":"4.2.3"}"""),
                        "/data/loader.js" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("// loader", Encoding.UTF8, "text/javascript") },
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    };
                case "hasheous.org" when path == "/api/v1/Lookup/ByHash":
                {
                    Lookups++;
                    var asked = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("md5").GetString()!;
                    return Hasheous.TryGetValue(asked, out var name)
                        ? Json(JsonSerializer.Serialize(new
                        {
                            name,
                            metadata = new[]
                            {
                                new { id = "42", status = "Mapped", source = "IGDB", link = "https://www.igdb.com/games/pixel-pond" },
                                new { id = "https://en.wikipedia.org/wiki/Pixel_Pond", status = "Mapped", source = "Wikipedia", link = "https://en.wikipedia.org/wiki/Pixel_Pond" },
                                new { id = "", status = "NotMapped", source = "Steam", link = "" },
                            },
                            attributes = new object[]
                            {
                                new { attributeType = "ImageId", attributeName = "Logo", value = "ABC123" },
                                new { attributeType = "LongString", attributeName = "AIDescription", value = "## Pixel Pond\n\nA **pond** of pixels." },
                            },
                        }))
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                case "hasheous.org" when path == "/api/v1/images/ABC123":
                    // Hasheous says JPEG for what's a PNG.
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/jpeg") } } };
                case "en.wikipedia.org" when path == "/w/api.php" && url.Query.Contains("titles=Pixel%20Pond"):
                    WikipediaLookups++;
                    return Json(JsonSerializer.Serialize(new
                    {
                        query = new
                        {
                            pages = new[]
                            {
                                new
                                {
                                    title = "Pixel Pond",
                                    pageprops = new { wikibase_item = "Q1" },
                                    extract = "Pixel Pond is a 1998 puzzle game.\nIt was well liked.",
                                    revisions = new[] { new { slots = new { main = new { content = WikiText } } } },
                                },
                            },
                        },
                    }));
                case "www.wikidata.org" when path == "/wiki/Special:EntityData/Q1.json":
                    return Json("""
                        {"entities":{"Q1":{"claims":{
                          "P577":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"time":"+1998-06-01T00:00:00Z","precision":11}}},
                                   "qualifiers":{"P291":[{"datavalue":{"value":{"id":"Q30"}}}]}},
                                  {"rank":"normal","mainsnak":{"datavalue":{"value":{"time":"+1998-00-00T00:00:00Z","precision":9}}},
                                   "qualifiers":{"P291":[{"datavalue":{"value":{"id":"Q17"}}}]}}],
                          "P178":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q2"}}}}],
                          "P123":[{"rank":"deprecated","mainsnak":{"datavalue":{"value":{"id":"Q3"}}}}],
                          "P136":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q4"}}}}],
                          "P86":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q5"}}}}],
                          "P179":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q6"}}}}],
                          "P852":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q14864328"}}}}],
                          "P908":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q14915514"}}}}],
                          "P914":[{"rank":"normal","mainsnak":{"datavalue":{"value":{"id":"Q102129161"}}}}]
                        }}}}
                        """);
                case "www.wikidata.org" when path == "/w/api.php":
                    return Json("""
                        {"entities":{"Q30":{"labels":{"en":{"value":"United States"}}},"Q17":{"labels":{"en":{"value":"Japan"}}},
                          "Q2":{"labels":{"en":{"value":"Lilypad Soft"}}},"Q4":{"labels":{"en":{"value":"puzzle video game"}}},
                          "Q5":{"labels":{"en":{"value":"Rana Croak"}}},"Q6":{"labels":{"en":{"value":"Pond Tales"}}}}}
                        """);
                case "query.wikidata.org" when path == "/sparql" && Uri.UnescapeDataString(url.Query).Contains("?parent"):
                    return Json("""{"results":{"bindings":[{"parent":{"value":"http://www.wikidata.org/entity/Q7"},"parentLabel":{"value":"Pond Universe"}}]}}""");
                case "query.wikidata.org" when path == "/sparql" && Uri.UnescapeDataString(url.Query).Contains("* wd:Q7 "):
                    return Json("""
                        {"results":{"bindings":[{"gameLabel":{"value":"Pixel Pond 2"}},{"gameLabel":{"value":"Frog Racer"}},{"gameLabel":{"value":"Pixel Pond"}}]}}
                        """);
                case "query.wikidata.org" when path == "/sparql" && Uri.UnescapeDataString(url.Query).Contains("* wd:Q6 "):
                    // The series' games, earliest first: the game itself, one the catalogue hasn't, and one unnamed.
                    return Json("""
                        {"results":{"bindings":[
                          {"gameLabel":{"value":"Pixel Pond"}},{"gameLabel":{"value":"Lily Leap"}},{"gameLabel":{"value":"Q99"}},
                          {"gameLabel":{"value":"Pond Tales Online"}},{"gameLabel":{"value":"Pixel Pond 2"}}]}}
                        """);
                case "thumbnails.libretro.com" when path.EndsWith('/'):
                    LibretroLists++;
                    return path switch
                    {
                        "/Nintendo%20-%20Game%20Boy/Named_Titles/" => Html("""<a href="Pixel%20Pond%20(Europe).png">x</a> <a href="Pixel%20Pond%20(USA).png">x</a>"""),
                        "/Nintendo%20-%20Game%20Boy/Named_Snaps/" => Html("""<a href="../">..</a> <a href="Pixel%20Pond%20(Europe).png">x</a>"""),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    };
                case "thumbnails.libretro.com":
                    Pictures.Add(Uri.UnescapeDataString(path));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/png") } } };
                case "api.thegamesdb.net":
                {
                    var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                    // Its health is checked while it looks up.
                    lock (TheGamesDbKeys)
                        TheGamesDbKeys.Add(query["apikey"] ?? "");
                    const string images = "https://cdn.thegamesdb.net/images/original/";
                    return path switch
                    {
                        "/v1/Games/ByGameHash" when query["hash"] == TheGamesDb && query["filter[type]"] == "md5" => Json("""
                            {"code":200,"data":{"count":1,"games":[{"id":53,"game_title":"Pixel Pond Deluxe","platform":4}]},
                             "include":{"boxart":{"base_url":{"original":"IMAGES"},
                               "data":{"53":[{"side":"back","filename":"boxart/back/53-1.jpg"},{"side":"front","filename":"boxart/front/53-1.jpg"}]}}}}
                            """.Replace("IMAGES", images)),
                        "/v1/Games/ByGameHash" => Json("""{"code":200,"data":{"count":0,"games":[]}}"""),
                        "/v1/Games/ByGameID" when query["id"] == "53" => Json("""
                            {"code":200,"data":{"count":1,"games":[{"id":53,"game_title":"Pixel Pond Deluxe","release_date":"1998-06-01","players":2,
                              "coop":"Yes","rating":"E10+ - Everyone 10+","overview":"A pond of pixels.","developers":[7],"publishers":[8],"genres":[5]}]}}
                            """),
                        "/v1/Developers/ByDeveloperID" => Json("""{"data":{"developers":{"7":{"id":7,"name":"Lilypad Soft"}}}}"""),
                        "/v1/Publishers/ByPublisherID" => Json("""{"data":{"publishers":{"8":{"id":8,"name":"Frog Press"}}}}"""),
                        "/v1/Genres" => Json("""{"data":{"genres":{"1":{"id":1,"name":"Action"},"5":{"id":5,"name":"Puzzle"}}}}"""),
                        "/v1/Games/Images" when query["games_id"] == "53" => Json("""
                            {"code":200,"data":{"count":3,"base_url":{"original":"IMAGES"},"images":{"53":[
                              {"type":"screenshot","filename":"screenshots/53-1.jpg"},{"type":"titlescreen","filename":"titlescreen/53-1.jpg"},
                              {"type":"screenshot","filename":"screenshots/53-2.jpg"}]}}}
                            """.Replace("IMAGES", images)),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    };
                }
                case "cdn.thegamesdb.net":
                    Pictures.Add(path);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/jpeg") } } };
                case "api.screenscraper.fr" when path == "/api2/jeuInfos.php":
                {
                    var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                    lock (ScreenScraperLogins)
                        ScreenScraperLogins.Add(string.Join(' ', new[] { "devid", "devpassword", "softname", "ssid", "sspassword", "output" }.Select(k => query[k])));
                    if (query["gameid"] != "3" && (query["md5"] != ScreenScraper || query["romnom"] != "Pixel Pond (USA).gb"))
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    string Media(string type, string region) =>
                        """{"type":"TYPE","region":"REGION","url":"https://neoclone.screenscraper.fr/api2/mediaJeu.php?devid=dev&jeuid=3&media=TYPE(REGION)","format":"png"}"""
                            .Replace("TYPE", type).Replace("REGION", region);
                    return Json("""
                        {"header":{"success":"true"},"response":{"jeu":{"id":"3",
                          "noms":[{"region":"jp","text":"Pikuseru Pondo"},{"region":"ss","text":"Pixel Pond Deluxe"}],
                          "synopsis":[{"langue":"fr","text":"Un étang de pixels."},{"langue":"en","text":"A pond of pixels."}],
                          "editeur":{"id":"1","text":"Frog Press"},"developpeur":{"id":"2","text":"Lilypad Soft"},"joueurs":{"text":"1-2"},
                          "dates":[{"region":"us","text":"1998-06-01"},{"region":"jp","text":"1998-03-01"}],
                          "genres":[{"id":"1","noms":[{"langue":"fr","text":"Réflexion"},{"langue":"en","text":"Puzzle"}]}],
                          "classifications":[{"type":"PEGI","text":"3"},{"type":"ESRB","text":"E"},{"type":"SS","text":"7"}],
                          "medias":[MEDIAS]}}}
                        """.Replace("MEDIAS", string.Join(',', new[] { ("box-2D", "eu"), ("box-2D", "us"), ("sstitle", "wor"), ("ss", "eu"), ("ss", "us") }.Select(m => Media(m.Item1, m.Item2)))));
                }
                case "neoclone.screenscraper.fr":
                {
                    var media = System.Web.HttpUtility.ParseQueryString(url.Query)["media"]!;
                    Pictures.Add(media.Replace("(", " ").TrimEnd(')'));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/png") } } };
                }
                case "romm":
                    RommAuthorization = request.Headers.Authorization?.ToString();
                    if (path == "/api/tasks/scan")
                    {
                        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
                        Scanned.AddRange(body.GetProperty("platform_fs_slugs").EnumerateArray().Select(e => e.GetString()!));
                        return Json("""{"task_id":"1","status":"queued"}""", HttpStatusCode.Accepted);
                    }
                    if (path == "/api/platforms")
                        return Json("""[{"id":1,"fs_slug":"gb"}]""");
                    if (path == "/api/roms")
                        return Json(Scanned.Count == 0 ? """{"items":[]}""" : """{"items":[{"id":7,"full_path":"roms/gb/Pixel Pond (USA).gb"}]}""");
                    if (path == "/api/roms/by-hash")
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
