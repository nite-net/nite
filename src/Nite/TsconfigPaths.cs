using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nite;

/// <summary>
/// <c>compilerOptions.paths</c> (and <c>baseUrl</c>) from the client's tsconfig, so imports such as
/// <c>@/components/Button</c> resolve in the dev server the way TypeScript and esbuild resolve them.
/// Starts from the config <see cref="ClientTsconfig.Find"/> picks (and its <c>extends</c>), so the
/// dev server and the build agree. Aliases declared only elsewhere (the root <c>tsconfig.json</c> of
/// Vite's layout) still apply: they are read from there, and <see cref="ClientTsconfig.ForBuild"/>
/// hands them to the build.
/// </summary>
internal sealed class TsconfigPaths
{
    private readonly List<(string Pattern, IReadOnlyList<string> Targets, string BaseDirectory)> _paths = [];
    private string? _baseUrl;

    /// <summary>The config file that declared the paths.</summary>
    public string? SourceFile { get; private set; }

    /// <summary>Each alias pattern, its targets, and the folder the targets are relative to.</summary>
    public IReadOnlyList<(string Pattern, IReadOnlyList<string> Targets, string BaseDirectory)> Entries => _paths;

    /// <summary>The resolved <c>baseUrl</c>, if any config declared one.</summary>
    public string? BaseUrl => _baseUrl;

    public bool IsEmpty => _paths.Count == 0 && _baseUrl is null;

    public static TsconfigPaths Load(string clientRoot)
    {
        var result = new TsconfigPaths();
        if (ClientTsconfig.Find(clientRoot) is not { } governing)
        {
            return result;
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        result.Visit(governing, visited);
        if (result.IsEmpty)
        {
            result.Visit(Path.Combine(clientRoot, "tsconfig.json"), visited);
        }
        return result;
    }

    /// <summary>The file an aliased import points at, or null when it is not an alias (a package, then).</summary>
    public string? Resolve(string specifier)
    {
        foreach (var (pattern, targets, baseDirectory) in _paths)
        {
            var star = pattern.IndexOf('*');
            string? captured = null;
            if (star < 0 && specifier == pattern)
            {
                captured = "";
            }
            else if (star >= 0 && specifier.Length >= pattern.Length - 1
                && specifier.StartsWith(pattern[..star], StringComparison.Ordinal)
                && specifier.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal))
            {
                captured = specifier[star..(specifier.Length - (pattern.Length - star - 1))];
            }
            if (captured is null)
            {
                continue;
            }

            foreach (var target in targets)
            {
                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, target.Replace("*", captured)));
                if (Dev.NiteDevServer.ResolveModuleFile(candidate) is { } file)
                {
                    return file;
                }
            }
        }

        // TypeScript also resolves non-relative imports against baseUrl, after paths.
        return _baseUrl is not null && Dev.NiteDevServer.ResolveModuleFile(Path.GetFullPath(Path.Combine(_baseUrl, specifier))) is { } fromBase
            ? fromBase
            : null;
    }

    private void Visit(string file, HashSet<string> visited)
    {
        if (!visited.Add(Path.GetFullPath(file)) || !File.Exists(file))
        {
            return;
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return; // an invalid tsconfig is the editor's problem to report, not ours
        }

        var directory = Path.GetDirectoryName(file)!;
        var options = json?["compilerOptions"];
        var baseUrl = String(options?["baseUrl"]) is { } url ? Path.GetFullPath(Path.Combine(directory, url)) : null;
        if (options?["paths"] is JsonObject paths && _paths.Count == 0)
        {
            SourceFile = Path.GetFullPath(file);
            foreach (var (pattern, value) in paths)
            {
                var targets = value is JsonArray array ? array.Select(String).OfType<string>().ToList() : [];
                _paths.Add((pattern, targets, baseUrl ?? directory));
            }
        }
        _baseUrl ??= baseUrl;

        if (json?["references"] is JsonArray references)
        {
            foreach (var reference in references)
            {
                if (String(reference?["path"]) is { } path)
                {
                    var target = Path.Combine(directory, path);
                    Visit(Directory.Exists(target) ? Path.Combine(target, "tsconfig.json") : target, visited);
                }
            }
        }

        var extends = json?["extends"] switch
        {
            JsonArray many => many.Select(String).OfType<string>().ToList(),
            var single => String(single) is { } one ? [one] : new List<string>(),
        };
        foreach (var parent in extends.Where(e => e.StartsWith('.')))
        {
            var target = Path.Combine(directory, parent);
            Visit(target.EndsWith(".json", StringComparison.Ordinal) ? target : target + ".json", visited);
        }
    }

    /// <summary>A string value, or null for anything else (a typo in a tsconfig must not throw).</summary>
    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
