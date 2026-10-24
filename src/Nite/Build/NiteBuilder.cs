using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Esbuild;
using Nite.Html;
using Nite.Runtime;

namespace Nite.Build;

/// <summary>
/// The production build: islands, configured entries and the island loader bundled together in a
/// single esbuild pass (minified, code-split, content-hashed) into <c>wwwroot/_nite</c>.
/// </summary>
public sealed class NiteBuilder(EsbuildService esbuild, ILogger? logger = null)
{
    private static readonly string[] AssetExtensions =
        [".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp4", ".webm", ".mp3", ".wav"];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<NiteManifest> BuildAsync(NiteProject project, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var config = project.Config;

        // Entry key → (source file, output name without hash).
        var entries = new Dictionary<string, (string File, string OutputName)>(StringComparer.Ordinal);
        var islands = project.FindIslands();
        foreach (var (name, file) in islands)
        {
            entries[project.RelativeToClientRoot(file)] = (file, "islands/" + name);
        }

        var htmlPages = HtmlEntries(project);
        foreach (var entry in config.Entries.Where(e => !e.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
        {
            var file = project.ResolveClientPath(entry) is { } path ? Dev.NiteDevServer.ResolveModuleFile(path) : null;
            if (file is null)
            {
                throw new InvalidOperationException($"Entry '{entry}' from nite.json was not found in {project.ClientRoot}.");
            }
            var key = project.RelativeToClientRoot(file);
            entries.TryAdd(key, (file, NitePaths.ToUrlPath(Path.ChangeExtension(key, null))));
        }

        // Module scripts and stylesheets referenced by HTML pages are entries too.
        var htmlReferences = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (page, html) in htmlPages)
        {
            var references = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reference in HtmlDocument.FindReferences(html))
            {
                var local = HtmlDocument.ResolveLocal(page, reference.Url);
                var target = local is null ? null : project.ResolveClientPath(local);
                if (target is null)
                {
                    continue;
                }
                var file = reference.Kind == HtmlReferenceKind.ModuleScript ? Dev.NiteDevServer.ResolveModuleFile(target) : File.Exists(target) ? target : null;
                if (file is null)
                {
                    throw new InvalidOperationException($"{page} references '{reference.Url}', which was not found.");
                }
                var key = project.RelativeToClientRoot(file);
                entries.TryAdd(key, (file, NitePaths.ToUrlPath(Path.ChangeExtension(key, null))));
                references[reference.Url] = key;
            }
            htmlReferences[page] = references;
        }

        if (islands.Count > 0)
        {
            var runtimeDirectory = Path.Combine(project.WorkDirectory, "runtime");
            Directory.CreateDirectory(runtimeDirectory);
            var loader = Path.Combine(runtimeDirectory, "islands.js");
            await File.WriteAllTextAsync(loader, RuntimeSources.IslandLoader, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, "renderer.js"), RuntimeSources.RendererForBuild(project), cancellationToken);
            entries[NiteManifest.RuntimeKey] = (loader, "islands-runtime");
        }

        if (Directory.Exists(project.OutputDirectory))
        {
            Directory.Delete(project.OutputDirectory, recursive: true);
        }
        Directory.CreateDirectory(project.OutputDirectory);

        var manifest = new NiteManifest
        {
            Islands = islands.ToDictionary(pair => pair.Key, pair => project.RelativeToClientRoot(pair.Value)),
        };

        if (entries.Count == 0)
        {
            _logger.LogWarning("Nothing to build: no islands in {Islands}, no index.html and no \"entries\" in nite.json", project.IslandsDirectory);
            await File.WriteAllTextAsync(Path.Combine(project.OutputDirectory, NiteManifest.FileName), manifest.ToJson(), cancellationToken);
            return manifest;
        }

        var flags = new List<string>
        {
            "--bundle", "--splitting", "--format=esm", "--platform=browser", "--minify", "--sourcemap",
            $"--target={config.Target}", $"--outdir={project.OutputDirectory}",
            "--entry-names=[dir]/[name]-[hash]", "--chunk-names=chunks/[name]-[hash]", "--asset-names=assets/[name]-[hash]",
            $"--public-path={NiteProject.BasePath}",
            "--jsx=automatic", $"--jsx-import-source={config.JsxImportSource}",
            "--define:process.env.NODE_ENV=\"production\"", "--conditions=production",
            "--loader:.json=json",
        };
        flags.AddRange(AssetExtensions.Select(extension => $"--loader:{extension}=file"));

        var result = await esbuild.BuildAsync(new EsbuildBuildOptions
        {
            WorkingDirectory = project.Directory,
            Entries = entries.Values.Select(e => new EsbuildEntry(e.File, e.OutputName)).ToList(),
            Flags = flags,
            Metafile = true,
        }, cancellationToken);

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        var outputs = JsonDocument.Parse(result.Metafile ?? "{}").RootElement.GetProperty("outputs");
        var byEntryPoint = new Dictionary<string, (string Path, JsonElement Output)>(StringComparer.Ordinal);
        foreach (var output in outputs.EnumerateObject())
        {
            if (output.Value.TryGetProperty("entryPoint", out var entryPoint))
            {
                byEntryPoint[Path.GetFullPath(Path.Combine(project.Directory, entryPoint.GetString()!))] = (output.Name, output.Value);
            }
        }

        foreach (var (key, (file, _)) in entries)
        {
            if (!byEntryPoint.TryGetValue(Path.GetFullPath(file), out var output))
            {
                throw new InvalidOperationException($"esbuild produced no output for {key}.");
            }

            var css = new List<string>();
            if (output.Output.TryGetProperty("cssBundle", out var cssBundle))
            {
                css.Add(OutputRelative(project, cssBundle.GetString()!));
            }

            manifest.Entries[key] = new NiteManifestEntry
            {
                File = OutputRelative(project, output.Path),
                Css = css,
                Imports = StaticImports(outputs, output.Path).Select(p => OutputRelative(project, p)).ToList(),
            };
        }

        foreach (var (page, html) in htmlPages)
        {
            var output = RewriteHtml(page, html, htmlReferences[page], manifest);
            var outputPath = Path.Combine(project.OutputDirectory, page);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, output, cancellationToken);
            manifest.Html[page] = page;
        }

        await File.WriteAllTextAsync(Path.Combine(project.OutputDirectory, NiteManifest.FileName), manifest.ToJson(), cancellationToken);

        var size = Directory.EnumerateFiles(project.OutputDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".map", StringComparison.Ordinal))
            .Sum(f => new FileInfo(f).Length);
        _logger.LogInformation("Built {Count} entr{Plural} ({Size:0.0} KiB) into {Output} in {Elapsed:0} ms",
            entries.Count, entries.Count == 1 ? "y" : "ies", size / 1024.0, project.OutputDirectory,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return manifest;
    }

    /// <summary><c>index.html</c> at the client root (if any) plus HTML pages listed in <c>entries</c>.</summary>
    private static List<(string Page, string Html)> HtmlEntries(NiteProject project)
    {
        var pages = project.Config.Entries
            .Where(e => e.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .Select(e => NitePaths.ToUrlPath(e).TrimStart('/'))
            .ToList();
        if (File.Exists(Path.Combine(project.ClientRoot, "index.html")) && !pages.Contains("index.html"))
        {
            pages.Insert(0, "index.html");
        }

        return pages.Select(page =>
        {
            var file = project.ResolveClientPath(page);
            if (file is null || !File.Exists(file))
            {
                throw new InvalidOperationException($"HTML entry '{page}' from nite.json was not found in {project.ClientRoot}.");
            }
            return (project.RelativeToClientRoot(file), File.ReadAllText(file));
        }).ToList();
    }

    /// <summary>
    /// Points an HTML page at the built files: each module script becomes its hashed bundle (with
    /// modulepreload hints for its chunks and links for its CSS), each stylesheet its hashed copy.
    /// URLs are root-relative under <c>/_nite</c>; the SPA endpoint adds PathBase when serving.
    /// </summary>
    private static string RewriteHtml(string page, string html, Dictionary<string, string> references, NiteManifest manifest) =>
        HtmlDocument.Rewrite(html, reference =>
        {
            if (!references.TryGetValue(reference.Url, out var key) || !manifest.Entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            string Url(string path) => HtmlEncoder.Default.Encode($"{NiteProject.BasePath}/{path}");
            if (reference.Kind == HtmlReferenceKind.Stylesheet)
            {
                return $"<link rel=\"stylesheet\" href=\"{Url(entry.File)}\">";
            }

            var tags = new StringBuilder();
            foreach (var css in entry.Css)
            {
                tags.Append($"<link rel=\"stylesheet\" href=\"{Url(css)}\">\n");
            }
            foreach (var chunk in entry.Imports)
            {
                tags.Append($"<link rel=\"modulepreload\" href=\"{Url(chunk)}\">\n");
            }
            tags.Append($"<script type=\"module\" src=\"{Url(entry.File)}\"></script>");
            return tags.ToString();
        });

    /// <summary>Every chunk an output imports statically, directly or through other chunks.</summary>
    private static IEnumerable<string> StaticImports(JsonElement outputs, string outputPath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([outputPath]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outputs.TryGetProperty(current, out var output) || !output.TryGetProperty("imports", out var imports))
            {
                continue;
            }
            foreach (var import in imports.EnumerateArray())
            {
                if (import.GetProperty("kind").GetString() == "import-statement"
                    && !import.TryGetProperty("external", out _)
                    && import.GetProperty("path").GetString() is { } path
                    && seen.Add(path))
                {
                    queue.Enqueue(path);
                }
            }
        }
        return seen.Order(StringComparer.Ordinal);
    }

    private static string OutputRelative(NiteProject project, string metafilePath) =>
        NitePaths.ToUrlPath(Path.GetRelativePath(project.OutputDirectory, Path.Combine(project.Directory, metafilePath)));
}
