using Nite.Import;

namespace Nite.Tests;

public class LockfileReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lockfiles", name);

    // Real lockfiles written by npm 10, pnpm 10, Yarn 1, Yarn 4 and Bun 1.4 for the same package.json:
    // react, react-dom, clsx, @types/react, and a file: dependency that must be ignored.
    [Theory]
    [InlineData("package-lock.json", PackageManager.Npm)]
    [InlineData("pnpm-lock.yaml", PackageManager.Pnpm)]
    [InlineData("yarn-classic.lock", PackageManager.Yarn)]
    [InlineData("yarn-berry.lock", PackageManager.YarnBerry)]
    [InlineData("bun.lock", PackageManager.Bun)]
    public void Reads_registry_versions_from_every_format(string file, PackageManager manager)
    {
        var versions = LockfileReader.Read(manager, Fixture(file));

        Assert.Equal(["19.3.1"], versions["react"]);
        Assert.Equal(["19.3.1"], versions["react-dom"]);
        Assert.Equal(["0.28.0"], versions["scheduler"]);
        Assert.Equal(["2.1.1"], versions["clsx"]);
        Assert.Equal(["19.3.0"], versions["@types/react"]);
        Assert.Equal(["3.2.3"], versions["csstype"]);
        Assert.False(versions.ContainsKey("@local/pkg"));
        Assert.False(versions.ContainsKey("fixture"));
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "pnpm-lock.yaml", PackageManager.Pnpm)]
    [InlineData("yarn-classic.lock", "yarn.lock", PackageManager.Yarn)]
    [InlineData("yarn-berry.lock", "yarn.lock", PackageManager.YarnBerry)]
    [InlineData("package-lock.json", "package-lock.json", PackageManager.Npm)]
    [InlineData("bun.lock", "bun.lock", PackageManager.Bun)]
    public void Detects_the_package_manager(string fixture, string fileName, PackageManager expected)
    {
        var directory = Directory.CreateTempSubdirectory("nite-detect-");
        try
        {
            File.Copy(Fixture(fixture), Path.Combine(directory.FullName, fileName));
            var (manager, path) = LockfileReader.Detect(directory.FullName);
            Assert.Equal(expected, manager);
            Assert.Equal(Path.Combine(directory.FullName, fileName), path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Reads_older_npm_and_pnpm_formats()
    {
        var npmV1 = LockfileReader.ReadNpm("""
            { "lockfileVersion": 1, "dependencies": {
                "react": { "version": "18.2.0", "dependencies": { "loose-envify": { "version": "1.4.0" } } },
                "local": { "version": "file:../local" } } }
            """);
        Assert.Equal(["18.2.0"], npmV1["react"]);
        Assert.Equal(["1.4.0"], npmV1["loose-envify"]);
        Assert.False(npmV1.ContainsKey("local"));

        var pnpm = LockfileReader.ReadPnpm("""
            lockfileVersion: '6.0'
            dependencies:
              react:
                specifier: ^18.2.0
                version: 18.2.0
            packages:
              /react@18.2.0:
                resolution: {integrity: sha512-x}
              /react-dom@18.2.0(react@18.2.0):
                resolution: {integrity: sha512-y}
              /@types/react/17.0.2:
                resolution: {integrity: sha512-z}
            """);
        Assert.Equal(["18.2.0"], pnpm["react"]);
        Assert.Equal(["18.2.0"], pnpm["react-dom"]);
        Assert.Equal(["17.0.2"], pnpm["@types/react"]);
    }
}

public class ProjectImporterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-import-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void ViteProject()
    {
        Write("package.json", """
            {
              "name": "shop", "private": true, "type": "module",
              "scripts": { "dev": "vite", "build": "tsc -b && vite build" },
              "dependencies": { "react": "^19.2.0", "react-dom": "^19.2.0", "ui-kit": "workspace:*" },
              "devDependencies": {
                "vite": "^7.1.0", "@vitejs/plugin-react": "^5.0.0", "typescript": "~5.9.0", "@types/react": "^19.2.0",
                "eslint": "^9.0.0", "date-fns": "^4.1.0", "tailwindcss": "^4.0.0", "@tailwindcss/vite": "^4.0.0", "vite-plugin-svgr": "^4.0.0",
                "postcss": "^8.5.0", "postcss-preset-mantine": "^1.18.0", "sass-embedded": "^1.90.0"
              }
            }
            """);
        Write("vite.config.ts", """
            import { defineConfig } from "vite";
            import react from "@vitejs/plugin-react";
            import svgr from "vite-plugin-svgr";
            import tailwindcss from "@tailwindcss/vite";
            export default defineConfig({ plugins: [react(), svgr(), tailwindcss()], server: { proxy: { "/api": "http://localhost:5000" } } });
            """);
        Write("tsconfig.json", """{ "files": [], "references": [{ "path": "./tsconfig.app.json" }] }""");
        Write("tsconfig.app.json", """{ "compilerOptions": { "baseUrl": ".", "paths": { "@/*": ["./src/*"] } }, "include": ["src"] }""");
        Write("index.html", """<html><body><div id="root"></div><script type="module" src="/src/main.tsx"></script></body></html>""");
        Write("src/main.tsx", """
            import { createRoot } from "react-dom/client";
            import { format } from "date-fns";
            import { cn } from "@/lib/utils";
            import "./index.css";
            console.log(import.meta.env.VITE_API_URL, format(new Date(), "P"), cn());
            """);
        Write("src/lib/utils.ts", "export function cn() { return ''; }");
        Write("src/index.css", "body { margin: 0 }");
        Write("src/Card.module.scss", ".card { color: red }");
        Write("postcss.config.cjs", "module.exports = { plugins: { 'postcss-preset-mantine': {} } };");
        Write("src/vite-env.d.ts", "/// <reference types=\"vite/client\" />\n\ninterface ImportMetaEnv { readonly VITE_API_URL: string }\n");
        Write("public/vite.svg", "<svg/>");
        Write(".env", "VITE_API_URL=/api\nSECRET=do-not-ship\n");
        Write("pnpm-lock.yaml", "lockfileVersion: '9.0'\n\npackages:\n\n  react@19.2.4:\n    resolution: {integrity: sha512-x}\n");
    }

    [Fact]
    public void Plans_a_vite_project()
    {
        ViteProject();
        var plan = ProjectImporter.Analyze(_root.FullName);

        Assert.Equal(ProjectImporter.Vite, plan.Framework);
        Assert.Equal(PackageManager.Pnpm, plan.Manager);
        Assert.Equal("19.2.4", plan.LockedVersion("react"));
        Assert.Equal(["VITE_"], plan.EnvPrefix);

        // date-fns is a devDependency in the source, but src/ imports it.
        Assert.Equal(["date-fns", "react", "react-dom"], plan.Dependencies.Keys);
        // Stylesheet tools run inside Nite, so they stay; Tailwind's Vite plugin is replaced.
        Assert.Equal(["@types/react", "postcss", "postcss-preset-mantine", "sass-embedded", "tailwindcss", "typescript"], plan.DevDependencies.Keys);
        var skipped = plan.Skipped.ToDictionary(s => s.Name, s => s.Reason);
        Assert.Contains("registry", skipped["ui-kit"]);
        Assert.Contains("Tailwind", skipped["@tailwindcss/vite"]);
        Assert.Contains("tooling", skipped["vite"]);
        Assert.Contains("tooling", skipped["eslint"]);
        Assert.Contains("tooling", skipped["vite-plugin-svgr"]);

        Assert.Contains(plan.Warnings, w => w.Contains("vite-plugin-svgr"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("tailwind", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Sass") || w.Contains("CSS modules"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("@/lib")); // the alias is not a package
        Assert.Contains(plan.Notes, n => n.Contains("proxy"));
        Assert.Contains(plan.Notes, n => n.Contains("Scripts"));

        var destinations = plan.Files.Select(f => f.Destination).ToList();
        Assert.Contains("Client/index.html", destinations);
        Assert.Contains("Client/src/main.tsx", destinations);
        Assert.Contains("Client/src/lib/utils.ts", destinations);
        Assert.Contains("Client/tsconfig.app.json", destinations);
        Assert.Contains("Client/postcss.config.cjs", destinations);
        Assert.Contains("Client/src/Card.module.scss", destinations);
        Assert.Contains("wwwroot/vite.svg", destinations);
        Assert.Contains(".env", destinations);
        Assert.DoesNotContain(destinations, d => d.Contains("vite.config"));
    }

    [Fact]
    public void Reads_sass_additional_data_from_vite_config()
    {
        // Mantine's Sass guide, verbatim.
        var config = """
            export default defineConfig({
              plugins: [react()],
              css: {
                preprocessorOptions: {
                  scss: {
                    api: 'modern-compiler',
                    additionalData: `@use "${path.join(process.cwd(), 'src/_mantine').replace(/\\/g, '/')}" as mantine;`,
                  },
                  less: { additionalData: '@import "./src/vars.less";' },
                },
              },
            });
            """;
        var found = ProjectImporter.AdditionalData(config).ToList();
        Assert.Equal(("scss", "@use \"src/_mantine\" as mantine;", true), found[0]);
        Assert.Equal(("less", "@import \"./src/vars.less\";", true), found[1]);

        var dynamic = ProjectImporter.AdditionalData("scss: { additionalData: `$theme: ${theme};` }").Single();
        Assert.False(dynamic.Complete);

        var plan = new ImportPlan { Source = "/x/app", Framework = ProjectImporter.Vite, ScssAdditionalData = "@use \"src/_mantine\" as mantine;" };
        var json = ProjectImporter.NiteJson(plan);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<NiteConfig>(json, NiteConfig.JsonOptions)!;
        Assert.Equal("@use \"src/_mantine\" as mantine;", parsed.Css.PreprocessorOptions.Scss.AdditionalData);
    }

    [Fact]
    public void Applies_a_vite_project_without_touching_the_source()
    {
        ViteProject();
        var before = Directory.EnumerateFiles(_root.FullName, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);
        var plan = ProjectImporter.Analyze(_root.FullName);
        var target = Directory.CreateTempSubdirectory("nite-import-target-");
        try
        {
            ProjectImporter.Apply(plan, target.FullName);

            var config = NiteConfig.Load(target.FullName);
            Assert.Equal(["VITE_"], config.EnvPrefix);
            Assert.Equal("^4.1.0", config.Dependencies["date-fns"]);
            var types = File.ReadAllText(Path.Combine(target.FullName, "Client/src/vite-env.d.ts"));
            Assert.DoesNotContain("<reference types=\"vite/client\"", types);
            Assert.Contains("interface ImportMeta", types);
            Assert.Contains("readonly VITE_API_URL: string", types); // the project's own declarations stay
            Assert.True(File.Exists(Path.Combine(target.FullName, "wwwroot/vite.svg")));

            Assert.Equal(before, Directory.EnumerateFiles(_root.FullName, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText));
        }
        finally
        {
            target.Delete(recursive: true);
        }
    }

    [Fact]
    public void Plans_a_create_react_app_project()
    {
        Write("package.json", """{ "dependencies": { "react": "^18.2.0", "react-dom": "^18.2.0", "react-scripts": "5.0.1", "@testing-library/react": "^13.4.0" }, "proxy": "http://localhost:5000" }""");
        Write("public/index.html", """<html><head><link rel="icon" href="%PUBLIC_URL%/favicon.ico"></head><body><div id="root"></div></body></html>""");
        Write("public/favicon.ico", "x");
        Write("src/index.js", "import React from 'react';\nconst App = () => <div>{process.env.REACT_APP_NAME}</div>;\n");
        Write("src/react-app-env.d.ts", "/// <reference types=\"react-scripts\" />\n");
        Write("src/App.test.js", "import { render } from '@testing-library/react';\n");

        var plan = ProjectImporter.Analyze(_root.FullName);

        Assert.Equal(ProjectImporter.CreateReactApp, plan.Framework);
        Assert.True(plan.JsxInJs);
        Assert.Equal(["REACT_APP_"], plan.EnvPrefix);
        Assert.Equal("/src/index.js", plan.EntryScript);
        Assert.Contains(plan.Skipped, s => s.Name == "react-scripts");
        Assert.DoesNotContain("@testing-library/react", plan.Dependencies.Keys); // only a test file imports it
        Assert.Contains(plan.Files, f => f.Destination == "Client/src/App.test.js"); // but the test is still copied
        Assert.DoesNotContain(plan.Files, f => f.Destination == "wwwroot/index.html");
        Assert.Contains(plan.Files, f => f.Destination == "Client/index.html" && f.Kind == ImportFileKind.CreateReactAppHtml);

        var html = ProjectImporter.ConvertCreateReactAppHtml(File.ReadAllText(Path.Combine(_root.FullName, "public/index.html")), plan.EntryScript!);
        Assert.Contains("href=\"/favicon.ico\"", html);
        Assert.Contains("<script type=\"module\" src=\"/src/index.js\"></script>\n</body>", html);
        var indented = ProjectImporter.ConvertCreateReactAppHtml("<html>\n  <body>\n    <div id=\"root\"></div>\n  </body>\n</html>", "/src/index.js");
        Assert.Contains("    <script type=\"module\" src=\"/src/index.js\"></script>\n  </body>", indented);
    }

    [Fact]
    public void Refuses_monorepos_and_unknown_layouts()
    {
        Write("package.json", """{ "workspaces": ["packages/*"] }""");
        Assert.Contains("monorepo", Assert.Throws<ImportException>(() => ProjectImporter.Analyze(_root.FullName)).Message);

        Write("package.json", """{ "dependencies": { "next": "15.0.0" } }""");
        Assert.Contains("index.html", Assert.Throws<ImportException>(() => ProjectImporter.Analyze(_root.FullName)).Message);
    }

    [Fact]
    public void Removes_vite_types_from_tsconfig_and_keeps_comments()
    {
        const string tsconfig = """
            {
              "compilerOptions": {
                /* Bundler mode */
                "types": ["vite/client"],
                "lib": ["ES2023", "DOM"]
              },
              "other": { "types": ["node", "vite/client", 'react-scripts'] }
            }
            """;
        var converted = ProjectImporter.ConvertTsConfig(tsconfig);
        Assert.Contains("/* Bundler mode */", converted);
        Assert.Contains("\"types\": []", converted);
        Assert.Contains("\"types\": [\"node\"]", converted);
        Assert.Contains("\"lib\": [\"ES2023\", \"DOM\"]", converted);
    }

    [Theory]
    [InlineData("^1.2.3", true)]
    [InlineData("latest", true)]
    [InlineData("*", true)]
    [InlineData("workspace:*", false)]
    [InlineData("file:../x", false)]
    [InlineData("github:user/repo", false)]
    [InlineData("user/repo", false)]
    [InlineData("npm:other@1", false)]
    [InlineData("https://example.com/x.tgz", false)]
    public void Knows_registry_specifiers(string spec, bool expected)
    {
        Assert.Equal(expected, ProjectImporter.IsRegistrySpec(spec));
    }
}

public class NiteEnvironmentTests : IDisposable
{
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-env-");

    public void Dispose() => _project.Delete(recursive: true);

    [Fact]
    public void Parses_dotenv_syntax()
    {
        var values = NiteEnvironment.ParseDotEnv("""
            # comment
            A=1
            export B = two words
            C="quoted # not a comment\nnext"
            D='single'
            E=value # trailing comment
            """).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("1", values["A"]);
        Assert.Equal("two words", values["B"]);
        Assert.Equal("quoted # not a comment\nnext", values["C"]);
        Assert.Equal("single", values["D"]);
        Assert.Equal("value", values["E"]);
    }

    [Fact]
    public void Only_prefixed_variables_reach_the_client_and_later_files_win()
    {
        File.WriteAllText(Path.Combine(_project.FullName, "nite.json"), """{ "envPrefix": ["VITE_"] }""");
        File.WriteAllText(Path.Combine(_project.FullName, ".env"), "VITE_API=/base\nVITE_NAME=app\nDATABASE_PASSWORD=secret\n");
        File.WriteAllText(Path.Combine(_project.FullName, ".env.production"), "VITE_API=/prod\n");
        var project = new NiteProject(_project.FullName);

        var development = NiteEnvironment.Load(project, NiteEnvironment.Development, _ => null);
        var production = NiteEnvironment.Load(project, NiteEnvironment.Production, key => key == "VITE_NAME" ? "from-ci" : null);

        Assert.Equal("/base", development["VITE_API"]);
        Assert.Equal("/prod", production["VITE_API"]);
        Assert.Equal("from-ci", production["VITE_NAME"]);
        Assert.DoesNotContain("DATABASE_PASSWORD", development.Keys);

        var flags = NiteEnvironment.DefineFlags(project, NiteEnvironment.Production, _ => null);
        Assert.Contains(flags, f => f.StartsWith("--define:import.meta.env={", StringComparison.Ordinal) && f.Contains("\"PROD\":true") && f.Contains("\"VITE_API\":\"/prod\""));
        Assert.Contains("--define:process.env.VITE_API=\"/prod\"", flags);
        Assert.DoesNotContain(flags, f => f.Contains("secret"));
    }
}

public class TsconfigPathsTests : IDisposable
{
    private readonly DirectoryInfo _client = Directory.CreateTempSubdirectory("nite-paths-");

    public void Dispose() => _client.Delete(recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_client.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Follows_references_to_find_paths()
    {
        Write("tsconfig.json", """{ "files": [], "references": [{ "path": "./tsconfig.app.json" }, { "path": "./tsconfig.node.json" }] }""");
        Write("tsconfig.app.json", """
            {
              // Vite and shadcn keep aliases here.
              "compilerOptions": { "baseUrl": ".", "paths": { "@/*": ["./src/*"], "~ui": ["./src/ui/index.ts"] } },
            }
            """);
        Write("src/lib/utils.ts", "");
        Write("src/components/Button/index.tsx", "");
        Write("src/ui/index.ts", "");

        var paths = TsconfigPaths.Load(_client.FullName);

        Assert.Equal(Path.Combine(_client.FullName, "src", "lib", "utils.ts"), paths.Resolve("@/lib/utils"));
        Assert.Equal(Path.Combine(_client.FullName, "src", "components", "Button", "index.tsx"), paths.Resolve("@/components/Button"));
        Assert.Equal(Path.Combine(_client.FullName, "src", "ui", "index.ts"), paths.Resolve("~ui"));
        Assert.Null(paths.Resolve("react"));
        Assert.Null(paths.Resolve("@/missing"));
        Assert.EndsWith("tsconfig.app.json", paths.SourceFile);
    }

    [Fact]
    public void Resolves_against_base_url_after_paths()
    {
        Write("tsconfig.json", """{ "compilerOptions": { "baseUrl": "src" } }""");
        Write("src/components/Card.tsx", "");
        var paths = TsconfigPaths.Load(_client.FullName);
        Assert.Equal(Path.Combine(_client.FullName, "src", "components", "Card.tsx"), paths.Resolve("components/Card"));
        Assert.Null(paths.Resolve("react"));
    }
}
