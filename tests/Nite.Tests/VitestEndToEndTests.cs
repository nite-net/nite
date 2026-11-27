using System.Diagnostics;
using Nite.Packages;
using Nite.Testing;

namespace Nite.Tests;

/// <summary>
/// Installs Vitest from the npm registry with NiteFx's own installer and runs it with Node, so a
/// change that breaks optional dependencies or the generated config fails here. Needs Node on
/// PATH (CI sets it up); without it there is nothing to run the tests with, and this returns.
/// </summary>
public class VitestEndToEndTests : IDisposable
{
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-vitest-e2e-");

    public void Dispose() => _project.Delete(recursive: true);

    [Fact]
    public async Task Runs_tests_with_aliases_env_and_client_config()
    {
        if (await NodeRuntime.FindAsync() is not { } node)
        {
            return;
        }

        var root = _project.FullName;
        Directory.CreateDirectory(Path.Combine(root, "Client", "src", "lib"));
        File.WriteAllText(Path.Combine(root, "nite.json"), """
            {
              "test": { "config": { "apiBase": "/api-test" } },
              "devDependencies": { "vitest": "^4.0.0", "jsdom": "^27.0.0" }
            }
            """);
        File.WriteAllText(Path.Combine(root, ".env"), "NITE_PUBLIC_GREETING=hello\n");
        File.WriteAllText(Path.Combine(root, "Client", "tsconfig.json"), """{ "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }""");
        File.WriteAllText(Path.Combine(root, "Client", "src", "lib", "math.ts"), "export const double = (n: number) => n * 2;\n");
        File.WriteAllText(Path.Combine(root, "Client", "src", "app.test.ts"), """
            import { expect, test } from "vitest";
            import { double } from "@/lib/math";
            import { config } from "nite:config";

            test("alias", () => expect(double(2)).toBe(4));
            test("env", () => expect(import.meta.env.NITE_PUBLIC_GREETING).toBe("hello"));
            test("config", () => expect(config.apiBase).toBe("/api-test"));
            test("dom", () => expect(typeof document.createElement).toBe("function"));
            """);

        var project = new NiteProject(root);
        await new PackageInstaller(new HttpClient()).InstallAsync(root, project.Config);
        var setup = VitestSetup.Find(project)!;
        Assert.True(setup.SupportsNode(node.Version), $"Node {node.Version} is too old for Vite ({setup.NodeRange}).");
        Assert.Equal("jsdom", setup.Environment());

        var start = new ProcessStartInfo(node.Path) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { setup.Entry, "run", "--config", setup.WriteConfig() })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));

        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.Contains("4 passed", await output);
    }
}
