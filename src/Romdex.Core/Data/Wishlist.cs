namespace Romdex.Core.Data;

/// <summary>
/// A game one user would like but hasn't got: for later, when nothing can fetch it now or they'd rather wait, as
/// Melodex's songs and albums are. It goes by itself once the library has it.
/// </summary>
public class WishlistEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>The game, on one system.</summary>
    public Guid TitleId { get; set; }

    /// <summary>The one dump wished for, if one was chosen; else the game in every region you want, as Get asks for it.</summary>
    public Guid? DumpId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
