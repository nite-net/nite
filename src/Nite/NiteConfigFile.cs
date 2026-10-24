using System.Text;
using System.Text.Json;

namespace Nite;

/// <summary>
/// Edits the dependency sections of <c>nite.json</c> in place. Only the section being changed is
/// rewritten, so comments and formatting elsewhere in the file survive; comments inside that one
/// section do not.
/// </summary>
public static class NiteConfigFile
{
    public const string Dependencies = "dependencies";
    public const string DevDependencies = "devDependencies";

    /// <summary>Adds or updates <paramref name="name"/> in <paramref name="section"/>, creating the file if needed.</summary>
    public static void SetDependency(string projectDirectory, string section, string name, string range)
    {
        var path = Path.Combine(projectDirectory, NiteConfig.FileName);
        var text = File.Exists(path) ? File.ReadAllText(path) : "{\n}\n";
        var entries = ReadSection(text, section);
        entries[name] = range;
        File.WriteAllText(path, WriteSection(text, section, entries));
    }

    /// <summary>Removes <paramref name="name"/> from whichever section has it. Returns the sections it was removed from.</summary>
    public static IReadOnlyList<string> RemoveDependency(string projectDirectory, string name)
    {
        var path = Path.Combine(projectDirectory, NiteConfig.FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path);
        var removedFrom = new List<string>();
        foreach (var section in new[] { Dependencies, DevDependencies })
        {
            var entries = ReadSection(text, section);
            if (entries.Remove(name))
            {
                text = WriteSection(text, section, entries);
                removedFrom.Add(section);
            }
        }
        if (removedFrom.Count > 0)
        {
            File.WriteAllText(path, text);
        }
        return removedFrom;
    }

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static SortedDictionary<string, string> ReadSection(string text, string section)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (FindSection(text, section) is not { } span)
        {
            return entries;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes.AsSpan(span.Start, span.Length), ReaderOptions);
        reader.Read(); // {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            entries[name] = reader.TokenType == JsonTokenType.String ? reader.GetString()! : "";
        }
        return entries;
    }

    internal static string WriteSection(string text, string section, IReadOnlyDictionary<string, string> entries)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = DetectIndent(text);

        var body = new StringBuilder("{");
        var first = true;
        foreach (var (name, range) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            body.Append(first ? "" : ",").Append(newline).Append(indent).Append(indent)
                .Append(JsonSerializer.Serialize(name)).Append(": ").Append(JsonSerializer.Serialize(range));
            first = false;
        }
        body.Append(entries.Count == 0 ? "}" : newline + indent + "}");

        if (FindSection(text, section) is { } span)
        {
            var before = Encoding.UTF8.GetString(bytes, 0, span.Start);
            var after = Encoding.UTF8.GetString(bytes, span.Start + span.Length, bytes.Length - span.Start - span.Length);
            return before + body + after;
        }

        // Not there yet: add it as the last property of the root object.
        var close = LastRootBrace(text);
        var head = text[..close].TrimEnd();
        var needsComma = !head.EndsWith('{') && !head.EndsWith(',');
        return head + (needsComma ? "," : "") + newline + indent + JsonSerializer.Serialize(section) + ": " + body + newline + text[close..];
    }

    /// <summary>The byte range of a top-level property's object value, or null when absent.</summary>
    private static (int Start, int Length)? FindSection(string text, string section)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(section))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new InvalidOperationException($"\"{section}\" in nite.json must be an object.");
                }
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                return (start, (int)reader.BytesConsumed - start);
            }
            if (reader.CurrentDepth >= 1 && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }
        return null;
    }

    private static int LastRootBrace(string text)
    {
        var index = text.LastIndexOf('}');
        return index < 0 ? throw new InvalidOperationException("nite.json is not a JSON object.") : index;
    }

    private static string DetectIndent(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith('"') && trimmed.Length < line.Length)
            {
                return line[..(line.Length - trimmed.Length)];
            }
        }
        return "  ";
    }
}
