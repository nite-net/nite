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
}
