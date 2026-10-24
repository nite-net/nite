using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nite.Css;

/// <summary>
/// Runs the project's PostCSS setup (postcss.config.* and the plugins it names, such as
/// postcss-preset-mantine) inside <see cref="JsEngine"/>. Configs are found the way
/// postcss-load-config finds them, in the client root first and then the project folder.
/// </summary>
/// <remarks>
/// Tailwind's PostCSS plugins are left out: <c>@tailwindcss/postcss</c> needs native code, and Nite
/// runs Tailwind itself (<see cref="TailwindCompiler"/>) before PostCSS, which is the same order.
/// </remarks>
internal sealed class PostCssRunner(ToolBundler bundler, string projectDirectory, ILogger logger) : IDisposable
{
    private static readonly string[] ConfigNames =
    [
        "postcss.config.js", "postcss.config.cjs", "postcss.config.mjs", "postcss.config.ts", "postcss.config.cts", "postcss.config.mts",
        ".postcssrc", ".postcssrc.json", ".postcssrc.js", ".postcssrc.cjs", ".postcssrc.mjs",
    ];

    /// <summary>Plugins Nite replaces with its own Tailwind step.</summary>
    internal static readonly string[] TailwindPlugins = ["tailwindcss", "@tailwindcss/postcss", "@tailwindcss/nesting"];

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private JsEngine? _engine;
    private string? _loadedConfig;

    /// <summary>The config file a project uses, or null when it has none.</summary>
    public static string? FindConfig(NiteProject project)
    {
        foreach (var directory in new[] { project.ClientRoot, project.Directory }.Distinct())
        {
            foreach (var name in ConfigNames)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            var packageJson = Path.Combine(directory, "package.json");
            if (File.Exists(packageJson) && HasPostCssField(packageJson))
            {
                return packageJson;
            }
        }
        return null;
    }

    private static bool HasPostCssField(string packageJson)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("postcss", out var value) && value.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Processes CSS through the config's plugins.</summary>
    public async Task<CssStepResult> ProcessAsync(string config, string css, string file, string mode)
    {
        var engine = await LoadAsync(config);
        var json = await engine.CallAsync("__nitePostcss", css, JsEngine.ToJsPath(file), mode);
        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw CssErrors.FromJs(error, file);
        }

        var dependencies = new List<string>();
        foreach (var dependency in root.GetProperty("dependencies").EnumerateArray())
        {
            dependencies.Add(JsEngine.FromJsPath(dependency.GetString()!));
        }
        foreach (var warning in root.GetProperty("warnings").EnumerateArray())
        {
            logger.LogWarning("{File}: {Warning}", Path.GetFileName(file), warning.GetString());
        }
        return new CssStepResult(root.GetProperty("css").GetString()!, dependencies);
    }

    /// <summary>Bundles the config and its plugins once, then reuses the engine until the config changes.</summary>
    private async Task<JsEngine> LoadAsync(string config)
    {
        await _loadLock.WaitAsync();
        try
        {
            var stamp = config + "|" + File.GetLastWriteTimeUtc(config).Ticks;
            if (_engine is not null && _loadedConfig == stamp)
            {
                return _engine;
            }

            if (!File.Exists(Path.Combine(projectDirectory, "node_modules", "postcss", "package.json")))
            {
                throw new CssException($"{Path.GetFileName(config)} needs the postcss package. Add it to \"devDependencies\" in nite.json.");
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var configModule = await ConfigModuleAsync(config);

            // First pass: evaluate the config on its own to learn which plugins it names by string.
            var probe = await bundler.BundleAsync("postcss-config", ConfigEntry(configModule));
            using var probeEngine = new JsEngine(projectDirectory, logger);
            await probeEngine.ExecuteFileAsync(probe);
            var names = JsonSerializer.Deserialize<List<string>>(await probeEngine.CallAsync("__nitePluginNames", Mode.Development))
                ?? [];
            names.AddRange(JsonSerializer.Deserialize<List<string>>(await probeEngine.CallAsync("__nitePluginNames", Mode.Production)) ?? []);
            names = names.Distinct(StringComparer.Ordinal).Where(n => !TailwindPlugins.Contains(n)).ToList();

            foreach (var name in names)
            {
                if (!File.Exists(Path.Combine(projectDirectory, "node_modules", name, "package.json")))
                {
                    throw new CssException($"{Path.GetFileName(config)} uses the PostCSS plugin '{name}', which is not installed. Add it to \"devDependencies\" in nite.json.");
                }
            }

            // Second pass: bundle PostCSS, the config and every named plugin together.
            var bundle = await bundler.BundleAsync("postcss", RunnerEntry(configModule, names));
            var engine = new JsEngine(projectDirectory, logger);
            await engine.ExecuteFileAsync(bundle);

            _engine?.Dispose();
            _engine = engine;
            _loadedConfig = stamp;
            logger.LogDebug("Loaded PostCSS from {Config} with {Plugins} in {Elapsed:0} ms", Path.GetFileName(config),
                names.Count == 0 ? "no named plugins" : string.Join(", ", names), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return engine;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>A module path esbuild can bundle for the config: the file itself, or a JSON copy for rc files and package.json.</summary>
    private async Task<string> ConfigModuleAsync(string config)
    {
        var name = Path.GetFileName(config);
        if (name is not (".postcssrc" or ".postcssrc.json" or "package.json"))
        {
            return config;
        }

        var text = await File.ReadAllTextAsync(config);
        if (name == "package.json")
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            text = document.RootElement.GetProperty("postcss").GetRawText();
        }
        var directory = Path.Combine(projectDirectory, ".nite", "tools");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "postcssrc.json");
        await File.WriteAllTextAsync(copy, text);
        return copy;
    }

    private const string Normalize = """
        const __niteLoaded = require(__NITE_CONFIG__);
        const __niteConfig = __niteLoaded && __niteLoaded.__esModule ? __niteLoaded.default : (__niteLoaded && __niteLoaded.default) || __niteLoaded;
        const __niteResolve = (mode, file) => {
          const ctx = { env: mode, mode, cwd: process.cwd(), file: file ? { dirname: require("path").dirname(file), basename: require("path").basename(file), extname: require("path").extname(file) } : undefined, options: {} };
          const config = typeof __niteConfig === "function" ? __niteConfig(ctx) : __niteConfig;
          return config || {};
        };
        // [name, options] for named plugins, [null, plugin] for plugin objects and functions.
        const __niteEntries = (config) => {
          const plugins = config.plugins || [];
          const entries = [];
          if (Array.isArray(plugins)) {
            for (const item of plugins) {
              if (!item) continue;
              if (typeof item === "string") entries.push([item, {}]);
              else if (Array.isArray(item)) entries.push([item[0], item[1] ?? {}]);
              else entries.push([null, item]);
            }
          } else {
            for (const [name, options] of Object.entries(plugins)) {
              if (options !== false) entries.push([name, options === true || options == null ? {} : options]);
            }
          }
          return entries;
        };
        """;

    private static string ConfigEntry(string configModule) =>
        Normalize.Replace("__NITE_CONFIG__", ToolBundler.Literal(configModule)) + """

        globalThis.__nitePluginNames = (mode) => JSON.stringify(__niteEntries(__niteResolve(mode)).filter((e) => e[0]).map((e) => e[0]));
        """;

    private static string RunnerEntry(string configModule, IReadOnlyList<string> names)
    {
        var builder = new StringBuilder(Normalize.Replace("__NITE_CONFIG__", ToolBundler.Literal(configModule)));
        builder.Append("\nconst postcss = require(\"postcss\");\n");
        builder.Append($"const __niteSkipped = new Set({JsonSerializer.Serialize(TailwindPlugins)});\n");
        builder.Append("const __niteNamed = {\n");
        foreach (var name in names)
        {
            builder.Append($"  {JsonSerializer.Serialize(name)}: () => require({JsonSerializer.Serialize(name)}),\n");
        }
        builder.Append("};\n");
        builder.Append("""
            const __niteProcessors = {};
            const __niteProcessor = (mode, file) => {
              if (__niteProcessors[mode] && typeof __niteConfig !== "function") return __niteProcessors[mode];
              const config = __niteResolve(mode, file);
              const plugins = [];
              for (const [name, value] of __niteEntries(config)) {
                if (name) {
                  if (__niteSkipped.has(name)) continue;
                  let plugin = __niteNamed[name]();
                  plugin = plugin && plugin.__esModule ? plugin.default : (plugin && plugin.default) || plugin;
                  plugins.push(typeof plugin === "function" ? plugin(value) : plugin);
                } else {
                  const pluginName = value.postcssPlugin || (typeof value === "function" && value.postcss ? value().postcssPlugin : undefined);
                  if (__niteSkipped.has(pluginName)) continue;
                  plugins.push(value);
                }
              }
              const options = {};
              for (const key of ["parser", "syntax", "stringifier"]) {
                const option = config[key];
                if (typeof option === "string") throw new Error(`PostCSS "${key}" given by name is not supported by Nite; require() it in the config.`);
                if (option) options[key] = option;
              }
              return (__niteProcessors[mode] = { processor: postcss(plugins), options });
            };

            globalThis.__nitePostcss = async (css, from, mode) => {
              try {
                const { processor, options } = __niteProcessor(mode, from);
                const result = await processor.process(css, { ...options, from, to: from, map: false });
                return JSON.stringify({
                  css: result.css,
                  dependencies: result.messages.filter((m) => m.type === "dependency" && m.file).map((m) => m.file),
                  warnings: result.warnings().map((w) => (w.line ? `${w.line}:${w.column} ` : "") + w.text + (w.plugin ? ` [${w.plugin}]` : "")),
                });
              } catch (error) {
                return JSON.stringify({ error: { message: error.reason || error.message || String(error), file: error.file || from, line: error.line || 0, column: error.column || 0, source: error.source || null, plugin: error.plugin || null } });
              }
            };
            """);
        return builder.ToString();
    }

    public void Dispose() => _engine?.Dispose();
}

/// <summary>Mode names as PostCSS configs see them in <c>ctx.env</c>.</summary>
internal static class Mode
{
    public const string Development = "development";
    public const string Production = "production";
}
