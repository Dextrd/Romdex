using System.Globalization;
using System.Text;
using Romdex.Contracts.Plugins.Index;

namespace Romdex.Plugins.DatFolder;

/// <summary>
/// Reads clrmamepro DATs, the older text format libretro keeps No-Intro's and Redump's DATs in: a <c>clrmamepro ( … )</c>
/// header naming the system and version, then a <c>game ( … )</c> per dump with a <c>rom ( … )</c> per file.
/// </summary>
public static class ClrMamePro
{
    /// <summary>The system a DAT describes, from its header alone; null when it isn't a clrmamepro DAT.</summary>
    public static IndexSystem? ReadHeader(TextReader reader)
    {
        var tokens = new Tokens(reader);
        if (tokens.Next() is not { Quoted: false, Text: "clrmamepro" } || tokens.Next() is not { Text: "(", Quoted: false })
            return null;
        var fields = ReadBlock(tokens);
        if (fields.GetValueOrDefault("name") is not { Length: > 0 } name)
            return null;
        return new IndexSystem(name, name, Logiqx.SourceOf(fields), fields.GetValueOrDefault("version") ?? "", Logiqx.DateOf(fields.GetValueOrDefault("date")));
    }

    /// <summary>Every dump a DAT lists, in its order.</summary>
    public static IReadOnlyList<IndexDump> ReadDumps(TextReader reader, CancellationToken ct)
    {
        var dumps = new List<IndexDump>();
        var tokens = new Tokens(reader);
        while (tokens.Next() is { } token)
        {
            if (token.Quoted || tokens.Next() is not { Text: "(", Quoted: false })
                continue;
            if (token.Text is not ("game" or "machine"))
            {
                ReadBlock(tokens);
                continue;
            }
            ct.ThrowIfCancellationRequested();
            var files = new List<IndexDumpFile>();
            string? romSerial = null;
            var game = ReadBlock(tokens, (key, t) =>
            {
                if (key != "rom")
                    return false;
                var rom = ReadBlock(t);
                if (rom.GetValueOrDefault("name") is { } file)
                    files.Add(new IndexDumpFile(file, long.TryParse(rom.GetValueOrDefault("size"), CultureInfo.InvariantCulture, out var size) ? size : 0,
                        Hash(rom.GetValueOrDefault("crc")), Hash(rom.GetValueOrDefault("md5")), Hash(rom.GetValueOrDefault("sha1"))));
                romSerial ??= rom.GetValueOrDefault("serial");
                return true;
            });
            if (game.GetValueOrDefault("name") is not { Length: > 0 } name)
                continue;
            var serial = game.GetValueOrDefault("serial") ?? romSerial;
            var description = game.GetValueOrDefault("description");
            var cloneOf = game.GetValueOrDefault("cloneof");
            dumps.Add(new IndexDump(name, string.IsNullOrEmpty(cloneOf) ? null : cloneOf, files, string.IsNullOrWhiteSpace(serial) ? null : serial.Trim(),
                name.StartsWith("[BIOS]", StringComparison.Ordinal), description == name ? null : description,
                game.GetValueOrDefault("category") is { } category && !string.IsNullOrWhiteSpace(category) ? category.Trim() : null));
        }
        return dumps;
    }

    // A block's fields, up to its closing bracket, the first of each kept. Nested blocks are skipped, unless nested
    // says it has read one itself.
    private static Dictionary<string, string> ReadBlock(Tokens tokens, Func<string, Tokens, bool>? nested = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        while (tokens.Next() is { } key)
        {
            if (key is { Text: ")", Quoted: false })
                break;
            if (tokens.Next() is not { } value)
                break;
            if (value is { Text: "(", Quoted: false })
            {
                if (nested?.Invoke(key.Text, tokens) != true)
                    ReadBlock(tokens);
                continue;
            }
            fields.TryAdd(key.Text, value.Text);
        }
        return fields;
    }

    private static string? Hash(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private readonly record struct Token(string Text, bool Quoted);

    // Words, quoted strings and brackets, one at a time.
    private sealed class Tokens(TextReader reader)
    {
        private readonly StringBuilder _text = new();

        public Token? Next()
        {
            int c;
            while ((c = reader.Peek()) >= 0 && char.IsWhiteSpace((char)c))
                reader.Read();
            if (c < 0)
                return null;
            reader.Read();
            if (c is '(' or ')')
                return new Token(((char)c).ToString(), false);
            _text.Clear();
            if (c == '"')
            {
                while ((c = reader.Read()) >= 0 && c != '"')
                    _text.Append((char)c);
                return new Token(_text.ToString(), true);
            }
            _text.Append((char)c);
            while ((c = reader.Peek()) >= 0 && !char.IsWhiteSpace((char)c) && c is not ('(' or ')'))
                _text.Append((char)reader.Read());
            return new Token(_text.ToString(), false);
        }
    }
}

/// <summary>Reads a DAT in either format: Logiqx XML, as No-Intro and Redump publish them, or clrmamepro, as libretro keeps them.</summary>
public static class Dats
{
    /// <summary>The system a DAT describes, from its header alone; null when it isn't a DAT.</summary>
    public static IndexSystem? ReadHeader(Stream stream) => Read(stream, Logiqx.ReadHeader, ClrMamePro.ReadHeader);

    /// <summary>Every dump a DAT lists, in its order.</summary>
    public static IReadOnlyList<IndexDump> ReadDumps(Stream stream, CancellationToken ct) =>
        Read(stream, r => Logiqx.ReadDumps(r, ct), r => ClrMamePro.ReadDumps(r, ct));

    /// <summary>When a DAT was made, by its version, or its header's date where the version isn't one; null when neither says.</summary>
    public static DateTimeOffset? DateOf(IndexSystem system) => Logiqx.DateOf(system.Version) ?? system.Date;

    /// <summary>
    /// Whether one DAT of a system is newer than another, by when each was made, as each group writes its versions
    /// differently; by their versions as written where either doesn't say when.
    /// </summary>
    public static bool IsNewer(IndexSystem system, IndexSystem than) =>
        (DateOf(system), DateOf(than)) is ({ } date, { } other) ? date > other : string.CompareOrdinal(system.Version, than.Version) > 0;

    // XML starts with a bracket, after any whitespace, which is skipped as XML allows none before its declaration.
    private static T Read<T>(Stream stream, Func<TextReader, T> xml, Func<TextReader, T> text)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int c;
        while ((c = reader.Peek()) >= 0 && char.IsWhiteSpace((char)c))
            reader.Read();
        return c == '<' ? xml(reader) : text(reader);
    }
}
