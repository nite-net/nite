using Nite.Dev;
using Nite.Packages;

namespace Nite.Tests;

public class ImportScannerTests
{
    // Shaped like esbuild's transform output.
    private const string Code = """
        import { jsxDEV } from "react/jsx-dev-runtime";
        import { useState } from "react";
        import { Button } from "./Button";
        import "./styles.css";
        export * from "../shared/util";
        import {
          a,
          b
        } from "@scope/pkg/sub";
        const lazy = () => import("./Lazy");
        const text = "not an import";
        """;

    [Fact]
    public void Finds_static_dynamic_and_side_effect_imports()
    {
        Assert.Equal(
            ["react/jsx-dev-runtime", "react", "./Button", "./styles.css", "../shared/util", "@scope/pkg/sub", "./Lazy"],
            ImportScanner.FindSpecifiers(Code));
    }

    [Fact]
    public void Rewrites_only_the_specifier()
    {
        var rewritten = ImportScanner.Rewrite(Code, s => s == "./Button" ? "./Button.tsx" : null);
        Assert.Contains("import { Button } from \"./Button.tsx\";", rewritten);
        Assert.Contains("import { useState } from \"react\";", rewritten);
    }

    [Theory]
    [InlineData("react", true)]
    [InlineData("@scope/pkg", true)]
    [InlineData("./x", false)]
    [InlineData("../x", false)]
    [InlineData("/abs", false)]
    [InlineData("https://esm.sh/react", false)]
    public void Detects_bare_specifiers(string specifier, bool bare)
    {
        Assert.Equal(bare, ImportScanner.IsBare(specifier));
    }

    [Theory]
    [InlineData("react", "react")]
    [InlineData("react-dom/client", "react-dom")]
    [InlineData("@scope/pkg", "@scope/pkg")]
    [InlineData("@scope/pkg/deep/file.js", "@scope/pkg")]
    public void Finds_the_package_name(string specifier, string package)
    {
        Assert.Equal(package, ImportScanner.PackageName(specifier));
    }

    [Fact]
    public void Lists_hoisting_candidates_nearest_first()
    {
        Assert.Equal(
            ["node_modules/a/node_modules/b/node_modules/x", "node_modules/a/node_modules/x", "node_modules/x"],
            PackageInstaller.VisibleLocations("node_modules/a/node_modules/b", "x"));
        Assert.Equal(["node_modules/x"], PackageInstaller.VisibleLocations("", "x"));
    }
}
