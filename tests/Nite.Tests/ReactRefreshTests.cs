using Nite.Dev;

namespace Nite.Tests;

public class ReactRefreshTests
{
    // Shaped like esbuild's --jsx=automatic --jsx-dev output.
    private const string Components = """
        import { jsxDEV } from "react/jsx-dev-runtime";
        import { useState, memo, forwardRef } from "react";
        import { useTodos } from "./hooks";
        const Badge = memo(function Badge2() {
          return /* @__PURE__ */ jsxDEV("i", {}, void 0, false, {
            fileName: "shapes.tsx"
          }, this);
        });
        const Field = forwardRef((p, ref) => /* @__PURE__ */ jsxDEV("input", { ref }, void 0, false, {
          fileName: "shapes.tsx"
        }, this));
        const Arrow = ({ n }) => {
          const [a] = useState(n);
          return /* @__PURE__ */ jsxDEV("b", { children: a }, void 0, false, {
            fileName: "shapes.tsx"
          }, this);
        };
        function helper() {
          return 1;
        }
        function App() {
          const [x, setX] = useState(0);
          const todos = useTodos();
          return /* @__PURE__ */ jsxDEV("div", { onClick: () => setX(helper()), children: x }, void 0, false, {
            fileName: "shapes.tsx"
          }, this);
        }
        export {
          Arrow,
          Badge,
          Field,
          App as default
        };
        //# sourceMappingURL=data:application/json;base64,AAAA
        """;

    [Fact]
    public void Finds_components_and_their_hooks()
    {
        var info = ReactRefresh.Analyze(Components);

        Assert.Equal(["Badge", "Field", "Arrow", "App"], info.Components.Select(c => c.Name));
        Assert.Equal("useState", info.Components.Single(c => c.Name == "Arrow").HookSignature);
        Assert.Equal("useState,useTodos", info.Components.Single(c => c.Name == "App").HookSignature);
        Assert.True(info.IsBoundary);
    }

    [Fact]
    public void A_non_component_export_makes_the_module_propagate()
    {
        var code = Components.Replace("  Arrow,\n", "  Arrow,\n  helper,\n");
        Assert.False(ReactRefresh.Analyze(code).IsBoundary);
    }

    [Fact]
    public void Screaming_case_constants_are_not_components()
    {
        const string code = """
            const API_URL = "/api";
            function Widget() {
              return null;
            }
            export {
              API_URL,
              Widget
            };
            """;
        var info = ReactRefresh.Analyze(code);
        Assert.Equal(["Widget"], info.Components.Select(c => c.Name));
        Assert.False(info.IsBoundary);
    }

    [Fact]
    public void Anonymous_default_exports_and_star_exports_are_not_boundaries()
    {
        Assert.False(ReactRefresh.Analyze("var stdin_default = () => null;\nexport {\n  stdin_default as default\n};\n").IsBoundary);
        Assert.False(ReactRefresh.Analyze("function A() {\n  return null;\n}\nexport * from \"./b\";\nexport {\n  A\n};\n").IsBoundary);
    }

    [Fact]
    public void Utility_modules_have_no_components()
    {
        const string code = """
            function plural(count, word) {
              return `${count} ${word}`;
            }
            export {
              plural
            };
            """;
        var info = ReactRefresh.Analyze(code);
        Assert.Empty(info.Components);
        Assert.False(info.IsBoundary);
    }

    [Theory]
    // export default App (Vite's and CRA's templates): esbuild aliases the component.
    [InlineData("function App() {\n  return /* @__PURE__ */ jsxDEV(\"div\", {});\n}\nvar App_default = App;\nexport {\n  App_default as default\n};\n", true)]
    // export default function () { … } with JSX
    [InlineData("function Card_default() {\n  return /* @__PURE__ */ jsx(\"div\", {});\n}\nexport {\n  Card_default as default\n};\n", true)]
    // export default memo(function Item() { … })
    [InlineData("var Item_default = memo(function Item() {\n  return /* @__PURE__ */ jsx(\"div\", {});\n});\nexport {\n  Item_default as default\n};\n", true)]
    // an anonymous default *utility* must keep propagating to its importers
    [InlineData("function format_default(x) {\n  return x.trim();\n}\nexport {\n  format_default as default\n};\n", false)]
    // an alias of something that is not a component
    [InlineData("const config = { a: 1 };\nvar config_default = config;\nexport {\n  config_default as default\n};\n", false)]
    public void Understands_default_export_shapes(string code, bool isBoundary)
    {
        Assert.Equal(isBoundary, ReactRefresh.Analyze(code).IsBoundary);
    }

    [Fact]
    public void Footer_goes_before_the_source_map_comment()
    {
        var info = ReactRefresh.Analyze(Components);
        var output = ReactRefresh.Append(Components, ReactRefresh.Footer(info, "routes/App.tsx", "../../refresh.js"));

        Assert.Contains("import * as __nite_refresh from \"../../refresh.js\";", output);
        Assert.Contains("__nite_refresh.register(App, \"routes/App.tsx App\");", output);
        Assert.Contains("__nite_refresh.sign(App, \"useState,useTodos\");", output);
        Assert.EndsWith("//# sourceMappingURL=data:application/json;base64,AAAA", output.TrimEnd());
    }
}
