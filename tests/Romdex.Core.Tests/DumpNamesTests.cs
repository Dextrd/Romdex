using Romdex.Core.Catalogue;

namespace Romdex.Core.Tests;

/// <summary>Reading No-Intro and Redump names into their parts.</summary>
public sealed class DumpNamesTests
{
    [Fact]
    public void Regions_languages_and_revision_are_read_from_the_name()
    {
        var name = DumpNames.Parse("Hopper Quest (USA, Europe) (En,Fr,De) (Rev 2)");

        Assert.Equal("Hopper Quest", name.Title);
        Assert.Equal(["USA", "Europe"], name.Regions);
        Assert.Equal(["En", "Fr", "De"], name.Languages);
        Assert.True(string.CompareOrdinal(name.Revision, DumpNames.Parse("Hopper Quest (USA) (Rev 1)").Revision) > 0);
        Assert.True(string.CompareOrdinal(DumpNames.Parse("Hopper Quest (USA) (Rev 1)").Revision, DumpNames.Parse("Hopper Quest (USA)").Revision) > 0);
        Assert.True(string.CompareOrdinal(DumpNames.Parse("Hopper Quest (USA) (v1.10)").Revision, DumpNames.Parse("Hopper Quest (USA) (v1.9)").Revision) > 0);
        Assert.Equal(DumpKinds.None, name.Kinds);
    }

    [Fact]
    public void Languages_come_from_the_regions_when_the_name_has_none()
    {
        Assert.Equal(["Ja"], DumpNames.Parse("Hopper Quest (Japan)").Languages);
        Assert.Equal(["En"], DumpNames.Parse("Hopper Quest (World)").Languages);
    }

    [Theory]
    [InlineData("Hopper Quest (USA) (Demo)", DumpKinds.Demo)]
    [InlineData("Hopper Quest (USA) (Kiosk)", DumpKinds.Demo)]
    [InlineData("Hopper Quest (USA) (Beta 2)", DumpKinds.Beta)]
    [InlineData("Hopper Quest (USA) (Proto 1)", DumpKinds.Proto)]
    [InlineData("Hopper Quest (USA) (Unl)", DumpKinds.Unlicensed)]
    [InlineData("Hopper Quest (World) (Aftermarket) (Unl)", DumpKinds.Aftermarket | DumpKinds.Unlicensed)]
    [InlineData("Hopper Quest (Europe) (Pirate)", DumpKinds.Pirate)]
    [InlineData("Hopper Quest (USA) (Alt)", DumpKinds.Alternate)]
    [InlineData("[BIOS] Handheld Boot ROM (World)", DumpKinds.Bios)]
    [InlineData("Hopper Quest (USA) [b]", DumpKinds.Bad)]
    public void Kinds_are_read_from_tags(string name, DumpKinds kinds) => Assert.Equal(kinds, DumpNames.Parse(name).Kinds);

    [Fact]
    public void Redump_categories_say_what_the_name_doesnt()
    {
        Assert.Equal(DumpKinds.Demo, DumpNames.Parse("Disc Hopper (Europe)", "Demos").Kinds);
        Assert.Equal(DumpKinds.Proto, DumpNames.Parse("Disc Hopper (Europe)", "Preproduction").Kinds);
        Assert.Equal(DumpKinds.None, DumpNames.Parse("Disc Hopper (Europe)", "Games").Kinds);
    }

    [Fact]
    public void Discs_of_one_release_share_it_and_one_title()
    {
        var first = DumpNames.Parse("Disc Hopper (USA) (Disc 1)");
        var second = DumpNames.Parse("Disc Hopper (USA) (Disc 2)");

        Assert.Equal("1", first.Disc);
        Assert.Equal("Disc Hopper (USA)", first.Release);
        Assert.Equal(first.Release, second.Release);
        Assert.Equal("Disc Hopper", second.Title);
        Assert.Null(DumpNames.Parse("Disc Hopper (USA)").Disc);
    }

    [Fact]
    public void Title_keys_ignore_case_and_spacing() =>
        Assert.Equal(DumpNames.TitleKey("Hopper  Quest"), DumpNames.TitleKey("hopper quest"));

    [Theory]
    [InlineData("Sonic & Knuckles", "Sonic and Knuckles")]
    [InlineData("The Sky Puzzle", "Sky Puzzle!")]
    [InlineData("Pokémon - Hopper Version", "Pokemon Hopper Version")]
    public void Games_on_other_systems_are_one_whatever_their_punctuation(string one, string other) =>
        Assert.Equal(Games.Key(one), Games.Key(other));

    [Fact]
    public void Games_with_other_words_are_not_one() =>
        Assert.NotEqual(Games.Key("Sky Puzzle"), Games.Key("Sky Puzzle 2"));

    [Theory]
    [InlineData("Sony - PlayStation 2", "PS2")]
    [InlineData("Sony - PlayStation", "PS1")]
    [InlineData("Nintendo - GameCube", "GC")]
    [InlineData("Nintendo - Nintendo Entertainment System (Headered)", "NES")]
    [InlineData("Nintendo - Game Boy Color", "GBC")]
    [InlineData("Test - Handheld", "Handheld")]
    [InlineData("Homebrew Things", "Homebrew Things")]
    public void Systems_have_short_names(string name, string expected) => Assert.Equal(expected, SystemNames.Short(name));

    [Theory]
    [InlineData("(GC & Xbox) 4.8/10 · (PS2) 4.7/10", "Nintendo - GameCube", "4.8/10")]
    [InlineData("(GC & Xbox) 4.8/10 · (PS2) 4.7/10", "Sony - PlayStation 2", "4.7/10")]
    [InlineData("XBOX: 73/100 · GCN: 72/100 · PC: 66/100", "Nintendo - GameCube", "72/100")]
    [InlineData("2.5/5 (GC) · 3/5 (PS2)", "Sony - PlayStation 2", "3/5")]
    [InlineData("(PS) 8/10", "Sony - PlayStation", "8/10")]
    [InlineData("(Genesis) 9/10 · (SNES) 8/10", "Sega - Mega Drive - Genesis", "9/10")]
    [InlineData("4/10, 2/10", "Nintendo - GameCube", "4/10, 2/10")]
    [InlineData("8/10 (Japan)", "Nintendo - GameCube", "8/10 (Japan)")]
    [InlineData("(PS2) 7/10 · 8/10", "Nintendo - GameCube", "8/10")]
    public void A_score_for_several_systems_is_the_one_for_this(string score, string system, string expected) =>
        Assert.Equal(expected, GameDetailsCache.ScoreHere(score, SystemNames.Names(system)));

    [Fact]
    public void A_score_only_for_other_systems_is_not_shown() =>
        Assert.Null(GameDetailsCache.ScoreHere("(PS2) D− · (GC & Xbox) C+", SystemNames.Names("Sega - Dreamcast")));

    [Fact]
    public void A_magazine_about_other_systems_is_not_shown_here()
    {
        Assert.Null(GameDetailsCache.ScoreHere("Nintendo Power", "8/10", SystemNames.Names("Sony - PlayStation 2")));
        Assert.Equal("8/10", GameDetailsCache.ScoreHere("Nintendo Power", "8/10", SystemNames.Names("Nintendo - GameCube")));
        Assert.Null(GameDetailsCache.ScoreHere("Official Xbox Magazine (UK)", "7/10", SystemNames.Names("Nintendo - GameCube")));
    }
}
