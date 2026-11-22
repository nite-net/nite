using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nite;

/// <summary>
/// The tsconfig that governs the client's sources, given to esbuild by both the dev server and
/// <c>nite build</c> so that both compile the same code the same way. Without it, options such as
/// <c>experimentalDecorators</c> and <c>useDefineForClassFields</c> (which Lit and other decorator
/// libraries depend on) only applied where esbuild happened to find them, and a component could
/// behave differently in development and production.
/// </summary>
/// <remarks>
/// Vite projects keep a solution-style <c>tsconfig.json</c> (<c>"files": []</c> plus
/// <c>references</c>) whose real options live in <c>tsconfig.app.json</c>. esbuild does not follow
/// <c>references</c>, so the governing file is picked here: the file that declares path aliases,
/// else the referenced config for app code (not <c>tsconfig.node.json</c>, which covers the Vite
/// config), else <c>tsconfig.json</c> itself.
/// </remarks>
internal static class ClientTsconfig
{
    /// <summary>
    /// The compiler options that change what esbuild emits for a single file. A build reads the
    /// rest (paths, baseUrl) from the file itself; a transform has no file system to resolve them.
    /// </summary>
    private static readonly string[] TransformOptions =
    [
        "alwaysStrict", "experimentalDecorators", "importsNotUsedAsValues", "jsx", "jsxFactory",
        "jsxFragmentFactory", "jsxImportSource", "preserveValueImports", "strict", "target",
        "useDefineForClassFields", "verbatimModuleSyntax",
    ];

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The governing tsconfig, or null when the client has none.</summary>
    public static string? Find(string clientRoot)
    {
        var root = Path.Combine(clientRoot, "tsconfig.json");
        if (!File.Exists(root))
        {
            return null;
        }
        if (TsconfigPaths.Load(clientRoot).SourceFile is { } aliases)
        {
            return aliases;
        }

        var json = Parse(root);
        if (json?["references"] is JsonArray references && json["compilerOptions"] is null)
        {
            var directory = Path.GetDirectoryName(root)!;
            var candidates = references
                .Select(r => r?["path"]?.GetValue<string>())
                .OfType<string>()
                .Select(p => Path.Combine(directory, p))
                .Select(p => Directory.Exists(p) ? Path.Combine(p, "tsconfig.json") : p)
                .Where(File.Exists)
                .ToList();
            if (candidates.FirstOrDefault(p => !Path.GetFileName(p).Contains("node", StringComparison.OrdinalIgnoreCase)) is { } app)
            {
                return Path.GetFullPath(app);
            }
        }
        return Path.GetFullPath(root);
    }

    /// <summary>
    /// The <c>--tsconfig-raw</c> flag for a transform: the governing file's compiler options with
    /// <c>extends</c> applied, limited to those that change a single file's output. Null without a tsconfig.
    /// </summary>
    public static string? TransformFlag(string clientRoot, string? projectDirectory = null)
    {
        if (Find(clientRoot) is not { } file)
        {
            return null;
        }

        var options = new JsonObject();
        Merge(file, options, new HashSet<string>(StringComparer.Ordinal), projectDirectory ?? clientRoot);
        var relevant = new JsonObject();
        foreach (var name in TransformOptions)
        {
            if (options[name] is { } value)
            {
                relevant[name] = value.DeepClone();
            }
        }
        var raw = new JsonObject { ["compilerOptions"] = relevant };
        return "--tsconfig-raw=" + raw.ToJsonString();
    }

    /// <summary>Applies a config's <c>extends</c> chain, then its own options on top, as TypeScript does.</summary>
    private static void Merge(string file, JsonObject into, HashSet<string> visited, string projectDirectory)
    {
        if (!visited.Add(Path.GetFullPath(file)) || Parse(file) is not { } json)
        {
            return;
        }

        var directory = Path.GetDirectoryName(file)!;
        var extends = json["extends"] switch
        {
            JsonValue single => [single.GetValue<string>()],
            JsonArray many => many.Select(e => e?.GetValue<string>()).OfType<string>().ToList(),
            _ => new List<string>(),
        };
        foreach (var parent in extends)
        {
            if (ResolveExtends(directory, parent, projectDirectory) is { } target)
            {
                Merge(target, into, visited, projectDirectory);
            }
        }

        if (json["compilerOptions"] is JsonObject options)
        {
            foreach (var (name, value) in options)
            {
                into[name] = value?.DeepClone();
            }
        }
    }

    /// <summary>A relative <c>extends</c>, or a package one (<c>@tsconfig/strictest</c>) found in node_modules.</summary>
    private static string? ResolveExtends(string directory, string specifier, string projectDirectory)
    {
        if (specifier.StartsWith('.') || Path.IsPathRooted(specifier))
        {
            var target = Path.GetFullPath(Path.Combine(directory, specifier));
            return File.Exists(target) ? target : File.Exists(target + ".json") ? target + ".json" : null;
        }

        foreach (var start in new[] { directory, projectDirectory })
        {
            for (var current = start; current is not null; current = Path.GetDirectoryName(current))
            {
                var package = Path.Combine(current, "node_modules", specifier);
                foreach (var candidate in new[] { package, package + ".json", Path.Combine(package, "tsconfig.json") })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        return null;
    }

    private static JsonNode? Parse(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // an invalid tsconfig is the editor's problem to report, not ours
        }
    }
}
