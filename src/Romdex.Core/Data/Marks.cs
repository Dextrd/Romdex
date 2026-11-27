using Romdex.Client;

namespace Romdex.Core.Data;

/// <summary>A star or rating one person has given a game, as Melodex's are on songs, albums and artists. Gone once it has neither.</summary>
public class Mark
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public MarkKind Kind { get; set; }

    /// <summary>The game: its title.</summary>
    public Guid ItemId { get; set; }

    public DateTime? StarredAt { get; set; }

    /// <summary>1 to 5.</summary>
    public int? Rating { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
