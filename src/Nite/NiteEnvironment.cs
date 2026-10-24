using System.Text.Json;

namespace Nite;

/// <summary>
/// Public environment variables for client code, the way Vite does it: values come from
/// <c>.env</c>, <c>.env.local</c>, <c>.env.[mode]</c> and <c>.env.[mode].local</c> in the project
/// folder (later files win) and from the process environment (which wins over files), but only
/// keys with one of the configured prefixes reach the browser. Everything else stays on the server.
/// </summary>
/// <remarks>
/// Code sees them as <c>import.meta.env.KEY</c> (with <c>MODE</c>, <c>DEV</c>, <c>PROD</c>,
/// <c>BASE_URL</c> and <c>SSR</c> alongside, as in Vite) and as <c>process.env.KEY</c> (as in
/// Create React App). Values are inlined at compile time.
/// </remarks>
public static class NiteEnvironment
{
    public const string Development = "development";
    public const string Production = "production";

    public static IEnumerable<string> FileNames(string mode) => [".env", ".env.local", $".env.{mode}", $".env.{mode}.local"];

    public static bool IsEnvFile(string fileName) => fileName == ".env" || fileName.StartsWith(".env.", StringComparison.Ordinal);

    public static IReadOnlyDictionary<string, string> Load(NiteProject project, string mode, Func<string, string?>? environment = null)
    {
        var prefixes = project.Config.EnvPrefix;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in FileNames(mode))
        {
            var path = Path.Combine(project.Directory, name);
            if (!File.Exists(path))
            {
                continue;
            }
            foreach (var (key, value) in ParseDotEnv(File.ReadAllText(path)))
            {
                if (HasPrefix(key, prefixes))
                {
                    values[key] = value;
                }
            }
        }

        // Variables already set in the environment win, so CI can override a committed .env.
        foreach (var key in values.Keys.ToList())
        {
            if ((environment ?? Environment.GetEnvironmentVariable)(key) is { } overridden)
            {
                values[key] = overridden;
            }
        }
        if (environment is null)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && HasPrefix(key, prefixes) && entry.Value is string value)
                {
                    values[key] = value;
                }
            }
        }
        return values;
    }

    /// <summary>esbuild <c>--define</c> flags that inline the public variables.</summary>
    public static List<string> DefineFlags(NiteProject project, string mode, Func<string, string?>? environment = null)
    {
        var values = Load(project, mode, environment);
        var meta = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            meta[key] = value;
        }
        meta["MODE"] = mode;
        meta["DEV"] = mode != Production;
        meta["PROD"] = mode == Production;
        meta["BASE_URL"] = "/";
        meta["SSR"] = false;

        var flags = new List<string> { $"--define:import.meta.env={JsonSerializer.Serialize(meta)}" };
        foreach (var (key, value) in values)
        {
            flags.Add($"--define:process.env.{key}={JsonSerializer.Serialize(value)}");
        }
        if (project.Config.EnvPrefix.Contains("REACT_APP_"))
        {
            // Create React App apps prefix asset URLs with this; Nite serves from the root.
            flags.Add("--define:process.env.PUBLIC_URL=\"\"");
        }
        return flags;
    }

    /// <summary>Parses KEY=VALUE lines: <c>#</c> comments, an optional <c>export</c>, and single or double quotes.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> ParseDotEnv(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value.LastIndexOf('"') > 0)
            {
                value = value[1..value.LastIndexOf('"')].Replace("\\n", "\n");
            }
            else if (value.Length >= 2 && value[0] == '\'' && value.LastIndexOf('\'') > 0)
            {
                value = value[1..value.LastIndexOf('\'')];
            }
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and >= 0)
            {
                value = value[..comment].TrimEnd();
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static bool HasPrefix(string key, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix => prefix.Length > 0 && key.StartsWith(prefix, StringComparison.Ordinal));
}
