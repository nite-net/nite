using Nite.Packages;
using Nite.Testing;

namespace Nite.Tests;

public class VitestSetupTests : IDisposable
{
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-vitest-");

    public void Dispose() => _project.Delete(recursive: true);

    private string Root => _project.FullName;

    private void Package(string name, string version, string extra = "")
    {
        var directory = Path.Combine(Root, "node_modules", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), $$"""{ "name": "{{name}}", "version": "{{version}}"{{extra}} }""");
    }

    private VitestSetup Setup(string niteJson = "{}")
    {
        File.WriteAllText(Path.Combine(Root, "nite.json"), niteJson);
        Directory.CreateDirectory(Path.Combine(Root, "Client"));
        return VitestSetup.Find(new NiteProject(Root))!;
    }

    [Fact]
    public void Finds_nothing_without_vitest() =>
        Assert.Null(VitestSetup.Find(new NiteProject(Root)));

    [Fact]
    public void Reads_the_entry_and_vites_node_range()
    {
        Package("vitest", "4.0.3", """, "bin": { "vitest": "./vitest.mjs" }""");
        Package("vite", "8.1.0", """, "engines": { "node": "^20.19.0 || >=22.12.0" }""");

        var setup = Setup();

        Assert.Equal("4.0.3", setup.Version.ToString());
        Assert.Equal(Path.Combine(Root, "node_modules", "vitest", "vitest.mjs"), setup.Entry);
        Assert.True(setup.SupportsNode(SemVersion.Parse("22.12.0")));
        Assert.True(setup.SupportsNode(SemVersion.Parse("20.19.1")));
        Assert.False(setup.SupportsNode(SemVersion.Parse("18.20.0")));
        Assert.False(setup.SupportsNode(SemVersion.Parse("22.11.0")));
    }

    [Fact]
    public void Carries_aliases_env_and_client_config_into_the_config()
    {
        Package("vitest", "4.0.3");
        Package("vite", "8.1.0");
        File.WriteAllText(Path.Combine(Root, ".env"), "NITE_PUBLIC_API=/api\nSECRET=hidden\n");
        File.WriteAllText(Path.Combine(Root, ".env.test"), "NITE_PUBLIC_API=/test-api\n");
        var setup = Setup("""{ "test": { "config": { "apiBase": "/x" }, "globals": true, "setupFiles": ["src/setup.ts"] } }""");
        File.WriteAllText(Path.Combine(Root, "Client", "tsconfig.json"), """{ "compilerOptions": { "paths": { "@/*": ["./src/*"], "~utils": ["./src/utils/index.ts"] } } }""");

        var config = setup.GenerateConfig();

        var client = NitePaths.ToUrlPath(Path.Combine(Root, "Client"));
        Assert.Contains($"find: new RegExp(\"^@\\\\/(.*)$\"), replacement: \"{client}/src/$1\"", config);
        Assert.Contains($"find: new RegExp(\"^~utils$\"), replacement: \"{client}/src/utils/index.ts\"", config);
        Assert.Contains("\"import.meta.env.NITE_PUBLIC_API\": \"\\u0022/test-api\\u0022\"", config);
        Assert.Contains("\"process.env.NITE_PUBLIC_API\"", config);
        Assert.DoesNotContain("SECRET", config);
        Assert.Contains("const niteConfig = Object.freeze({ \"apiBase\": \"/x\" });", config);
        Assert.Contains("\"\\0nite:config\"", config);
        Assert.Contains("globals: true", config);
        Assert.Contains("setupFiles: [\"src/setup.ts\"]", config);
        Assert.Contains("export default base;", config);
    }

    [Fact]
    public void Picks_a_dom_environment_when_one_is_installed()
    {
        Package("vitest", "4.0.3");
        Assert.Equal("node", Setup().Environment());

        Package("happy-dom", "20.0.0");
        Assert.Equal("happy-dom", Setup().Environment());

        Package("jsdom", "27.0.0");
        Assert.Equal("jsdom", Setup().Environment());
        Assert.Equal("node", Setup("""{ "test": { "environment": "node" } }""").Environment());
    }

    [Theory]
    [InlineData("7.1.0", "esbuild: { jsx: \"automatic\", jsxImportSource: \"preact\" }")]
    [InlineData("8.0.0", "oxc: { jsx: { runtime: \"automatic\", importSource: \"preact\" } }")]
    public void Sets_the_jsx_import_source_for_the_installed_vite(string vite, string expected)
    {
        Package("vitest", "4.0.3");
        Package("vite", vite);

        Assert.Contains(expected, Setup("""{ "jsxImportSource": "preact" }""").GenerateConfig());
    }

    [Fact]
    public void Leaves_jsx_alone_for_react() =>
        Assert.DoesNotContain("jsx", WithVitest().GenerateConfig().Replace("nite:config", ""), StringComparison.Ordinal);

    [Fact]
    public void Compiles_jsx_in_js_files_for_create_react_app_projects()
    {
        Package("vitest", "5.1.2");
        Package("vite", "8.4.0");

        var config = Setup("""{ "jsxInJs": true }""").GenerateConfig();

        Assert.Contains("import * as vite from \"vite\";", config);
        Assert.Contains("name: \"nite:jsx-in-js\"", config);
        Assert.Contains("name: \"nite:config\"", config); // still there after the extra plugin
    }

    [Fact]
    public void Turns_on_coverage_from_nite_json()
    {
        Package("vitest", "4.0.3");
        var setup = Setup("""{ "test": { "coverage": "istanbul" } }""");

        var config = setup.GenerateConfig();

        Assert.Contains("enabled: true,\n      provider: \"istanbul\"", config);
        Assert.Contains($"reportsDirectory: \"{NitePaths.ToUrlPath(Path.Combine(Root, ".nite", "coverage"))}\"", config);
        Assert.False(setup.HasCoverageProvider("istanbul"));
        Package("@vitest/coverage-istanbul", "4.0.3");
        Assert.True(setup.HasCoverageProvider("istanbul"));
    }

    [Fact]
    public void Rejects_an_unknown_coverage_provider()
    {
        Package("vitest", "4.0.3");
        var error = Assert.Throws<InvalidOperationException>(() => Setup("""{ "test": { "coverage": "c8" } }""").GenerateConfig());
        Assert.Contains("\"v8\" or \"istanbul\"", error.Message);
    }

    [Fact]
    public void Merges_the_projects_own_config_over_its_own()
    {
        var setup = WithVitest();
        File.WriteAllText(Path.Combine(Root, "vitest.config.ts"), "export default {};");

        var config = setup.GenerateConfig();

        Assert.Contains($"import user from \"{NitePaths.ToUrlPath(Path.Combine(Root, "vitest.config.ts"))}\";", config);
        Assert.Contains("return mergeConfig(base, own ?? {});", config);
        Assert.DoesNotContain("export default base;", config);
    }

    [Fact]
    public void Writes_the_config_under_the_work_directory()
    {
        var path = WithVitest().WriteConfig();
        Assert.Equal(Path.Combine(Root, ".nite", "vitest.config.mjs"), path);
        Assert.StartsWith("// Generated by NiteFx", File.ReadAllText(path));
    }

    private VitestSetup WithVitest()
    {
        Package("vitest", "4.0.3");
        return Setup();
    }
}
