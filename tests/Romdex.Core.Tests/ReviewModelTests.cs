using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romdex.Client;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>
/// What Review stands on, below the API: links people make, matches remembered across DATs, hidden and deferred files,
/// dumps kept without some files, and why a file couldn't be read. Every dump is made up.
/// </summary>
public sealed class ReviewModelTests : IDisposable
{
    private const string Handheld = "Test - Handheld";
    private const string Disc = "Test - Disc";

    private readonly MadeUpDats _dats = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public ReviewModelTests()
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
    public async Task A_match_a_new_DAT_takes_away_is_remembered_with_the_version_it_was_in()
    {
        var files = _dats.Dump(Handheld, "Drift Racer (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/drift.gb", files[0].Data);
        await RefreshAndScanAsync();

        var past = Assert.Single(await QueryAsync(db => db.PastMatches.ToListAsync()));
        Assert.Equal(("Drift Racer (USA)", "Drift Racer (USA).gb", "20261101-000000"), (past.DumpName, past.FileName, past.DatVersion));
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(files[0].Data)), past.Sha1);

        // The DAT redumps it: the file matches nothing now, but what it matched is still known.
        _dats.Redump(Handheld, "Drift Racer (USA)");
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());

        Assert.Empty(await QueryAsync(db => db.LibraryMatches.ToListAsync()));
        past = Assert.Single(await QueryAsync(db => db.PastMatches.ToListAsync()));
        Assert.Equal("20261101-000000", past.DatVersion);
        Assert.Contains((await _api.ListUnknownAsync()).Items, u => u.Path == "Handheld/drift.gb");

        // Once the library no longer has the bytes, they're forgotten.
        File.Delete(Path.Combine(_dats.Roms, "Handheld", "drift.gb"));
        _dats.Put("Handheld/other.gb", [1, 2, 3]);
        await RunAsync(await _api.ScanLibraryAsync());
        Assert.Empty(await QueryAsync(db => db.PastMatches.ToListAsync()));
    }

    [Fact]
    public async Task A_link_to_a_dump_owns_it_until_the_DAT_matches_the_bytes_itself()
    {
        var real = _dats.Dump(Handheld, "Name Only (USA)", [".gb"]);
        _dats.Dump(Handheld, "Later Listed (USA)", [".gb"]);
        _dats.WriteDats();
        byte[] modified = [.. real[0].Data[..^1], 0];
        _dats.Put("Handheld/Name Only (USA).gb", modified);
        await RefreshAndScanAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal(TitleStatus.Bad, (await TitleAsync(system.Id, "Name Only")).Status);

        // Someone approves it as the dump it's named as.
        await QueryAsync(async db =>
        {
            var file = await db.DumpFiles.SingleAsync(f => f.Name == "Name Only (USA).gb");
            db.ManualMatches.Add(new ManualMatch { Sha1 = Convert.ToHexStringLower(SHA1.HashData(modified)), DumpFileId = file.Id, Name = "Name Only (USA)", Reason = ManualMatchReason.Approved });
            return await db.SaveChangesAsync();
        });
        await MatchAsync();
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Name Only")).Status);
        Assert.NotNull(Assert.Single(await QueryAsync(db => db.LibraryMatches.ToListAsync())).ManualMatchId);
        Assert.DoesNotContain((await _api.ListUnknownAsync()).Items, u => u.Path == "Handheld/Name Only (USA).gb");
        // A link is no DAT match: nothing is remembered from it.
        Assert.Empty(await QueryAsync(db => db.PastMatches.ToListAsync()));

        // A DAT that lists the bytes wins over the link.
        _dats.Redump(Handheld, "Later Listed (USA)", data: modified);
        _dats.WriteDats("20261108-000000");
        await RunAsync(await _api.RefreshCatalogueAsync());
        var match = Assert.Single(await QueryAsync(db => db.LibraryMatches.Include(m => m.DumpFile).ToListAsync()));
        Assert.Null(match.ManualMatchId);
        Assert.Equal("Later Listed (USA).gb", match.DumpFile!.Name);
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Later Listed")).Status);
    }

    [Fact]
    public async Task Hidden_files_are_never_matched_and_neither_they_nor_deferred_ones_are_listed()
    {
        var hopper = _dats.Dump(Handheld, "Hopper Quest (USA)", [".gb"]);
        var named = _dats.Dump(Handheld, "Name Only (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("Handheld/hopper.gb", hopper[0].Data);
        _dats.Put("Handheld/Name Only (USA).gb", [.. named[0].Data[..^1], 0]);
        _dats.Put("Handheld/homebrew.gb", [1, 2, 3]);
        await RefreshAndScanAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());

        await QueryAsync(async db =>
        {
            foreach (var file in await db.LibraryFiles.ToListAsync())
            {
                if (file.Path is "Handheld/hopper.gb" or "Handheld/Name Only (USA).gb")
                    file.HiddenAt = DateTime.UtcNow;
                if (file.Path == "Handheld/homebrew.gb")
                    file.DeferredAt = DateTime.UtcNow;
            }
            return await db.SaveChangesAsync();
        });
        await MatchAsync();

        // Hidden: as if it weren't there, so the game is missing, not owned or bad.
        Assert.Equal(TitleStatus.Missing, (await TitleAsync(system.Id, "Hopper Quest")).Status);
        Assert.Equal(TitleStatus.Missing, (await TitleAsync(system.Id, "Name Only")).Status);
        Assert.Empty((await _api.ListUnknownAsync()).Items);
    }

    [Fact]
    public async Task A_dump_kept_without_some_files_is_whole_until_others_go_too()
    {
        var disc = _dats.Dump(Disc, "Half Track (Europe)", [".cue", ".bin", ".bin"]);
        _dats.WriteDats();
        _dats.Put($"Disc/Half Track (Europe)/{disc[0].Name}", disc[0].Data);
        _dats.Put($"Disc/Half Track (Europe)/{disc[1].Name}", disc[1].Data);
        await RefreshAndScanAsync();
        var system = Assert.Single(await _api.ListSystemsAsync());
        Assert.Equal(TitleStatus.Bad, (await TitleAsync(system.Id, "Half Track")).Status);

        await QueryAsync(async db =>
        {
            db.KeptIncompletes.Add(new KeptIncomplete { DumpId = (await db.Dumps.SingleAsync()).Id, Missing = disc[2].Name });
            return await db.SaveChangesAsync();
        });
        await MatchAsync();
        Assert.Equal(TitleStatus.Owned, (await TitleAsync(system.Id, "Half Track")).Status);

        File.Delete(Path.Combine(_dats.Roms, "Disc", "Half Track (Europe)", disc[1].Name));
        await RunAsync(await _api.ScanLibraryAsync());
        Assert.Equal(TitleStatus.Bad, (await TitleAsync(system.Id, "Half Track")).Status);
    }

    [Fact]
    public async Task A_damaged_archive_is_told_apart_from_one_a_missing_tool_cant_read()
    {
        _dats.Dump(Handheld, "Anything (USA)", [".gb"]);
        _dats.WriteDats();
        File.WriteAllBytes(Path.Combine(_dats.Roms, "damaged.zip"), [80, 75, 3, 4, 1, 2]);
        await RefreshAndScanAsync();

        var file = Assert.Single(await QueryAsync(db => db.LibraryFiles.ToListAsync()));
        Assert.NotNull(file.Error);
        Assert.Equal(ReadFailure.Damaged, file.Failure);
    }

    private async Task<TitleSummary> TitleAsync(Guid systemId, string name) =>
        Assert.Single((await _api.ListTitlesAsync(systemId)).Items, t => t.Name == name);

    private async Task<T> QueryAsync<T>(Func<RomdexDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RomdexDbContext>());
    }

    private async Task MatchAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryMatcher>().MatchAsync(CancellationToken.None);
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
