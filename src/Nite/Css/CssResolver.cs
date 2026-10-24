using System.Text.Json;

namespace Nite.Css;

/// <summary>
/// Resolves stylesheet imports the way CSS tooling does: relative paths, and package paths
/// through <c>package.json</c> <c>exports</c> with the <c>style</c> condition first
/// (<c>@import "tailwindcss"</c> → <c>node_modules/tailwindcss/index.css</c>).
/// </summary>
internal static class CssResolver
{
    private static readonly string[] Conditions = ["style", "default", "import", "require", "browser"];

    public static string? Resolve(string specifier, string baseDirectory)
    {
        specifier = specifier.Split('?', '#')[0];
        if (specifier.StartsWith("~", StringComparison.Ordinal))
        {
            specifier = specifier[1..]; // the webpack convention for "from node_modules"
        }
        if (specifier.StartsWith('.') || Path.IsPathRooted(specifier))
        {
            return WithCssExtension(Path.GetFullPath(Path.Combine(baseDirectory, specifier)));
        }

        var (package, subpath) = Split(specifier);
        for (var directory = baseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var packageDirectory = Path.Combine(directory, "node_modules", package);
            if (Directory.Exists(packageDirectory))
            {
                return ResolveInPackage(packageDirectory, subpath);
            }
        }
        return null;
    }

    /// <summary>The package name and the subpath after it: <c>@scope/pkg/a.css</c> → (<c>@scope/pkg</c>, <c>./a.css</c>).</summary>
    internal static (string Package, string Subpath) Split(string specifier)
    {
        var parts = specifier.Split('/');
        var nameParts = specifier.StartsWith('@') && parts.Length > 1 ? 2 : 1;
        var package = string.Join('/', parts.Take(nameParts));
        var rest = string.Join('/', parts.Skip(nameParts));
        return (package, rest.Length == 0 ? "." : "./" + rest);
    }

    private static string? ResolveInPackage(string packageDirectory, string subpath)
    {
        var manifestPath = Path.Combine(packageDirectory, "package.json");
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = manifest.RootElement;
            if (root.TryGetProperty("exports", out var exports))
            {
                var target = ResolveExports(exports, subpath);
                return target is null ? null : WithCssExtension(Path.GetFullPath(Path.Combine(packageDirectory, target)));
            }
            if (subpath == "." && root.TryGetProperty("style", out var style) && style.GetString() is { } styleFile)
            {
                return WithCssExtension(Path.GetFullPath(Path.Combine(packageDirectory, styleFile)));
            }
        }
        return WithCssExtension(Path.GetFullPath(Path.Combine(packageDirectory, subpath == "." ? "index.css" : subpath)));
    }

    internal static string? ResolveExports(JsonElement exports, string subpath)
    {
        // "exports": "./index.css" or { "style": … } is shorthand for the "." entry.
        if (exports.ValueKind == JsonValueKind.String || (exports.ValueKind == JsonValueKind.Object && !exports.EnumerateObject().Any(p => p.Name.StartsWith('.'))))
        {
            return subpath == "." ? Conditional(exports) : null;
        }
        if (exports.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (exports.TryGetProperty(subpath, out var exact))
        {
            return Conditional(exact);
        }

        // Subpath patterns: "./*": "./dist/*", longest prefix wins.
        string? best = null;
        var bestLength = -1;
        foreach (var entry in exports.EnumerateObject())
        {
            var star = entry.Name.IndexOf('*');
            if (star < 0)
            {
                continue;
            }
            var prefix = entry.Name[..star];
            var suffix = entry.Name[(star + 1)..];
            if (subpath.Length >= prefix.Length + suffix.Length && subpath.StartsWith(prefix, StringComparison.Ordinal)
                && subpath.EndsWith(suffix, StringComparison.Ordinal) && prefix.Length > bestLength
                && Conditional(entry.Value) is { } target)
            {
                bestLength = prefix.Length;
                best = target.Replace("*", subpath[prefix.Length..(subpath.Length - suffix.Length)]);
            }
        }
        return best;
    }

    private static string? Conditional(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Array:
                return value.EnumerateArray().Select(Conditional).FirstOrDefault(t => t is not null);
            case JsonValueKind.Object:
                foreach (var condition in Conditions)
                {
                    if (value.TryGetProperty(condition, out var nested) && Conditional(nested) is { } target)
                    {
                        return target;
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private static string? WithCssExtension(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }
        if (File.Exists(path + ".css"))
        {
            return path + ".css";
        }
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "index.css")) ? Path.Combine(path, "index.css") : null;
    }
}
