using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>The About page's "Built on" names everything Romdex works with, so it's kept up to date as plugins are added.</summary>
public sealed class AboutPageTests
{
    // How the page names a plugin whose folder doesn't say it as written.
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DatFolder"] = "DAT folder",
    };

    [Fact]
    public void Built_on_names_every_plugin()
    {
        var root = TestRepository.Root;
        var page = File.ReadAllText(Path.Combine(root, "src", "Romdex.Web", "Pages", "About.razor"));
        var plugins = Directory.GetDirectories(Path.Combine(root, "src"), "Romdex.Plugins.*").Select(d => Path.GetFileName(d)["Romdex.Plugins.".Length..])
            // The retrievers, which have repositories of their own, by their templates under Settings → Plugins.
            .Concat(Romdex.Web.Pages.Plugins.KnownSources.Where(t => t.Endpoint is { Length: > 0 }).Select(t => t.Key))
            .ToList();
        Assert.NotEmpty(plugins);
        Assert.All(plugins, plugin => Assert.Contains(Named.GetValueOrDefault(plugin, plugin), page, StringComparison.OrdinalIgnoreCase));
    }
}
