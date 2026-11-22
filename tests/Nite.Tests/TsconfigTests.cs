using System.Text.Json.Nodes;
using Jint;
using Nite.Build;
using Nite.Dev;
using Nite.Esbuild;

namespace Nite.Tests;

public class ClientTsconfigTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-tsconfig-");

    public void Dispose() => _root.Delete(recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Full(string relative) => Path.GetFullPath(Path.Combine(_root.FullName, relative));

    private static JsonObject Options(string flag)
    {
        Assert.StartsWith("--tsconfig-raw=", flag);
        return JsonNode.Parse(flag["--tsconfig-raw=".Length..])!["compilerOptions"]!.AsObject();
    }

    [Fact]
    public void Finds_nothing_without_a_tsconfig()
    {
        Assert.Null(ClientTsconfig.Find(_root.FullName));
        Assert.Null(ClientTsconfig.TransformFlag(_root.FullName));
    }

    [Fact]
    public void Uses_tsconfig_json_when_it_holds_the_options()
    {
        Write("tsconfig.json", """{ "compilerOptions": { "experimentalDecorators": true } }""");

        Assert.Equal(Full("tsconfig.json"), ClientTsconfig.Find(_root.FullName));
    }

    [Fact]
    public void Follows_a_vite_solution_config_to_the_app_config()
    {
        Write("tsconfig.json", """{ "files": [], "references": [{ "path": "./tsconfig.node.json" }, { "path": "./tsconfig.app.json" }] }""");
        Write("tsconfig.node.json", """{ "compilerOptions": { "experimentalDecorators": false } }""");
        Write("tsconfig.app.json", """{ "compilerOptions": { "experimentalDecorators": true } }""");

        Assert.Equal(Full("tsconfig.app.json"), ClientTsconfig.Find(_root.FullName));
        Assert.True(Options(ClientTsconfig.TransformFlag(_root.FullName)!)["experimentalDecorators"]!.GetValue<bool>());
    }

    [Fact]
    public void Applies_extends_and_keeps_only_options_that_change_a_transform()
    {
        Write("tsconfig.base.json", """{ "compilerOptions": { "experimentalDecorators": true, "useDefineForClassFields": true, "strict": true } }""");
        Write("tsconfig.json", """
            {
              // comments and trailing commas, as editors allow
              "extends": "./tsconfig.base",
              "compilerOptions": { "useDefineForClassFields": false, "noEmit": true, "paths": { "@/*": ["./*"] }, },
            }
            """);

        var options = Options(ClientTsconfig.TransformFlag(_root.FullName)!);

        Assert.True(options["experimentalDecorators"]!.GetValue<bool>());
        Assert.False(options["useDefineForClassFields"]!.GetValue<bool>());
        Assert.True(options["strict"]!.GetValue<bool>());
        Assert.False(options.ContainsKey("noEmit"));
        Assert.False(options.ContainsKey("paths"));
    }

    [Fact]
    public void Resolves_a_package_extends_from_node_modules()
    {
        Write("node_modules/@tsconfig/lit/tsconfig.json", """{ "compilerOptions": { "experimentalDecorators": true } }""");
        Write("tsconfig.json", """{ "extends": "@tsconfig/lit/tsconfig.json" }""");

        Assert.True(Options(ClientTsconfig.TransformFlag(_root.FullName)!)["experimentalDecorators"]!.GetValue<bool>());
    }
}

/// <summary>
/// The dev server and <c>nite build</c> must compile decorators and class fields the same way, or a
/// Lit component that works in development breaks in production (or the reverse). Each case
/// compiles the same module both ways and runs it.
/// </summary>
[Trait("Category", "Integration")]
public class DecoratorParityTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-decorators-");

    public void Dispose() => _root.Delete(recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // Experimental (TypeScript) decorators get (target, key); standard ones get (value, context).
    // The subclass field shadows a setter: [[Set]] semantics (useDefineForClassFields: false, which
    // Lit's reactive properties need) call it, [[Define]] semantics do not.
    private const string Module = """
        const log: string[] = [];
        (globalThis as any).log = log;
        function probe(...args: any[]): any {
          const context = args[1];
          log.push(typeof context === "object" && context !== null && "kind" in context ? "standard:" + context.kind : "experimental");
        }

        @probe
        class Widget {
          @probe label = "hi";
        }

        class Base {
          set value(_: number) { log.push("setter"); }
        }
        class Derived extends Base {
          value = 1;
        }

        new Widget();
        new Derived();
        """;

    private const string Experimental = """{ "experimentalDecorators": true, "useDefineForClassFields": false }""";

    public static TheoryData<string, string, string> Layouts => new()
    {
        { "no tsconfig", "", "standard:field,standard:class" },
        { "tsconfig.json", $$"""{ "compilerOptions": {{Experimental}} }""", "experimental,experimental,setter" },
        {
            "Vite layout",
            $$"""{ "files": [], "references": [{ "path": "./tsconfig.app.json" }] }|tsconfig.app.json={ "compilerOptions": {{Experimental}} }""",
            "experimental,experimental,setter"
        },
        {
            "Vite layout with path aliases",
            $$"""{ "files": [], "references": [{ "path": "./tsconfig.app.json" }] }|tsconfig.app.json={ "compilerOptions": { "experimentalDecorators": true, "useDefineForClassFields": false, "baseUrl": ".", "paths": { "@/*": ["./*"] } } }""",
            "experimental,experimental,setter"
        },
        {
            "extends",
            $$"""{ "extends": "./tsconfig.base.json" }|tsconfig.base.json={ "compilerOptions": {{Experimental}} }""",
            "experimental,experimental,setter"
        },
        { "standard decorators, define semantics", """{ "compilerOptions": { "target": "ES2022" } }""", "standard:field,standard:class" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Dev_and_production_compile_decorators_alike(string layout, string tsconfig, string expected)
    {
        _ = layout;
        Write("nite.json", "{}");
        Write("Client/index.html", "<!doctype html><html><head></head><body><script type=\"module\" src=\"./main.ts\"></script></body></html>");
        Write("Client/main.ts", Module);
        if (tsconfig.Length > 0)
        {
            var files = tsconfig.Split('|');
            Write("Client/tsconfig.json", files[0]);
            foreach (var extra in files.Skip(1))
            {
                var split = extra.IndexOf('=');
                Write("Client/" + extra[..split], extra[(split + 1)..]);
            }
        }

        var project = new NiteProject(_root.FullName);
        await using var esbuild = new EsbuildService(await EsbuildBinary.EnsureAsync(new HttpClient()));

        var file = Path.Combine(project.ClientRoot, "main.ts");
        var dev = await esbuild.TransformAsync(Module, NiteDevServer.TransformFlags(project.Config, "ts", file, [],
            ClientTsconfig.TransformFlag(project.ClientRoot, project.Directory)));

        var manifest = await new NiteBuilder(esbuild).BuildAsync(project);
        var production = File.ReadAllText(Path.Combine(project.OutputDirectory, manifest.Entries["main.ts"].File));

        Assert.Equal(expected, Run(dev.Code));
        Assert.Equal(expected, Run(production));
    }

    private static string Run(string module)
    {
        var engine = new Engine();
        engine.Modules.Add("main", module);
        engine.Modules.Import("main");
        return engine.Evaluate("globalThis.log.join(',')").AsString();
    }
}
