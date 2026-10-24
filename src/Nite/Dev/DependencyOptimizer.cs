using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nite.Esbuild;

namespace Nite.Dev;

/// <summary>
/// Vite-style dependency pre-bundling. Every bare specifier the app imports becomes one ESM file in
/// <c>.nite/deps</c>, built in a single esbuild pass with code splitting, so packages that share a
/// dependency (react-dom and react) share one copy of it. CommonJS packages get a generated wrapper
/// that re-exports their named exports, because browsers cannot import names from CJS.
/// </summary>
internal sealed class DependencyOptimizer(EsbuildService esbuild, ILogger logger)
{
    private const string MetaFileName = "_meta.json";

    public async Task<DependencyBundle> OptimizeAsync(NiteProject project, IEnumerable<string> specifiers, string lockFingerprint, CancellationToken cancellationToken)
    {
        var wanted = specifiers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var hash = Hash(lockFingerprint, project.Config.JsxImportSource, string.Join('\n', wanted));
        var outDirectory = Path.Combine(project.WorkDirectory, "deps");
        var sourceDirectory = Path.Combine(project.WorkDirectory, "deps-src");

        if (TryLoad(outDirectory, hash) is { } cached)
        {
            return cached;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Reset(outDirectory);
        Reset(sourceDirectory);

        var probes = await ProbeAsync(project, wanted, sourceDirectory, cancellationToken);
        var entries = new List<EsbuildEntry>();
        var imports = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (specifier, probe) in probes)
        {
            var name = SafeName(specifier);
            var wrapper = Path.Combine(sourceDirectory, name + ".js");
            await File.WriteAllTextAsync(wrapper, BuildWrapper(project, specifier, probe), cancellationToken);
            entries.Add(new EsbuildEntry(wrapper, name));
            imports[specifier] = $"deps/{name}.js?v={hash}";
        }

        if (entries.Count > 0)
        {
            await esbuild.BuildAsync(new EsbuildBuildOptions
            {
                WorkingDirectory = project.Directory,
                Entries = entries,
                Flags =
                [
                    "--bundle", "--splitting", "--format=esm", "--platform=browser", "--target=es2022",
                    $"--outdir={outDirectory}", "--chunk-names=chunk-[hash]", "--sourcemap",
                    "--define:process.env.NODE_ENV=\"development\"", "--conditions=development",
                    "--jsx=automatic", $"--jsx-import-source={project.Config.JsxImportSource}",
                ],
            }, cancellationToken);
        }

        var bundle = new DependencyBundle(hash, imports);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, MetaFileName), JsonSerializer.Serialize(bundle), cancellationToken);
        logger.LogInformation("Pre-bundled {Count} dependenc{Plural} in {Elapsed:0} ms", imports.Count, imports.Count == 1 ? "y" : "ies",
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return bundle;
    }

    /// <summary>
    /// Works out, for each specifier, which file it resolves to, whether that file is CJS or ESM, and
    /// what it exports. Specifiers that cannot be resolved are dropped with a warning instead of
    /// failing the whole bundle.
    /// </summary>
    private async Task<Dictionary<string, Probe>> ProbeAsync(NiteProject project, List<string> specifiers, string sourceDirectory, CancellationToken cancellationToken)
    {
        var probeDirectory = Path.Combine(sourceDirectory, "probe");
        Directory.CreateDirectory(probeDirectory);
        var stubs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var specifier in specifiers)
        {
            var stub = Path.Combine(probeDirectory, SafeName(specifier) + ".js");
            await File.WriteAllTextAsync(stub, $"import * as m from {JsonSerializer.Serialize(specifier)};\nconsole.log(m);\n", cancellationToken);
            stubs[specifier] = stub;
        }

        JsonElement metafile = default;
        while (stubs.Count > 0)
        {
            try
            {
                var result = await esbuild.BuildAsync(new EsbuildBuildOptions
                {
                    WorkingDirectory = project.Directory,
                    Entries = stubs.Values.Select(path => new EsbuildEntry(path)).ToList(),
                    Flags = ["--bundle", "--format=esm", "--platform=browser", $"--outdir={Path.Combine(probeDirectory, "out")}",
                        "--conditions=development", "--define:process.env.NODE_ENV=\"development\""],
                    Write = false,
                    Metafile = true,
                }, cancellationToken);
                metafile = JsonDocument.Parse(result.Metafile ?? "{}").RootElement;
                break;
            }
            catch (EsbuildException ex) when (ex.Errors.Any(e => e.File is not null))
            {
                // Drop the stubs that failed to resolve and try the rest again.
                var failed = stubs.Where(s => ex.Errors.Any(e => e.File is { } file && Path.GetFullPath(Path.Combine(project.Directory, file)) == s.Value)).ToList();
                if (failed.Count == 0)
                {
                    throw;
                }
                foreach (var (specifier, _) in failed)
                {
                    logger.LogWarning("Cannot pre-bundle '{Specifier}': {Error}", specifier,
                        ex.Errors.First(e => e.File is { } file && Path.GetFullPath(Path.Combine(project.Directory, file)) == stubs[specifier]).Text);
                    stubs.Remove(specifier);
                }
            }
        }

        var probes = new Dictionary<string, Probe>(StringComparer.Ordinal);
        if (stubs.Count == 0)
        {
            return probes;
        }

        var inputs = metafile.GetProperty("inputs");
        var esmFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (specifier, stub) in stubs)
        {
            var stubKey = NitePaths.ToUrlPath(Path.GetRelativePath(project.Directory, stub));
            var resolved = inputs.GetProperty(stubKey).GetProperty("imports").EnumerateArray()
                .First(i => i.TryGetProperty("original", out var original) && original.GetString() == specifier)
                .GetProperty("path").GetString()!;
            var isEsm = inputs.TryGetProperty(resolved, out var input) && input.TryGetProperty("format", out var format) && format.GetString() == "esm";
            var fullPath = Path.GetFullPath(Path.Combine(project.Directory, resolved));

            if (isEsm)
            {
                esmFiles[specifier] = fullPath;
                probes[specifier] = new Probe(IsEsm: true, HasDefault: false, []);
            }
            else
            {
                probes[specifier] = new Probe(IsEsm: false, HasDefault: true, CjsExportLexer.GetExports(fullPath));
            }
        }

        if (esmFiles.Count > 0)
        {
            // Building the ESM entry files directly makes esbuild report their exact export names.
            var result = await esbuild.BuildAsync(new EsbuildBuildOptions
            {
                WorkingDirectory = project.Directory,
                Entries = esmFiles.Select(e => new EsbuildEntry(e.Value, SafeName(e.Key))).ToList(),
                Flags = ["--bundle", "--format=esm", "--platform=browser", $"--outdir={Path.Combine(probeDirectory, "esm")}",
                    "--conditions=development", "--define:process.env.NODE_ENV=\"development\""],
                Write = false,
                Metafile = true,
            }, cancellationToken);

            var outputs = JsonDocument.Parse(result.Metafile ?? "{}").RootElement.GetProperty("outputs");
            foreach (var (specifier, _) in esmFiles)
            {
                var output = outputs.EnumerateObject().FirstOrDefault(o =>
                    o.Name.EndsWith("/" + SafeName(specifier) + ".js", StringComparison.Ordinal));
                var hasDefault = output.Value.ValueKind == JsonValueKind.Object
                    && output.Value.GetProperty("exports").EnumerateArray().Any(e => e.GetString() == "default");
                probes[specifier] = probes[specifier] with { HasDefault = hasDefault };
            }
        }

        return probes;
    }

    private static string BuildWrapper(NiteProject project, string specifier, Probe probe)
    {
        var quoted = JsonSerializer.Serialize(specifier);
        var builder = new StringBuilder();
        if (probe.IsEsm)
        {
            builder.Append("export * from ").Append(quoted).Append(";\n");
            if (probe.HasDefault)
            {
                builder.Append("export { default } from ").Append(quoted).Append(";\n");
            }
        }
        else
        {
            // esbuild's namespace object for a CJS module carries every property of module.exports.
            builder.Append("import * as __nite_ns from ").Append(quoted).Append(";\n");
            builder.Append("export default __nite_ns.default;\n");
            if (probe.Exports.Count > 0)
            {
                builder.Append("export const { ").AppendJoin(", ", probe.Exports).Append(" } = __nite_ns;\n");
            }
        }
        return builder.ToString();
    }

    /// <summary>A file name for a specifier: <c>react/jsx-runtime</c> → <c>react__jsx-runtime</c>.</summary>
    internal static string SafeName(string specifier) =>
        specifier.Replace("/", "__").Replace("@", "").Replace(".", "_");

    private static DependencyBundle? TryLoad(string outDirectory, string hash)
    {
        var path = Path.Combine(outDirectory, MetaFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var bundle = JsonSerializer.Deserialize<DependencyBundle>(File.ReadAllText(path));
            return bundle?.Hash == hash ? bundle : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Reset(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        Directory.CreateDirectory(directory);
    }

    private static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts.Prepend(EsbuildBinary.Version)))))[..10];

    private sealed record Probe(bool IsEsm, bool HasDefault, IReadOnlyCollection<string> Exports);
}

/// <summary>The pre-bundled dependencies: specifier → URL (relative to <c>/_nite/</c>).</summary>
internal sealed record DependencyBundle(string Hash, IReadOnlyDictionary<string, string> Imports);
