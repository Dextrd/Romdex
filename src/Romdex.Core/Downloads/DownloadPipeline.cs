using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Core.Data;
using DownloadState = Romdex.Core.Data.DownloadState;
using Romdex.Core.Library;
using Romdex.Core.Apps;
using Romdex.Core.Catalogue;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Downloads;

/// <summary>
/// Takes one staged item to the library: identifies it by hash, decides whether it's wanted, converts it if its system
/// says to, has the igir worker file it, and records what it filed. Anything that matches nothing, is incomplete or
/// isn't what was asked for is held for review, never filed.
/// </summary>
public sealed class DownloadPipeline(
    RomdexDbContext db,
    ContainerReader reader,
    IFilingWorker worker,
    Filing filing,
    LibraryViewBuilder views,
    LibraryScanner scanner,
    LibraryMatcher matcher,
    ConnectorPlugins connectors,
    AppSync appSync,
    TaskQueue tasks,
    IOptions<RomdexOptions> options,
    ILogger<DownloadPipeline> logger)
{
    // Sheets a CHD makes up its own of, so the DAT's can't be expected: a disc in a CHD is whole when its tracks are.
    private static readonly string[] Sheets = [".cue", ".gdi"];

    /// <summary>A staged file, or a file inside a staged archive, hashed.</summary>
    private sealed record Staged(string Path, Container Container, ContentHashes? Hashes, string? Error)
    {
        /// <summary>The dump files it is, and whether each matched only without its header.</summary>
        public List<(DumpFile File, bool Headerless)> Matches { get; } = [];
    }

    /// <param name="accepted">Someone accepted it from review, so an alternative the system holds is filed anyway.</param>
    public async Task RunAsync(Download item, bool accepted, IProgress<string> progress, CancellationToken ct)
    {
        var directory = StagingDirectory(item);
        if (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
        {
            await SetAsync(item, DownloadState.Failed, "The plugin staged nothing.", ct);
            return;
        }

        progress.Report($"Identifying {item.Name}");
        var staged = await HashAsync(directory, ct);
        var (whole, partial) = await IdentifyAsync(staged, ct);
        item.FilesJson = JsonSerializer.Serialize(staged.Select(Info).ToList(), RomdexJson.Options);
        // Identified afresh each run, as the catalogue may have changed since.
        db.DownloadDumps.RemoveRange(item.Dumps);
        item.Dumps.Clear();
        await db.SaveChangesAsync(ct);

        if (whole.Count == 0)
        {
            var message = partial.Count > 0
                ? $"It's incomplete: {partial[0].Dump.Name} is missing {Names(partial[0].Missing)}."
                : staged.All(s => s.Error is not null) ? "None of its files could be read." : "It matches nothing in the catalogue.";
            await SetAsync(item, DownloadState.NeedsReview, message, ct);
            return;
        }

        foreach (var dump in whole)
            item.Dumps.Add(new DownloadDump { DumpId = dump.Id, Alternative = false });

        if (await AskedForAsync(item, ct) is { } asked && !whole.Any(d => asked.Matches(d)))
        {
            await SetAsync(item, DownloadState.NeedsReview, $"It isn't what was asked for, {asked.Name}: it's {Names(whole.Select(d => d.Name))}.", ct);
            return;
        }

        // Decide: a dump already in the library isn't filed again; another dump of a title you have is an
        // alternative, which each system files, holds or discards.
        var toFile = new List<(Dump Dump, CatalogueSystem System, FilingSettings Settings)>();
        var notes = new List<string>();
        var held = new List<string>();
        foreach (var dump in whole)
        {
            var system = (await db.Systems.FindAsync([dump.SystemId], ct))!;
            var owner = system.BiosForId is { } parentId ? (await db.Systems.FindAsync([parentId], ct))! : system;
            var settings = await filing.GetAsync(owner.Name, ct);
            var (status, alternative) = await StandingAsync(owner.Id, dump, ct);
            item.Dumps.First(d => d.DumpId == dump.Id).Alternative = alternative;
            if (status == DumpStatus.Good)
            {
                notes.Add($"{dump.Name} is already in the library");
                continue;
            }
            if (alternative && settings.Alternatives == AlternativePolicy.Discard && !accepted)
            {
                notes.Add($"{dump.Name} is another dump of a game you have, which {owner.Name} discards");
                continue;
            }
            if (alternative && settings.Alternatives == AlternativePolicy.Review && !accepted)
                held.Add(dump.Name);
            toFile.Add((dump, system, settings));
        }
        if (held.Count > 0)
        {
            await SetAsync(item, DownloadState.NeedsReview, $"{Names(held)} {(held.Count == 1 ? "is another dump" : "are other dumps")} of a game you have, " +
                "which this system holds for review. Accept to file it.", ct);
            return;
        }
        if (toFile.Count == 0)
        {
            DeleteStaging(directory);
            await SetAsync(item, DownloadState.Discarded, Sentence(notes), ct);
            return;
        }

        await db.SaveChangesAsync(ct);
        var filed = new List<string>();
        foreach (var (dump, system, settings) in toFile)
        {
            if (settings.Storage is StorageFormat.Chd or StorageFormat.Rvz)
            {
                progress.Report($"Converting {dump.Name} to {settings.Storage.ToString().ToUpperInvariant()}");
                if (await ConvertAsync(item, directory, staged, dump, settings.Storage, ct) is { } note)
                    notes.Add(note);
            }
            progress.Report($"Filing {dump.Name}");
            var mode = settings.Storage switch
            {
                StorageFormat.Loose => FilingMode.Extract,
                StorageFormat.Zip => FilingMode.Zip,
                _ => FilingMode.Move,
            };
            // igir only looks for headers inside an archive when it writes the files out of it, and moves an archive
            // whole, extras and all; either way, that dump is zipped afresh.
            if (mode == FilingMode.Move && NeedsRewriting(staged, dump))
                mode = FilingMode.Zip;
            var files = await db.DumpFiles.AsNoTracking().Where(f => f.DumpId == dump.Id).ToListAsync(ct);
            var result = await worker.FileAsync(new FileRequest(item.StagingDirectory, OneEntryDat.For(system.Name, dump, files),
                settings.Layout == LibraryLayout.Romm ? "romm" : "datName", mode), ct);
            if (result.Files.Count == 0)
                throw new FilingWorkerException($"igir filed nothing for {dump.Name}.");
            item.Dumps.First(d => d.DumpId == dump.Id).FiledPaths = string.Join('|', result.Files);
            filed.AddRange(result.Files);
            logger.LogInformation("[{Item}] Filed {Dump} as {Paths}", item.Name, dump.Name, string.Join(", ", result.Files));
        }

        progress.Report($"Recording {item.Name}");
        await scanner.ScanPathsAsync(filed, ct);
        // The scan lets go of everything it tracked, the item included.
        db.Update(item);
        await matcher.MatchAsync(ct);
        foreach (var (dump, system, _) in toFile)
            if ((await StandingAsync(system.BiosForId ?? system.Id, dump, ct)).Status != DumpStatus.Good)
                throw new FilingWorkerException($"{dump.Name} was filed but doesn't verify in the library. Look at {string.Join(", ", filed)}.");

        DeleteStaging(directory);
        notes.Insert(0, $"Filed {Names(toFile.Select(f => f.Dump.Name))}");
        await SetAsync(item, DownloadState.Filed, Sentence(notes), ct);

        // The connected app, if any, scans for it and gives its ID; a metadata plugin, if any, finds its cover.
        if (await connectors.GetActiveAsync(ct) is not null)
            appSync.Add(filed);
        await ArtLookupTask.QueueIfUsefulAsync(db, tasks, ct);
    }

    /// <summary>
    /// Converts a staged disc to CHD or RVZ, in staging, and keeps the result only if what it holds matches the DAT;
    /// else the disc is filed as it came. Returns a note for the item's message, if there's something to say.
    /// </summary>
    private async Task<string?> ConvertAsync(Download item, string directory, List<Staged> staged, Dump dump, StorageFormat format, CancellationToken ct)
    {
        var target = format == StorageFormat.Chd ? Container.Chd : Container.Rvz;
        var own = staged.Where(s => s.Matches.Any(m => m.File.DumpId == dump.Id)).ToList();
        if (own.Any(s => s.Container == target))
            return null;
        if (own.Any(s => s.Container != Container.Raw))
            return $"{dump.Name} came in an archive, so it's filed as it is rather than as {format.ToString().ToUpperInvariant()}";
        var files = await db.DumpFiles.AsNoTracking().Where(f => f.DumpId == dump.Id).ToListAsync(ct);
        var source = format == StorageFormat.Chd
            ? files.FirstOrDefault(f => Sheets.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase))
              ?? files.FirstOrDefault(f => Path.GetExtension(f.Name).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            : files.FirstOrDefault(f => Path.GetExtension(f.Name) is ".iso" or ".gcm");
        if (source is null)
            return $"{dump.Name} isn't a disc image {format.ToString().ToUpperInvariant()} can be made from, so it's filed as it is";

        // The tool reads the cue sheet's own file names, which are the DAT's, so the files take those names first, in a
        // folder of their own.
        var folder = $"convert-{dump.Id:N}";
        Directory.CreateDirectory(Path.Combine(directory, folder));
        // The worker runs as another user and writes the converted file here.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(directory, folder), (UnixFileMode)0b111_111_111);
        foreach (var file in files)
            if (own.FirstOrDefault(s => s.Matches.Any(m => m.File.Id == file.Id)) is { } match)
                File.Move(Path.Combine(directory, match.Path), Path.Combine(directory, folder, file.Name), overwrite: true);

        var kind = format.ToString().ToLowerInvariant();
        ConvertResult converted;
        IReadOnlyList<ContentHashes> contents;
        try
        {
            converted = await worker.ConvertAsync(new ConvertRequest(item.StagingDirectory, kind, $"{folder}/{source.Name}"), ct);
            contents = (await worker.ReadAsync(new ReadRequest("staging", $"{item.StagingDirectory}/{converted.File}"), ct)).Contents;
        }
        catch (FilingWorkerException e)
        {
            // The tool failed, or made something it can't read back. The originals are untouched, so they're filed; what
            // it made, beside them, goes.
            File.Delete(Path.Combine(directory, folder, Path.ChangeExtension(source.Name, "." + kind)));
            logger.LogWarning("[{Item}] {Dump} couldn't be converted to {Format}, so the original is kept: {Reason}", item.Name, dump.Name, kind, e.Message);
            return $"{dump.Name} couldn't be converted to {kind.ToUpperInvariant()}, so it's filed as it came";
        }
        var verified = files.Where(f => !(format == StorageFormat.Chd && Sheets.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase)))
            .All(f => contents.Any(c => Same(c.Sha1, c.Md5, c.Crc32, c.Size, f)));
        var convertedPath = Path.Combine(directory, converted.File);
        if (!verified)
        {
            File.Delete(convertedPath);
            logger.LogWarning("[{Item}] {Dump} as {Format} didn't match the DAT, so the original is kept", item.Name, dump.Name, kind);
            return $"{dump.Name} didn't match the DAT once converted to {kind.ToUpperInvariant()}, so it's filed as it came";
        }
        // Verified, the originals can go: only the converted file is left for igir to file.
        foreach (var file in files)
            File.Delete(Path.Combine(directory, folder, file.Name));
        return null;
    }

    public string StagingDirectory(Download item) => Path.Combine(Path.GetFullPath(options.Value.StagingPath), item.StagingDirectory);

    public static void DeleteStaging(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private async Task<List<Staged>> HashAsync(string directory, CancellationToken ct)
    {
        var staged = new List<Staged>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (LibraryScanner.Ignored.Contains(Path.GetExtension(path)))
                continue;
            var relative = LibraryScanner.Relative(directory, path);
            var container = ContainerReader.ContainerOf(path);
            try
            {
                foreach (var hashes in await reader.ReadAsync(path, ct))
                    staged.Add(new Staged(relative, container, hashes, null));
            }
            catch (Exception e) when (e is UnreadableFileException or IOException or UnauthorizedAccessException)
            {
                staged.Add(new Staged(relative, container, null, e.Message));
            }
        }
        return staged;
    }

    /// <summary>
    /// The dumps the staged files hold whole, and those they hold only some of, most complete first. Matched as the
    /// library is: by SHA-1 where the DAT gives one, else MD5, else CRC32, and always size; with and without a header.
    /// </summary>
    private async Task<(List<Dump> Whole, List<(Dump Dump, List<string> Missing)> Partial)> IdentifyAsync(List<Staged> staged, CancellationToken ct)
    {
        var hashes = staged.Where(s => s.Hashes is not null).SelectMany(s => s.Hashes!.Headerless is { } bare
            ? new[] { (s.Hashes.Sha1, s.Hashes.Md5, s.Hashes.Crc32), (bare.Sha1, bare.Md5, bare.Crc32) }
            : [(s.Hashes.Sha1, s.Hashes.Md5, s.Hashes.Crc32)]).ToList();
        var shas = hashes.Select(h => h.Item1).Distinct().ToList();
        var md5s = hashes.Select(h => h.Item2).Distinct().ToList();
        var crcs = hashes.Select(h => h.Item3).Distinct().ToList();
        var candidates = await db.DumpFiles.AsNoTracking().Include(f => f.Dump)
            .Where(f => shas.Contains(f.Sha1!) || f.Sha1 == null && (md5s.Contains(f.Md5!) || f.Md5 == null && crcs.Contains(f.Crc32!)))
            .ToListAsync(ct);

        foreach (var s in staged.Where(s => s.Hashes is not null))
            foreach (var file in candidates)
            {
                if (Same(s.Hashes!.Sha1, s.Hashes.Md5, s.Hashes.Crc32, s.Hashes.Size, file))
                    s.Matches.Add((file, false));
                else if (s.Hashes.Headerless is { } bare && Same(bare.Sha1, bare.Md5, bare.Crc32, bare.Size, file))
                    s.Matches.Add((file, true));
            }

        var dumpIds = staged.SelectMany(s => s.Matches).Select(m => m.File.DumpId).Distinct().ToList();
        var dumps = await db.Dumps.AsNoTracking().Include(d => d.Files).Where(d => dumpIds.Contains(d.Id)).ToListAsync(ct);
        var matched = staged.SelectMany(s => s.Matches.Select(m => (s, m.File.Id))).ToLookup(x => x.Id, x => x.s);

        bool Whole(Dump dump)
        {
            var fromChd = dump.Files.Any(f => matched[f.Id].Any(s => s.Container == Container.Chd));
            return dump.Files.Count > 0 && dump.Files.All(f => matched[f.Id].Any() || fromChd && Sheets.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase));
        }
        var whole = dumps.Where(Whole).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var wholeIds = whole.Select(d => d.Id).ToHashSet();
        // A track shared with a whole dump of another region doesn't make that region look partly here.
        var explained = staged.Where(s => s.Matches.Any(m => wholeIds.Contains(m.File.DumpId))).ToHashSet();
        var partial = dumps.Where(d => !wholeIds.Contains(d.Id) && d.Files.Any(f => matched[f.Id].Any(s => !explained.Contains(s))))
            .Select(d => (Dump: d, Missing: d.Files.Where(f => !matched[f.Id].Any()).Select(f => f.Name).ToList()))
            .OrderBy(p => p.Missing.Count).ThenBy(p => p.Dump.Name, StringComparer.Ordinal).ToList();
        return (whole, partial);
    }

    private static bool Same(string sha1, string md5, string crc32, long size, DumpFile file) =>
        (file.Size == size || size < 0) && (file.Sha1 is not null ? file.Sha1 == sha1 : file.Md5 is not null ? file.Md5 == md5 : file.Crc32 == crc32);

    /// <summary>Whether a dump comes in an archive igir can't move as it is: one with other files in it, or a header to leave out.</summary>
    private static bool NeedsRewriting(List<Staged> staged, Dump dump)
    {
        var own = staged.Where(s => s.Matches.Any(m => m.File.DumpId == dump.Id) && s.Container is Container.Zip or Container.SevenZip).ToList();
        return own.Any(s => s.Matches.Any(m => m.File.DumpId == dump.Id && m.Headerless))
            || own.Select(s => s.Path).Distinct().Any(archive => staged.Any(s => s.Path == archive && !s.Matches.Any(m => m.File.DumpId == dump.Id)));
    }

    /// <summary>Where a dump stands in the library, and whether another dump of its title is there.</summary>
    private async Task<(DumpStatus Status, bool Alternative)> StandingAsync(Guid systemId, Dump dump, CancellationToken ct)
    {
        var view = await views.GetAsync(systemId, ct);
        if (view is null)
            return (DumpStatus.Missing, false);
        if (view.ById.GetValueOrDefault(dump.TitleId) is { } title)
        {
            var status = title.Dumps.FirstOrDefault(d => d.Id == dump.Id)?.Status ?? DumpStatus.Missing;
            // Another disc of the same release is part of the same game, not an alternative to it.
            var other = title.Dumps.Any(d => d.Id != dump.Id && d.Release != dump.Release && d.Status == DumpStatus.Good);
            return (status, other);
        }
        return (view.Bios.FirstOrDefault(b => b.Id == dump.Id)?.Status ?? DumpStatus.Missing, false);
    }

    private sealed record Asked(string Name, Guid TitleId, Guid? DumpId)
    {
        public bool Matches(Dump dump) => DumpId is { } id ? dump.Id == id : dump.TitleId == TitleId;
    }

    private async Task<Asked?> AskedForAsync(Download item, CancellationToken ct)
    {
        if (item.TitleId is not { } titleId)
            return null;
        if (item.WantedDumpId is { } dumpId && await db.Dumps.FindAsync([dumpId], ct) is { } wanted)
            return new Asked(wanted.Name, titleId, dumpId);
        var title = await db.Titles.FindAsync([titleId], ct);
        return new Asked(title?.Name ?? "the game", titleId, null);
    }

    private static StagedFileInfo Info(Staged s) => new(
        s.Path, s.Hashes?.Entry, s.Hashes?.Size ?? 0, s.Hashes?.Crc32, s.Hashes?.Sha1,
        s.Matches.Select(m => $"{m.File.Dump!.Name}: {m.File.Name}{(m.Headerless ? " (without its header)" : "")}").ToList(), s.Error);

    private async Task SetAsync(Download item, DownloadState state, string? message, CancellationToken ct)
    {
        item.State = state;
        item.Message = message;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var log = state is DownloadState.Failed or DownloadState.NeedsReview ? LogLevel.Warning : LogLevel.Information;
        logger.Log(log, "[{Item}] {State}: {Message}", item.Name, state, message);
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "nothing",
            1 => list[0],
            _ => string.Join(", ", list[..^1]) + " and " + list[^1],
        };
    }

    private static string Sentence(List<string> parts) => parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
}
