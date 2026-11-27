using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Catalogue;
using Romdex.Core.Data;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Library;

/// <summary>Hashes what's new or changed in the library, then matches it all against the catalogue.</summary>
public sealed class LibraryScanTask(LibraryScanner scanner, LibraryMatcher matcher, RomdexDbContext db, TaskQueue queue, ReviewService review, TaskFeed feed)
    : ITaskHandler
{
    public const string TaskKind = "library-scan";

    public string Kind => TaskKind;

    public string Title(string? payload) => "Library scan";

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var rows = feed.Start(task);
        var scan = await scanner.ScanAsync(progress, ct, rows);
        progress.Report("Matching");
        await matcher.MatchAsync(ct);
        await SayMatchedAsync(rows, scan.Read, ct);
        if (scan.Hashed > 0 || scan.Removed > 0)
            await db.OptimiseAsync(ct);
        var notes = new List<string>();
        if (scan.Removed > 0)
            notes.Add($"{Count(scan.Removed, "file")} gone");
        if (scan.Unreadable > 0)
            notes.Add($"{Count(scan.Unreadable, "file")} couldn't be read");
        // What's left to review, as Melodex's scan says, for its notification to go there.
        var waiting = await review.SummarizeAsync(ct);
        var toReview = waiting.BadDumps + waiting.Matching + waiting.Outdated + waiting.Corrupted + waiting.Incomplete + waiting.CouldntFetch;
        if (toReview > 0)
            notes.Add($"{toReview:N0} to review");
        if (scan.Hashed > 0)
            await ArtLookupTask.QueueIfUsefulAsync(db, queue, ct);
        progress.Report($"{Count(scan.Files, "file")}: {scan.Hashed:N0} hashed ({LibraryScanner.Size(scan.HashedBytes)})" + string.Concat(notes.Select(n => "; " + n)));
    }

    // Each file read this time, once matched: the game it is, or that it waits under Review. Melodex's scan list says the
    // song a file matched the same way.
    private async Task SayMatchedAsync(TaskRows rows, IReadOnlyList<string> read, CancellationToken ct)
    {
        foreach (var chunk in read.Chunk(500))
        {
            var paths = await db.LibraryFiles.AsNoTracking().Where(f => chunk.Contains(f.Path) && f.Error == null).Select(f => f.Path).ToListAsync(ct);
            var matched = (await db.LibraryMatches.AsNoTracking().Where(m => chunk.Contains(m.Content!.File!.Path))
                    .Select(m => new { m.Content!.File!.Path, m.DumpFile!.Dump!.Name, m.DumpFile.Dump.TitleId }).ToListAsync(ct))
                .ToLookup(m => m.Path, StringComparer.Ordinal);
            var files = paths.Select(p => new { Path = p, Dumps = matched[p].Select(m => new { m.Name, m.TitleId }).Distinct().ToList() });
            foreach (var file in files)
            {
                var name = Path.GetFileName(file.Path);
                var folder = Path.GetDirectoryName(file.Path)?.Replace('\\', '/') is { Length: > 0 } f ? f : null;
                rows.Put(file.Path, file.Dumps.Count == 0 ? TaskRowState.Review : TaskRowState.Done, file.Dumps.Count == 0 ? name : string.Join(", ", file.Dumps.Select(d => d.Name)), folder is null ? name : $"{folder}/{name}",
                    file.Dumps.Count == 0 ? "Matches nothing" : null,
                    file.Dumps.Count == 0 ? "review" : $"titles/{file.Dumps[0].TitleId}");
            }
        }
    }

    private static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n:N0} {one}s";
}
