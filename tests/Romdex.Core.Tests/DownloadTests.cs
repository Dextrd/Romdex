using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Romdex.Client;
using Romdex.Core.Downloads;
using Romdex.Core.Plugins;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>
/// Downloads through the API: a retriever hands something over, the core identifies it by hash, decides whether it's
/// wanted, and has the worker file it. The plugin and the worker are stand-ins; every dump is made up.
/// </summary>
public sealed class DownloadTests : IDisposable
{
    private const string Handheld = "Test - Handheld";
    private const string Console = "Test - Console";
    private const string Disc = "Test - Disc";

    private readonly MadeUpDats _dats = new();
    private readonly string _staging;
    private readonly FakeWorker _worker;
    private readonly FakeRetriever _plugin;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public DownloadTests()
    {
        _staging = Path.Combine(_dats.Root, "staging");
        Directory.CreateDirectory(_staging);
        _worker = new FakeWorker(_staging, _dats.Roms);
        _plugin = new FakeRetriever(_staging);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Romdex:DataPath", _dats.Data)
            .UseSetting("Romdex:LibraryPath", _dats.Roms)
            .UseSetting("Romdex:DatPath", _dats.Dats)
            .UseSetting("Romdex:StagingPath", _staging)
            .UseSetting("Romdex:CatalogueRefreshHours", "0")
            .UseSetting("Romdex:DownloadCheckMinutes", "0")
            .UseSetting("Romdex:WorkerUrl", "http://fake-worker")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFilingWorker>();
                services.AddSingleton<IFilingWorker>(_worker);
                services.AddHttpClient(RetrievalPlugins.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _plugin);
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
    public async Task What_arrives_is_identified_by_hash_and_filed()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        var rabbit = _dats.Dump(Console, "Rocket Rabbit (Europe)", [".nes"], size: 16384);
        _dats.WriteDats();
        await SetUpAsync();

        // A loose cartridge under any name, and a headered one in a zip, which has to be written out of it.
        _plugin.Drop("pond", ("whatever.gb", pond[0].Data));
        var header = new byte[16];
        "NES\x1A"u8.CopyTo(header);
        _plugin.Drop("rabbit", ("rabbit.zip", Zips.Of(("rabbit.nes", [.. header, .. rabbit[0].Data]))));
        var items = await ArriveAsync(2);

        Assert.All(items, i => Assert.Equal(DownloadState.Filed, i.State));
        var filedPond = items.Single(i => i.Name == "pond");
        Assert.Equal("Pixel Pond (USA)", Assert.Single(filedPond.Dumps).Dump.Name);
        Assert.Equal(["romm/Pixel Pond (USA).gb"], filedPond.Dumps[0].Filed);
        Assert.Equal("1 files", filedPond.Notes);
        Assert.Contains("(without its header)", Assert.Single(items.Single(i => i.Name == "rabbit").Files).Matches[0]);
        Assert.Equal(FilingMode.Zip, _worker.Filed.Single(f => f.Dat.Contains("Rocket Rabbit")).Mode);
        Assert.Equal(FilingMode.Move, _worker.Filed.Single(f => f.Dat.Contains("Pixel Pond")).Mode);

        // In the library, verified, and gone from staging.
        foreach (var system in await _api.ListSystemsAsync())
            Assert.Equal(1, system.Owned);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_staging, "downloads")));

        // The same again isn't filed twice.
        _plugin.Drop("pond again", ("again.gb", pond[0].Data));
        var again = Assert.Single(await ArriveAsync(1));
        Assert.Equal(DownloadState.Discarded, again.State);
        Assert.Contains("already in the library", again.Message);
    }

    [Fact]
    public async Task What_can_not_be_verified_is_held_and_never_filed()
    {
        var disc = _dats.Dump(Disc, "Harbor Lights (Japan)", [".cue", ".bin", ".bin"]);
        _dats.Dump(Handheld, "Moss Maze (Japan)", [".gb"]);
        _dats.WriteDats();
        await SetUpAsync();

        _plugin.Drop("junk", ("homebrew.gb", [1, 2, 3, 4]));
        _plugin.Drop("harbor", disc[0], disc[1]);
        var items = await ArriveAsync(2);

        var junk = items.Single(i => i.Name == "junk");
        Assert.Equal(DownloadState.NeedsReview, junk.State);
        Assert.Equal("It matches nothing in the catalogue.", junk.Message);
        Assert.False(junk.CanAccept);
        var harbor = items.Single(i => i.Name == "harbor");
        Assert.Equal(DownloadState.NeedsReview, harbor.State);
        Assert.Equal($"It's incomplete: Harbor Lights (Japan) is missing {disc[2].Name}.", harbor.Message);
        Assert.Empty(_worker.Filed);

        // Rejected, its files are gone.
        junk = await _api.RejectDownloadAsync(junk.Id);
        Assert.Equal(DownloadState.Rejected, junk.State);
        Assert.False(Directory.Exists(Path.Combine(_staging, "downloads", junk.Id.ToString())));
        Assert.Equal(1, (await _api.ListDownloadsAsync()).Waiting);
    }

    [Fact]
    public async Task Alternatives_follow_the_system_and_can_be_accepted()
    {
        var usa = _dats.Dump(Handheld, "Tide Bell (USA)", [".gb"]);
        var europe = _dats.Dump(Handheld, "Tide Bell (Europe)", [".gb"], cloneOf: "Tide Bell (USA)");
        _dats.WriteDats();
        _dats.Put("Handheld/Tide Bell (USA).gb", usa[0].Data);
        await SetUpAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal([StorageFormat.Keep, StorageFormat.Loose, StorageFormat.Zip], await _api.ListStorageFormatsAsync(system.Id));
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SetFilingAsync(system.Id, new FilingSettings(Storage: StorageFormat.Chd)));
        await _api.SetFilingAsync(system.Id, new FilingSettings(LibraryLayout.DatName, StorageFormat.Zip, AlternativePolicy.Review));

        _plugin.Drop("europe", ("tide.gb", europe[0].Data));
        var item = Assert.Single(await ArriveAsync(1));
        Assert.Equal(DownloadState.NeedsReview, item.State);
        Assert.True(Assert.Single(item.Dumps).Alternative);
        Assert.True(item.CanAccept);

        await _api.AcceptDownloadAsync(item.Id);
        item = await WaitAsync(item.Id);
        Assert.Equal(DownloadState.Filed, item.State);
        Assert.Equal([$"{Handheld}/Tide Bell (Europe).zip"], item.Dumps[0].Filed);
    }

    [Fact]
    public async Task A_disc_is_converted_and_kept_only_once_it_reads_back_and_verifies()
    {
        var disc = _dats.Dump(Disc, "Signal Lost (USA)", [".cue", ".bin", ".bin"]);
        var other = _dats.Dump(Disc, "Harbor Lights (Japan)", [".cue", ".bin"]);
        _dats.WriteDats();
        await SetUpAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal([StorageFormat.Keep, StorageFormat.Chd], await _api.ListStorageFormatsAsync(system.Id));
        await _api.SetFilingAsync(system.Id, new FilingSettings(Storage: StorageFormat.Chd));

        // Named otherwise than the DAT has them: the tracks take the DAT's names before the cue sheet is read.
        _plugin.Drop("signal", ("a.cue", disc[0].Data), ("b.bin", disc[1].Data), ("c.bin", disc[2].Data));
        var item = Assert.Single(await ArriveAsync(1));
        Assert.Equal(DownloadState.Filed, item.State);
        var convert = Assert.Single(_worker.Converted);
        Assert.Equal("chd", convert.Format);
        Assert.EndsWith("/Signal Lost (USA).cue", convert.Source);
        Assert.Equal(["romm/Signal Lost (USA).chd"], item.Dumps[0].Filed);
        Assert.Equal(1, (await _api.GetSystemAsync(system.Id))!.Owned);

        // A CHD the tool can't read back goes, and the disc is filed as it came.
        _worker.Unreadable = true;
        _plugin.Drop("harbor", ("x.cue", other[0].Data), ("y.bin", other[1].Data));
        item = Assert.Single(await ArriveAsync(1));
        Assert.Equal(DownloadState.Filed, item.State);
        Assert.Contains("couldn't be converted to CHD, so it's filed as it came", item.Message);
        Assert.Equal([$"romm/Harbor Lights (Japan)/{other[1].Name}", $"romm/Harbor Lights (Japan)/{other[0].Name}"], item.Dumps[0].Filed.Order());
    }

    [Fact]
    public async Task A_game_is_found_through_the_plugins_and_must_be_what_was_asked_for()
    {
        var lantern = _dats.Dump(Handheld, "Lantern Run (World)", [".gb"]);
        var quarry = _dats.Dump(Handheld, "Quiet Quarry (USA)", [".gb"]);
        _dats.WriteDats();
        await SetUpAsync();
        var titles = (await _api.ListAllTitlesAsync()).Items;
        var wanted = titles.Single(t => t.Name == "Lantern Run");

        _plugin.Drop("not it", ("q.gb", quarry[0].Data));
        var found = await _api.SearchDownloadsAsync(wanted.Id);
        Assert.Empty(found.Problems);
        var offer = Assert.Single(found.Offers);
        var target = Assert.Single(_plugin.Searches).Target;
        Assert.Equal(Handheld, target.System);
        Assert.Equal("Lantern Run (World)", Assert.Single(target.Dumps).Name);
        Assert.Equal(lantern[0].Data.Length, target.Dumps[0].Files[0].Size);

        var item = await _api.StartDownloadAsync(new StartDownload(wanted.Id, offer.PluginId, offer.Candidate));
        item = await WaitAsync(item.Id);
        Assert.Equal(DownloadState.NeedsReview, item.State);
        Assert.Equal("It isn't what was asked for, Lantern Run: it's Quiet Quarry (USA).", item.Message);
        Assert.Equal("Lantern Run", item.Title!.Name);

        _plugin.Drop("it", ("l.gb", lantern[0].Data));
        offer = (await _api.SearchDownloadsAsync(wanted.Id)).Offers.Single();
        item = await WaitAsync((await _api.StartDownloadAsync(new StartDownload(wanted.Id, offer.PluginId, offer.Candidate))).Id);
        Assert.Equal(DownloadState.Filed, item.State);
    }

    [Fact]
    public async Task A_library_file_that_matches_nothing_can_go_back_through_downloads()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        _dats.Put("odd/stray.gb", [9, 9, 9]);
        await SetUpAsync();

        var unknown = (await _api.ListUnknownAsync()).Items;
        var stray = Assert.Single(unknown);
        var item = await WaitAsync((await _api.ReclaimLibraryFileAsync(stray.FileId)).Id);
        Assert.Equal(DownloadState.NeedsReview, item.State);
        Assert.Equal("Library", item.Source);
        Assert.Equal("It matches nothing in the catalogue.", item.Message);
        Assert.False(File.Exists(Path.Combine(_dats.Roms, "odd", "stray.gb")));
        Assert.Equal(0, (await _api.ListUnknownAsync()).Total);
        Assert.Equal(item.Id, Assert.Single((await _api.ListDownloadsAsync(DownloadState.NeedsReview)).Items).Id);

        // What matches the catalogue stays where it is.
        var status = await _api.GetLibraryStatusAsync();
        var owned = (await _api.GetTitleAsync((await _api.SearchCatalogueAsync("pixel pond", null, null)).Items[0].Id))!.Dumps[0].Files[0].Library[0];
        var refused = await Assert.ThrowsAsync<RomdexApiException>(() => _api.ReclaimLibraryFileAsync(owned.FileId));
        Assert.Contains("matches the catalogue", refused.Message);
        Assert.Equal(1, status.Files);
    }

    [Fact]
    public async Task The_library_is_checked_against_where_downloads_would_file_it()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        var maze = _dats.Dump(Handheld, "Moss Maze (Japan)", [".gb"]);
        var quarry = _dats.Dump(Handheld, "Quiet Quarry (USA)", [".gb", ".gbc"]);
        var harbor = _dats.Dump(Disc, "Harbor Lights (Japan)", [".cue", ".bin"]);
        var signal = _dats.Dump(Disc, "Signal Lost (USA)", [".cue", ".bin"]);
        _dats.WriteDats();
        _dats.Put("romm/Pixel Pond (USA).gb", pond[0].Data);
        _dats.Zip("old/Moss Maze.zip", maze[0]);
        _dats.Put("romm/Quiet Quarry (USA)/" + quarry[0].Name, quarry[0].Data);
        _dats.Put("romm/Quiet Quarry (USA)/" + quarry[1].Name, quarry[1].Data);
        _dats.Put("Harbor/" + harbor[0].Name, harbor[0].Data);  // half a game isn't one to move
        _worker.PutChd("romm/Signal Lost (USA).chd", signal[1].Data);  // whole without its cue sheet, which a CHD makes up
        await SetUpAsync();
        var systems = await _api.ListSystemsAsync();

        var report = await _api.GetAdoptionAsync(systems.Single(s => s.Name == Handheld).Id);
        Assert.Equal(3, report.Checked);
        Assert.Equal(2, report.InPlace);
        var move = Assert.Single(report.Moves);
        Assert.Equal("Moss Maze (Japan)", move.Dump.Name);
        Assert.Equal(["old/Moss Maze.zip"], move.From);
        Assert.Equal(["romm/Moss Maze (Japan).zip"], move.To);
        var discs = await _api.GetAdoptionAsync(systems.Single(s => s.Name == Disc).Id);
        Assert.Equal((1, 1), (discs.Checked, discs.InPlace));

        // With the DAT's name for a folder, nothing's in place; and nothing was moved to find that out.
        await _api.SetFilingAsync(systems.Single(s => s.Name == Handheld).Id, new FilingSettings(Layout: LibraryLayout.DatName));
        report = await _api.GetAdoptionAsync(systems.Single(s => s.Name == Handheld).Id);
        Assert.Equal(0, report.InPlace);
        Assert.Contains([$"{Handheld}/Quiet Quarry (USA)/{quarry[0].Name}", $"{Handheld}/Quiet Quarry (USA)/{quarry[1].Name}"], report.Moves.Select(m => m.To));
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "old", "Moss Maze.zip")));
    }

    private async Task SetUpAsync()
    {
        await RunAsync(await _api.RefreshCatalogueAsync());
        await RunAsync(await _api.ScanLibraryAsync());
        await _api.AddPluginAsync(new AddPlugin(Endpoint: FakeRetriever.Endpoint));
    }

    /// <summary>Asks the plugin for what's arrived, and waits for that many new items to settle.</summary>
    private async Task<IReadOnlyList<DownloadInfo>> ArriveAsync(int count)
    {
        var before = (await _api.ListDownloadsAsync()).Items.Select(i => i.Id).ToHashSet();
        await _api.CheckDownloadsAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var items = (await _api.ListDownloadsAsync()).Items.Where(i => !before.Contains(i.Id)).ToList();
            if (items.Count >= count && items.All(Settled))
                return items;
            Assert.True(DateTime.UtcNow < deadline, $"Waiting for {count} arrivals: {string.Join("; ", items.Select(i => $"{i.Name} {i.State} {i.Message}"))}");
            await Task.Delay(100);
        }
    }

    private async Task<DownloadInfo> WaitAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var item = (await _api.GetDownloadAsync(id))!;
            if (Settled(item))
                return item;
            Assert.True(DateTime.UtcNow < deadline, $"{item.Name}: {item.State} {item.Message}");
            await Task.Delay(100);
        }
    }

    private static bool Settled(DownloadInfo item) => item.State is not (DownloadState.Fetching or DownloadState.Queued or DownloadState.Working);

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
