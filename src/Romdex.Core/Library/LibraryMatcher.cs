using Microsoft.EntityFrameworkCore;
using Romdex.Core.Data;

namespace Romdex.Core.Library;

/// <summary>
/// Matches what's in the library to the catalogue by hash alone: there's no fuzzy fallback and no confidence to weigh.
/// A dump file is matched by its SHA-1 where the DAT gives one, else its MD5, else its CRC32, and always its size. Each
/// content is tried as it is and, where it has a header, without it. Then the links people made count, for bytes the
/// DATs match to nothing; and every DAT match is remembered, so a file a new DAT stops matching can be told apart.
/// Hidden files are never matched.
/// </summary>
public sealed class LibraryMatcher(RomdexDbContext db, LibraryViews views)
{
    // Contents of files nobody hid: what the library matches.
    private const string Visible = "WITH Visible AS (SELECT * FROM LibraryContents WHERE LibraryFileId NOT IN (SELECT Id FROM LibraryFiles WHERE HiddenAt IS NOT NULL))";

    // Matched again from scratch each time: it's a few indexed joins, and it means a new DAT or a rescan can't leave a
    // stale match behind.
    private static readonly string[] Statements =
    [
        "DELETE FROM LibraryMatches;",
        Insert(headerless: false),
        Insert(headerless: true),
        Manual(headerless: false),
        Manual(headerless: true),
    ];

    private static string Insert(bool headerless)
    {
        var prefix = headerless ? "Headerless" : "";
        var flag = headerless ? 1 : 0;
        // A size of -1 is one the reader couldn't give, as for an RVZ: the hashes decide alone.
        var size = headerless ? "c.HeaderlessSize" : "c.Size";
        return $"""
            {Visible}
            INSERT OR IGNORE INTO LibraryMatches (ContentId, DumpFileId, Headerless)
            SELECT c.Id, f.Id, {flag} FROM Visible c JOIN DumpFiles f ON f.Sha1 = c.{prefix}Sha1 AND (f.Size = {size} OR {size} < 0)
            UNION
            SELECT c.Id, f.Id, {flag} FROM Visible c JOIN DumpFiles f ON f.Sha1 IS NULL AND f.Md5 = c.{prefix}Md5 AND (f.Size = {size} OR {size} < 0)
            UNION
            SELECT c.Id, f.Id, {flag} FROM Visible c JOIN DumpFiles f
                ON f.Sha1 IS NULL AND f.Md5 IS NULL AND f.Crc32 = c.{prefix}Crc32 AND (f.Size = {size} OR {size} < 0);
            """;
    }

    // A link to a dump file counts only for bytes the DATs matched to nothing: a DAT that adds them wins.
    private static string Manual(bool headerless) => $"""
        {Visible}
        INSERT OR IGNORE INTO LibraryMatches (ContentId, DumpFileId, Headerless, ManualMatchId)
        SELECT c.Id, m.DumpFileId, {(headerless ? 1 : 0)}, m.Id FROM Visible c JOIN ManualMatches m ON m.Sha1 = c.{(headerless ? "Headerless" : "")}Sha1
        WHERE m.DumpFileId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM LibraryMatches x WHERE x.ContentId = c.Id AND x.ManualMatchId IS NULL);
        """;

    public async Task MatchAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var statement in Statements)
                await db.Database.ExecuteSqlRawAsync(statement, ct);
            await RememberAsync(now, ct);
            await transaction.CommitAsync(ct);
        }
        views.Invalidate();
    }

    // Every DAT match now is remembered, by the bytes that matched and the names they matched; what the library no
    // longer has the bytes of is forgotten.
    private async Task RememberAsync(DateTime now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO PastMatches (Sha1, SystemName, DumpName, FileName, DatVersion, FirstMatchedAt, LastMatchedAt)
            SELECT DISTINCT CASE WHEN m.Headerless THEN c.HeaderlessSha1 ELSE c.Sha1 END, s.Name, d.Name, f.Name, s.DatVersion, {now}, {now}
            FROM LibraryMatches m
            JOIN LibraryContents c ON c.Id = m.ContentId
            JOIN DumpFiles f ON f.Id = m.DumpFileId
            JOIN Dumps d ON d.Id = f.DumpId
            JOIN Systems s ON s.Id = d.SystemId
            WHERE m.ManualMatchId IS NULL
            ON CONFLICT (Sha1, SystemName, DumpName, FileName) DO UPDATE SET DatVersion = excluded.DatVersion, LastMatchedAt = excluded.LastMatchedAt;
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM PastMatches WHERE NOT EXISTS (SELECT 1 FROM LibraryContents c WHERE c.Sha1 = PastMatches.Sha1)
                AND NOT EXISTS (SELECT 1 FROM LibraryContents c WHERE c.HeaderlessSha1 = PastMatches.Sha1);
            """, ct);
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
