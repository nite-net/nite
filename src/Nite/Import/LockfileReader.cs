using System.Text.Json;
using System.Text.RegularExpressions;
using Nite.Packages;

namespace Nite.Import;

/// <summary>The package managers whose projects Nite can import.</summary>
public enum PackageManager { None, Npm, Pnpm, Yarn, YarnBerry, Bun }

/// <summary>
/// Reads the package versions a project currently uses out of its lockfile. Nite does not convert
/// lockfiles: it resolves its own tree, preferring these versions wherever they satisfy the ranges,
/// so an imported project keeps running what it ran before. Only registry versions are returned;
/// file:, link:, workspace:, git and similar entries are skipped.
/// </summary>
public static partial class LockfileReader
{
    /// <summary>Finds the lockfile in a project directory, most specific first.</summary>
    public static (PackageManager Manager, string? Path) Detect(string directory)
    {
        string At(string name) => System.IO.Path.Combine(directory, name);

        if (File.Exists(At("pnpm-lock.yaml"))) return (PackageManager.Pnpm, At("pnpm-lock.yaml"));
        if (File.Exists(At("bun.lock"))) return (PackageManager.Bun, At("bun.lock"));
        if (File.Exists(At("yarn.lock")))
        {
            var berry = File.ReadLines(At("yarn.lock")).Take(20).Any(line => line.StartsWith("__metadata:", StringComparison.Ordinal));
            return (berry ? PackageManager.YarnBerry : PackageManager.Yarn, At("yarn.lock"));
        }
        if (File.Exists(At("package-lock.json"))) return (PackageManager.Npm, At("package-lock.json"));
        if (File.Exists(At("npm-shrinkwrap.json"))) return (PackageManager.Npm, At("npm-shrinkwrap.json"));
        if (File.Exists(At("bun.lockb"))) return (PackageManager.Bun, null); // binary; see Read
        return (PackageManager.None, null);
    }

    /// <summary>Package name → the versions the lockfile has for it.</summary>
    public static Dictionary<string, HashSet<string>> Read(PackageManager manager, string path)
    {
        var text = File.ReadAllText(path);
        return manager switch
        {
            PackageManager.Npm => ReadNpm(text),
            PackageManager.Pnpm => ReadPnpm(text),
            PackageManager.Yarn => ReadYarnClassic(text),
            PackageManager.YarnBerry => ReadYarnBerry(text),
            PackageManager.Bun => ReadBun(text),
            _ => [],
        };
    }

    /// <summary>package-lock.json v2/v3 ("packages", keyed by install path) or v1 (nested "dependencies").</summary>
    internal static Dictionary<string, HashSet<string>> ReadNpm(string text)
    {
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        if (root.TryGetProperty("packages", out var packages))
        {
            foreach (var entry in packages.EnumerateObject())
            {
                var index = entry.Name.LastIndexOf("node_modules/", StringComparison.Ordinal);
                if (index < 0 || entry.Value.TryGetProperty("link", out _) || !entry.Value.TryGetProperty("version", out var version))
                {
                    continue; // the root project, a workspace folder, or a link
                }
                Add(versions, entry.Name[(index + "node_modules/".Length)..], version.GetString());
            }
        }
        else if (root.TryGetProperty("dependencies", out var dependencies))
        {
            void Walk(JsonElement level)
            {
                foreach (var entry in level.EnumerateObject())
                {
                    if (entry.Value.TryGetProperty("version", out var version))
                    {
                        Add(versions, entry.Name, version.GetString());
                    }
                    if (entry.Value.TryGetProperty("dependencies", out var nested))
                    {
                        Walk(nested);
                    }
                }
            }
            Walk(dependencies);
        }
        return versions;
    }

    /// <summary>
    /// pnpm-lock.yaml: package keys sit at two-space indent under <c>packages:</c> (and
    /// <c>snapshots:</c> in v9), as <c>name@version</c>, <c>/name@version</c> (v6 to v8) or
    /// <c>/name/version</c> (v5), sometimes quoted and with a peer suffix.
    /// </summary>
    internal static Dictionary<string, HashSet<string>> ReadPnpm(string text)
    {
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inPackages = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                inPackages = line.TrimEnd() is "packages:" or "snapshots:";
                continue;
            }
            if (!inPackages || !line.StartsWith("  ", StringComparison.Ordinal) || line.Length < 3 || char.IsWhiteSpace(line[2]))
            {
                continue;
            }

            var key = line.Trim().TrimEnd(':').Trim('\'', '"').TrimStart('/');
            var peerSuffix = key.IndexOf('(');
            if (peerSuffix > 0)
            {
                key = key[..peerSuffix];
            }

            var at = key.LastIndexOf('@');
            if (at > 0)
            {
                Add(versions, key[..at], key[(at + 1)..]);
            }
            else if (key.LastIndexOf('/') is var slash and > 0)
            {
                Add(versions, key[..slash], key[(slash + 1)..]); // v5: /name/version
            }
        }
        return versions;
    }

    /// <summary>
    /// yarn.lock v1: entries start in column 0 with their comma-separated specifiers
    /// (<c>react@^19.0.0, react@^19.2.0:</c>) followed by an indented <c>version "19.3.1"</c>.
    /// </summary>
    internal static Dictionary<string, HashSet<string>> ReadYarnClassic(string text)
    {
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]))
            {
                var first = line.TrimEnd().TrimEnd(':').Split(',')[0].Trim().Trim('"');
                var at = first.LastIndexOf('@');
                var range = at > 0 ? first[(at + 1)..] : "";
                // Registry entries carry a semver range or tag; anything with a protocol is not one.
                name = at > 0 && !range.Contains(':') && !range.Contains('/') ? first[..at] : null;
                continue;
            }
            if (name is not null && YarnClassicVersion().Match(line) is { Success: true } match)
            {
                Add(versions, name, match.Groups[1].Value);
                name = null;
            }
        }
        return versions;
    }

    /// <summary>yarn.lock v2+ (Berry): only <c>resolution: "name@npm:version"</c> lines are registry packages.</summary>
    internal static Dictionary<string, HashSet<string>> ReadYarnBerry(string text)
    {
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match match in YarnBerryResolution().Matches(text))
        {
            Add(versions, match.Groups["name"].Value, match.Groups["version"].Value);
        }
        return versions;
    }

    /// <summary>bun.lock (JSON with comments and trailing commas): <c>"packages": { "x": ["name@version", …] }</c>.</summary>
    internal static Dictionary<string, HashSet<string>> ReadBun(string text)
    {
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("packages", out var packages))
        {
            return versions;
        }
        foreach (var entry in packages.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() == 0 || entry.Value[0].GetString() is not { } id)
            {
                continue;
            }
            var at = id.LastIndexOf('@');
            if (at > 0)
            {
                Add(versions, id[..at], id[(at + 1)..]);
            }
        }
        return versions;
    }

    /// <summary>Records a version only if it is a plain semver, which filters out file:, workspace:, git and tarball entries.</summary>
    private static void Add(Dictionary<string, HashSet<string>> versions, string name, string? version)
    {
        if (string.IsNullOrEmpty(name) || version is null || !SemVersion.TryParse(version, out _))
        {
            return;
        }
        if (!versions.TryGetValue(name, out var set))
        {
            versions[name] = set = new HashSet<string>(StringComparer.Ordinal);
        }
        set.Add(version);
    }

    [GeneratedRegex(@"^\s+version:?\s+""?([^""\s]+)""?")]
    private static partial Regex YarnClassicVersion();

    [GeneratedRegex(@"^\s+resolution:\s+""(?<name>@?[^@""]+)@npm:(?<version>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex YarnBerryResolution();
}
