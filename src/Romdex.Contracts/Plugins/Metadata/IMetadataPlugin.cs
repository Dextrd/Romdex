using Romdex.Contracts.Plugins.Index;

namespace Romdex.Contracts.Plugins.Metadata;

/// <summary>
/// Metadata plugin contract (version 1.0): what a game the catalogue has is known as. By a dump's hashes, the name
/// people know the game by, its cover and its IDs elsewhere, such as IGDB's; by those IDs, what it is: when it came
/// out, who made it, its genres, reviews, age ratings and the other games in its series; and by the names its DAT gives it, pictures of it. Romdex
/// keeps what it's told, so implementations should look up their source directly and not keep answers on their own;
/// a list of what the source has, to look in, is fine.
/// </summary>
/// <remarks>
/// A metadata plugin never says what exists: that's the catalogue's job, an <see cref="IIndexPlugin"/>'s. What it
/// answers changes only how a game is named and shown, never whether it's owned, missing or bad. A plugin can answer
/// either question or both: one that only adds details to what another found returns <c>null</c> from
/// <see cref="LookUpAsync"/>, and one that only finds games by hash needn't implement <see cref="DetailsAsync"/>.
/// Both return <c>null</c> when the source doesn't know the game, and throw for anything else;
/// <see cref="MetadataUnavailableException"/> says to try again later.
/// </remarks>
public interface IMetadataPlugin : IPlugin
{
    /// <summary>What the source knows about the dump with this file, by its hashes. Null when it knows nothing.</summary>
    /// <param name="file">The dump's file, as the catalogue lists it: for a disc, its first track.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    Task<DumpMetadata?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken);

    /// <summary>What the source knows about a game, by the IDs every metadata plugin found for it. Null when it knows nothing.</summary>
    /// <param name="ids">The game's IDs, by scheme, such as <c>igdb</c> or <c>wikipedia</c>, whichever plugin found them.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    Task<GameDetails?> DetailsAsync(IReadOnlyDictionary<string, string> ids, CancellationToken cancellationToken) =>
        Task.FromResult<GameDetails?>(null);

    /// <summary>
    /// Pictures of the game on one system, such as its title screen and a screenshot, by the names its DAT gives the
    /// system and its dumps. None when the source has none.
    /// </summary>
    /// <param name="game">The game on one system: the system's and dumps' names, and the IDs every metadata plugin found.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    Task<IReadOnlyList<GameImage>> ImagesAsync(GameImageQuery game, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GameImage>>([]);
}

/// <param name="Title">The game's name, as people know it.</param>
/// <param name="CoverUrl">A picture of its box or cover, if there is one.</param>
/// <param name="Ids">The source's own IDs, and those it knows elsewhere, such as an IGDB ID, by scheme, lowercased.</param>
/// <param name="Links">Pages about the game, such as its IGDB page, by the name of the site.</param>
/// <param name="Description">What the game is, in a paragraph or so, as plain text.</param>
public sealed record DumpMetadata(
    string? Title,
    string? CoverUrl,
    IReadOnlyDictionary<string, string> Ids,
    IReadOnlyDictionary<string, string>? Links = null,
    string? Description = null);

/// <summary>What a game is. Every part is optional: a source fills in what it knows.</summary>
/// <param name="Source">Where it's from, to credit it, such as <c>Wikipedia</c>.</param>
/// <param name="SourceUrl">The page it's from, to read more.</param>
/// <param name="Summary">What the game is, in a paragraph or a few, as plain text.</param>
/// <param name="Released">When it came out, in each region the source knows of.</param>
/// <param name="Developers">The studios that made it.</param>
/// <param name="Publishers">Who published it.</param>
/// <param name="Genres">Its genres, such as <c>platform game</c>.</param>
/// <param name="Modes">How many can play, such as <c>single-player video game</c>.</param>
/// <param name="Series">The series it's part of.</param>
/// <param name="Credits">Who made it, by role, such as <c>Director</c> or <c>Composer</c>.</param>
/// <param name="Scores">What reviewers gave it, as each wrote it, such as <c>88/100</c>.</param>
/// <param name="Ratings">What age rating boards gave it, such as PEGI's 12.</param>
/// <param name="SeriesGames">The games in each of its series, by name, earliest first, on any platform: Romdex shows those its
/// catalogue has.</param>
public sealed record GameDetails(
    string Source,
    string? SourceUrl = null,
    string? Summary = null,
    IReadOnlyList<GameRelease>? Released = null,
    IReadOnlyList<string>? Developers = null,
    IReadOnlyList<string>? Publishers = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Modes = null,
    IReadOnlyList<string>? Series = null,
    IReadOnlyList<GameCredit>? Credits = null,
    IReadOnlyList<GameScore>? Scores = null,
    IReadOnlyList<GameRating>? Ratings = null,
    IReadOnlyList<GameSeries>? SeriesGames = null);

/// <summary>A series of games, such as <c>Sonic the Hedgehog</c>.</summary>
/// <param name="Name">The series' name.</param>
/// <param name="Games">Its games' names, as people know them, earliest first.</param>
public sealed record GameSeries(string Name, IReadOnlyList<string> Games);

/// <summary>When a game came out somewhere.</summary>
/// <param name="Region">Where, such as <c>North America</c> or <c>Japan</c>; null when the source doesn't say.</param>
/// <param name="Date">When, as <c>yyyy-MM-dd</c>, <c>yyyy-MM</c> or <c>yyyy</c>, as precisely as the source knows.</param>
/// <param name="Platform">On what, such as <c>PlayStation 2</c>, for a game that came out on several at different times; null when the source doesn't say.</param>
public sealed record GameRelease(string? Region, string Date, string? Platform = null);

/// <summary>Who made a game in one role.</summary>
/// <param name="Role">Such as <c>Director</c> or <c>Composer</c>.</param>
/// <param name="Names">Who, in the source's order.</param>
public sealed record GameCredit(string Role, IReadOnlyList<string> Names);

/// <summary>What a reviewer gave a game.</summary>
/// <param name="Reviewer">Who, such as <c>Metacritic</c> or <c>Famitsu</c>.</param>
/// <param name="Score">As they gave it, such as <c>88/100</c> or <c>32/40</c>.</param>
public sealed record GameScore(string Reviewer, string Score);

/// <summary>What an age rating board gave a game.</summary>
/// <param name="Board">Which, by its usual name: <c>ESRB</c>, <c>PEGI</c>, <c>CERO</c>, <c>USK</c> or <c>ACB</c>.</param>
/// <param name="Rating">The rating, lowercased as its mark is named: for ESRB <c>ec</c>, <c>e</c>, <c>e10</c>, <c>t</c>, <c>m</c>,
/// <c>ao</c> or <c>rp</c>; for PEGI and USK the age, such as <c>12</c>; for CERO <c>a</c> to <c>d</c> or <c>z</c>; for ACB
/// <c>g</c>, <c>pg</c>, <c>m</c>, <c>ma15</c> or <c>r18</c>.</param>
/// <param name="Name">As people read it, such as <c>Everyone 10+</c> or <c>PEGI 12</c>.</param>
public sealed record GameRating(string Board, string Rating, string Name);

/// <summary>A game on one system, to find pictures of.</summary>
/// <param name="System">The system, as its DAT names it, such as <c>Sony - PlayStation 2</c>.</param>
/// <param name="DumpNames">The game's dumps on it, as the DAT names them, the likeliest to have pictures first.</param>
/// <param name="Ids">The game's IDs, by scheme, as for <see cref="IMetadataPlugin.DetailsAsync"/>.</param>
public sealed record GameImageQuery(string System, IReadOnlyList<string> DumpNames, IReadOnlyDictionary<string, string> Ids);

/// <summary>A picture of a game.</summary>
/// <param name="Kind">What it shows: <c>title</c> for its title screen, <c>screenshot</c> for play.</param>
/// <param name="Url">Where it is: Romdex fetches and keeps it.</param>
/// <param name="Source">Where it's from, to credit it.</param>
public sealed record GameImage(string Kind, string Url, string Source);

/// <summary>
/// Thrown by a metadata plugin when its source can't answer right now: overloaded, rate-limiting, down or unreachable.
/// The core stops looking up for now and carries on where it left off next time.
/// </summary>
public sealed class MetadataUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
