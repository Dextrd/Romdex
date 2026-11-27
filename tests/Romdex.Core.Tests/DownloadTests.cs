using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romdex.Client;
using System.Text.Json;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Downloads;
using Romdex.Core.Events;
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
    private readonly WebhookReceiver _receiver = new();
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
            .UseSetting("Romdex:ScheduledTasks", "false")
            .UseSetting("Romdex:DownloadCheckMinutes", "0")
            .UseSetting("Romdex:WorkerUrl", "http://fake-worker")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFilingWorker>();
                services.AddSingleton<IFilingWorker>(_worker);
                services.AddHttpClient(RetrievalPlugins.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _plugin);
                services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver);
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
    public async Task Webhooks_hear_once_when_a_download_is_filed_or_held()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        await SetUpAsync();
        await _api.CreateWebhookAsync(new SaveWebhook("Downloads", "http://receiver.test/hook", [EventTypes.DownloadFiled, EventTypes.DownloadNeedsReview]));

        _plugin.Drop("pond", ("whatever.gb", pond[0].Data));
        _plugin.Drop("junk", ("homebrew.gb", [1, 2, 3, 4]));
        await ArriveAsync(2);
        var announcer = _factory.Services.GetServices<IHostedService>().OfType<EventAnnouncer>().Single();
        while (await announcer.AnnounceAsync(CancellationToken.None))
        {
        }

        var sent = (await _receiver.WaitAsync(2)).Select(r => JsonSerializer.Deserialize<RomdexEvent>(r.Body, RomdexJson.Options)!).ToList();
        var filed = sent.Single(e => e.Type == EventTypes.DownloadFiled).DataAs<DownloadEventData>()!.Download;
        Assert.Equal("pond", filed.Name);
        Assert.Equal("Pixel Pond (USA)", Assert.Single(filed.Dumps).Dump.Name);
        Assert.Equal("junk", sent.Single(e => e.Type == EventTypes.DownloadNeedsReview).DataAs<DownloadEventData>()!.Download.Name);

        // Looking again finds nothing new to send.
        Assert.False(await announcer.AnnounceAsync(CancellationToken.None));
        await Task.Delay(500);
        Assert.Equal(2, _receiver.Received.Count);
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
        // Each is held for a reason Review sorts it by.
        using (var scope = _factory.Services.CreateScope())
        {
            var held = scope.ServiceProvider.GetRequiredService<Data.RomdexDbContext>().Downloads.ToDictionary(d => d.Name, d => d.HeldFor);
            Assert.Equal(HoldReason.Unmatched, held["junk"]);
            Assert.Equal(HoldReason.Incomplete, held["harbor"]);
        }

        // Rejected, its files are gone.
        junk = await _api.RejectDownloadAsync(junk.Id);
        Assert.Equal(DownloadState.Rejected, junk.State);
        Assert.False(Directory.Exists(Path.Combine(_staging, "downloads", junk.Id.ToString())));
        Assert.Equal(1, (await _api.ListDownloadsAsync()).Waiting);
    }

    [Fact]
    public async Task Alternatives_are_filed_beside_what_you_have_or_thrown_away_as_the_system_says()
    {
        var usa = _dats.Dump(Handheld, "Tide Bell (USA)", [".gb"]);
        var europe = _dats.Dump(Handheld, "Tide Bell (Europe)", [".gb"], cloneOf: "Tide Bell (USA)");
        var japan = _dats.Dump(Handheld, "Tide Bell (Japan)", [".gb"], cloneOf: "Tide Bell (USA)");
        _dats.WriteDats();
        _dats.Put("Handheld/Tide Bell (USA).gb", usa[0].Data);
        await SetUpAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal([StorageFormat.Keep, StorageFormat.Loose, StorageFormat.Zip], await _api.ListStorageFormatsAsync(system.Id));
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SetFilingAsync(system.Id, new FilingSettings(Storage: StorageFormat.Chd)));

        await _api.SetFilingAsync(system.Id, new FilingSettings(LibraryLayout.DatName, StorageFormat.Zip, AlternativePolicy.Discard));
        _plugin.Drop("japan", ("tide.gb", japan[0].Data));
        var item = Assert.Single(await ArriveAsync(1));
        Assert.Equal(DownloadState.Discarded, item.State);
        Assert.True(Assert.Single(item.Dumps).Alternative);

        await _api.SetFilingAsync(system.Id, new FilingSettings(LibraryLayout.DatName, StorageFormat.Zip, AlternativePolicy.File));
        _plugin.Drop("europe", ("tide.gb", europe[0].Data));
        item = Assert.Single(await ArriveAsync(1));
        Assert.Equal(DownloadState.Filed, item.State);
        Assert.True(Assert.Single(item.Dumps).Alternative);
        Assert.Equal([$"{Handheld}/Tide Bell (Europe).zip"], item.Dumps[0].Filed);
    }

    [Fact]
    public async Task Review_deletes_a_file_it_lists_and_what_the_library_has_of_an_incomplete_dump()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        var disc = _dats.Dump(Disc, "Harbor Lights (Japan)", [".cue", ".bin", ".bin"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        _dats.Put("odd/stray.gb", [9, 9, 9]);
        _dats.Put($"Harbor/{disc[0].Name}", disc[0].Data);
        _dats.Put($"Harbor/{disc[1].Name}", disc[1].Data);
        await SetUpAsync();

        var stray = Assert.Single((await _api.ListUnmatchedAsync()).Items);
        var deleted = await _api.DeleteLibraryFileAsync(stray.FileId);
        Assert.Equal(DownloadState.Rejected, deleted.State);
        Assert.False(File.Exists(Path.Combine(_dats.Roms, "odd", "stray.gb")));
        Assert.Empty((await _api.ListUnmatchedAsync()).Items);

        // What matches the catalogue isn't Review's to delete.
        var owned = (await _api.GetTitleAsync((await _api.SearchCatalogueAsync("pixel pond", null, null)).Items[0].Id))!.Dumps[0].Files[0].Library[0];
        Assert.Contains("matches the catalogue", (await Assert.ThrowsAsync<RomdexApiException>(() => _api.DeleteLibraryFileAsync(owned.FileId))).Message);

        var harbor = Assert.Single((await _api.ListIncompleteAsync()).Items);
        Assert.Equal([disc[2].Name], harbor.Missing);
        Assert.Equal(2, (await _api.DeleteIncompleteAsync(harbor.Dump.Id)).Count);
        Assert.False(Directory.EnumerateFiles(Path.Combine(_dats.Roms, "Harbor")).Any());
        Assert.Empty((await _api.ListIncompleteAsync()).Items);
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
        Assert.Equal(HoldReason.NotAskedFor, item.HeldFor);
        Assert.Equal(item.Id, Assert.Single((await _api.ListCouldntFetchAsync()).Arrivals).Id);
        Assert.Equal(1, (await _api.GetReviewSummaryAsync()).CouldntFetch);

        // Searching again says which offer was turned away before, and why.
        _plugin.Drop("not it", ("q.gb", quarry[0].Data));
        Assert.Equal(item.Message, Assert.Single((await _api.SearchDownloadsAsync(wanted.Id)).Offers).TurnedAway);

        // Kept anyway, it's filed as what it is.
        await _api.AcceptDownloadAsync(item.Id);
        item = await WaitAsync(item.Id);
        Assert.Equal(DownloadState.Filed, item.State);
        Assert.Equal("Quiet Quarry (USA)", Assert.Single(item.Dumps).Dump.Name);
        Assert.Empty((await _api.ListCouldntFetchAsync()).Arrivals);

        _plugin.Drop("it", ("l.gb", lantern[0].Data));
        offer = (await _api.SearchDownloadsAsync(wanted.Id)).Offers.Single(o => o.Candidate.CandidateId == "it");
        Assert.Null(offer.TurnedAway);
        item = await WaitAsync((await _api.StartDownloadAsync(new StartDownload(wanted.Id, offer.PluginId, offer.Candidate))).Id);
        Assert.Equal(DownloadState.Filed, item.State);
    }

    [Fact]
    public async Task Getting_a_game_tries_offer_after_offer_until_one_is_filed()
    {
        var lantern = _dats.Dump(Handheld, "Lantern Run (World)", [".gb"]);
        var quarry = _dats.Dump(Handheld, "Quiet Quarry (USA)", [".gb"]);
        _dats.WriteDats();
        await SetUpAsync();
        var wanted = (await _api.ListAllTitlesAsync()).Items.Single(t => t.Name == "Lantern Run");

        // The plugin says the wrong one is surer, so it's tried first; it's another game, so the next is tried.
        _plugin.Drop("not it", ("q.gb", quarry[0].Data));
        _plugin.Drop("it", ("l.gb", lantern[0].Data));
        _plugin.Scores["not it"] = 0.99;
        var fetch = await _api.FetchTitleAsync(wanted.Id);
        var request = await SettleAsync(Assert.Single(fetch.Requests!).Id);
        Assert.Equal(RequestState.Done, request.State);
        Assert.Equal(["not it", "it"], request.Attempts.Select(a => a.Name));
        // The other game it kept to accept is thrown away once the right one is in.
        Assert.Equal(DownloadState.Rejected, request.Attempts[0].State);
        Assert.Equal(DownloadState.Filed, request.Attempts[1].State);
        Assert.Equal(TitleStatus.Owned, (await _api.GetTitleAsync(wanted.Id))!.Status);

        // Had, in every region wanted: nothing more to get.
        fetch = await _api.FetchTitleAsync(wanted.Id);
        Assert.Empty(fetch.Requests!);
        Assert.Equal("You have it in every region you want.", Assert.Single(fetch.Problems));
    }

    [Fact]
    public async Task A_request_out_of_offers_waits_to_be_accepted_tried_again_or_dismissed()
    {
        var lantern = _dats.Dump(Handheld, "Lantern Run (World)", [".gb"]);
        var quarry = _dats.Dump(Handheld, "Quiet Quarry (USA)", [".gb"]);
        _dats.WriteDats();
        await SetUpAsync();
        var wanted = (await _api.ListAllTitlesAsync()).Items.Single(t => t.Name == "Lantern Run");

        _plugin.Drop("not it", ("q.gb", quarry[0].Data));
        var request = await SettleAsync(Assert.Single((await _api.FetchTitleAsync(wanted.Id)).Requests!).Id);
        Assert.Equal(RequestState.Failed, request.State);
        Assert.Contains("It isn't what was asked for", request.Message);
        Assert.Equal("not it", request.Acceptable!.Name);
        var waiting = await _api.ListCouldntFetchAsync();
        Assert.Equal(request.Id, Assert.Single(waiting.Requests).Id);
        Assert.Empty(waiting.Arrivals);
        Assert.Equal(1, (await _api.GetReviewSummaryAsync()).CouldntFetch);

        // Looking again with nothing new fails again; with something new, it's fetched.
        _plugin.Drop("not it", ("q.gb", quarry[0].Data));
        Assert.Equal(RequestState.Failed, (await _api.TryRequestAgainAsync(request.Id)).State);
        _plugin.Drop("it", ("l.gb", lantern[0].Data));
        await _api.TryRequestAgainAsync(request.Id);
        request = await SettleAsync(request.Id);
        Assert.Equal(RequestState.Done, request.State);
        Assert.Empty((await _api.ListCouldntFetchAsync()).Requests);
    }

    [Fact]
    public async Task Getting_a_game_asks_for_each_wanted_region_it_came_out_in()
    {
        var europe = _dats.Dump(Handheld, "Tide Bell (Europe)", [".gb"]);
        var usa = _dats.Dump(Handheld, "Tide Bell (USA)", [".gb"], cloneOf: "Tide Bell (Europe)");
        _dats.Dump(Handheld, "Tide Bell (Japan)", [".gb"], cloneOf: "Tide Bell (Europe)");
        _dats.WriteDats();
        await SetUpAsync();
        await _api.SetDefaultPreferencesAsync(SystemPreferences.Default with { Wanted = ["Europe", "USA"], Fallback = ["Japan"] });
        var title = Assert.Single((await _api.ListAllTitlesAsync()).Items);

        _plugin.Drop("eu", ("e.gb", europe[0].Data));
        _plugin.DumpNames["eu"] = "Tide Bell (Europe)";
        _plugin.Drop("us", ("u.gb", usa[0].Data));
        _plugin.DumpNames["us"] = "Tide Bell (USA)";
        var fetch = await _api.FetchTitleAsync(title.Id);
        Assert.Equal([["Europe"], ["USA"]], fetch.Requests!.Select(r => r.Regions));
        foreach (var request in fetch.Requests!)
            Assert.Equal(RequestState.Done, (await SettleAsync(request.Id)).State);
        var got = (await _api.GetTitleAsync(title.Id))!;
        Assert.Equal(RegionFit.Best, got.Fit);
        Assert.Equal(DumpStatus.Good, got.Dumps.Single(d => d.Name == "Tide Bell (Europe)").Status);
        Assert.Equal(DumpStatus.Good, got.Dumps.Single(d => d.Name == "Tide Bell (USA)").Status);
    }

    [Fact]
    public async Task Replacing_a_bad_dump_deletes_it_once_the_good_copy_is_filed()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", [.. pond[0].Data[..^1], 0]);
        await SetUpAsync();

        var bad = Assert.Single((await _api.ListBadDumpsAsync()).Items);
        _plugin.Drop("good pond", ("p.gb", pond[0].Data));
        var fetch = await _api.FetchTitleAsync(bad.LooksLike!.TitleId!.Value, bad.LooksLike.Id, [bad.FileId]);
        var item = await WaitAsync(fetch.Download!.Id);
        Assert.Equal(DownloadState.Filed, item.State);
        Assert.Contains("replacing gb/Pixel Pond (USA).gb, now deleted", item.Message);

        Assert.NotEqual("gb/Pixel Pond (USA).gb", Assert.Single(item.Dumps[0].Filed));
        Assert.False(File.Exists(Path.Combine(_dats.Roms, "gb", "Pixel Pond (USA).gb")));
        Assert.Empty((await _api.ListBadDumpsAsync()).Items);
        Assert.Equal(TitleStatus.Owned, (await _api.GetTitleAsync(bad.LooksLike.TitleId.Value))!.Status);
        // The bad copy is noted on Downloads as replaced.
        Assert.Contains((await _api.ListDownloadsAsync()).Items, d => d.State == DownloadState.Rejected && d.Message == "Replaced by good pond, and deleted.");
    }

    [Fact]
    public async Task Retrievers_are_asked_for_dumps_in_tier_order_and_never_for_a_never_region()
    {
        _dats.Dump(Handheld, "Tide Bell (Europe)", [".gb"]);
        _dats.Dump(Handheld, "Tide Bell (USA)", [".gb"], cloneOf: "Tide Bell (Europe)");
        _dats.Dump(Handheld, "Tide Bell (Japan)", [".gb"], cloneOf: "Tide Bell (Europe)");
        var korea = _dats.Dump(Handheld, "Tide Bell (Korea)", [".gb"], cloneOf: "Tide Bell (Europe)");
        _dats.WriteDats();
        await SetUpAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        await _api.SetSystemPreferencesAsync(system.Id, SystemPreferences.Default with { Wanted = ["Japan"], Fallback = ["USA", "Europe"], Never = ["Korea"] });
        var title = Assert.Single((await _api.ListAllTitlesAsync()).Items);

        await _api.SearchDownloadsAsync(title.Id);
        Assert.Equal(["Tide Bell (Japan)", "Tide Bell (USA)", "Tide Bell (Europe)"], _plugin.Searches[^1].Target.Dumps.Select(d => d.Name));

        // An offer the plugin says is the Korean dump isn't fetched.
        _plugin.Drop("korea", ("k.gb", korea[0].Data));
        _plugin.DumpNames["korea"] = "Tide Bell (Korea)";
        var fetch = await _api.FetchTitleAsync(title.Id);
        Assert.Null(fetch.Download);
        Assert.Contains("Nothing offered is of Japan.", fetch.Problems);

        // Asked for by name, it's fetched all the same.
        var korean = (await _api.GetTitleAsync(title.Id))!.Dumps.Single(d => d.Name == "Tide Bell (Korea)");
        await _api.SearchDownloadsAsync(title.Id, korean.Id);
        Assert.Contains("Tide Bell (Korea)", _plugin.Searches[^1].Target.Dumps.Select(d => d.Name));
    }

    [Fact]
    public void Offers_are_ranked_by_the_dump_wanted_then_verified_then_how_sure_the_plugin_is()
    {
        var target = new RetrievalTarget(Handheld, "Tide Bell",
            [new TargetDump("Tide Bell (Europe)", ["Europe"], null, []), new TargetDump("Tide Bell (USA)", ["USA"], null, [])]);
        DownloadOffer Offer(string id, string? dump = null, bool? verified = null, double? score = null) =>
            new(Guid.Empty, "p", new Candidate(id, id, "s", DumpName: dump, Verified: verified, Score: score));
        var ranked = RetrievalSearch.Rank([
            Offer("unnamed, sure", score: 0.99),
            Offer("usa", dump: "Tide Bell (USA)"),
            Offer("europe, unsure", dump: "Tide Bell (Europe)", score: 0.3),
            Offer("europe, verified", dump: "Tide Bell (Europe)", verified: true, score: 0.3),
            Offer("unnamed, unsure", score: 0.1),
        ], target);
        Assert.Equal(["europe, verified", "europe, unsure", "usa", "unnamed, sure", "unnamed, unsure"], ranked.Select(o => o.Candidate.CandidateId));
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

    [Fact]
    public async Task The_library_is_formatted_to_its_layout_and_arrivals_follow_it()
    {
        var pond = _dats.Dump(Handheld, "Pixel Pond (USA)", [".gb"]);
        var quarry = _dats.Dump(Handheld, "Quiet Quarry (USA)", [".gb", ".gbc"]);
        var boot = _dats.Dump(Handheld, "[BIOS] Handheld Boot (World)", [".bin"]);
        var maze = _dats.Dump(Handheld, "Moss Maze (Japan)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("romm/Pixel Pond (USA).gb", pond[0].Data);
        _dats.Put("romm/Quiet Quarry (USA)/" + quarry[0].Name, quarry[0].Data);
        _dats.Put("romm/Quiet Quarry (USA)/" + quarry[1].Name, quarry[1].Data);
        _dats.Put("romm/" + boot[0].Name, boot[0].Data);
        _dats.Put("romm/stray.gb", [1, 2, 3]);
        await SetUpAsync();
        var handheld = (await _api.ListSystemsAsync()).Single(s => s.Name == Handheld);

        // It's in RomM's layout already, as the library is by default.
        var detected = await _api.DetectLibraryFormatAsync();
        Assert.Equal(new LibraryFormat(LibraryLayout.Romm, "", "", true), detected.Format);
        Assert.Equal((1, 1), (detected.Systems, detected.Fitting));
        Assert.Equal(3, (await _api.PreviewLibraryFormatAsync()).InPlace);

        // An ES-DE layout, as EmuDeck keeps it: games in roms, BIOS files all together in bios.
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SetLibraryFormatAsync(new LibraryFormat(LibraryLayout.EsDe, "../roms")));
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SetLibraryFormatAsync(new LibraryFormat(LibraryLayout.Custom)));
        var format = await _api.SetLibraryFormatAsync(new LibraryFormat(LibraryLayout.EsDe, "/roms/", "bios", BiosBySystem: false));
        Assert.Equal(new LibraryFormat(LibraryLayout.EsDe, "roms", "bios", false), format);
        var preview = await _api.PreviewLibraryFormatAsync();
        Assert.Equal((3, 0), (preview.Checked, preview.InPlace));
        Assert.Equal(["bios/" + boot[0].Name], preview.Moves.Single(m => m.Dump.Name == boot[0].Name.Replace(".bin", "")).To);
        Assert.Equal([$"roms/es/Quiet Quarry (USA)/{quarry[0].Name}", $"roms/es/Quiet Quarry (USA)/{quarry[1].Name}"],
            preview.Moves.Single(m => m.Dump.Name == "Quiet Quarry (USA)").To);
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "romm", "Pixel Pond (USA).gb")));

        // Formatting moves them, leaves what matches nothing, and the library follows without hashing anything again.
        await RunAsync(await _api.FormatLibraryAsync());
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "roms", "es", "Pixel Pond (USA).gb")));
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "roms", "es", "Quiet Quarry (USA)", quarry[1].Name)));
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "bios", boot[0].Name)));
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "romm", "stray.gb")));
        Assert.False(Directory.Exists(Path.Combine(_dats.Roms, "romm", "Quiet Quarry (USA)")));
        preview = await _api.PreviewLibraryFormatAsync(handheld.Id);
        Assert.Equal((3, 3), (preview.Checked, preview.InPlace));
        Assert.Equal(new LibraryFormat(LibraryLayout.EsDe, "roms", "bios", false), (await _api.DetectLibraryFormatAsync()).Format);

        // What arrives from a retriever goes where the layout keeps the rest.
        _plugin.Drop("maze", ("maze.gb", maze[0].Data));
        Assert.Equal(DownloadState.Filed, Assert.Single(await ArriveAsync(1)).State);
        Assert.Equal("roms/es", _worker.Filed.Last().Folder);
        Assert.True(File.Exists(Path.Combine(_dats.Roms, "roms", "es", "Moss Maze (Japan).gb")));

        // A system can keep a folder of its own naming.
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.SetFilingAsync(handheld.Id, new FilingSettings(LibraryLayout.Custom)));
        await _api.SetFilingAsync(handheld.Id, new FilingSettings(LibraryLayout.Custom, Folder: "Game Boy"));
        preview = await _api.PreviewLibraryFormatAsync(handheld.Id);
        Assert.Contains(["roms/Game Boy/Moss Maze (Japan).gb"], preview.Moves.Select(m => m.To));
        Assert.DoesNotContain(preview.Moves, m => m.Dump.Name.StartsWith("[BIOS]"));
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

    /// <summary>Waits for a request to be done, or out of offers.</summary>
    private async Task<RequestInfo> SettleAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            var request = (await _api.GetRequestAsync(id))!;
            if (request.State != RequestState.Active)
                return request;
            Assert.True(DateTime.UtcNow < deadline, $"Request {id}: {request.State} {request.Message}");
            await Task.Delay(200);
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
