using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Nite.Css;

/// <summary>
/// Tailwind CSS v4 without its native scanner: Tailwind's own compiler (the <c>tailwindcss</c>
/// package, plain JavaScript) runs in <see cref="JsEngine"/>, and <see cref="TailwindScanner"/>
/// supplies the class candidates. Automatic source detection covers the whole project folder, so
/// classes used in Razor views (.cshtml, .razor) are found too.
/// </summary>
internal sealed partial class TailwindCompiler(ToolBundler bundler, NiteProject project, ILogger logger) : IDisposable
{
    private const int UtilitiesFeature = 16;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CompiledStylesheet> _compiled = new(StringComparer.Ordinal);
    private JsEngine? _engine;

    public TailwindScanner Scanner { get; } = new();

    /// <summary>Whether a stylesheet uses Tailwind (and so has to go through it).</summary>
    public static bool Uses(string css) => TailwindDirective().IsMatch(css);

    /// <summary>Stylesheets compiled so far whose output depends on scanned classes.</summary>
    public IReadOnlyCollection<string> StylesheetsWithUtilities =>
        _compiled.Values.Where(c => c.HasUtilities).Select(c => c.File).ToList();

    public async Task<CssStepResult> ProcessAsync(string file, string css)
    {
        var engine = await LoadAsync();
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file + "\0" + css)))[..16];

        // A compile also depends on what it loaded (@import, @reference, @plugin files).
        if (!_compiled.TryGetValue(file, out var compiled) || compiled.Id != id || !compiled.DependenciesUnchanged())
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var json = await engine.CallAsync("__niteTailwindCompile", id, css, JsEngine.ToJsPath(Path.GetDirectoryName(file)!), JsEngine.ToJsPath(file));
            logger.LogDebug("Tailwind compiled {File} in {Elapsed:0} ms", project.RelativeToClientRoot(file), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw CssErrors.FromJs(error, file);
            }

            var sources = new List<TailwindSource>();
            var rootSource = root.GetProperty("root");
            if (rootSource.ValueKind == JsonValueKind.Null)
            {
                sources.Add(new TailwindSource(project.Directory, ".", Auto: true));
            }
            else if (rootSource.ValueKind == JsonValueKind.Object)
            {
                sources.Add(new TailwindSource(JsEngine.FromJsPath(rootSource.GetProperty("base").GetString()!), rootSource.GetProperty("pattern").GetString()!));
            }
            foreach (var source in root.GetProperty("sources").EnumerateArray())
            {
                sources.Add(new TailwindSource(
                    JsEngine.FromJsPath(source.GetProperty("base").GetString()!),
                    source.GetProperty("pattern").GetString()!,
                    source.GetProperty("negated").GetBoolean()));
            }

            var dependencies = root.GetProperty("dependencies").EnumerateArray().Select(d => JsEngine.FromJsPath(d.GetString()!)).ToList();
            var hasUtilities = (root.GetProperty("features").GetInt32() & UtilitiesFeature) != 0;
            compiled = new CompiledStylesheet(file, id, sources, dependencies, hasUtilities,
                dependencies.Where(File.Exists).Distinct().ToDictionary(d => d, d => File.GetLastWriteTimeUtc(d).Ticks));

            // Drop the engine-side compiler this one replaces (the same id was just overwritten).
            if (_compiled.TryGetValue(file, out var previous) && previous.Id != id)
            {
                await engine.CallAsync("__niteTailwindForget", previous.Id);
            }
            _compiled[file] = compiled;
        }

        var buildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (compiled.HasUtilities)
        {
            Scanner.Scan(compiled.Sources);
        }
        var scanned = System.Diagnostics.Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
        var candidates = compiled.HasUtilities ? Scanner.Candidates : [];
        var output = await engine.CallAsync("__niteTailwindBuild", compiled.Id, JsonSerializer.Serialize(candidates));
        logger.LogDebug("Tailwind built {File} from {Count} candidates (scan {Scan:0} ms, build {Build:0} ms)", project.RelativeToClientRoot(file),
            candidates.Count, scanned, System.Diagnostics.Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds - scanned);
        return new CssStepResult(output, compiled.Dependencies);
    }

    /// <summary>Whether a changed file could hold classes for a compiled stylesheet.</summary>
    public bool CoversFile(string file) =>
        _compiled.Values.Any(c => c.HasUtilities && c.Sources.Any(s => !s.Negated && Covers(s, file)));

    private static bool Covers(TailwindSource source, string file)
    {
        var root = Path.GetFullPath(Path.Combine(source.Base, source.Pattern.Split('*')[0].TrimEnd('/')));
        return (file == root || file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            && (!source.Auto || TailwindScanner.IsScannable(Path.GetRelativePath(source.Base, file)));
    }

    private async Task<JsEngine> LoadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_engine is not null)
            {
                return _engine;
            }

            var manifest = Path.Combine(project.Directory, "node_modules", "tailwindcss", "package.json");
            if (!File.Exists(manifest))
            {
                throw new CssException("This stylesheet uses Tailwind, but the tailwindcss package is not installed. Add it to \"devDependencies\" in nite.json.");
            }
            using (var document = JsonDocument.Parse(File.ReadAllText(manifest)))
            {
                var version = document.RootElement.GetProperty("version").GetString() ?? "";
                if (!version.StartsWith("4.", StringComparison.Ordinal))
                {
                    throw new CssException($"Nite supports Tailwind CSS 4; this project has {version}. See https://tailwindcss.com/docs/upgrade-guide.");
                }
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var bundle = await bundler.BundleAsync("tailwind", Entry);
            var engine = new JsEngine(project.Directory, logger, LoadModule);
            await engine.ExecuteFileAsync(bundle);
            logger.LogDebug("Loaded Tailwind in {Elapsed:0} ms", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return _engine = engine;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Bundles a module Tailwind asks for (<c>@plugin "@tailwindcss/typography"</c>, <c>@config</c>)
    /// into a script that leaves it in <c>globalThis.__niteModule</c>. Runs synchronously because
    /// the engine is waiting on it; the bundler does not need the engine.
    /// </summary>
    private string LoadModule(string id, string baseDirectory)
    {
        var target = id.StartsWith('.') || Path.IsPathRooted(id) ? Path.GetFullPath(Path.Combine(baseDirectory, id)) : id;
        var entry = $"""
            const loaded = require({ToolBundler.Literal(target)});
            globalThis.__niteModule = loaded && loaded.__esModule ? loaded.default : (loaded && loaded.default) || loaded;
            """;
        var bundle = bundler.BundleAsync("tailwind-module", entry).GetAwaiter().GetResult();
        return File.ReadAllText(bundle);
    }

    private const string Entry = """
        const tailwind = require("tailwindcss");
        const path = require("path");
        const compilers = new Map();

        const failure = (error, from) => JSON.stringify({ error: { message: error.message || String(error), file: error.file || from, line: error.line || 0, column: error.column || 0 } });

        globalThis.__niteTailwindCompile = async (id, css, base, from) => {
          const dependencies = [];
          try {
            const compiler = await tailwind.compile(css, {
              base,
              from,
              loadStylesheet: async (request, fromBase) => {
                const file = __host.resolveStylesheet(request, fromBase);
                if (!file) throw new Error(`Cannot find stylesheet '${request}' (imported from ${fromBase})`);
                dependencies.push(file);
                return { path: file, base: path.dirname(file), content: __host.readFile(file) };
              },
              loadModule: async (request, fromBase) => {
                (0, eval)(__host.bundleModule(request, fromBase));
                const module = globalThis.__niteModule;
                delete globalThis.__niteModule;
                return { path: request, base: fromBase, module };
              },
            });
            compilers.set(id, compiler);
            return JSON.stringify({ root: compiler.root, sources: compiler.sources, features: compiler.features, dependencies });
          } catch (error) {
            return failure(error, from);
          }
        };

        globalThis.__niteTailwindBuild = (id, candidates) => compilers.get(id).build(JSON.parse(candidates));
        globalThis.__niteTailwindForget = (id) => { compilers.delete(id); return ""; };
        """;

    public void Dispose() => _engine?.Dispose();

    private sealed record CompiledStylesheet(string File, string Id, IReadOnlyList<TailwindSource> Sources, IReadOnlyList<string> Dependencies,
        bool HasUtilities, IReadOnlyDictionary<string, long> Stamps)
    {
        public bool DependenciesUnchanged() =>
            Stamps.All(pair => System.IO.File.Exists(pair.Key) && System.IO.File.GetLastWriteTimeUtc(pair.Key).Ticks == pair.Value);
    }

    [GeneratedRegex("""@import\s+(?:url\(\s*)?["']tailwindcss(?:/[^"']*)?["']|@(?:tailwind|reference|apply|theme|plugin|config|utility|custom-variant|source)\b""")]
    private static partial Regex TailwindDirective();
}
