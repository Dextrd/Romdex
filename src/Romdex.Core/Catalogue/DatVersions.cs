using System.Text.RegularExpressions;
using Romdex.Contracts.Plugins.Index;
using Romdex.Plugins.DatFolder;

namespace Romdex.Core.Catalogue;

/// <summary>Telling which DATs are of one system, and which of them is newest, whoever published them.</summary>
public static partial class DatVersions
{
    /// <inheritdoc cref="Dats.DateOf"/>
    public static DateTimeOffset? DateOf(IndexSystem system) => Dats.DateOf(system);

    /// <summary>
    /// What makes DATs one system: their names, without what says only how the files are kept, such as
    /// <c>(Headered)</c>, and whatever the spacing and punctuation. Known systems' other names count as theirs too.
    /// </summary>
    public static string SystemKey(string datName)
    {
        var name = FormatOnly().Replace(datName, "").Trim();
        return !name.Contains(" (", StringComparison.Ordinal) && KnownSystems.Find(name) is { } known
            ? "known:" + known.Short
            : string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s*\((?:Headered|Headerless|Decrypted|Encrypted|Parent-Clone|Retool)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FormatOnly();
}
