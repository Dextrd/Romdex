using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Romdex.Client;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>
/// Review through the API: each tab's list, and deferring, hiding, linking and keeping. Deleting needs the igir worker,
/// so it's with the download tests. Every dump is made up.
/// </summary>
public sealed class ReviewApiTests : IDisposable
{
    private const string Handheld = "Test - Handheld";
    private const string Disc = "Test - Disc";

    private readonly MadeUpDats _dats = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public ReviewApiTests()
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
    public async Task Each_file_is_under_the_one_tab_that_says_what_can_be_done_about_it()
    {
        var named = _dats.Dump(Handheld, "Name Only (USA)", [".gb"]);
        var drift = _dats.Dump(Handheld, "Drift Racer (USA)", [".gb"]);
        var disc = _dats.Dump(Disc, "Half Track (Europe)", [".cue", ".bin", ".bin"]);
        _dats.WriteDats();
        _dats.Put("Handheld/Name Only (USA).gb", [.. named[0].Data[..^1], 0]);
        _dats.Put("Handheld/drift.gb", drift[0].Data);
        _dats.Put("Handheld/homebrew.gb", [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dats.Roms, "Handheld", "Drift Racer (USA).zip"), [80, 75, 3, 4, 1, 2]);
        _dats.Put($"Disc/Half Track (Europe)/{disc[0].Name}", disc[0].Data);
        _dats.Put($"Disc/Half Track (Europe)/{disc[1].Name}", disc[1].Data);
        await RefreshAndScanAsync();
        _dats.Redump(Handheld, "Drift Racer (USA)");
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());

        var summary = await _api.GetReviewSummaryAsync();
        Assert.Equal((1, 1, 1, 1, 1, 0), (summary.BadDumps, summary.Matching, summary.Outdated, summary.Corrupted, summary.Incomplete, summary.CouldntFetch));
        Assert.Null(summary.NoTool);

        var bad = Assert.Single((await _api.ListBadDumpsAsync()).Items);
        Assert.Equal(("Handheld/Name Only (USA).gb", "Name Only (USA)"), (bad.Path, bad.LooksLike!.Name));
        Assert.Equal("Handheld/homebrew.gb", Assert.Single((await _api.ListUnmatchedAsync()).Items).Path);

        var outdated = Assert.Single((await _api.ListOutdatedAsync()).Items);
        Assert.Equal("Handheld/drift.gb", outdated.Path);
        Assert.Equal(("Drift Racer (USA)", "20261101-000000"), (outdated.Was!.Dump, outdated.Was.DatVersion));
        Assert.Equal("Drift Racer (USA)", outdated.Was.Now!.Name);

        // A damaged archive, with the dump its name suggests to fetch again.
        var corrupted = Assert.Single((await _api.ListCorruptedAsync()).Items);
        Assert.Equal((ReadFailure.Damaged, "Drift Racer (USA)"), (corrupted.Failure!.Value, corrupted.LooksLike!.Name));

        var incomplete = Assert.Single((await _api.ListIncompleteAsync()).Items);
        Assert.Equal(("Half Track (Europe)", "Half Track", Disc), (incomplete.Dump.Name, incomplete.Title, incomplete.System));
        Assert.Equal([disc[2].Name], incomplete.Missing);
        Assert.Equal(2, incomplete.Have.Count);
    }

    [Fact]
    public async Task A_deferred_file_waits_aside_still_matched_and_a_hidden_one_is_gone_until_put_back()
    {
        var later = _dats.Dump(Handheld, "Later Listed (USA)", [".gb"]);
        var hopper = _dats.Dump(Handheld, "Hopper Quest (USA)", [".gb"]);
        _dats.WriteDats();
        byte[] homebrew = [1, 2, 3];
        _dats.Put("Handheld/homebrew.gb", homebrew);
        _dats.Put("Handheld/odd.gb", [4, 5, 6]);
        _dats.Put("Handheld/hopper.gb", hopper[0].Data);
        await RefreshAndScanAsync();
        var unmatched = (await _api.ListUnmatchedAsync()).Items.ToDictionary(f => f.Path);

        await _api.DeferFileAsync(unmatched["Handheld/homebrew.gb"].FileId);
        await _api.HideFileAsync(unmatched["Handheld/odd.gb"].FileId);
        Assert.Empty((await _api.ListUnmatchedAsync()).Items);
        Assert.Equal("Handheld/homebrew.gb", Assert.Single((await _api.ListSetAsideAsync()).Items).Path);
        Assert.Equal("Handheld/odd.gb", Assert.Single((await _api.ListSetAsideAsync(hidden: true)).Items).Path);
        var summary = await _api.GetReviewSummaryAsync();
        Assert.Equal((0, 1, 1), (summary.Matching, summary.Deferred, summary.Hidden));

        // A matched file isn't Review's to set aside.
        var system = Assert.Single(await _api.ListSystemsAsync());
        var owned = (await _api.GetTitleAsync((await TitleAsync(system.Id, "Hopper Quest")).Id))!.Dumps[0].Files[0].Library[0];
        Assert.Contains("matches the catalogue", (await Assert.ThrowsAsync<RomdexApiException>(() => _api.HideFileAsync(owned.FileId))).Message);

        // Deferred, it's still matched: a DAT that lists it later finds it.
        _dats.Redump(Handheld, "Later Listed (USA)", data: homebrew);
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Later Listed")).Status);

        await _api.PutBackFileAsync(unmatched["Handheld/odd.gb"].FileId);
        Assert.Equal("Handheld/odd.gb", Assert.Single((await _api.ListUnmatchedAsync()).Items).Path);
        Assert.Empty((await _api.ListSetAsideAsync(hidden: true)).Items);
    }

    [Fact]
    public async Task Linking_says_what_a_file_is_as_a_dump_or_as_a_version_no_DAT_lists()
    {
        var named = _dats.Dump(Handheld, "Name Only (USA)", [".gb"]);
        _dats.Dump(Handheld, "Sky Puzzle (World)", [".gb"]);
        var drift = _dats.Dump(Handheld, "Drift Racer (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/Name Only (USA).gb", [.. named[0].Data[..^1], 0]);
        _dats.Put("Handheld/sky-es.gb", [1, 2, 3]);
        _dats.Put("Handheld/drift.gb", drift[0].Data);
        await RefreshAndScanAsync();
        _dats.Redump(Handheld, "Drift Racer (USA)");
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());
        var system = Assert.Single(await _api.ListSystemsAsync());

        // Approved as the dump it's named as.
        var bad = Assert.Single((await _api.ListBadDumpsAsync()).Items);
        var approved = await _api.LinkFileAsync(new LinkFile(bad.FileId, DumpId: bad.LooksLike!.Id));
        Assert.Equal((ManualMatchReason.Approved, "Name Only (USA)"), (approved.Reason, approved.Dump!.Name));
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Name Only")).Status);
        Assert.Empty((await _api.ListBadDumpsAsync()).Items);

        // Kept as the dump it was before the DAT changed.
        var outdated = Assert.Single((await _api.ListOutdatedAsync()).Items);
        var kept = await _api.LinkFileAsync(new LinkFile(outdated.FileId, DumpId: outdated.Was!.Now!.Id));
        Assert.Equal(ManualMatchReason.KeptOutdated, kept.Reason);
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Drift Racer")).Status);

        // A translation: a version of the game, which doesn't make it owned.
        var sky = await TitleAsync(system.Id, "Sky Puzzle");
        var translation = Assert.Single((await _api.ListUnmatchedAsync()).Items);
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.LinkFileAsync(new LinkFile(translation.FileId, TitleId: sky.Id)));
        var version = await _api.LinkFileAsync(new LinkFile(translation.FileId, TitleId: sky.Id, Name: "Sky Puzzle (Spanish translation)"));
        Assert.Equal((ManualMatchReason.Searched, (DumpRef?)null, sky.Id), (version.Reason, version.Dump, version.TitleId));
        Assert.Empty((await _api.ListUnmatchedAsync()).Items);
        Assert.Equal(TitleStatus.Missing, (await TitleAsync(system.Id, "Sky Puzzle")).Status);
        Assert.Equal(version.Id, Assert.Single(await _api.ListLinksAsync(sky.Id)).Id);
        Assert.Equal(3, (await _api.ListLinksAsync()).Count);

        // Unlinked, it's back where it was.
        await _api.UnlinkFileAsync(approved.Id);
        Assert.Equal(TitleStatus.Bad, (await TitleAsync(system.Id, "Name Only")).Status);
        Assert.Single((await _api.ListBadDumpsAsync()).Items);
    }

    [Fact]
    public async Task Each_tab_sorts_as_asked()
    {
        _dats.Dump(Handheld, "Anything (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/a/zebra.gb", new byte[300]);
        _dats.Put("Handheld/b/apple.gb", new byte[100]);
        _dats.Put("Handheld/c/mango.gb", new byte[200]);
        await RefreshAndScanAsync();

        Assert.Equal(["Handheld/a/zebra.gb", "Handheld/b/apple.gb", "Handheld/c/mango.gb"], (await _api.ListUnmatchedAsync()).Items.Select(f => f.Path));
        Assert.Equal(["apple.gb", "mango.gb", "zebra.gb"], (await _api.ListUnmatchedAsync(sort: "name")).Items.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal([300L, 200, 100], (await _api.ListUnmatchedAsync(sort: "size", descending: true)).Items.Select(f => f.Size));
        // Paged, the order holds across pages.
        Assert.Equal("mango.gb", Path.GetFileName(Assert.Single((await _api.ListUnmatchedAsync(skip: 1, take: 1, sort: "name")).Items).Path));
    }

    [Fact]
    public async Task An_incomplete_dump_kept_as_it_is_counts_as_whole_until_its_let_go()
    {
        var disc = _dats.Dump(Disc, "Half Track (Europe)", [".cue", ".bin", ".bin"]);
        _dats.WriteDats();
        _dats.Put($"Disc/Half Track (Europe)/{disc[0].Name}", disc[0].Data);
        _dats.Put($"Disc/Half Track (Europe)/{disc[1].Name}", disc[1].Data);
        await RefreshAndScanAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());

        var dump = Assert.Single((await _api.ListIncompleteAsync()).Items).Dump;
        await _api.KeepIncompleteAsync(dump.Id);
        Assert.Empty((await _api.ListIncompleteAsync()).Items);
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Half Track")).Status);
        await Assert.ThrowsAsync<RomdexApiException>(() => _api.KeepIncompleteAsync(dump.Id));

        await _api.UnkeepIncompleteAsync(dump.Id);
        Assert.Single((await _api.ListIncompleteAsync()).Items);
        Assert.Equal(TitleStatus.Bad, (await TitleAsync(system.Id, "Half Track")).Status);
    }

    private async Task<TitleSummary> TitleAsync(Guid systemId, string name) =>
        Assert.Single((await _api.ListTitlesAsync(systemId)).Items, t => t.Name == name);

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
