using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Build;
using Nite.Css;
using Jint;
using Nite.Dev;

namespace Nite.Tests;

public class TailwindScannerTests
{
    private static HashSet<string> Extract(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        TailwindScanner.Extract(text, found);
        return found;
    }

    [Fact]
    public void Finds_classes_in_jsx_and_razor()
    {
        var found = Extract("""
            <div className={cn("flex items-center", active && 'bg-blue-500/50')}>
            <p class="md:hover:text-red-600 -mt-4 !font-bold w-[137px] text-(--brand)">@Model.Title</p>
            """);

        Assert.Superset(new HashSet<string> { "flex", "items-center", "bg-blue-500/50", "md:hover:text-red-600", "-mt-4", "!font-bold", "font-bold", "w-[137px]", "text-(--brand)" }, found);
        Assert.DoesNotContain("cn(", found);
        Assert.DoesNotContain("Model.Title", found);
    }

    [Fact]
    public void Keeps_arbitrary_values_with_quotes_and_commas_whole()
    {
        var found = Extract("""<div className="bg-[url('/hero.png')] grid-cols-[1fr,2fr] [&>*]:p-4 content-['hi']">""");
        Assert.Contains("bg-[url('/hero.png')]", found);
        Assert.Contains("grid-cols-[1fr,2fr]", found);
        Assert.Contains("[&>*]:p-4", found);
        Assert.Contains("content-['hi']", found);
    }

    [Fact]
    public void Skips_what_cannot_be_a_class()
    {
        var found = Extract("""
            <Button onClick={() => go()} /> <path d="M12 4.5c-1.2 0-2.3.4-3.2 1.1" /> https://example.com/a.b 42 2xl:flex
            """);
        Assert.DoesNotContain("Button", found);
        Assert.DoesNotContain("M12", found);
        Assert.DoesNotContain("42", found);
        Assert.DoesNotContain(found, f => f.Contains("//"));
        Assert.Contains("2xl:flex", found);
    }

    [Fact]
    public void Expands_braces_for_globs()
    {
        Assert.Equal(["src/**/*.js", "src/**/*.tsx"], TailwindScanner.ExpandBraces("src/**/*.{js,tsx}"));
        Assert.Equal(["a.x", "a.y", "b.x", "b.y"], TailwindScanner.ExpandBraces("{a,b}.{x,y}").Order());
    }

    [Fact]
    public void Scans_sources_with_gitignore_and_negation()
    {
        var root = Directory.CreateTempSubdirectory("nite-scan-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Pages"));
            Directory.CreateDirectory(Path.Combine(root, "dist"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "lib"));
            Directory.CreateDirectory(Path.Combine(root, "legacy"));
            File.WriteAllText(Path.Combine(root, "Pages", "Index.cshtml"), "<div class=\"rotate-12\"></div>");
            File.WriteAllText(Path.Combine(root, "dist", "built.js"), "'from-dist'");
            File.WriteAllText(Path.Combine(root, "node_modules", "lib", "x.js"), "'from-node-modules'");
            File.WriteAllText(Path.Combine(root, "legacy", "old.html"), "<b class=\"from-legacy\">");
            File.WriteAllText(Path.Combine(root, "site.css"), ".from-css {}");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "dist/\n# comment\n");

            var scanner = new TailwindScanner();
            Assert.True(scanner.Scan([new TailwindSource(root, ".", Auto: true), new TailwindSource(root, "legacy", Negated: true)]));

            Assert.Contains("rotate-12", scanner.Candidates);
            Assert.DoesNotContain("from-dist", scanner.Candidates);
            Assert.DoesNotContain("from-node-modules", scanner.Candidates);
            Assert.DoesNotContain("from-legacy", scanner.Candidates);
            Assert.DoesNotContain("from-css", scanner.Candidates);

            // An explicit @source reaches into node_modules.
            Assert.True(scanner.Scan([new TailwindSource(root, "node_modules/lib")]));
            Assert.Contains("from-node-modules", scanner.Candidates);

            // Nothing new the second time; a save that adds a class is new.
            Assert.False(scanner.Scan([new TailwindSource(root, ".", Auto: true), new TailwindSource(root, "legacy", Negated: true)]));
            File.WriteAllText(Path.Combine(root, "Pages", "Index.cshtml"), "<div class=\"rotate-12 skew-x-6\"></div>");
            File.SetLastWriteTimeUtc(Path.Combine(root, "Pages", "Index.cshtml"), DateTime.UtcNow.AddSeconds(5));
            Assert.True(scanner.ScanFiles([Path.Combine(root, "Pages", "Index.cshtml")]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detects_tailwind_stylesheets()
    {
        Assert.True(TailwindCompiler.Uses("@import \"tailwindcss\";"));
        Assert.True(TailwindCompiler.Uses("@import 'tailwindcss/theme.css' layer(theme);"));
        Assert.True(TailwindCompiler.Uses("@reference \"../app.css\";\n.a { @apply p-4; }"));
        Assert.False(TailwindCompiler.Uses(".a { color: red; }\n@import './other.css';"));
    }
}

public class CssResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-css-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Resolves_packages_through_the_style_condition_and_patterns()
    {
        Write("node_modules/tailwindcss/package.json", """
            { "exports": { ".": { "style": "./index.css", "import": "./dist/lib.mjs" }, "./theme": "./theme.css", "./*": "./css/*" } }
            """);
        var index = Write("node_modules/tailwindcss/index.css", "");
        var theme = Write("node_modules/tailwindcss/theme.css", "");
        var extra = Write("node_modules/tailwindcss/css/extra.css", "");
        var from = Path.Combine(_root.FullName, "src", "styles");
        Directory.CreateDirectory(from);

        Assert.Equal(index, CssResolver.Resolve("tailwindcss", from));
        Assert.Equal(theme, CssResolver.Resolve("tailwindcss/theme", from));
        Assert.Equal(extra, CssResolver.Resolve("tailwindcss/extra.css", from));
        Assert.Null(CssResolver.Resolve("missing-package", from));
    }

    [Fact]
    public void Resolves_relative_paths_scoped_packages_and_tilde()
    {
        var local = Write("src/theme.css", "");
        var scoped = Write("node_modules/@acme/ui/styles.css", "");
        Write("node_modules/@acme/ui/package.json", "{}");
        var src = Path.Combine(_root.FullName, "src");

        Assert.Equal(local, CssResolver.Resolve("./theme", src));
        Assert.Equal(scoped, CssResolver.Resolve("@acme/ui/styles.css", src));
        Assert.Equal(scoped, CssResolver.Resolve("~@acme/ui/styles.css", src));
        Assert.Equal(("@acme/ui", "./a/b.css"), CssResolver.Split("@acme/ui/a/b.css"));
        Assert.Equal(("pkg", "."), CssResolver.Split("pkg"));
    }

    [Fact]
    public void Finds_the_postcss_config_like_postcss_load_config()
    {
        var project = new NiteProject(_root.FullName);
        Assert.Null(PostCssRunner.FindConfig(project));

        Write("package.json", """{ "name": "x", "postcss": { "plugins": {} } }""");
        Assert.EndsWith("package.json", PostCssRunner.FindConfig(project));

        Write("postcss.config.mjs", "export default {}");
        Assert.EndsWith("postcss.config.mjs", PostCssRunner.FindConfig(project));

        // The client root wins over the project folder.
        Write("Client/postcss.config.cjs", "module.exports = {}");
        Assert.Equal(Path.Combine(project.ClientRoot, "postcss.config.cjs"), PostCssRunner.FindConfig(project));
    }
}

public class CssPipelineUnitTests
{
    [Theory]
    [InlineData("a.css", true, false)]
    [InlineData("a.module.scss", true, true)]
    [InlineData("dir/Card.module.less?import", true, true)]
    [InlineData("theme.sass", true, false)]
    [InlineData("modules.css", true, false)]
    [InlineData("app.tsx", false, false)]
    public void Classifies_stylesheets(string path, bool stylesheet, bool module)
    {
        Assert.Equal(stylesheet, CssPipeline.IsStylesheet(path));
        Assert.Equal(module, CssPipeline.IsCssModule(path));
    }

    [Fact]
    public void Reads_class_maps_from_esbuild_output()
    {
        var code = """
            // Client/src/Card.module.css
            var Card_default = {
              card: "Card_ab12_card",
              "is-active": "Card_ab12_is-active",
              title: "Card_ab12_card Card_ab12_title"
            };
            export {
              Card_default as default
            };
            """;
        var classes = NiteDevServer.ParseClassMap(code);
        Assert.Equal("Card_ab12_card", classes["card"]);
        Assert.Equal("Card_ab12_is-active", classes["is-active"]);
        Assert.Equal("Card_ab12_card Card_ab12_title", classes["title"]);
        Assert.Equal(3, classes.Count);
    }

    [Fact]
    public void Adds_browser_targets_when_only_a_language_version_is_given()
    {
        Assert.Equal("es2022,chrome107,edge107,firefox104,safari16", NiteBuilder.BuildTarget("es2022"));
        Assert.Equal("es2020,safari15", NiteBuilder.BuildTarget("es2020,safari15"));
    }

    [Fact]
    public void Round_trips_protobuf_messages()
    {
        var bytes = new ProtoWriter()
            .String(1, "hello")
            .Bool(2, true)
            .Enum(3, 2)
            .Message(4, new ProtoWriter().String(1, "nested").String(1, "twice"))
            .String(5, "ünïcode")
            .ToArray();

        var message = ProtoMessage.Parse(bytes);
        Assert.Equal("hello", message.String(1));
        Assert.Equal(1UL, message.Number(2));
        Assert.Equal(2UL, message.Number(3));
        Assert.Equal(["nested", "twice"], message.Message(4)!.Strings(1));
        Assert.Equal("ünïcode", message.String(5));
        Assert.False(message.Has(9));

        var big = new MemoryStream();
        ProtoWriter.Varint(big, 300);
        var offset = 0;
        Assert.Equal(300UL, ProtoMessage.ReadVarint(big.ToArray(), ref offset));
    }

    [Fact]
    public void Node_lite_gives_tools_path_fs_url_and_structured_clone()
    {
        var root = Directory.CreateTempSubdirectory("nite-js-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "contents");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            using var engine = new JsEngine(root, NullLogger.Instance);
            var result = engine.RunAsync(e => e.Evaluate("""
                (() => {
                  const path = require("node:path"), fs = require("fs"), url = require("url");
                  const cwd = process.cwd();
                  let missing;
                  try { fs.readFileSync(cwd + "/nope.txt"); } catch (error) { missing = error.code; }
                  const clone = structuredClone({ m: new Map([["k", [1, 2]]]), d: new Date(0) });
                  return JSON.stringify({
                    join: path.join("/a/b", "../c", "./d.css"),
                    resolve: path.resolve("/x", "y", "../z"),
                    relative: path.relative("/a/b/c", "/a/d"),
                    ext: path.extname("/a/b.module.css"),
                    dirname: path.dirname("/a/b/c.css"),
                    read: fs.readFileSync(cwd + "/a.txt", "utf8"),
                    dir: fs.statSync(cwd + "/sub").isDirectory(),
                    entries: fs.readdirSync(cwd).sort(),
                    missing,
                    file: url.fileURLToPath(url.pathToFileURL(cwd + "/a b.css")),
                    href: new URL("./x.css", "file:///p/q/r.css").href,
                    clone: clone.m.get("k").length + ":" + (clone.d instanceof Date),
                  });
                })()
                """).AsString()).GetAwaiter().GetResult();

            using var json = JsonDocument.Parse(result);
            var r = json.RootElement;
            Assert.Equal("/a/c/d.css", r.GetProperty("join").GetString());
            Assert.Equal("/x/z", r.GetProperty("resolve").GetString());
            Assert.Equal("../../d", r.GetProperty("relative").GetString());
            Assert.Equal(".css", r.GetProperty("ext").GetString());
            Assert.Equal("/a/b", r.GetProperty("dirname").GetString());
            Assert.Equal("contents", r.GetProperty("read").GetString());
            Assert.True(r.GetProperty("dir").GetBoolean());
            Assert.Equal(["a.txt", "sub"], r.GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("ENOENT", r.GetProperty("missing").GetString());
            Assert.Equal(JsEngine.ToJsPath(root) + "/a b.css", r.GetProperty("file").GetString());
            Assert.Equal("file:///p/q/x.css", r.GetProperty("href").GetString());
            Assert.Equal("2:True", r.GetProperty("clone").GetString(), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
