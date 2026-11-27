namespace Romdex.Client;

/// <summary>What a star or rating is on. Melodex's are songs, albums and artists; Romdex's, games.</summary>
public enum MarkKind
{
    /// <summary>A game, by its title ID.</summary>
    Game,
}

/// <summary>A star or rating you've given a game in your library. Each person has their own.</summary>
/// <param name="StarredAt">When it was starred; null if it isn't.</param>
/// <param name="Rating">1 to 5; null if it isn't rated.</param>
public sealed record ItemMark(MarkKind Kind, Guid Id, DateTime? StarredAt, int? Rating);

/// <summary>Body of <c>PUT /marks/{kind}/{id}</c>: what to change, leaving out what stays as it is.</summary>
/// <param name="Starred">Star it, or take its star away.</param>
/// <param name="Rating">1 to 5, or 0 to take its rating away.</param>
public sealed record SetMark(bool? Starred = null, int? Rating = null);
