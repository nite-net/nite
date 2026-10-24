using Microsoft.Extensions.Logging.Abstractions;
using Nite.Css;
using Nite.Esbuild;
using Nite.Packages;

namespace Nite.Tests;

/// <summary>
/// A project with real postcss, less, sass and tailwindcss packages installed (from npm, once per
/// run), and the Sass compiler binary. Integration tests, like the esbuild ones.
/// </summary>
public sealed class CssProjectFixture : IAsyncLifetime
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("nite-css-project-");
    public EsbuildService Esbuild { get; private set; } = null!;
    public NiteProject Project { get; private set; } = null!;
    public CssPipeline Pipeline { get; private set; } = null!;

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public async Task InitializeAsync()
    {
        Write("nite.json", """
            {
              "css": { "preprocessorOptions": { "scss": { "additionalData": "@use \"tokens\" as t;" } } },
              "devDependencies": { "postcss": "^8.5.0", "less": "^4.2.0", "sass": "^1.90.0", "tailwindcss": "^4.1.0" }
            }
            """);
        Project = new NiteProject(Root.FullName);
        await new PackageInstaller(Http).InstallAsync(Project.Directory, Project.Config);
        Esbuild = new EsbuildService(await EsbuildBinary.EnsureAsync(Http));

        // A PostCSS plugin of the project's own, named by path in the config.
        Write("Client/postcss.config.cjs", "module.exports = { plugins: [require('./shout.cjs')()] };");
        Write("Client/shout.cjs", """
            module.exports = () => ({
              postcssPlugin: "shout",
              Declaration(decl) { if (decl.value === "red") decl.value = "blue"; },
            });
            module.exports.postcss = true;
            """);
        Write("Client/_tokens.scss", "$brand: #ff0000;\n@mixin pad { padding: 4px; }\n");
        Pipeline = new CssPipeline(Project, Esbuild, Http, NullLoggerFactory.Instance);
    }

    public async Task DisposeAsync()
    {
        await Pipeline.DisposeAsync();
        await Esbuild.DisposeAsync();
        Root.Delete(recursive: true);
    }
}

[Trait("Category", "Integration")]
public class CssPipelineTests(CssProjectFixture fixture) : IClassFixture<CssProjectFixture>
{
    [Fact]
    public async Task Runs_the_projects_postcss_config()
    {
        var file = fixture.Write("Client/plain.css", ".a { color: red; }");
        var result = await fixture.Pipeline.ProcessAsync(file, Mode.Development);
        Assert.Contains("color: blue", result.Css);
        Assert.Contains(Path.Combine(fixture.Project.ClientRoot, "postcss.config.cjs"), result.Dependencies);
    }

    [Fact]
    public async Task Compiles_sass_with_additional_data_partials_and_postcss()
    {
        var file = fixture.Write("Client/src/card.scss", ".card { color: t.$brand; @include t.pad; .title { color: red; } }");
        var result = await fixture.Pipeline.ProcessAsync(file, Mode.Development);
        Assert.Contains(".card .title", result.Css);
        Assert.Contains("padding: 4px", result.Css);
        Assert.Contains("color: #ff0000", result.Css); // Sass keeps hex; only the literal "red" is PostCSS's
        Assert.Contains("color: blue", result.Css);
        Assert.Contains(Path.Combine(fixture.Project.ClientRoot, "_tokens.scss"), result.Dependencies);
    }

    [Fact]
    public async Task Reports_sass_errors_at_the_line_in_the_file()
    {
        var file = fixture.Write("Client/src/broken.scss", ".a {\n  color: $nope;\n}\n");
        var error = await Assert.ThrowsAsync<CssException>(() => fixture.Pipeline.ProcessAsync(file, Mode.Development));
        Assert.Equal(file, error.File);
        Assert.Equal(2, error.Line); // not shifted by the additionalData line
        Assert.Equal("  color: $nope;", error.LineText);
    }

    [Fact]
    public async Task Compiles_less_with_imports()
    {
        fixture.Write("Client/src/vars.less", "@accent: #12b886;");
        var file = fixture.Write("Client/src/theme.less", "@import \"./vars\";\n.note { border-color: @accent; .x { width: (10px * 2); } }");
        var result = await fixture.Pipeline.ProcessAsync(file, Mode.Development);
        Assert.Contains(".note .x", result.Css);
        Assert.Contains("width: 20px", result.Css);
        Assert.Contains(Path.Combine(fixture.Project.ClientRoot, "src", "vars.less"), result.Dependencies);
    }

    [Fact]
    public async Task Builds_tailwind_from_scanned_classes()
    {
        fixture.Write("Client/src/App.tsx", "export const App = () => <div className=\"flex p-4 text-brand md:grid\" />;");
        fixture.Write("Views/Home.cshtml", "<p class=\"underline\">Razor</p>");
        var file = fixture.Write("Client/src/app.css", "@import \"tailwindcss\";\n@theme { --color-brand: #123456; }\n");
        var result = await fixture.Pipeline.ProcessAsync(file, Mode.Development);
        Assert.Contains(".flex", result.Css);
        Assert.Contains(".text-brand", result.Css);
        Assert.Contains(".underline", result.Css); // found in a Razor view outside the client root
        Assert.DoesNotContain(".grid-cols-3", result.Css);
        Assert.Contains("--color-brand: #123456", result.Css);
    }

    [Fact]
    public async Task Bundles_css_modules_and_urls_through_esbuild()
    {
        fixture.Write("Client/src/logo.png", "png");
        fixture.Write("Client/src/Panel.module.scss", ".panel { color: red; background: url(./logo.png); .inner { @include t.pad; } }");
        var entry = fixture.Write("Client/src/main.js", "import s from './Panel.module.scss'; console.log(s.panel, s.inner);");

        var result = await fixture.Esbuild.BuildAsync(new EsbuildBuildOptions
        {
            WorkingDirectory = fixture.Project.Directory,
            Entries = [new EsbuildEntry(entry)],
            Flags = ["--bundle", "--format=esm", $"--outdir={Path.Combine(fixture.Root.FullName, "out")}", "--loader:.png=file"],
            Write = false,
            Plugins = [fixture.Pipeline.CreatePlugin(Mode.Production)],
        });

        var css = System.Text.Encoding.UTF8.GetString(result.OutputFiles.Single(f => f.Path.EndsWith(".css")).Contents);
        var js = System.Text.Encoding.UTF8.GetString(result.OutputFiles.Single(f => f.Path.EndsWith(".js")).Contents);
        Assert.Contains("color: blue", css);
        Assert.Matches(@"\.Panel_module_panel|\.Panel_panel", css);
        Assert.Contains("logo-", css); // the asset was copied with a hash
        Assert.Contains("inner:", js);
    }
}
