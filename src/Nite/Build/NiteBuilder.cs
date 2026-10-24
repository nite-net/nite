using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Esbuild;
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

        foreach (var entry in config.Entries)
        {
            var file = project.ResolveClientPath(entry) is { } path ? Dev.NiteDevServer.ResolveModuleFile(path) : null;
            if (file is null)
            {
                throw new InvalidOperationException($"Entry '{entry}' from nite.json was not found in {project.ClientRoot}.");
            }
            var key = project.RelativeToClientRoot(file);
            entries.TryAdd(key, (file, NitePaths.ToUrlPath(Path.ChangeExtension(key, null))));
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
            _logger.LogWarning("Nothing to build: no islands in {Islands} and no \"entries\" in nite.json", project.IslandsDirectory);
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

        await File.WriteAllTextAsync(Path.Combine(project.OutputDirectory, NiteManifest.FileName), manifest.ToJson(), cancellationToken);

        var size = Directory.EnumerateFiles(project.OutputDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".map", StringComparison.Ordinal))
            .Sum(f => new FileInfo(f).Length);
        _logger.LogInformation("Built {Count} entr{Plural} ({Size:0.0} KiB) into {Output} in {Elapsed:0} ms",
            entries.Count, entries.Count == 1 ? "y" : "ies", size / 1024.0, project.OutputDirectory,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return manifest;
    }

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
