using Nite.Esbuild;

namespace Nite.Tests;

/// <summary>
/// Talks to a real esbuild process. The binary is downloaded from npm on first run and cached, so
/// these need network once; filter them out with <c>--filter Category!=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public class EsbuildServiceTests : IAsyncLifetime
{
    private static readonly HttpClient Http = new();
    private EsbuildService _esbuild = null!;

    public async Task InitializeAsync()
    {
        _esbuild = new EsbuildService(await EsbuildBinary.EnsureAsync(Http));
    }

    public async Task DisposeAsync() => await _esbuild.DisposeAsync();

    [Fact]
    public async Task Transforms_tsx_concurrently()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => _esbuild.TransformAsync(
            $"export const n: number = {i}; export const el = <b>{{n}}</b>;",
            ["--loader=tsx", "--format=esm", "--jsx=automatic"])));

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Contains($"const n = {i};", results[i].Code);
            Assert.Contains("react/jsx-runtime", results[i].Code);
        }
    }

    [Fact]
    public async Task Reports_syntax_errors_with_locations()
    {
        var error = await Assert.ThrowsAsync<EsbuildException>(() =>
            _esbuild.TransformAsync("const [a, b = f();", ["--loader=ts", "--sourcefile=broken.ts"]));

        var message = Assert.Single(error.Errors);
        Assert.Equal("broken.ts", message.File);
        Assert.Equal(1, message.Line);
    }

    [Fact]
    public async Task Builds_in_memory_with_a_metafile()
    {
        var directory = Directory.CreateTempSubdirectory("nite-build-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "a.ts"), "import { b } from './b'; console.log(b);");
            File.WriteAllText(Path.Combine(directory.FullName, "b.ts"), "export const b: string = 'bee';");

            var result = await _esbuild.BuildAsync(new EsbuildBuildOptions
            {
                WorkingDirectory = directory.FullName,
                Entries = [new EsbuildEntry(Path.Combine(directory.FullName, "a.ts"), "out")],
                Flags = ["--bundle", "--format=esm", $"--outdir={Path.Combine(directory.FullName, "dist")}"],
                Write = false,
                Metafile = true,
            });

            var output = Assert.Single(result.OutputFiles);
            Assert.EndsWith("out.js", output.Path);
            Assert.Contains("\"bee\"", System.Text.Encoding.UTF8.GetString(output.Contents));
            Assert.Contains("\"b.ts\"", result.Metafile);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Runs_dotnet_plugins_for_resolve_and_load()
    {
        var directory = Directory.CreateTempSubdirectory("nite-plugin-");
        try
        {
            var entry = Path.Combine(directory.FullName, "main.js");
            File.WriteAllText(entry, "import { greeting } from 'virtual:greeting'; import './site.fancy'; console.log(greeting);");
            File.WriteAllText(Path.Combine(directory.FullName, "site.fancy"), "body { color: red }");

            var plugin = new EsbuildPlugin { Name = "test" };
            plugin.OnResolve.Add(new EsbuildOnResolve("^virtual:", "", args =>
                Task.FromResult<EsbuildResolveResult?>(new EsbuildResolveResult { Path = args.Path, Namespace = "virtual" })));
            plugin.OnLoad.Add(new EsbuildOnLoad(".*", "virtual", _ =>
                Task.FromResult<EsbuildLoadResult?>(new EsbuildLoadResult { Contents = "export const greeting = 'from .NET';", Loader = "js" })));
            plugin.OnLoad.Add(new EsbuildOnLoad(@"\.fancy$", "file", async args =>
                new EsbuildLoadResult { Contents = (await File.ReadAllTextAsync(args.Path)).Replace("red", "blue"), Loader = "css" }));

            var result = await _esbuild.BuildAsync(new EsbuildBuildOptions
            {
                WorkingDirectory = directory.FullName,
                Entries = [new EsbuildEntry(entry)],
                Flags = ["--bundle", "--format=esm", $"--outdir={Path.Combine(directory.FullName, "dist")}"],
                Write = false,
                Plugins = [plugin],
            });

            var js = System.Text.Encoding.UTF8.GetString(result.OutputFiles.Single(f => f.Path.EndsWith(".js")).Contents);
            var css = System.Text.Encoding.UTF8.GetString(result.OutputFiles.Single(f => f.Path.EndsWith(".css")).Contents);
            Assert.Contains("from .NET", js);
            Assert.Contains("color: blue", css);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Reports_plugin_errors_at_their_location()
    {
        var directory = Directory.CreateTempSubdirectory("nite-plugin-");
        try
        {
            var entry = Path.Combine(directory.FullName, "main.css");
            File.WriteAllText(entry, ".a { color: red }");
            var plugin = new EsbuildPlugin { Name = "test" };
            plugin.OnLoad.Add(new EsbuildOnLoad(@"\.css$", "", args =>
                throw new EsbuildPluginException("Undefined variable.", args.Path, 3, 7, "  color: $x;")));

            var error = await Assert.ThrowsAsync<EsbuildException>(() => _esbuild.BuildAsync(new EsbuildBuildOptions
            {
                WorkingDirectory = directory.FullName,
                Entries = [new EsbuildEntry(entry)],
                Flags = ["--bundle", $"--outdir={Path.Combine(directory.FullName, "dist")}"],
                Write = false,
                Plugins = [plugin],
            }));

            var message = Assert.Single(error.Errors);
            Assert.Equal("Undefined variable.", message.Text);
            Assert.Equal(3, message.Line);
            Assert.Equal(7, message.Column);
            Assert.Equal("  color: $x;", message.LineText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
