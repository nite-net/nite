using Nite.Html;

namespace Nite.Tests;

public class HtmlDocumentTests
{
    private const string Page = """
        <!doctype html>
        <html lang="en">
          <head>
            <link rel="icon" href="/favicon.svg" />
            <link rel="stylesheet" href="./styles.css" />
            <link rel=stylesheet href='https://cdn.example.com/x.css'>
            <script src="/legacy.js"></script>
          </head>
          <body>
            <script type="module" src="./main.tsx"></script>
            <script type="module">console.log("inline")</script>
          </body>
        </html>
        """;

    [Fact]
    public void Finds_module_scripts_and_stylesheets_only()
    {
        Assert.Equal(
            [
                new HtmlReference(HtmlReferenceKind.Stylesheet, "./styles.css"),
                new HtmlReference(HtmlReferenceKind.Stylesheet, "https://cdn.example.com/x.css"),
                new HtmlReference(HtmlReferenceKind.ModuleScript, "./main.tsx"),
            ],
            HtmlDocument.FindReferences(Page));
    }

    [Fact]
    public void Rewrites_tags_and_keeps_the_rest()
    {
        var output = HtmlDocument.Rewrite(Page, r => r.Url == "./main.tsx" ? "<script type=\"module\" src=\"/_nite/main-ABC.js\"></script>" : null);

        Assert.Contains("<script type=\"module\" src=\"/_nite/main-ABC.js\"></script>", output);
        Assert.Contains("<link rel=\"stylesheet\" href=\"./styles.css\" />", output);
        Assert.Contains("console.log(\"inline\")", output);
    }

    [Fact]
    public void Injects_at_the_start_of_head()
    {
        var output = HtmlDocument.InjectHead(Page, "<script type=\"importmap\">{}</script>\n");
        Assert.True(output.IndexOf("importmap", StringComparison.Ordinal) < output.IndexOf("favicon", StringComparison.Ordinal));
        Assert.StartsWith("<html><head>\n<x>", HtmlDocument.InjectHead("<html><head></head></html>", "<x>"));
        Assert.Contains("<head>\n<x></head>", HtmlDocument.InjectHead("<html><body></body></html>", "<x>"));
    }

    [Theory]
    [InlineData("index.html", "./main.tsx", "main.tsx")]
    [InlineData("index.html", "main.tsx", "main.tsx")]
    [InlineData("index.html", "/src/app.tsx", "src/app.tsx")]
    [InlineData("admin/index.html", "./main.tsx?v=1", "admin/main.tsx")]
    [InlineData("admin/index.html", "../shared/x.css", "shared/x.css")]
    [InlineData("index.html", "../outside.tsx", null)]
    [InlineData("index.html", "https://cdn.example.com/x.js", null)]
    [InlineData("index.html", "//cdn.example.com/x.js", null)]
    public void Resolves_local_urls_against_the_page(string page, string url, string? expected)
    {
        Assert.Equal(expected, HtmlDocument.ResolveLocal(page, url));
    }
}
