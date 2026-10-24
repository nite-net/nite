using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nite.Esbuild;

namespace Nite.Css;

/// <summary>
/// Bundles a JavaScript build tool (PostCSS with the project's plugins, Tailwind, Less) from the
/// project's node_modules into one script for <see cref="JsEngine"/>. Bundles are cached in
/// <c>.nite/tools</c> and rebuilt when the entry or any file that went into them changes.
/// </summary>
internal sealed class ToolBundler(EsbuildService esbuild, string projectDirectory, string workDirectory)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Bundles <paramref name="entrySource"/> (CommonJS, resolved from the project) and returns the script's path.</summary>
    public async Task<string> BundleAsync(string name, string entrySource, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(workDirectory, "tools");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(EsbuildBinary.Version + "\0" + entrySource)))[..12];
        var output = Path.Combine(directory, $"{name}-{hash}.js");
        var inputsFile = output + ".inputs.json";

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(output) && InputsUnchanged(inputsFile))
            {
                return output;
            }

            Directory.CreateDirectory(directory);
            // The entry sits in the project folder so its requires resolve from the project's node_modules.
            var entry = Path.Combine(directory, $"{name}-{hash}.entry.cjs");
            await File.WriteAllTextAsync(entry, entrySource, cancellationToken);

            var result = await esbuild.BuildAsync(new EsbuildBuildOptions
            {
                WorkingDirectory = projectDirectory,
                Entries = [new EsbuildEntry(entry)],
                Flags =
                [
                    "--bundle", "--platform=node", "--format=iife", "--target=es2022", $"--outfile={output}",
                    "--resolve-extensions=.js,.cjs,.mjs,.ts,.cts,.mts,.json",
                    "--define:import.meta.url=__nite_import_meta_url", "--define:import.meta.dirname=__nite_import_meta_dirname",
                    "--define:process.env.NODE_ENV=\"production\"", "--log-override:empty-import-meta=silent",
                    "--log-override:require-resolve-not-external=silent", "--log-override:commonjs-variable-in-esm=silent",
                ],
                Metafile = true,
            }, cancellationToken);

            var inputs = JsonDocument.Parse(result.Metafile ?? "{}").RootElement.GetProperty("inputs").EnumerateObject()
                .Select(i => Path.GetFullPath(Path.Combine(projectDirectory, i.Name)))
                .Where(File.Exists)
                .ToDictionary(path => path, Stamp);
            await File.WriteAllTextAsync(inputsFile, JsonSerializer.Serialize(inputs), cancellationToken);
            return output;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool InputsUnchanged(string inputsFile)
    {
        try
        {
            var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(inputsFile));
            return inputs is not null && inputs.All(pair => File.Exists(pair.Key) && Stamp(pair.Key) == pair.Value);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
    }

    /// <summary>A JavaScript string literal for a path, with forward slashes.</summary>
    public static string Literal(string path) => JsonSerializer.Serialize(JsEngine.ToJsPath(path));
}
