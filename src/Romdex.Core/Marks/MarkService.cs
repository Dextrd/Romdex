using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Data;

namespace Romdex.Core.Marks;

/// <summary>Each person's stars and ratings on games, as Melodex's are on songs, albums and artists.</summary>
public sealed class MarkService(RomdexDbContext db)
{
    public async Task<IReadOnlyList<ItemMark>> ListAsync(Guid user, CancellationToken ct) =>
        (await db.Marks.AsNoTracking().Where(m => m.UserId == user).ToListAsync(ct))
        .OrderByDescending(m => m.StarredAt ?? m.ChangedAt).Select(Info).ToList();

    /// <summary>The games this person has starred.</summary>
    public async Task<HashSet<Guid>> StarredAsync(Guid user, CancellationToken ct) =>
        (await db.Marks.AsNoTracking().Where(m => m.UserId == user && m.StarredAt != null).Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();

    /// <summary>Stars, unstars or rates something; what's left of its mark, or null once it has neither.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A rating that isn't 0 to 5.</exception>
    public async Task<ItemMark?> SetAsync(Guid user, MarkKind kind, Guid id, SetMark change, CancellationToken ct)
    {
        if (change.Rating is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(change), "A rating is 1 to 5, or 0 to take it away.");
        try
        {
            return await ChangeAsync(user, kind, id, change, ct);
        }
        catch (DbUpdateException)
        {
            // A star and a rating given a moment apart both made its first mark; the one that lost changes the other's.
            db.ChangeTracker.Clear();
            return await ChangeAsync(user, kind, id, change, ct);
        }
    }

    private async Task<ItemMark?> ChangeAsync(Guid user, MarkKind kind, Guid id, SetMark change, CancellationToken ct)
    {
        var mark = await db.Marks.FirstOrDefaultAsync(m => m.UserId == user && m.Kind == kind && m.ItemId == id, ct);
        if (mark is null)
        {
            mark = new Mark { UserId = user, Kind = kind, ItemId = id };
            db.Marks.Add(mark);
        }
        if (change.Starred is { } starred)
            mark.StarredAt = starred ? mark.StarredAt ?? DateTime.UtcNow : null;
        if (change.Rating is { } rating)
            mark.Rating = rating == 0 ? null : rating;
        mark.ChangedAt = DateTime.UtcNow;
        var gone = mark.StarredAt is null && mark.Rating is null;
        if (gone)
        {
            if (db.Entry(mark).State == EntityState.Added)
                db.Entry(mark).State = EntityState.Detached;
            else
                db.Marks.Remove(mark);
        }
        await db.SaveChangesAsync(ct);
        return gone ? null : Info(mark);
    }

    private static ItemMark Info(Mark mark) => new(mark.Kind, mark.ItemId, mark.StarredAt, mark.Rating);
}
