using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romdex.Contracts.Plugins;

/// <summary>The kinds of plugin. Each does one job, and only that one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginType>))]
public enum PluginType
{
    /// <summary>
    /// The catalogue: which systems and dumps exist, from preservation DATs such as No-Intro's and Redump's. What these
    /// list is what Romdex matches files against, so it decides what is owned, missing or bad.
    /// </summary>
    Index,

    /// <summary>
    /// Titles and covers for dumps the catalogue already has, looked up by their hashes. They never add to or change
    /// what exists: only how a game is named and shown.
    /// </summary>
    Metadata,

    /// <summary>Brings dumps in: finds one of a game, or hands over ones that arrived, into staging. Runs out of process behind the HTTP contract.</summary>
    Retrieval,

    /// <summary>Tells an app that uses the library, such as RomM, about new games, and opens games there.</summary>
    Connector,
}

/// <summary>
/// Describes a plugin to the core and the web app. Every plugin ships one: .NET plugins return it from
/// <see cref="IPluginFactory.Manifest"/>, retrievers serve it at <c>GET /v1/manifest</c>.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Stable, globally unique ID in reverse-DNS style, for example <c>romdex.index.dat-folder</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>The plugin's own version (semver).</summary>
    public required string Version { get; init; }

    /// <summary>Which contract the plugin implements.</summary>
    public required PluginType Type { get; init; }

    /// <summary>The contract version the plugin was built against, as <c>major.minor</c>.</summary>
    public required string ContractVersion { get; init; }

    /// <summary>Short description shown in the plugin catalogue.</summary>
    public string? Description { get; init; }

    /// <summary>Project homepage or repository.</summary>
    public string? Homepage { get; init; }

    /// <summary>
    /// Capability names from <see cref="Index.IndexCapabilities"/>, <see cref="Retrieval.RetrievalCapabilities"/> or
    /// <see cref="Connectors.ConnectorCapabilities"/>. Metadata plugins have none.
    /// The core and API only offer what is declared here.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// JSON Schema (draft 2020-12) for the plugin's settings. The web app renders the setup form from it.
    /// Mark secrets with <c>"writeOnly": true</c> so the API never returns them.
    /// </summary>
    public JsonElement? SettingsSchema { get; init; }

    /// <summary>What the plugin touches: hosts it calls and paths it reads or writes. Shown before install.</summary>
    public PluginAccess Access { get; init; } = new();

    /// <summary>
    /// The ID of another plugin whose settings this one uses, for example an app's connection. The plugin has
    /// no settings of its own, can only be set up once that plugin is, and gets the first such instance's settings.
    /// </summary>
    public string? UsesSettingsOf { get; init; }

    /// <summary>The highest request rate the core may send through this plugin, if the upstream service has one.</summary>
    public double? MaxRequestsPerSecond { get; init; }

    /// <summary>
    /// Retriever plugins only: checks the plugin's settings offer as buttons, each answered by
    /// <c>POST /v1/checks/{id}</c>, for things the health check doesn't cover.
    /// </summary>
    public IReadOnlyList<PluginCheck> Checks { get; init; } = [];
}

/// <summary>A check a plugin's settings offer as a button.</summary>
/// <param name="Id">Its ID.</param>
/// <param name="Title">The button's label.</param>
/// <param name="Description">What it checks.</param>
public sealed record PluginCheck(string Id, string Title, string? Description = null);

/// <summary>The network and file access a plugin needs, declared up front.</summary>
public sealed record PluginAccess
{
    /// <summary>Hosts the plugin contacts, for example <c>redump.info</c>. Use <c>*</c> for user-configured hosts.</summary>
    public IReadOnlyList<string> Network { get; init; } = [];

    /// <summary>Paths or volumes the plugin reads or writes, for example <c>staging (write)</c>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}
