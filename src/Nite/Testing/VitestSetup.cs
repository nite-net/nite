using System.Text;
using System.Text.Json;
using Nite.Packages;

namespace Nite.Testing;

/// <summary>
/// What a project has installed for Vitest, and the Vitest configuration that matches its
/// <c>nite.json</c>. Vitest compiles tests with Vite, not with NiteFx, so the configuration
/// carries over the settings that change how code resolves and compiles: path aliases, public
/// environment variables, <c>nite:config</c> and the JSX import source.
/// </summary>
public sealed class VitestSetup
{
    /// <summary>Vitest versions before this lack fixes NiteFx relies on (CVE-2025-24964 among them).</summary>
    public static readonly SemVersion MinimumVersion = SemVersion.Parse("3.2.0");

    /// <summary>The generated configuration, under <c>.nite</c>.</summary>
    public const string ConfigFileName = "vitest.config.mjs";

    private static readonly string[] UserConfigNames =
        ["vitest.config.ts", "vitest.config.mts", "vitest.config.js", "vitest.config.mjs", "vitest.config.cts", "vitest.config.cjs"];

    private VitestSetup(NiteProject project, string vitestDirectory, SemVersion version, string entry, SemVersion? viteVersion, string? nodeRange)
    {
        Project = project;
        VitestDirectory = vitestDirectory;
        Version = version;
        Entry = entry;
        ViteVersion = viteVersion;
        NodeRange = nodeRange;
    }

    public NiteProject Project { get; }

    /// <summary>The installed <c>vitest</c> package folder.</summary>
    public string VitestDirectory { get; }

    public SemVersion Version { get; }

    /// <summary>The script that starts Vitest (its <c>bin</c>), run with Node.</summary>
    public string Entry { get; }

    /// <summary>The Vite that Vitest runs on; null when it cannot be found.</summary>
    public SemVersion? ViteVersion { get; }

    /// <summary>The Node versions that Vite supports (its <c>engines.node</c>).</summary>
    public string? NodeRange { get; }

    /// <summary>The installed Vitest, or null when the project has none.</summary>
    public static VitestSetup? Find(NiteProject project)
    {
        var directory = Path.Combine(project.Directory, "node_modules", "vitest");
        if (ReadManifest(directory) is not { } vitest || !SemVersion.TryParse(vitest.Version, out var version))
        {
            return null;
        }

        var bin = vitest.Root.TryGetProperty("bin", out var binElement)
            ? binElement.ValueKind == JsonValueKind.String ? binElement.GetString()
                : binElement.TryGetProperty("vitest", out var named) ? named.GetString() : null
            : null;
        var entry = Path.GetFullPath(Path.Combine(directory, bin ?? "vitest.mjs"));

        // Vite resolves from Vitest's folder: nested under it, or hoisted to the top.
        var vite = ReadManifest(Path.Combine(directory, "node_modules", "vite")) ?? ReadManifest(Path.Combine(project.Directory, "node_modules", "vite"));
        var viteVersion = vite is not null && SemVersion.TryParse(vite.Value.Version, out var parsedVite) ? parsedVite : null;
        var nodeRange = vite?.Root.TryGetProperty("engines", out var engines) == true && engines.TryGetProperty("node", out var node) ? node.GetString() : null;
        return new VitestSetup(project, directory, version, entry, viteVersion, nodeRange);
    }

    /// <summary>Whether Node <paramref name="version"/> runs this Vite; true when Vite does not say.</summary>
    public bool SupportsNode(SemVersion version) =>
        NodeRange is null || !SemVerRange.TryParse(NodeRange, out var range) || range.IsSatisfiedBy(version);

    /// <summary>The configuration file of the project's own, which the generated one merges over itself.</summary>
    public string? UserConfig =>
        new[] { Project.Directory, Project.ClientRoot }.Distinct()
            .SelectMany(folder => UserConfigNames.Select(name => Path.Combine(folder, name)))
            .FirstOrDefault(File.Exists);

    /// <summary>The coverage provider package for <paramref name="provider"/>, if installed.</summary>
    public bool HasCoverageProvider(string provider) =>
        File.Exists(Path.Combine(Project.Directory, "node_modules", "@vitest", "coverage-" + provider, "package.json"));

    /// <summary>The <c>test.coverage</c> provider from nite.json, checked; null when coverage is not on.</summary>
    public string? CoverageProvider => Project.Config.Test.Coverage switch
    {
        null or "" => null,
        "v8" or "istanbul" => Project.Config.Test.Coverage,
        var other => throw new InvalidOperationException($"test.coverage \"{other}\" in nite.json is not a coverage provider. Use \"v8\" or \"istanbul\"."),
    };

    /// <summary>Where coverage reports go: <c>.nite/coverage</c>.</summary>
    public string CoverageDirectory => Path.Combine(Project.WorkDirectory, "coverage");

    /// <summary>Writes the configuration to <c>.nite/vitest.config.mjs</c> and returns its path.</summary>
    public string WriteConfig()
    {
        Directory.CreateDirectory(Project.WorkDirectory);
        var path = Path.Combine(Project.WorkDirectory, ConfigFileName);
        var contents = GenerateConfig();
        if (!File.Exists(path) || File.ReadAllText(path) != contents)
        {
            File.WriteAllText(path, contents);
        }
        return path;
    }

    /// <summary>The Vitest configuration module for this project.</summary>
    public string GenerateConfig()
    {
        var project = Project;
        var config = project.Config;
        var js = new StringBuilder();
        js.Append("// Generated by NiteFx from nite.json on every nite test. Do not edit: put your own settings in vitest.config.ts,\n");
        js.Append("// which this file merges over its own.\n");
        js.Append("import { mergeConfig } from \"vitest/config\";\n");
        if (config.JsxInJs)
        {
            js.Append("import * as vite from \"vite\";\n");
        }
        if (UserConfig is { } user)
        {
            js.Append("import user from ").Append(Json(NitePaths.ToUrlPath(user))).Append(";\n");
        }
        js.Append('\n');

        js.Append("const niteConfig = Object.freeze(").Append(config.Test.Config is { } values ? values.GetRawText() : "{}").Append(");\n\n");

        js.Append("const base = {\n");
        js.Append("  root: ").Append(Json(NitePaths.ToUrlPath(project.ClientRoot))).Append(",\n");

        var aliases = TsconfigPaths.Load(project.ClientRoot).Entries.Where(e => e.Targets.Count > 0).ToList();
        if (aliases.Count > 0)
        {
            js.Append("  resolve: {\n    alias: [\n");
            foreach (var (pattern, targets, baseDirectory) in aliases)
            {
                var target = NitePaths.ToUrlPath(Path.GetFullPath(Path.Combine(baseDirectory, targets[0].Replace("*", "__NITE_STAR__"))));
                var find = "^" + EscapeRegex(pattern).Replace("\\*", "(.*)") + "$";
                js.Append("      { find: new RegExp(").Append(Json(find)).Append("), replacement: ")
                    .Append(Json(target.Replace("__NITE_STAR__", "$1"))).Append(" },\n");
            }
            js.Append("    ],\n  },\n");
        }

        var env = NiteEnvironment.Load(project, "test");
        if (env.Count > 0)
        {
            js.Append("  define: {\n");
            foreach (var (key, value) in env)
            {
                var code = Json(Json(value));
                js.Append("    ").Append(Json("import.meta.env." + key)).Append(": ").Append(code).Append(",\n");
                js.Append("    ").Append(Json("process.env." + key)).Append(": ").Append(code).Append(",\n");
            }
            js.Append("  },\n");
        }

        if (config.JsxImportSource != "react")
        {
            // Vite 8 compiles with Oxc, earlier versions with esbuild.
            js.Append(ViteVersion is { Major: < 8 }
                ? $"  esbuild: {{ jsx: \"automatic\", jsxImportSource: {Json(config.JsxImportSource)} }},\n"
                : $"  oxc: {{ jsx: {{ runtime: \"automatic\", importSource: {Json(config.JsxImportSource)} }} }},\n");
        }

        if (config.JsxInJs)
        {
            // Create React App allows JSX in .js files, which Vite does not compile as JSX.
            js.Append($$"""
                  plugins: [
                    {
                      name: "nite:jsx-in-js",
                      enforce: "pre",
                      async transform(code, id) {
                        const file = id.split("?")[0];
                        if (!file.endsWith(".js") || file.includes("/node_modules/")) return null;
                        return vite.transformWithOxc
                          ? vite.transformWithOxc(code, file, { lang: "jsx", jsx: { runtime: "automatic", importSource: {{Json(config.JsxImportSource)}} } })
                          : vite.transformWithEsbuild(code, file, { loader: "jsx", jsx: "automatic", jsxImportSource: {{Json(config.JsxImportSource)}} });
                      },
                    },

                """);
        }
        else
        {
            js.Append("  plugins: [\n");
        }
        js.Append("""
                {
                  // nite:config gives code under test nite.json's test.config, not the server's values.
                  name: "nite:config",
                  enforce: "pre",
                  resolveId(id) {
                    return id === "nite:config" ? "\0nite:config" : null;
                  },
                  load(id) {
                    return id === "\0nite:config" ? `export const config = ${JSON.stringify(niteConfig)};\nexport default config;\n` : null;
                  },
                },
              ],

            """);

        js.Append("  test: {\n");
        js.Append("    environment: ").Append(Json(Environment())).Append(",\n");
        if (config.Test.Globals)
        {
            js.Append("    globals: true,\n");
        }
        if (config.Test.SetupFiles.Count > 0)
        {
            js.Append("    setupFiles: ").Append(JsonSerializer.Serialize(config.Test.SetupFiles)).Append(",\n");
        }
        if (config.Test.Include.Count > 0)
        {
            js.Append("    include: ").Append(JsonSerializer.Serialize(config.Test.Include)).Append(",\n");
        }
        js.Append("    coverage: {\n");
        if (CoverageProvider is { } provider)
        {
            js.Append("      enabled: true,\n      provider: ").Append(Json(provider)).Append(",\n");
        }
        js.Append("      reporter: [\"text\", \"html\"],\n");
        js.Append("      reportsDirectory: ").Append(Json(NitePaths.ToUrlPath(CoverageDirectory))).Append(",\n");
        js.Append("    },\n");
        js.Append("  },\n};\n\n");

        js.Append(UserConfig is null
            ? "export default base;\n"
            : """
              export default async (env) => {
                const own = typeof user === "function" ? await user(env) : await user;
                return mergeConfig(base, own ?? {});
              };

              """);
        return js.ToString();
    }

    /// <summary>The test environment: nite.json's, or a DOM if one is installed.</summary>
    public string Environment() =>
        Project.Config.Test.Environment is { Length: > 0 } configured ? configured
        : Installed("jsdom") ? "jsdom"
        : Installed("happy-dom") ? "happy-dom"
        : "node";

    private bool Installed(string package) => File.Exists(Path.Combine(Project.Directory, "node_modules", package, "package.json"));

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string EscapeRegex(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if ("\\^$.|?*+()[]{}/".Contains(c)) builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static (string Version, JsonElement Root)? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            return root.TryGetProperty("version", out var version) && version.GetString() is { } text ? (text, root) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
