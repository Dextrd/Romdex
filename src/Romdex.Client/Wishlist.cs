namespace Romdex.Client;

/// <summary>A game on your wishlist, as the catalogue has it.</summary>
/// <param name="TitleId">The game, on one system.</param>
/// <param name="DumpId">The one dump wished for, if one was; else the game in every region you want.</param>
/// <param name="Name">The game's name, or the dump's when one was wished for.</param>
/// <param name="Fit">How well what the library has of it fits your regions, as its dot shows; null with nothing whole.</param>
public sealed record WishlistItem(
    Guid Id,
    Guid TitleId,
    Guid? DumpId,
    string Name,
    Guid SystemId,
    string System,
    string? Cover,
    TitleStatus Status,
    RegionFit? Fit,
    IReadOnlyList<string>? WantedMissing,
    DateTime AddedAt);

/// <summary>Your wishlist, newest first, without what the library has now.</summary>
/// <param name="CanFetch">A retriever that searches is on, so what's on it can be got now.</param>
public sealed record Wishlist(bool CanFetch, IReadOnlyList<WishlistItem> Items);

/// <summary>A game to put on your wishlist, or one of its dumps.</summary>
public sealed record AddToWishlist(Guid TitleId, Guid? DumpId = null);

/// <summary>How many games went on your wishlist, of those asked to.</summary>
public sealed record AddedToWishlist(int Added);
