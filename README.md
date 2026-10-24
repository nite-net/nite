<p align="center">
  <img src="docs/logo.png" alt="(n)ite" width="360">
</p>

# nite

TypeScript and React for ASP.NET Core with a Vite-style dev loop and no Node.js toolchain.

Build the frontend and the backend in one project, one process, one `dotnet watch`: edit a React
component and it updates in place with its state intact; edit a C# endpoint and the page re-fetches
without reloading. `dotnet publish` produces minified, hashed, code-split bundles served by
`MapStaticAssets`. No `node`, `npm`, `package.json` or Vite anywhere.

Two ways to use it:

- **A single-page app**: `Client/index.html` plus React (or plain TypeScript), served by
  `app.MapNiteSpa()` next to your API. Same origin, so no CORS and no dev proxy.
- **Islands in Razor pages**: server-rendered pages with interactive React components where you
  need them.

```cshtml
<island name="TodoList" props='new { title = "Today", items = Model.Todos }' client="visible">
    @* Server-rendered fallback until the island loads *@
    <ul>@foreach (var t in Model.Todos) { <li>@t.Title</li> }</ul>
</island>
```

## How it works

Nite does the orchestration in .NET and hands compilation to **esbuild**, the same Go binary Vite
uses for dependency pre-bundling.

| Piece | What Nite does |
|---|---|
| **esbuild** | Downloads the platform binary from npm (`@esbuild/linux-x64` etc.), verifies its integrity hash, caches it per user, and keeps one `esbuild --service` process alive for the life of the app, talking esbuild's binary stdio protocol (the same channel its JS API uses). |
| **Packages** | A small npm client in C#: resolves semver ranges from the registry, hoists into a normal `node_modules`, writes `nite.lock.json`, verifies tarball integrity, never runs install scripts. Editors and `tsc` see packages where they expect them. |
| **Dev server** (`/_nite`) | Each source file is transformed on request (cached until it changes). Relative imports are rewritten to canonical URLs so a module never loads twice. Bare imports (`react`) go through an **import map** to pre-bundled dependencies. |
| **Pre-bundling** | Every bare import becomes one ESM file, built in a single code-split esbuild pass so `react-dom` and your code share one React. CommonJS packages (React itself, still) get a generated wrapper whose named exports come from a `cjs-module-lexer`-style scan. |
| **React Fast Refresh** | esbuild has no Fast Refresh transform, so Nite reads esbuild's output, registers each component with `react-refresh` and signs it with its hook calls. A module graph tracks who imports what; a change walks up to the nearest module whose exports are all components, re-imports it, and React swaps the new code in. Changing hooks remounts the component instead of crashing. If the walk reaches the page's entry module, the page reloads. |
| **Other live updates** | CSS (imported or `<link>`ed) swaps in place. Compile errors show an overlay without reloading and clear when the file is fixed. C# hot reloads raise a `nite:server-update` event in the browser. |
| **HTML entries** | `Client/index.html` is a real entry, as in Vite: local module scripts and stylesheets are compiled in dev and replaced with hashed bundles in production. |
| **Islands** | A 1.5 KB (minified) loader finds `data-island` elements, loads each island's module on `load`, `idle`, `visible` or a `media` query, and mounts it. A `MutationObserver` mounts islands added later (htmx, Turbo, Blazor enhanced navigation) and unmounts removed ones. |
| **Production** | One esbuild build for islands, HTML entries and other entries: minified, split into shared chunks, plus a manifest. Output lands in `wwwroot/_nite`, so `MapStaticAssets` serves it precompressed. |

Hot updates land in roughly 100 to 200 ms. A page of islands ships React and ReactDOM once (about
58 KB with Brotli) and each island adds about 0.5 KB.

## Getting started

```bash
dotnet tool install --global Nite.Cli    # the `nite` command

nite init my-app                         # React SPA + minimal API (or: --template islands)
cd my-app
nite watch                               # dotnet watch, with Nite driving the browser
nite add react-router                    # packages, pnpm style
dotnet publish -c Release                # runs `nite build`, ships wwwroot/_nite
```

The packages are not on NuGet yet. Until they are, build from this repository and point new
projects at it: `nite init my-app --nite-source path/to/nite` (run the CLI with
`dotnet run --project src/Nite.Cli --`). The samples work the same way:

```bash
cd samples/Nite.Spa && nite watch                        # React SPA + minimal API
cd samples/Nite.Sample && nite watch --browser-refresh   # Razor Pages with React islands
```

The first run downloads esbuild and the packages in `nite.json` (a few seconds); after that,
startup reuses the cache and the lockfile.

### A single-page app

```
MyApp/
  Program.cs
  nite.json
  Client/
    index.html        <script type="module" src="./main.tsx"></script>
    main.tsx          createRoot(...).render(<App />)
    App.tsx
  wwwroot/            static files as-is (favicon, images)
```

```csharp
builder.Services.AddNite();

var app = builder.Build();
app.UseNite();                       // dev server under /_nite; no-op outside Development

app.MapGet("/api/todos", ...);       // your API, same origin as the app
app.MapStaticAssets();
app.MapNiteSpa();                    // index.html for "/" and client-side routes like /todos/3
```

`MapNiteSpa` is a fallback route, so real endpoints always win. It never answers under `/api`
(pass `apiPrefix` to change that), so a mistyped API call gets a 404, not a page of HTML. In
development it serves `Client/index.html` with the dev client injected; in production, the built
copy with hashed URLs.

### Islands in Razor pages

```cshtml
@* _ViewImports.cshtml *@
@addTagHelper *, Nite

@* _Layout.cshtml, inside <head> *@
<nite-head />
```

```tsx
// Client/islands/Counter.tsx
import { useState } from "react";

export default function Counter({ start }: { start: number }) {
  const [count, setCount] = useState(start);
  return <button onClick={() => setCount(count + 1)}>{count}</button>;
}
```

```cshtml
<island name="Counter" props="new { start = 5 }" />
<island name="Chart" props="Model.Series" client="visible" />
<island name="Sidebar" client="media" media="(min-width: 900px)" />
<island name="admin/UserTable" export="UserTable" tag="section" />
```

Props are serialized as camelCase JSON. Child content is rendered by the server and replaced
when the island mounts, so it works as a loading state and as content for crawlers. Modules that
aren't islands can be loaded with `<nite-module src="main.ts" />` (list them in `entries`).

Components can import CSS (`import "./x.css"`), JSON and assets (`import logo from "./logo.svg"`
gives a URL), as with Vite.

### `nite.json`

```jsonc
{
  "clientRoot": "Client",          // client source folder
  "islands": "islands",            // Client/islands/*.tsx are usable as <island name="...">
  "entries": [],                   // more HTML pages or <nite-module> scripts (index.html is automatic)
  "renderer": "react",             // island renderer, or a module exporting mount(el, Component, props)
  "jsxImportSource": "react",      // "preact" works too, with a custom renderer
  "fastRefresh": true,             // installs react-refresh as a dev dependency when React is used
  "target": "es2022",
  "dependencies": { "react": "^19.3.1", "react-dom": "^19.3.1" },
  "devDependencies": { "@types/react": "^19.2.0", "@types/react-dom": "^19.2.0" }
}
```

Use `nite add` / `nite rm` rather than editing by hand (comments in the file are kept), or edit
`dependencies` while the app is running and Nite installs, re-bundles and reloads the page.
Commit `nite.lock.json`.

`dotnet publish` runs `nite build` through the targets in the Nite package, using the `nite` tool.
Set `NiteCommand` (for example to `dotnet tool run nite` for a local tool), or `NiteBuildEnabled`
to `false` to skip it.

## Full-stack dev with `nite watch`

`nite watch` runs `dotnet watch`: it handles the C# side and Nite handles the client side.

| You edit | What happens |
|---|---|
| A React component | Fast Refresh: updated in place, state kept (about 100 to 200 ms). |
| A helper module (`format.ts`) | The components that use it refresh; state kept. |
| CSS | Swapped in place. |
| The entry (`main.tsx`) or `index.html` | Page reloads. |
| C# that hot reload can apply | Applied in the running app; the browser gets a `nite:server-update` event. |
| C# that needs a restart (new endpoint in `Program.cs`, say) | dotnet watch restarts the app; the page waits for it and reloads (about 10 s). |

Plain `dotnet watch` injects its own browser script, which reloads the page after **every** C#
change. That suits Razor pages, but in a single-page app it throws away client state for an
API-only change. `nite watch` turns it off (`DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1`, which only
works from the environment, not `launchSettings.json`) and Nite takes over, reloading after
restarts by itself. For Razor pages, keep dotnet's refresh with `nite watch --browser-refresh`.
Arguments after `--` go to dotnet watch: `nite watch -- --launch-profile https`.

Let pages re-fetch when server code changes:

```ts
export function onServerUpdate(callback: () => void) {
  addEventListener("nite:server-update", callback);
  return () => removeEventListener("nite:server-update", callback);
}

useEffect(() => {
  load();
  return onServerUpdate(load); // edit the endpoint in C#, see the new data, keep your state
}, []);
```

The event never fires in production, so this costs nothing there.

## CLI

| Command | Aliases | What it does |
|---|---|---|
| `nite init [dir]` | | Scaffold a project: `--template spa` (default) or `islands`, `--name`, `--nite-source <repo>`, `--no-install`. |
| `nite install [pkg...]` | `add`, `i` | No arguments: install everything in `nite.json`. With packages (`react`, `react@^19`, `@types/node@latest`): add them, then install. `-D` for devDependencies, `-E` to pin exactly. |
| `nite remove <pkg...>` | `rm`, `delete`, `uninstall` | Remove from `nite.json` and `node_modules`. |
| `nite list` | `ls` | Direct packages with their ranges and installed versions; `--all` adds transitive ones. |
| `nite update [pkg...]` | `up` | Show current, wanted and latest versions, then update within the ranges. `--check` only reports; `--latest` moves the ranges to the latest versions. |
| `nite build` | | Production build into `wwwroot/_nite` (publish runs this). |
| `nite watch` | | `dotnet watch` with Nite handling the browser; see above. |
| `nite version` | `--version` | The Nite version (and esbuild and .NET versions). |
| `nite update-self` | | `dotnet tool update Nite.Cli`, for however nite is installed: local tool manifest, global, or `--tool-path`. |

Project commands work on the current directory; `-C <dir>` points them elsewhere, and
`--verbose` shows debug output. Installs keep the versions already in `nite.lock.json` unless a
range forces a change, so adding one package never upgrades the others; that's what
`nite update` is for.

## Limits and non-goals

Nite is intentionally smaller than Vite. Things it does not do (yet, or ever):

- **No Vite/Rollup plugins.** Vue SFCs, Svelte, MDX, Tailwind-as-a-plugin and friends need a JS
  runtime. If you need them, use Vite; it's the right tool.
- **No type checking**, same as Vite: esbuild strips types. Run `tsc --noEmit` (or the native
  TypeScript compiler) in CI.
- **Fast Refresh is heuristic.** Components are found in esbuild's output by shape: PascalCase
  functions, arrow functions, `memo` and `forwardRef`. Hook signatures cover the component's own
  hook calls; if a *custom* hook changes its internal hook order, save again or reload. Class
  components and higher-order components other than `memo`/`forwardRef` refresh by propagating to
  their importers.
- **No SSR of islands.** Islands render on the client; use the fallback content for the
  server-side picture.
- **Registry packages only**: no git, `file:` or `npm:` alias dependencies. Optional dependencies
  are skipped and install scripts never run, so packages that need a postinstall step won't work.
- `tsconfig` `paths` aliases, CSS `url()` rewriting in dev, and CSS from inside npm packages are
  not handled yet.
- Production asset URLs assume the app is served from the site root (`/_nite/...`), except in
  HTML served by `MapNiteSpa`, which adds the request's path base. Hashed bundles are revalidated
  with ETags rather than cached as immutable.

## Development

```bash
dotnet build
dotnet test                                          # includes esbuild integration tests (needs network once)
dotnet test --filter "Category!=Integration"         # offline
```

Environment variables: `NITE_CACHE_DIR` moves the per-user cache (esbuild binary and tarballs);
`NITE_ESBUILD_PATH` points to an esbuild binary you supply (it must be version 0.28.2).

## License

[Apache License 2.0](LICENSE).
