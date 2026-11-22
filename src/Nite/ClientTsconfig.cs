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
/// <c>references</c>, so the governing file is picked here: the referenced config for app code (not
/// <c>tsconfig.node.json</c>, which covers the Vite config), else <c>tsconfig.json</c> itself.
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

    /// <summary>The governing tsconfig, or null when the client has none. Path aliases do not affect the choice.</summary>
    public static string? Find(string clientRoot)
    {
        var root = Path.Combine(clientRoot, "tsconfig.json");
        if (!File.Exists(root))
        {
            return null;
        }

        // A root with references (Vite's layout) is not the config for the app's code, even when it
        // also has compilerOptions: shadcn's setup puts paths there as well as in tsconfig.app.json.
        if (Parse(root)?["references"] is JsonArray references)
        {
            var directory = Path.GetDirectoryName(root)!;
            var app = references
                .Select(r => r?["path"] is JsonValue value && value.TryGetValue<string>(out var path) ? path : null)
                .OfType<string>()
                .Select(p => Path.Combine(directory, p))
                .Select(p => Directory.Exists(p) ? Path.Combine(p, "tsconfig.json") : p)
                .Where(File.Exists)
                .FirstOrDefault(p => !Path.GetFileName(p).Contains("node", StringComparison.OrdinalIgnoreCase));
            if (app is not null)
            {
                return Path.GetFullPath(app);
            }
        }
        return Path.GetFullPath(root);
    }

    /// <summary>
    /// The file for <c>nite build</c>'s <c>--tsconfig</c>. Usually <see cref="Find"/>'s file (esbuild
    /// follows its <c>extends</c>, so paths in a base config still resolve). When the only path aliases
    /// are declared outside that file's chain (paths in the root <c>tsconfig.json</c> of Vite's layout,
    /// not in <c>tsconfig.app.json</c>), the dev server still resolves them through
    /// <see cref="TsconfigPaths"/>, so the build gets a generated config in <paramref name="workDirectory"/>
    /// that extends the governing file and adds those aliases.
    /// </summary>
    public static string? ForBuild(string clientRoot, string workDirectory, string? projectDirectory = null)
    {
        if (Find(clientRoot) is not { } file)
        {
            return null;
        }

        var aliases = TsconfigPaths.Load(clientRoot);
        if (aliases.SourceFile is not { } declaring || Chain(file, projectDirectory ?? clientRoot).Contains(declaring))
        {
            return file;
        }

        Directory.CreateDirectory(workDirectory);
        var generated = Path.Combine(workDirectory, "tsconfig.build.json");
        string Relative(string path) => "./" + Path.GetRelativePath(workDirectory, path).Replace('\\', '/');
        var paths = new JsonObject();
        foreach (var (pattern, targets, baseDirectory) in aliases.Entries)
        {
            paths[pattern] = new JsonArray(targets.Select(t => (JsonNode)Relative(Path.Combine(baseDirectory, t))).ToArray());
        }
        var options = new JsonObject { ["paths"] = paths };
        if (aliases.BaseUrl is { } baseUrl)
        {
            options["baseUrl"] = Relative(baseUrl);
        }
        var json = new JsonObject { ["extends"] = file, ["compilerOptions"] = options };
        File.WriteAllText(generated, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return generated;
    }

    /// <summary>A config and the configs it extends, as full paths.</summary>
    internal static HashSet<string> Chain(string file, string projectDirectory)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string current)
        {
            if (!chain.Add(Path.GetFullPath(current)) || Parse(current) is not { } json)
            {
                return;
            }
            foreach (var parent in Extends(json))
            {
                if (ResolveExtends(Path.GetDirectoryName(current)!, parent, projectDirectory) is { } target)
                {
                    Visit(target);
                }
            }
        }
        Visit(file);
        return chain;
    }

    /// <summary>The <c>extends</c> entries of a config; a value of the wrong type counts as none.</summary>
    private static List<string> Extends(JsonNode json) => json["extends"] switch
    {
        JsonValue single when single.TryGetValue<string>(out var one) => [one],
        JsonArray many => many.Select(e => e is JsonValue value && value.TryGetValue<string>(out var each) ? each : null).OfType<string>().ToList(),
        _ => [],
    };

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
        foreach (var parent in Extends(json))
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
