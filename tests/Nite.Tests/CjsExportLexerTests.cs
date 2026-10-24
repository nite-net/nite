using Nite.Dev;

namespace Nite.Tests;

public class CjsExportLexerTests
{
    [Fact]
    public void Finds_common_export_shapes()
    {
        const string source = """
            "use strict";
            exports.useState = function () {};
            module.exports.useEffect = () => {};
            exports["Fragment"] = Symbol.for("react.fragment");
            Object.defineProperty(exports, "version", { value: "1.0.0" });
            Object.defineProperty(exports, "__esModule", { value: true });
            exports.default = 1;
            if (exports.useState === undefined) {}
            notexports.ignored = 1;
            """;

        Assert.Equal(["Fragment", "useEffect", "useState", "version"], CjsExportLexer.GetExportsFromSource(source));
    }

    [Fact]
    public void Reads_object_literal_exports()
    {
        Assert.Equal(["a", "b", "c"], CjsExportLexer.GetExportsFromSource("module.exports = { a, b: 2, \"c\": 3, ...rest };"));
    }

    [Fact]
    public void Follows_reexports_like_react_index_files()
    {
        var directory = Directory.CreateTempSubdirectory("nite-cjs-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "cjs"));
            File.WriteAllText(Path.Combine(directory.FullName, "index.js"), """
                if (process.env.NODE_ENV === "production") {
                  module.exports = require("./cjs/lib.production.js");
                } else {
                  module.exports = require("./cjs/lib.development");
                }
                """);
            File.WriteAllText(Path.Combine(directory.FullName, "cjs", "lib.production.js"), "exports.createRoot = 1; exports.hydrateRoot = 2;");
            File.WriteAllText(Path.Combine(directory.FullName, "cjs", "lib.development.js"), "exports.createRoot = 1; exports.hydrateRoot = 2; exports.devOnly = 3;");

            Assert.Equal(["createRoot", "devOnly", "hydrateRoot"], CjsExportLexer.GetExports(Path.Combine(directory.FullName, "index.js")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Drops_reserved_words_and_invalid_identifiers()
    {
        Assert.Empty(CjsExportLexer.GetExportsFromSource("exports[\"not-valid\"] = 1; exports.default = 2; exports[\"class\"] = 3;"));
    }
}
