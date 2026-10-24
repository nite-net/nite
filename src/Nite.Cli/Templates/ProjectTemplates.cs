namespace Nite.Cli.Templates;

/// <summary>
/// The files <c>nite init</c> writes. Placeholders: __NAME__ (project name), __NAMESPACE__ (a valid
/// C# namespace), __NITE_REFERENCE__ (how the project gets Nite) and __PORT__.
/// </summary>
internal static class ProjectTemplates
{
    public const string Spa = "spa";
    public const string Islands = "islands";
    public static readonly string[] Names = [Spa, Islands];

    public static IReadOnlyDictionary<string, string> For(string template) => template switch
    {
        Spa => Shared.Concat(SpaFiles).ToDictionary(),
        Islands => Shared.Concat(IslandFiles).ToDictionary(),
        _ => throw new ArgumentOutOfRangeException(nameof(template)),
    };

    private static readonly Dictionary<string, string> Shared = new()
    {
        ["__NAME__.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk.Web">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <RootNamespace>__NAMESPACE__</RootNamespace>
              </PropertyGroup>

            __NITE_REFERENCE__
            </Project>

            """,
        ["appsettings.json"] = """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning"
                }
              },
              "AllowedHosts": "*"
            }

            """,
        ["Properties/launchSettings.json"] = """
            {
              "$schema": "https://json.schemastore.org/launchsettings.json",
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "dotnetRunMessages": true,
                  "launchBrowser": true,
                  "applicationUrl": "http://localhost:__PORT__",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development"
                  }
                }
              }
            }

            """,
        [".gitignore"] = """
            bin/
            obj/
            node_modules/
            .nite/
            wwwroot/_nite/

            """,
        ["Client/tsconfig.json"] = """
            {
              // Editor settings only: Nite compiles with esbuild, which strips types without
              // checking them. Run `tsc --noEmit` in CI for type checking.
              "compilerOptions": {
                "target": "ES2022",
                "module": "ESNext",
                "moduleResolution": "Bundler",
                "jsx": "react-jsx",
                "strict": true,
                "noEmit": true,
                "isolatedModules": true,
                "skipLibCheck": true,
                "lib": ["ES2022", "DOM", "DOM.Iterable"],
                "typeRoots": ["../node_modules/@types"]
              },
              "include": ["**/*.ts", "**/*.tsx"]
            }

            """,
    };

    private static readonly Dictionary<string, string> SpaFiles = new()
    {
        ["nite.json"] = """
            {
              // Client/index.html is the entry; app.MapNiteSpa() serves it.
              "clientRoot": "Client",
              "dependencies": {
                "react": "^19.3.1",
                "react-dom": "^19.3.1"
              },
              "devDependencies": {
                "@types/react": "^19.2.0",
                "@types/react-dom": "^19.2.0"
              }
            }

            """,
        ["Program.cs"] = """
            using Nite;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddNite();

            var app = builder.Build();

            // Development: compiles Client/ on request with hot updates. Production: no-op.
            app.UseNite();

            app.MapGet("/api/hello", () => new { message = "Hello from ASP.NET Core", time = DateTimeOffset.Now });

            app.MapStaticAssets();

            // Serves Client/index.html for "/" and client-side routes; /api/* never falls back.
            app.MapNiteSpa();

            app.Run();

            """,
        ["Client/index.html"] = """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>__NAME__</title>
                <link rel="stylesheet" href="./styles.css" />
              </head>
              <body>
                <div id="root"></div>
                <script type="module" src="./main.tsx"></script>
              </body>
            </html>

            """,
        ["Client/main.tsx"] = """
            import { StrictMode } from "react";
            import { createRoot } from "react-dom/client";
            import { App } from "./App";

            createRoot(document.getElementById("root")!).render(
              <StrictMode>
                <App />
              </StrictMode>,
            );

            """,
        ["Client/App.tsx"] = """
            import { useEffect, useState } from "react";

            type Hello = { message: string; time: string };

            export function App() {
              const [hello, setHello] = useState<Hello | null>(null);
              const [count, setCount] = useState(0);

              useEffect(() => {
                const load = () => fetch("/api/hello").then((r) => r.json()).then(setHello);
                load();
                // Under `nite watch`, C# hot reloads raise this event: re-fetch, keep state.
                addEventListener("nite:server-update", load);
                return () => removeEventListener("nite:server-update", load);
              }, []);

              return (
                <main>
                  <h1>__NAME__</h1>
                  <p className="muted">
                    Edit <code>Client/App.tsx</code> or <code>Program.cs</code> and save.
                  </p>
                  <p>{hello ? `${hello.message} at ${new Date(hello.time).toLocaleTimeString()}` : "Calling the API…"}</p>
                  <button onClick={() => setCount((c) => c + 1)}>Clicked {count} times</button>
                </main>
              );
            }

            """,
        ["Client/styles.css"] = """
            :root {
              color-scheme: light dark;
              font: 16px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
            }

            main {
              max-width: 640px;
              margin: 4rem auto;
              padding: 0 16px;
            }

            .muted {
              opacity: 0.7;
            }

            button {
              font: inherit;
              padding: 0.5rem 1rem;
              border-radius: 8px;
              border: 0;
              background: #4b5bdc;
              color: white;
              cursor: pointer;
            }

            """,
    };

    private static readonly Dictionary<string, string> IslandFiles = new()
    {
        ["nite.json"] = """
            {
              // Components in Client/islands/ can be placed in Razor pages with <island name="...">.
              "clientRoot": "Client",
              "islands": "islands",
              "dependencies": {
                "react": "^19.3.1",
                "react-dom": "^19.3.1"
              },
              "devDependencies": {
                "@types/react": "^19.2.0",
                "@types/react-dom": "^19.2.0"
              }
            }

            """,
        ["Program.cs"] = """
            using Nite;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddRazorPages();
            builder.Services.AddNite();

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            // Development: compiles Client/ on request with hot updates. Production: no-op.
            app.UseNite();

            app.UseRouting();
            app.MapStaticAssets();
            app.MapRazorPages().WithStaticAssets();

            app.Run();

            """,
        ["Pages/_ViewImports.cshtml"] = """
            @namespace __NAMESPACE__.Pages
            @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
            @addTagHelper *, Nite

            """,
        ["Pages/_ViewStart.cshtml"] = """
            @{
                Layout = "_Layout";
            }

            """,
        ["Pages/Shared/_Layout.cshtml"] = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>@ViewData["Title"] · __NAME__</title>
                <link rel="stylesheet" href="~/site.css" asp-append-version="true" />
                <nite-head />
            </head>
            <body>
                <main>
                    @RenderBody()
                </main>
            </body>
            </html>

            """,
        ["Pages/Index.cshtml"] = """
            @page
            @{
                ViewData["Title"] = "Home";
            }

            <h1>__NAME__</h1>
            <p class="muted">This page is Razor. The counter below is a React island from <code>Client/islands/Counter.tsx</code>.</p>

            <island name="Counter" props="new { start = 0 }">
                <p class="muted">Loading…</p>
            </island>

            """,
        ["Pages/Error.cshtml"] = """
            @page
            <h1>Something went wrong.</h1>

            """,
        ["Client/islands/Counter.tsx"] = """
            import { useState } from "react";

            export default function Counter({ start }: { start: number }) {
              const [count, setCount] = useState(start);
              return <button onClick={() => setCount((c) => c + 1)}>Clicked {count} times</button>;
            }

            """,
        ["wwwroot/site.css"] = """
            :root {
              color-scheme: light dark;
              font: 16px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
            }

            main {
              max-width: 640px;
              margin: 4rem auto;
              padding: 0 16px;
            }

            .muted {
              opacity: 0.7;
            }

            button {
              font: inherit;
              padding: 0.5rem 1rem;
              border-radius: 8px;
              border: 0;
              background: #4b5bdc;
              color: white;
              cursor: pointer;
            }

            """,
    };
}
