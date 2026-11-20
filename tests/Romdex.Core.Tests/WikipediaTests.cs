using Romdex.Contracts.Plugins.Metadata;
using Romdex.Plugins.Libretro;
using Romdex.Plugins.ScreenScraper;
using Romdex.Plugins.TheGamesDb;
using Romdex.Plugins.Wikipedia;

namespace Romdex.Core.Tests;

/// <summary>Reading a Wikipedia article's infobox and review box, as articles about games write them. The games are made up.</summary>
public sealed class WikipediaTests
{
    private const string Article = """
        {{Short description|2004 video game}}
        {{Infobox video game
        | title = Kite Story
        | image = Kite Story cover.jpg
        | developer = [[Lilypad Soft|Lilypad Soft Oxford]]
        | publisher = [[Paper Games]]{{efn|Released in Europe under the [[Paper Games Europe]] name.}}
        | director = {{Unbulleted list|Rana Croak|Toad Hall}}
        | composer = [[Newt Song]]<ref name="credits">{{cite web |url=https://example.com |title=Credits}}</ref>
        | series = ''[[Kite (series)|Kite]]''
        | platforms = {{Plainlist|
        * [[PlayStation 2]]
        * [[Xbox (console)|Xbox]]
        }}
        | released = '''PlayStation 2''', '''Xbox'''{{vgrelease|NA|September 28, 2004<ref name="r">{{cite web |title=Review}}</ref>|EU|October 8, 2004|AUS|October 2004}}
        '''Windows'''{{vgrelease|NA|2005}}
        | genre = [[Platform game|Platform]], [[action-adventure]]
        | modes = [[Single-player]], [[multiplayer]]
        }}
        '''''Kite Story''''' is a game.
        {{Video game reviews
        | MC = Xbox: 66/100<br />PS2: 64/100<ref>{{cite web |url=https://example.com}}</ref>
        | GSpot = 7.3/10
        | rev1 = [[Frog Monthly]] | rev1Score = 4/5
        | width = 30em
        }}
        """;

    [Fact]
    public void The_infobox_says_who_made_the_game_and_when_it_came_out_where()
    {
        var details = Infobox.Read(Article)!;

        Assert.Equal(["Lilypad Soft Oxford"], details.Developers);
        // Footnotes and references aren't part of a name.
        Assert.Equal(["Paper Games"], details.Publishers);
        Assert.Equal(["Rana Croak", "Toad Hall"], Assert.Single(details.Credits!, c => c.Role == "Director").Names);
        Assert.Equal(["Newt Song"], Assert.Single(details.Credits!, c => c.Role == "Composer").Names);
        Assert.Equal(["Kite"], details.Series);
        Assert.Equal(["Platform", "action-adventure"], details.Genres);
        Assert.Equal(["Single-player", "multiplayer"], details.Modes);
        // Each group of platforms in bold has its own dates.
        Assert.Equal(
        [
            new GameRelease("North America", "2004-09-28", "PlayStation 2, Xbox"),
            new GameRelease("Europe", "2004-10-08", "PlayStation 2, Xbox"),
            new GameRelease("Australia", "2004-10", "PlayStation 2, Xbox"),
            new GameRelease("North America", "2005", "Windows"),
        ], details.Released);
    }

    [Fact]
    public void A_date_says_what_it_came_out_on_in_brackets() =>
        Assert.Equal([new GameRelease("North America", "2004-01-06", "GCN"), new GameRelease("Japan", "2003-12-30", null)],
            Infobox.Releases("{{vgrelease|NA|January 6, 2004 (GCN)|JP|30 December 2003}}"));

    [Fact]
    public void The_review_box_gives_each_reviewers_score_as_written() =>
        Assert.Equal([("Metacritic", "Xbox: 66/100 · PS2: 64/100"), ("GameSpot", "7.3/10"), ("Frog Monthly", "4/5")],
            Reviews.Read(Article).Select(s => (s.Reviewer, s.Score)));

    [Fact]
    public void A_review_box_may_start_with_a_comment() =>
        Assert.Equal([("Metacritic", "(GC) 51/100 · (Xbox) 49/100"), ("X-Play", "1/5")],
            Reviews.Read("""
                {{Video game reviews
                <!-- Aggregators -->
                | MC = (GC) 51/100<ref name=MCGC /><br />(Xbox) 49/100<ref name=MCXBOX />
                <!-- Reviewers -->
                | XPlay = 1/5<ref name="xplay"/>
                }}
                """).Select(s => (s.Reviewer, s.Score)));

    [Fact]
    public void A_short_name_for_a_platform_keeps_its_short_name() =>
        Assert.Equal([("IGN", "GC: 8/10 · PS2: 7/10")],
            Reviews.Read("{{Video game reviews | IGN = {{abbr|GC|GameCube}}: 8/10<br />{{tooltip|PS2|PlayStation 2}}: 7/10 }}").Select(s => (s.Reviewer, s.Score)));

    [Fact]
    public void An_article_without_boxes_has_nothing_to_say()
    {
        Assert.Null(Infobox.Read("'''Kite Story''' is a game."));
        Assert.Empty(Reviews.Read("'''Kite Story''' is a game."));
    }

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Kite_Story_(video_game)", "en.wikipedia.org", "Kite Story (video game)")]
    [InlineData("https://fr.m.wikipedia.org/wiki/Kite_Story", "fr.wikipedia.org", "Kite Story")]
    [InlineData("https://wikipedia.org/wiki/Kite_Story", "en.wikipedia.org", "Kite Story")]
    public void Articles_are_found_by_their_address(string link, string site, string title) =>
        Assert.Equal((site, title), Wikipedia.Article(link));

    [Theory]
    [InlineData("http://en.wikipedia.org/wiki/Kite_Story")]
    [InlineData("https://en.wikipedia.org.example.com/wiki/Kite_Story")]
    [InlineData("https://example.com/wiki/Kite_Story")]
    public void Nothing_but_wikipedia_is_asked(string link) => Assert.Null(Wikipedia.Article(link));

    [Theory]
    [InlineData("Kite Story: Return? (USA) (En,Fr)", "Kite Story_ Return_ (USA) (En,Fr)")]
    [InlineData("Toad & Newt (Europe)", "Toad _ Newt (Europe)")]
    public void Libretro_files_pictures_by_the_dump_name_without_what_a_file_name_cant_have(string dump, string file) =>
        Assert.Equal(file, LibretroThumbnails.FileName(dump));

    [Fact]
    public void Libretro_knows_a_system_by_its_dats_name_or_without_what_the_dat_adds() =>
        Assert.Equal(["Nintendo - Nintendo Entertainment System (Headerless)", "Nintendo - Nintendo Entertainment System"],
            LibretroThumbnails.Systems("Nintendo - Nintendo Entertainment System (Headerless)"));

    [Theory]
    [InlineData("E - Everyone", "e", "Everyone")]
    [InlineData("E10+ - Everyone 10+", "e10", "Everyone 10+")]
    [InlineData("M - Mature 17+", "m", "Mature 17+")]
    [InlineData("Not Rated", null, null)]
    [InlineData(null, null, null)]
    public void TheGamesDBs_ratings_are_ESRBs(string? given, string? rating, string? name) =>
        Assert.Equal(rating is null ? null : new GameRating("ESRB", rating, name!), TheGamesDb.Rating(given));

    [Theory]
    [InlineData("PEGI", "12", "PEGI", "12")]
    [InlineData("pegi", "16+", "PEGI", "16")]
    [InlineData("ESRB", "E10+", "ESRB", "e10")]
    [InlineData("CERO", "B", "CERO", "b")]
    [InlineData("USK", "6", "USK", "6")]
    [InlineData("OFLC", "MA15+", "ACB", "ma15")]
    [InlineData("PEGI", "4", null, null)]
    [InlineData("SS", "7", null, null)]
    public void ScreenScrapers_ratings_are_by_the_boards_marks(string board, string given, string? expectedBoard, string? rating) =>
        Assert.Equal((expectedBoard, rating), (ScreenScraper.Rating(board, given)?.Board, ScreenScraper.Rating(board, given)?.Rating));
}
