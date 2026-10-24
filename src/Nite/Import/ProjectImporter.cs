using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nite.Dev;
using Nite.Packages;

namespace Nite.Import;

/// <summary>
/// Turns an existing npm/yarn/pnpm/bun web project (Vite, or Create React App) into the client half
/// of a Nite project. <see cref="Analyze"/> only reads and produces a plan, so <c>--dry-run</c> is
/// free; <see cref="Apply"/> copies files into a new project. The source is never modified.
/// </summary>
public static partial class ProjectImporter
{
    public const string Vite = "vite";
    public const string CreateReactApp = "create-react-app";
    public const string Static = "static";

    // Build and lint tooling that needs Node to run: Nite replaces the build and has no runner for the rest.
    private static readonly string[] ToolingPrefixes =
    [
        "vite", "@vitejs/", "react-scripts", "@craco/", "webpack", "@babel/", "babel-", "eslint", "@eslint/",
        "typescript-eslint", "@typescript-eslint/", "prettier", "vitest", "@vitest/", "jest", "@jest/", "ts-jest",
        "@testing-library/", "rollup", "@rollup/", "esbuild", "@swc/", "globals",
        "husky", "lint-staged", "concurrently", "cross-env", "nodemon", "@types/node", "@types/jest",
    ];

    private static readonly string[] SupportedVitePlugins = ["@vitejs/plugin-react", "@vitejs/plugin-react-swc", "@tailwindcss/vite"];

    // Tailwind's integrations Nite replaces by running Tailwind itself (they need native code).
    private static readonly string[] TailwindIntegrations = ["@tailwindcss/vite", "@tailwindcss/postcss", "@tailwindcss/cli", "@tailwindcss/node", "@tailwindcss/oxide", "@tailwindcss/webpack"];

    /// <summary>Config files for the stylesheet tools, copied next to the client code.</summary>
    private static readonly string[] StyleConfigFiles =
    [
        "postcss.config.js", "postcss.config.cjs", "postcss.config.mjs", "postcss.config.ts", "postcss.config.cts", "postcss.config.mts",
        ".postcssrc", ".postcssrc.json", ".postcssrc.js", ".postcssrc.cjs", ".postcssrc.mjs",
        "tailwind.config.js", "tailwind.config.cjs", "tailwind.config.mjs", "tailwind.config.ts",
    ];

    public static ImportPlan Analyze(string source)
    {
        source = Path.GetFullPath(source);
        var packageJsonPath = Path.Combine(source, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            throw new ImportException($"{source} has no package.json, so it is not an npm-style project.");
        }

        using var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath), new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = packageJson.RootElement;
        var plan = new ImportPlan { Source = source };

        if (root.TryGetProperty("workspaces", out _) || File.Exists(Path.Combine(source, "pnpm-workspace.yaml")))
        {
            throw new ImportException("This is a monorepo (it has workspaces). Import one app at a time: nite init --from <repo>/packages/<app>.");
        }

        var dependencies = Section(root, "dependencies");
        var devDependencies = Section(root, "devDependencies");

        // Layout
        string? html;
        if (dependencies.ContainsKey("react-scripts") || devDependencies.ContainsKey("react-scripts"))
        {
            plan.Framework = CreateReactApp;
            html = Path.Combine(source, "public", "index.html");
            plan.EnvPrefix.Add("REACT_APP_");
            plan.JsxInJs = true;
        }
        else if (File.Exists(Path.Combine(source, "index.html")))
        {
            plan.Framework = dependencies.ContainsKey("vite") || devDependencies.ContainsKey("vite") || ViteConfig(source) is not null ? Vite : Static;
            html = Path.Combine(source, "index.html");
            plan.EnvPrefix.Add("VITE_");
        }
        else
        {
            throw new ImportException("Could not find an index.html (Vite) or public/index.html (Create React App). Those are the layouts Nite imports.");
        }
        if (!File.Exists(html))
        {
            throw new ImportException($"{Path.GetRelativePath(source, html)} is missing.");
        }

        var sourceDirectory = Path.Combine(source, "src");
        if (!Directory.Exists(sourceDirectory))
        {
            throw new ImportException("The project has no src/ folder.");
        }

        // What the code actually imports decides which dev dependencies are really runtime ones.
        var aliases = TsconfigPaths.Load(source);
        var imported = new HashSet<string>(StringComparer.Ordinal);
        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var scan = ScanSources(sourceFiles, aliases, imported);

        if (plan.Framework == CreateReactApp)
        {
            var entry = NiteProject.ModuleExtensions.Select(e => Path.Combine(sourceDirectory, "index" + e)).FirstOrDefault(File.Exists)
                ?? throw new ImportException("Create React App projects start at src/index.(js|jsx|ts|tsx); none was found.");
            plan.EntryScript = "/src/" + Path.GetFileName(entry);
        }

        // Packages
        foreach (var (name, spec, isDev) in dependencies.Select(d => (d.Key, d.Value, false)).Concat(devDependencies.Select(d => (d.Key, d.Value, true))))
        {
            if (plan.Dependencies.ContainsKey(name) || plan.DevDependencies.ContainsKey(name))
            {
                continue;
            }
            if (!IsRegistrySpec(spec))
            {
                plan.Skipped.Add(new SkippedPackage(name, spec, "not a registry version (file:, workspace:, git and URLs are not supported)"));
            }
            else if (TailwindIntegrations.Contains(name))
            {
                plan.Skipped.Add(new SkippedPackage(name, spec, "Nite runs Tailwind itself; the tailwindcss package is all it needs"));
            }
            else if (IsStyleTooling(name))
            {
                // PostCSS (and its plugins), Sass, Less and Tailwind run inside Nite, from node_modules.
                plan.DevDependencies[name] = spec;
            }
            else if (IsTooling(name) && !imported.Contains(name))
            {
                plan.Skipped.Add(new SkippedPackage(name, spec, "build or test tooling; Nite replaces the build and does not run the rest"));
            }
            else if (!isDev || imported.Contains(name))
            {
                plan.Dependencies[name] = spec;
                if (isDev)
                {
                    plan.Notes.Add($"{name} is a devDependency but src/ imports it, so it is a dependency here.");
                }
            }
            else if (name.StartsWith("@types/", StringComparison.Ordinal) || name == "typescript")
            {
                plan.DevDependencies[name] = spec;
            }
            else
            {
                plan.Skipped.Add(new SkippedPackage(name, spec, "dev tool that src/ does not import"));
            }
        }

        foreach (var package in imported.Where(p => !dependencies.ContainsKey(p) && !devDependencies.ContainsKey(p) && !p.StartsWith("node:", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            plan.Warnings.Add($"src/ imports '{package}', which package.json does not list. Add it with nite add {package}.");
        }

        var tailwind = dependencies.GetValueOrDefault("tailwindcss") ?? devDependencies.GetValueOrDefault("tailwindcss");
        if (tailwind is not null && tailwind.TrimStart('^', '~', '=', '>', 'v', ' ').StartsWith("3", StringComparison.Ordinal))
        {
            plan.Warnings.Add("Tailwind CSS 3 is not supported; Nite runs Tailwind 4. See https://tailwindcss.com/docs/upgrade-guide (npx @tailwindcss/upgrade does most of it).");
        }
        else if ((dependencies.ContainsKey("@tailwindcss/vite") || devDependencies.ContainsKey("@tailwindcss/vite") || dependencies.ContainsKey("@tailwindcss/postcss") || devDependencies.ContainsKey("@tailwindcss/postcss"))
            && tailwind is null)
        {
            plan.DevDependencies["tailwindcss"] = devDependencies.GetValueOrDefault("@tailwindcss/vite") ?? devDependencies.GetValueOrDefault("@tailwindcss/postcss") ?? "^4.0.0";
            plan.Notes.Add("Added tailwindcss, which @tailwindcss/vite brought in before.");
        }
        foreach (var key in new[] { "overrides", "resolutions" })
        {
            if (root.TryGetProperty(key, out _))
            {
                plan.Warnings.Add($"package.json \"{key}\" is not supported; Nite resolves versions from the ranges alone.");
            }
        }
        if (root.TryGetProperty("pnpm", out var pnpm) && pnpm.TryGetProperty("overrides", out _))
        {
            plan.Warnings.Add("pnpm.overrides is not supported; Nite resolves versions from the ranges alone.");
        }
        if (root.TryGetProperty("proxy", out _))
        {
            plan.Notes.Add("The dev \"proxy\" setting is not needed: the app and its API share an origin in Nite.");
        }
        if (root.TryGetProperty("scripts", out var scripts) && scripts.EnumerateObject().Any())
        {
            plan.Notes.Add($"Scripts are not imported ({string.Join(", ", scripts.EnumerateObject().Select(s => s.Name))}). Use nite watch and nite build.");
        }

        plan.Warnings.AddRange(scan.Warnings);
        AnalyzeViteConfig(source, plan, aliases);

        // Locked versions
        (plan.Manager, plan.Lockfile) = LockfileReader.Detect(source);
        if (plan.Lockfile is not null)
        {
            try
            {
                plan.LockedVersions = LockfileReader.Read(plan.Manager, plan.Lockfile);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or IOException)
            {
                plan.Warnings.Add($"Could not read {Path.GetFileName(plan.Lockfile)} ({ex.Message}); versions will come from the ranges.");
            }
        }
        else if (plan.Manager == PackageManager.Bun)
        {
            plan.Warnings.Add("bun.lockb is binary; run `bun install --save-text-lockfile` first to keep the exact versions. Using the ranges.");
        }
        else
        {
            plan.Notes.Add("No lockfile; versions come from the ranges and may be newer than the project used.");
        }

        // Files
        plan.Files.Add(new ImportFile(html, "Client/index.html", plan.Framework == CreateReactApp ? ImportFileKind.CreateReactAppHtml : ImportFileKind.Copy));
        foreach (var file in sourceFiles)
        {
            var relative = NitePaths.ToUrlPath(Path.GetRelativePath(source, file));
            var name = Path.GetFileName(file);
            var kind = name is "vite-env.d.ts" or "react-app-env.d.ts" ? ImportFileKind.EnvironmentTypes : ImportFileKind.Copy;
            plan.Files.Add(new ImportFile(file, "Client/" + relative, kind));
        }
        foreach (var name in StyleConfigFiles)
        {
            var path = Path.Combine(source, name);
            if (File.Exists(path))
            {
                plan.Files.Add(new ImportFile(path, "Client/" + name, ImportFileKind.Copy));
            }
        }
        foreach (var tsconfig in Directory.EnumerateFiles(source, "tsconfig*.json"))
        {
            plan.Files.Add(new ImportFile(tsconfig, "Client/" + Path.GetFileName(tsconfig), ImportFileKind.TsConfig));
        }
        if (!plan.Files.Any(f => f.Kind == ImportFileKind.EnvironmentTypes)
            && sourceFiles.Any(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal)))
        {
            // Newer Vite templates pull these types in through tsconfig "types" instead of a file.
            plan.Files.Add(new ImportFile("", "Client/src/nite-env.d.ts", ImportFileKind.GeneratedEnvironmentTypes));
        }
        var publicDirectory = Path.Combine(source, "public");
        if (Directory.Exists(publicDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(publicDirectory, "*", SearchOption.AllDirectories))
            {
                if (plan.Framework == CreateReactApp && file == html)
                {
                    continue;
                }
                plan.Files.Add(new ImportFile(file, "wwwroot/" + NitePaths.ToUrlPath(Path.GetRelativePath(publicDirectory, file)), ImportFileKind.Copy));
            }
        }
        foreach (var mode in new[] { NiteEnvironment.Development, NiteEnvironment.Production })
        {
            foreach (var name in NiteEnvironment.FileNames(mode).Distinct())
            {
                var path = Path.Combine(source, name);
                if (File.Exists(path) && plan.Files.All(f => f.Source != path))
                {
                    plan.Files.Add(new ImportFile(path, name, ImportFileKind.Copy));
                }
            }
        }

        plan.HasNodeTypes = plan.Dependencies.ContainsKey("@types/node") || plan.DevDependencies.ContainsKey("@types/node");
        return plan;
    }

    /// <summary>Writes the plan's files into a project directory, never overwriting. Returns the paths written.</summary>
    public static List<string> Apply(ImportPlan plan, string projectDirectory)
    {
        var written = new List<string>();
        foreach (var file in plan.Files)
        {
            var target = Path.Combine(projectDirectory, file.Destination);
            if (File.Exists(target))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            switch (file.Kind)
            {
                case ImportFileKind.CreateReactAppHtml:
                    File.WriteAllText(target, ConvertCreateReactAppHtml(File.ReadAllText(file.Source), plan.EntryScript!));
                    break;
                case ImportFileKind.EnvironmentTypes:
                    File.WriteAllText(target, ConvertEnvironmentTypes(File.ReadAllText(file.Source), plan.HasNodeTypes));
                    break;
                case ImportFileKind.GeneratedEnvironmentTypes:
                    File.WriteAllText(target, ConvertEnvironmentTypes("/// <reference types=\"vite/client\" />\n", plan.HasNodeTypes));
                    break;
                case ImportFileKind.TsConfig:
                    File.WriteAllText(target, ConvertTsConfig(File.ReadAllText(file.Source)));
                    break;
                default:
                    File.Copy(file.Source, target);
                    break;
            }
            written.Add(file.Destination);
        }

        var niteJson = Path.Combine(projectDirectory, NiteConfig.FileName);
        if (!File.Exists(niteJson))
        {
            File.WriteAllText(niteJson, NiteJson(plan));
            written.Add(NiteConfig.FileName);
        }
        return written;
    }

    internal static string NiteJson(ImportPlan plan)
    {
        static string Object(IReadOnlyDictionary<string, string> values, string indent) =>
            values.Count == 0 ? "{}" : "{\n" + string.Join(",\n", values.Select(v => $"{indent}  {JsonSerializer.Serialize(v.Key)}: {JsonSerializer.Serialize(v.Value)}")) + $"\n{indent}}}";

        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append($"  // Imported from {Path.GetFileName(plan.Source)} ({plan.Framework}{(plan.Lockfile is null ? "" : ", " + Path.GetFileName(plan.Lockfile))}) by nite init --from.\n");
        builder.Append("  \"clientRoot\": \"Client\",\n");
        builder.Append($"  \"envPrefix\": {JsonSerializer.Serialize(plan.EnvPrefix)},\n");
        if (plan.JsxInJs)
        {
            builder.Append("  \"jsxInJs\": true,\n");
        }
        if (plan.ScssAdditionalData is not null || plan.LessAdditionalData is not null)
        {
            var languages = new List<string>();
            if (plan.ScssAdditionalData is not null)
            {
                languages.Add($"      \"scss\": {{ \"additionalData\": {JsonSerializer.Serialize(plan.ScssAdditionalData, NiteConfig.JsonOptions)} }}");
            }
            if (plan.LessAdditionalData is not null)
            {
                languages.Add($"      \"less\": {{ \"additionalData\": {JsonSerializer.Serialize(plan.LessAdditionalData, NiteConfig.JsonOptions)} }}");
            }
            builder.Append("  \"css\": {\n    \"preprocessorOptions\": {\n").Append(string.Join(",\n", languages)).Append("\n    }\n  },\n");
        }
        builder.Append($"  \"dependencies\": {Object(plan.Dependencies, "  ")},\n");
        builder.Append($"  \"devDependencies\": {Object(plan.DevDependencies, "  ")}\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    /// <summary>Create React App's public/index.html: drop %PUBLIC_URL% and add the entry script CRA injects at build time.</summary>
    internal static string ConvertCreateReactAppHtml(string html, string entryScript)
    {
        html = html.Replace("%PUBLIC_URL%", "", StringComparison.Ordinal);
        var body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (body < 0)
        {
            return html + $"\n<script type=\"module\" src=\"{entryScript}\"></script>\n";
        }

        // Insert on its own line, indented one level deeper than </body>.
        var lineStart = html.LastIndexOf('\n', body) + 1;
        var indent = html[lineStart..body];
        return string.IsNullOrWhiteSpace(indent) || indent.Length == 0
            ? html.Insert(lineStart, $"{indent}  <script type=\"module\" src=\"{entryScript}\"></script>\n")
            : html.Insert(body, $"<script type=\"module\" src=\"{entryScript}\"></script>\n");
    }

    /// <summary>Swaps Vite's or CRA's type reference for declarations of what Nite provides; other declarations stay.</summary>
    internal static string ConvertEnvironmentTypes(string text, bool hasNodeTypes)
    {
        var declarations = new StringBuilder("""
            // What Nite provides to client code (replaces the "vite/client" or "react-scripts" types).
            interface ImportMetaEnv {
              readonly MODE: string;
              readonly DEV: boolean;
              readonly PROD: boolean;
              readonly BASE_URL: string;
              readonly SSR: boolean;
              readonly [key: string]: string | boolean | undefined;
            }

            interface ImportMeta {
              readonly env: ImportMetaEnv;
            }

            declare module "*.css";
            declare module "*.svg" { const url: string; export default url; }
            declare module "*.png" { const url: string; export default url; }
            declare module "*.jpg" { const url: string; export default url; }
            declare module "*.jpeg" { const url: string; export default url; }
            declare module "*.gif" { const url: string; export default url; }
            declare module "*.webp" { const url: string; export default url; }
            declare module "*.avif" { const url: string; export default url; }
            declare module "*.ico" { const url: string; export default url; }
            declare module "*.woff2" { const url: string; export default url; }

            """);
        if (!hasNodeTypes)
        {
            declarations.Append("""
                // process.env.NODE_ENV and public variables are inlined at build time.
                declare const process: { env: { readonly NODE_ENV: "development" | "production"; readonly [key: string]: string | undefined } };

                """);
        }
        return EnvironmentTypesReference().Replace(text, declarations.ToString(), 1);
    }

    /// <summary>
    /// Drops type packages Nite does not install ("vite/client", "react-scripts") from tsconfig
    /// "types" arrays, editing the text so comments survive. nite-env.d.ts takes their place.
    /// </summary>
    internal static string ConvertTsConfig(string text) =>
        TypesArray().Replace(text, match =>
        {
            var items = match.Groups["items"].Value.Split(',')
                .Select(i => i.Trim())
                .Where(i => i.Length > 0 && i.Trim('"', '\'') is not ("vite/client" or "react-scripts"))
                .ToList();
            return $"{match.Groups["head"].Value}[{string.Join(", ", items)}]";
        });

    private static (List<string> Warnings, int Files) ScanSources(List<string> files, TsconfigPaths aliases, HashSet<string> imported)
    {
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        // Test files are copied, but what they import (testing libraries) is not a runtime dependency.
        var code = files.Where(f => NiteProject.ModuleExtensions.Contains(Path.GetExtension(f)) && !IsTestFile(f)).ToList();
        foreach (var file in code)
        {
            var text = File.ReadAllText(file);
            foreach (var specifier in ImportScanner.FindSpecifiers(text).Concat(RequireCall().Matches(text).Select(m => m.Groups["spec"].Value)))
            {
                if (ImportScanner.IsBare(specifier) && aliases.Resolve(specifier.Split('?')[0]) is null)
                {
                    imported.Add(ImportScanner.PackageName(specifier));
                }
                if (specifier.Contains("?raw", StringComparison.Ordinal) || specifier.Contains("?worker", StringComparison.Ordinal) || specifier.Contains("?url", StringComparison.Ordinal) || specifier.Contains("?inline", StringComparison.Ordinal))
                {
                    warnings.Add($"Vite import suffixes such as '{specifier[specifier.IndexOf('?')..]}' are not supported (in {Path.GetFileName(file)}).");
                }
            }
            if (text.Contains("import.meta.glob", StringComparison.Ordinal))
            {
                warnings.Add($"import.meta.glob is Vite-only and not supported (in {Path.GetFileName(file)}).");
            }
            if (SvgComponentImport().IsMatch(text))
            {
                warnings.Add($"SVGs imported as React components (svgr) are not supported (in {Path.GetFileName(file)}); they import as URLs.");
            }
        }

        if (files.Any(f => Path.GetExtension(f) is ".styl" or ".stylus"))
        {
            warnings.Add("Stylus is not supported; convert those stylesheets to CSS, Sass or Less.");
        }
        return (warnings.ToList(), code.Count);
    }

    internal static bool IsTestFile(string path)
    {
        var name = Path.GetFileName(path);
        return TestFileName().IsMatch(name) || name.StartsWith("setupTests.", StringComparison.Ordinal)
            || path.Replace('\\', '/').Contains("/__tests__/", StringComparison.Ordinal);
    }

    private static void AnalyzeViteConfig(string source, ImportPlan plan, TsconfigPaths aliases)
    {
        if (ViteConfig(source) is not { } config)
        {
            return;
        }

        var text = File.ReadAllText(config);
        foreach (var specifier in ImportScanner.FindSpecifiers(text).Where(ImportScanner.IsBare).Select(ImportScanner.PackageName).Distinct())
        {
            if (specifier == "vite" || specifier.StartsWith("node:", StringComparison.Ordinal) || specifier is "path" or "url" or "fs")
            {
                continue;
            }
            if (!SupportedVitePlugins.Contains(specifier))
            {
                plan.Warnings.Add($"vite.config uses {specifier}, which has no Nite equivalent; whatever it does is not imported.");
            }
        }
        if (text.Contains("alias", StringComparison.Ordinal) && aliases.IsEmpty)
        {
            plan.Warnings.Add("vite.config defines resolve.alias; Nite reads aliases from tsconfig \"paths\", so add them there.");
        }
        if (BaseOption().IsMatch(text))
        {
            plan.Warnings.Add("vite.config sets \"base\"; Nite serves the app from the site root.");
        }
        if (text.Contains("proxy", StringComparison.Ordinal))
        {
            plan.Notes.Add("The dev server proxy in vite.config is not needed: the app and its API share an origin in Nite.");
        }
        foreach (var (language, value, complete) in AdditionalData(text))
        {
            if (!complete)
            {
                plan.Warnings.Add($"vite.config sets css.preprocessorOptions.{language}.additionalData with code Nite cannot evaluate; copy the result into \"css\" in nite.json.");
            }
            else if (language is "scss" or "sass")
            {
                plan.ScssAdditionalData = value;
            }
            else if (language == "less")
            {
                plan.LessAdditionalData = value;
            }
        }
        if (EnvPrefixOption().Match(text) is { Success: true } prefix)
        {
            plan.EnvPrefix.Clear();
            plan.EnvPrefix.AddRange(prefix.Groups["values"].Value.Split(',').Select(v => v.Trim().Trim('"', '\'', '`')).Where(v => v.Length > 0));
        }
    }

    /// <summary>
    /// additionalData strings from vite.config's css.preprocessorOptions. Template literals are
    /// handled when each interpolation only builds a path from a string literal, as in Mantine's
    /// guide: <c>`@use "${path.join(process.cwd(), 'src/_mantine')…}" as mantine;`</c> becomes
    /// <c>@use "src/_mantine" as mantine;</c>, which resolves because the client root is a load path.
    /// </summary>
    internal static IEnumerable<(string Language, string Value, bool Complete)> AdditionalData(string config)
    {
        foreach (Match match in AdditionalDataKey().Matches(config))
        {
            var before = config[..match.Index];
            var language = LanguageKey().Matches(before).LastOrDefault()?.Groups["lang"].Value ?? "scss";
            var start = match.Index + match.Length;
            if (start >= config.Length || config[start] is not ('"' or '\'' or '`'))
            {
                yield return (language, "", false);
                continue;
            }

            var quote = config[start];
            var value = new StringBuilder();
            var complete = true;
            var i = start + 1;
            for (; i < config.Length && config[i] != quote; i++)
            {
                if (config[i] == '\\' && i + 1 < config.Length)
                {
                    value.Append(config[++i] switch { 'n' => '\n', 't' => '\t', var c => c });
                }
                else if (quote == '`' && config[i] == '$' && i + 1 < config.Length && config[i + 1] == '{')
                {
                    var depth = 0;
                    var end = i + 1;
                    for (; end < config.Length; end++)
                    {
                        if (config[end] == '{') depth++;
                        else if (config[end] == '}' && --depth == 0) break;
                    }
                    var expression = config[(i + 2)..Math.Min(end, config.Length)];
                    if (PathExpression().Match(expression) is { Success: true } path && (expression.Contains("process.cwd()") || expression.Contains("__dirname")))
                    {
                        value.Append(path.Groups["path"].Value.TrimStart('.', '/'));
                    }
                    else
                    {
                        complete = false;
                    }
                    i = end;
                }
                else
                {
                    value.Append(config[i]);
                }
            }
            yield return (language, value.ToString(), complete && i < config.Length);
        }
    }

    private static string? ViteConfig(string source) =>
        new[] { "ts", "js", "mts", "mjs", "cts", "cjs" }.Select(e => Path.Combine(source, $"vite.config.{e}")).FirstOrDefault(File.Exists);

    private static Dictionary<string, string> Section(JsonElement root, string name)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in section.EnumerateObject())
            {
                values[property.Name] = property.Value.GetString() ?? "";
            }
        }
        return values;
    }

    internal static bool IsRegistrySpec(string spec) =>
        !spec.Contains(':') && !spec.Contains('/') && (spec.Length == 0 || SemVerRange.TryParse(spec, out _) || TagLike().IsMatch(spec));

    internal static bool IsStyleTooling(string name) =>
        name is "postcss" or "autoprefixer" or "sass" or "sass-embedded" or "less" or "tailwindcss" or "cssnano"
        || name.StartsWith("postcss-", StringComparison.Ordinal) || name.StartsWith("@csstools/", StringComparison.Ordinal)
        || (name.StartsWith("@tailwindcss/", StringComparison.Ordinal) && !TailwindIntegrations.Contains(name));

    private static bool IsTooling(string name) =>
        ToolingPrefixes.Any(prefix => name == prefix || (prefix.EndsWith('/') ? name.StartsWith(prefix, StringComparison.Ordinal) : name.StartsWith(prefix + "-", StringComparison.Ordinal) || name == prefix))
        || TailwindIntegrations.Contains(name);

    [GeneratedRegex(@"\.(test|spec)\.[cm]?[jt]sx?$")]
    private static partial Regex TestFileName();

    [GeneratedRegex(@"\brequire\(\s*(?<q>[""'])(?<spec>[^""'\r\n]+)\k<q>\s*\)")]
    private static partial Regex RequireCall();

    [GeneratedRegex(@"import\s*\{[^}]*\bReactComponent\b[^}]*\}\s*from\s*[""'][^""']+\.svg")]
    private static partial Regex SvgComponentImport();

    [GeneratedRegex(@"^\s*///\s*<reference\s+types=[""'](?:vite/client|react-scripts)[""']\s*/>\s*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex EnvironmentTypesReference();

    [GeneratedRegex(@"(?<head>""types""\s*:\s*)\[(?<items>[^\]]*)\]")]
    private static partial Regex TypesArray();

    [GeneratedRegex(@"\bbase\s*:\s*[""'`]")]
    private static partial Regex BaseOption();

    [GeneratedRegex(@"\benvPrefix\s*:\s*\[?(?<values>[^\]\n]+?)\]?\s*,?\s*$", RegexOptions.Multiline)]
    private static partial Regex EnvPrefixOption();

    [GeneratedRegex(@"\badditionalData\s*:\s*")]
    private static partial Regex AdditionalDataKey();

    [GeneratedRegex(@"\b(?<lang>scss|sass|less)\s*:\s*\{")]
    private static partial Regex LanguageKey();

    [GeneratedRegex(@"(?<q>['""])(?<path>[^'""]+)\k<q>")]
    private static partial Regex PathExpression();

    [GeneratedRegex(@"^[a-z][a-z0-9._-]*$")]
    private static partial Regex TagLike();
}

public sealed class ImportPlan
{
    public string Source { get; init; } = "";
    public string Framework { get; set; } = ProjectImporter.Static;
    public PackageManager Manager { get; set; }
    public string? Lockfile { get; set; }
    public string? EntryScript { get; set; }
    public List<string> EnvPrefix { get; } = [];
    public bool JsxInJs { get; set; }
    public bool HasNodeTypes { get; set; }
    public string? ScssAdditionalData { get; set; }
    public string? LessAdditionalData { get; set; }
    public SortedDictionary<string, string> Dependencies { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> DevDependencies { get; } = new(StringComparer.Ordinal);
    public List<SkippedPackage> Skipped { get; } = [];
    public Dictionary<string, HashSet<string>> LockedVersions { get; set; } = [];
    public List<ImportFile> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>The version the lockfile has for a direct dependency, if exactly one.</summary>
    public string? LockedVersion(string name) =>
        LockedVersions.TryGetValue(name, out var versions) && versions.Count > 0 ? versions.OrderBy(v => SemVersion.Parse(v)).Last() : null;
}

public sealed record SkippedPackage(string Name, string Spec, string Reason);

public enum ImportFileKind { Copy, CreateReactAppHtml, EnvironmentTypes, GeneratedEnvironmentTypes, TsConfig }

public sealed record ImportFile(string Source, string Destination, ImportFileKind Kind);

public sealed class ImportException(string message) : Exception(message);
