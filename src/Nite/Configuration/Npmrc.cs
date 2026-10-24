using System.Text.RegularExpressions;

namespace Nite.Configuration;

/// <summary>
/// Reads <c>.npmrc</c> files: <c>key=value</c> lines, <c>#</c>/<c>;</c> comments, optional quotes and
/// <c>${VAR}</c> expansion (<c>${VAR?}</c> for "empty if unset"), as npm does. Nite only acts on the
/// registry and credential keys; everything else is ignored.
/// </summary>
public static partial class Npmrc
{
    public static IReadOnlyDictionary<string, string> Parse(string text, Func<string, string?> environment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[')
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = Expand(line[..equals].Trim(), environment);
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }
            values[key] = Expand(value, environment);
        }
        return values;
    }

    public static IReadOnlyDictionary<string, string> Load(string path, Func<string, string?> environment) =>
        File.Exists(path) ? Parse(File.ReadAllText(path), environment) : new Dictionary<string, string>();

    private static string Expand(string text, Func<string, string?> environment) =>
        Variable().Replace(text, match => environment(match.Groups["name"].Value) ?? "");

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\??\}")]
    private static partial Regex Variable();
}
