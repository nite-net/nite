# nite

React islands (and TypeScript in general) for ASP.NET Core, without a Node.js toolchain.

Write components in `.tsx`, drop them into Razor pages with a tag helper, and get a Vite-style dev
loop: compile on request, pre-bundled npm packages, CSS hot swap, an error overlay. `dotnet publish`
produces minified, hashed, code-split bundles served by `MapStaticAssets`. No `node`, `npm`,
`package.json` or Vite anywhere.

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
| **Live updates** | A file watcher pushes over a WebSocket. CSS edits swap in place and keep component state; other edits reload the page. Compile errors and missing packages show an overlay with file, line and source. |
| **Islands** | A 1.5 KB (minified) loader finds `data-island` elements, loads each island's module on `load`, `idle`, `visible` or a `media` query, and mounts it. A `MutationObserver` mounts islands added later (htmx, Turbo, Blazor enhanced navigation) and unmounts removed ones. |
| **Production** | One esbuild build: islands, entries and the loader, minified and split into shared chunks, plus a manifest the tag helpers read. Output lands in `wwwroot/_nite`, so `MapStaticAssets` fingerprints and precompresses it. |

For the sample app, a page ships React and ReactDOM once (about 58 KB with Brotli) and each island
adds about 0.5 KB.

## Getting started

The repository holds the library (`src/Nite`), the CLI (`src/Nite.Cli`), a sample
(`samples/Nite.Sample`) and tests. There are no NuGet packages yet, so reference the projects
directly, as the sample does.

```bash
cd samples/Nite.Sample
dotnet run                     # http://localhost:5025, Development mode
dotnet publish -c Release      # runs `nite build`, ships wwwroot/_nite
```

The first run downloads esbuild and the packages in `nite.json` (a few seconds); after that,
startup reuses the cache and the lockfile.

### 1. Wire it up

```csharp
builder.Services.AddNite();
// ...
app.UseNite();          // before UseRouting / MapStaticAssets; a no-op outside Development
```

```cshtml
@* _ViewImports.cshtml *@
@addTagHelper *, Nite

@* _Layout.cshtml, inside <head> *@
<nite-head />
```

For `dotnet publish`, import `build/Nite.targets` and set `NiteCliPath` (see
`samples/Nite.Sample/Nite.Sample.csproj`).

### 2. Configure packages: `nite.json`

```jsonc
{
  "clientRoot": "Client",          // client source folder
  "islands": "islands",            // Client/islands/*.tsx are usable as <island name="...">
  "entries": [],                   // other modules pages load with <nite-module src="...">
  "renderer": "react",             // or a module exporting mount(el, Component, props)
  "jsxImportSource": "react",      // "preact" works too, with a custom renderer
  "target": "es2022",
  "dependencies": { "react": "^19.3.1", "react-dom": "^19.3.1" },
  "devDependencies": { "@types/react": "^19.2.0", "@types/react-dom": "^19.2.0" }
}
```

Edit `dependencies` while the app is running and Nite installs, re-bundles and reloads the page.
Commit `nite.lock.json`.

### 3. Write islands

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
when the island mounts, so it works as a loading state and as content for crawlers.

Components can import CSS (`import "./x.css"`), JSON and assets (`import logo from "./logo.svg"`
gives a URL), as with Vite.

## CLI

```bash
dotnet run --project src/Nite.Cli -- install [project-dir]   # node_modules + nite.lock.json
dotnet run --project src/Nite.Cli -- build   [project-dir]   # production build into wwwroot/_nite
```

## Limits and non-goals

Nite is intentionally smaller than Vite. Things it does not do (yet, or ever):

- **No Vite/Rollup plugins.** Vue SFCs, Svelte, MDX, Tailwind-as-a-plugin and friends need a JS
  runtime. If you need them, use Vite; it's the right tool.
- **No type checking**, same as Vite: esbuild strips types. Run `tsc --noEmit` (or the native
  TypeScript compiler) in CI.
- **No React Fast Refresh.** Script edits reload the page; CSS edits swap in place. Islands keep
  that cheap, but component state is lost on a script edit.
- **No SSR of islands.** Islands render on the client; use the fallback content for the
  server-side picture.
- **Registry packages only**: no git, `file:` or `npm:` alias dependencies. Optional dependencies
  are skipped and install scripts never run, so packages that need a postinstall step won't work.
- `tsconfig` `paths` aliases, CSS `url()` rewriting in dev, and CSS from inside npm packages are
  not handled yet.
- Production asset URLs assume the app is served from the site root (`/_nite/...`).

## Development

```bash
dotnet build
dotnet test                                          # includes esbuild integration tests (needs network once)
dotnet test --filter "Category!=Integration"         # offline
```

Environment variables: `NITE_CACHE_DIR` moves the per-user cache (esbuild binary and tarballs);
`NITE_ESBUILD_PATH` points to an esbuild binary you supply (it must be version 0.28.2).
