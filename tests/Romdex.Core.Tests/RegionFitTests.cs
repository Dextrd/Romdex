using Romdex.Client;
using Romdex.Core.Catalogue;
using Romdex.Core.Library;

namespace Romdex.Core.Tests;

/// <summary>The region dot: how well what the library has of a game fits the region tiers.</summary>
public sealed class RegionFitTests
{
    private static readonly SystemPreferences Prefs = SystemPreferences.Default with { Wanted = ["Europe", "USA"], Fallback = ["Japan"], Never = ["Korea"] };

    private static DumpView Dump(params string[] regions) =>
        new(Guid.NewGuid(), Guid.Empty, string.Join(", ", regions), string.Join(", ", regions), null, regions, [], "", default, DumpStatus.Good, false, Prefs.TierOf(regions));

    private static (RegionFit? Fit, IReadOnlyList<string> Missing) Fit(DumpView[] released, params DumpView[] owned) =>
        LibraryViewBuilder.FitOf(released, owned, Prefs);

    [Fact]
    public void Gold_is_every_wanted_region_it_came_out_in()
    {
        DumpView europe = Dump("Europe"), usa = Dump("USA"), japan = Dump("Japan");
        Assert.Equal(RegionFit.Best, Fit([europe, usa, japan], europe, usa).Fit);
        // A region it never came out in doesn't count against it.
        Assert.Equal(RegionFit.Best, Fit([europe, japan], europe).Fit);
        // A World dump is every region.
        var world = Dump("World");
        Assert.Equal(RegionFit.Best, Fit([world, europe, usa], world).Fit);
    }

    [Fact]
    public void Green_is_some_wanted_region_and_says_which_are_missing()
    {
        DumpView europe = Dump("Europe"), usa = Dump("USA");
        var (fit, missing) = Fit([europe, usa], europe);
        Assert.Equal(RegionFit.Some, fit);
        Assert.Equal(["USA"], missing);
    }

    [Fact]
    public void Amber_is_only_a_fallback_while_theres_better()
    {
        DumpView europe = Dump("Europe"), japan = Dump("Japan"), brazil = Dump("Brazil");
        Assert.Equal(RegionFit.Fallback, Fit([europe, japan], japan).Fit);
        // Out in no wanted region: the best fallback there is is gold, a lesser one amber.
        Assert.Equal(RegionFit.Best, Fit([japan, brazil], japan).Fit);
        Assert.Equal(RegionFit.Fallback, Fit([japan, brazil], brazil).Fit);
    }

    [Fact]
    public void Red_is_only_regions_never_wanted_and_nothing_is_no_dot()
    {
        DumpView europe = Dump("Europe"), korea = Dump("Korea");
        Assert.Equal(RegionFit.Never, Fit([europe, korea], korea).Fit);
        Assert.Equal(RegionFit.Never, Fit([korea], korea).Fit);
        Assert.Null(Fit([europe, korea]).Fit);
    }
}
