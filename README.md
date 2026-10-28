<p align="center">
  <img src="docs/logo.png" alt="NiteFx" width="360">
</p>

# NiteFx

TypeScript and React for ASP.NET Core with a Vite-style dev loop and no Node.js toolchain.

*NiteFx* is short for **Nite Framework**, and *Nite* is pronounced like **night**, not *neat*. (Vite is French for "quick" and pronounced *veet*.) The packages (`Nite`, `Nite.Cli`, `Nite.Templates`), the C# namespace and the `nite` command keep the short name.

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

NiteFx does the orchestration in .NET and hands compilation to **esbuild**, the same Go binary Vite
uses for dependency pre-bundling.

| Piece | What NiteFx does |
|---|---|
| **esbuild** | Downloads the platform binary from npm (`@esbuild/linux-x64` etc.), verifies its integrity hash, caches it per user, and keeps one `esbuild --service` process alive for the life of the app, talking esbuild's binary stdio protocol (the same channel its JS API uses). |
| **Packages** | A small npm client in C#: resolves semver ranges from the registry, hoists into a normal `node_modules`, writes `nite.lock.json`, verifies tarball integrity, never runs install scripts. Editors and `tsc` see packages where they expect them. |
| **Dev server** (`/_nite`) | Each source file is transformed on request (cached until it changes). Relative imports are rewritten to canonical URLs so a module never loads twice. Bare imports (`react`) go through an **import map** to pre-bundled dependencies. |
| **Pre-bundling** | Every bare import becomes one ESM file, built in a single code-split esbuild pass so `react-dom` and your code share one React. CommonJS packages (React itself, still) get a generated wrapper whose named exports come from a `cjs-module-lexer`-style scan. |
| **React Fast Refresh** | esbuild has no Fast Refresh transform, so NiteFx reads esbuild's output, registers each component with `react-refresh` and signs it with its hook calls. A module graph tracks who imports what; a change walks up to the nearest module whose exports are all components, re-imports it, and React swaps the new code in. Changing hooks remounts the component instead of crashing. If the walk reaches the page's entry module, the page reloads. |
| **Stylesheets** | Sass (Dart Sass's native binary, over its embedded protocol), Less, Tailwind CSS 4 and your PostCSS config run inside NiteFx, then esbuild bundles the result: CSS modules, `@import` from packages, `url()` assets. The JavaScript tools (PostCSS and its plugins, Tailwind's compiler, Less) come from your `node_modules` and run in [Jint](https://github.com/sebastienros/jint), a JavaScript interpreter written in C#. See [Stylesheets](#stylesheets). |
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
nite init my-app --from ../vite-app      # ...or import an existing Vite / Create React App project
cd my-app
nite watch                               # dotnet watch, with Nite driving the browser
nite add react-router                    # packages, pnpm style
dotnet publish -c Release                # runs `nite build`, ships wwwroot/_nite
```

Prefer `dotnet new`? The same two templates come as a template pack, which also puts them in
Visual Studio's and Rider's new-project dialogs:

```bash
dotnet new install Nite.Templates
dotnet new nite-spa -o my-app            # or: dotnet new nite-islands
```

`nite init` and `dotnet new` write the same files (`nite init` also installs the packages; with
`dotnet new` the first `nite watch` does). Either way you need the `nite` tool for `nite watch`
and for Release builds.

To work on NiteFx itself, point new projects at a checkout instead of the NuGet package:
`nite init my-app --nite-source path/to/nite` (run the CLI with
`dotnet run --project src/Nite.Cli --`). The samples work the same way:

```bash
cd samples/Nite.Spa && nite watch                        # React SPA + minimal API
cd samples/Nite.Sample && nite watch --browser-refresh   # Razor Pages with React islands
dotnet run --project samples/Nite.Aspire/AppHost         # the SPA and an API under Aspire
cd samples/Nite.Identity && nite watch                   # Identity sign-in, BFF style: sessions, CSRF, strict CSP
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
copy with hashed URLs. For more than one app, give each its own path:
`app.MapNiteSpa("login/index.html", path: "/login")` answers `/login` and everything under it,
next to the catch-all `app.MapNiteSpa()` ([`samples/Nite.Identity`](samples/Nite.Identity)).

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

Components can import CSS (`import "./x.css"`, `import styles from "./x.module.scss"`), JSON and
assets (`import logo from "./logo.svg"` gives a URL), as with Vite.

### `nite.json`

```jsonc
{
  "clientRoot": "Client",          // client source folder
  "islands": "islands",            // Client/islands/*.tsx are usable as <island name="...">
  "entries": [],                   // more HTML pages or <nite-module> scripts (index.html is automatic)
  "renderer": "react",             // island renderer, or a module exporting mount(el, Component, props)
  "jsxImportSource": "react",      // "preact" works too, with a custom renderer
  "fastRefresh": true,             // installs react-refresh as a dev dependency when React is used
  "envPrefix": ["NITE_PUBLIC_"],   // env vars with these prefixes reach client code (see below)
  "jsxInJs": false,                // allow JSX in .js files (Create React App style)
  "target": "es2022",              // plus Vite's default browsers, so CSS is lowered for them too
  "css": {                         // see Stylesheets
    "postCss": true,
    "preprocessorOptions": { "scss": { "additionalData": "" }, "less": {} }
  },
  "dependencies": { "react": "^19.3.1", "react-dom": "^19.3.1" },
  "devDependencies": { "@types/react": "^19.2.0", "@types/react-dom": "^19.2.0" }
}
```

Use `nite add` / `nite rm` rather than editing by hand (comments in the file are kept), or edit
`dependencies` while the app is running and NiteFx installs, re-bundles and reloads the page.
Commit `nite.lock.json`.

### Install scripts

NiteFx never runs `preinstall`, `install` or `postinstall` scripts: they are arbitrary code, and
most of them build or download things for Node.js, which a browser has no use for. When a
package that has one is resolved (the registry marks it with `hasInstallScript`), NiteFx says so
once, after resolving, with a clearer message for the usual suspects:

| Package | What NiteFx says |
|---|---|
| `core-js`, `core-js-pure`, `es5-ext` | The script only prints a message; nothing is missing. |
| `esbuild` | The script fetches the esbuild binary, which NiteFx downloads and runs itself. |
| `node-sass` | Warning: it builds LibSass. NiteFx compiles Sass with Dart Sass's native binary; use `sass` instead. |
| `bcrypt`, `better-sqlite3`, `sqlite3`, `argon2`, `canvas` | Warning: a native Node.js addon, which cannot run in a browser. Do that work in ASP.NET Core. |
| anything else | Warning: the package may not work if it needs its script to download or build something. |

The messages appear when the lockfile is resolved (`nite add`, `nite update`, a changed
`nite.json`), not on every install from a current `nite.lock.json`.

`dotnet publish` runs `nite build` through the targets in the Nite package, using the `nite` tool.
Set `NiteCommand` (for example to `dotnet tool run nite` for a local tool), or `NiteBuildEnabled`
to `false` to skip it.

### Environment variables and path aliases

Variables from `.env`, `.env.local`, `.env.development` / `.env.production` (and their `.local`
variants) in the project folder, and from the process environment, are inlined into client code
**only if** their name starts with an `envPrefix` (default `NITE_PUBLIC_`). Code reads them as
`import.meta.env.NITE_PUBLIC_API` (with `MODE`, `DEV`, `PROD` and `BASE_URL` alongside, as in Vite)
or `process.env.NITE_PUBLIC_API` (as in Create React App). Anything else in `.env`, such as database
passwords, never reaches the browser. Editing a `.env` file reloads the page with the new values.

Those values are inlined when code is compiled, so a production build carries whatever was set when
`nite build` ran. For values that differ per environment or per request (an Application Insights
connection string, the signed-in user's name, a feature flag), let the server hand them over when it
serves the page:

```csharp
builder.Services.AddNite(options => options.ClientConfig = context => new
{
    AppInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"],
    User = context.User.Identity?.Name,
});
```

```ts
import { config } from "nite:config";   // { appInsights, user }, as of this page load
```

`MapNiteSpa` and `<nite-head />` write the values into the page as JSON (pages that carry them are
sent `Cache-Control: private`), so one build runs in every environment. Describe their shape in
`Client/nite-env.d.ts`, which `nite init` creates. `ClientConfigAsync` takes an async callback.
[`samples/Nite.Aspire`](samples/Nite.Aspire) uses it to start Application Insights in the browser.

`compilerOptions.paths` in the client's tsconfig (following `extends` and Vite's `references` to
`tsconfig.app.json`) work in development and production, so `import { cn } from "@/lib/utils"`
resolves as it does in your editor.

## Stylesheets

Stylesheets go through the same steps as in Vite, in the same order, in development and in
production builds alike:

1. **Sass** (`.scss`, `.sass`) or **Less** (`.less`), by extension.
2. **Tailwind CSS 4**, when the file uses it (`@import "tailwindcss"`, `@apply`, `@theme`, …).
3. **PostCSS**, when the project has a config (`postcss.config.*`, `.postcssrc*`, or `"postcss"` in
   package.json), with its plugins.
4. **esbuild**: inlines `@import` (from packages too, through `exports` and the `style`
   condition), scopes CSS modules (`*.module.css`, `.module.scss`, `.module.less`, with `composes`),
   handles `url()` assets, and in production minifies and lowers for the target browsers.

Install the tools you use as dev dependencies, as you would for Vite: `nite add -D sass-embedded`
(or `sass`), `less`, `tailwindcss`, `postcss` plus its plugins. NiteFx runs them, no Node needed:

- **Sass** runs as the native Dart Sass binary, from the `sass-embedded-<platform>` npm package
  that matches your `sass`/`sass-embedded` version (downloaded once into the cache, like esbuild).
  One compiler process serves every compile. `pkg:` URLs work, and the client root and
  `node_modules` are load paths.
- **PostCSS, Tailwind and Less** are plain JavaScript. NiteFx bundles them from your `node_modules`
  with esbuild and runs the bundle in Jint, with just enough of Node's built-ins (`fs`, `path`,
  `url`…) for them to work. The interpreter is slower than Node: expect a second or two the first
  time a dev server starts compiling a Tailwind stylesheet, then incremental updates in tens of
  milliseconds. Production builds pay it once.
- **Tailwind** is Tailwind's own compiler (the `tailwindcss` package). NiteFx stands in for its
  native file scanner: class names are found in the whole project folder, so classes used only in
  Razor views (`.cshtml`, `.razor`) count too. `.gitignore`d folders, `bin`, `obj` and
  `node_modules` are skipped, and `@source`, `@source not` and `@source inline()` work as
  documented. `@plugin` and `@config` load from `node_modules`. `@tailwindcss/vite` and
  `@tailwindcss/postcss` are not needed (they need native code); if your PostCSS config lists the
  latter, NiteFx skips it and runs Tailwind itself. Tailwind 3 is not supported.
- PostCSS runs on your stylesheets, not on CSS shipped in packages (it is already built, and slow
  to re-process). Set `"css": { "postCss": false }` to turn it off.

Preprocessor options use Vite's names. `additionalData` is prepended to every file, which is how
Sass setups share variables and mixins; files in the client root can be used by name:

```jsonc
"css": {
  "preprocessorOptions": {
    "scss": { "additionalData": "@use \"src/mantine\" as mantine;", "loadPaths": [], "silenceDeprecations": [] },
    "less": { "additionalData": "", "paths": [], "javascriptEnabled": false, "globalVars": {}, "modifyVars": {} }
  }
}
```

In development every stylesheet updates in place, and so does everything that depends on a file
you save: a Sass partial, a Less import, the PostCSS config, or (for Tailwind) any source file that
gains a class. CSS modules update the components that use them, with state kept. Errors show in
the overlay with the file and line (Sass line numbers account for `additionalData`).

### Mantine

[Mantine](https://mantine.dev) works as its [Vite guide](https://mantine.dev/guides/vite/) sets it
up: `@mantine/core/styles.css` imported from code, `postcss-preset-mantine` and
`postcss-simple-vars` in `postcss.config.cjs` (mixins, `rem()`, `light-dark()` and the breakpoint
variables in your own CSS modules), and optionally Sass with the `_mantine.scss` helpers through
`additionalData`. Importing a Mantine Vite app with `nite init --from` carries all of that over,
including the `additionalData` from `vite.config`.

```bash
nite add @mantine/core @mantine/hooks
nite add -D postcss postcss-preset-mantine postcss-simple-vars
```

## Importing an existing project

```bash
nite init my-app --from ../vite-app --dry-run   # see what would happen
nite init my-app --from ../vite-app             # do it (the source is never modified)
```

Works with **Vite** (React) and **Create React App** projects managed by npm, Yarn (classic or
Berry), pnpm or Bun. NiteFx reads the project and:

- Copies `index.html` and `src/` into `Client/`, `public/` into `wwwroot/`, tsconfig and
  PostCSS/Tailwind config files into `Client/`, and `.env*` files into the project.
- Keeps **the exact versions the project runs today**: it reads `package-lock.json`, `yarn.lock`,
  `pnpm-lock.yaml` or `bun.lock` and prefers those versions while resolving (the binary
  `bun.lockb` is not read; run `bun install --save-text-lockfile` first).
- Imports runtime `dependencies`, plus any devDependency that `src/` actually imports (bundlers
  never cared which list a library was in), plus `@types/*`, `typescript` and the stylesheet tools
  (PostCSS and its plugins, Sass, Less, Tailwind and its plugins). Build, lint and test
  tooling (Vite, Babel, ESLint, oxlint, Jest, Vitest, testing libraries…) is skipped, as are
  `file:`, `workspace:`, git and URL dependencies. Imports from test files do not count.
- Copies `css.preprocessorOptions.*.additionalData` from vite.config into `nite.json` (including
  the path-building template literal in Mantine's guide); anything it cannot evaluate is reported.
- Sets `envPrefix` (`VITE_` or `REACT_APP_`, or `envPrefix` from vite.config), turns on `jsxInJs`
  for Create React App, rewrites `public/index.html`'s `%PUBLIC_URL%` and adds its entry script,
  and swaps the `vite/client` / `react-scripts` types for NiteFx's (`nite-env.d.ts`).
- Reports what it could not bring along: Vite plugins other than React's and Tailwind's, Stylus,
  Tailwind 3, `import.meta.glob`, `?raw`/`?url`/`?worker` imports, SVGs as React components (svgr),
  `overrides`/`resolutions`, and package.json scripts. The dev `proxy` is not needed: the app and
  its API share an origin.

Monorepos (`workspaces`) are refused; import one app at a time with `--from <repo>/packages/<app>`.
Next.js, Remix and Astro apps have their own servers and are out of scope.

## Full-stack dev with `nite watch`

`nite watch` runs `dotnet watch`: it handles the C# side and NiteFx handles the client side.

| You edit | What happens |
|---|---|
| A React component | Fast Refresh: updated in place, state kept (about 100 to 200 ms). |
| A helper module (`format.ts`) | The components that use it refresh; state kept. |
| CSS, Sass, Less | Swapped in place; so is every stylesheet that imports the file. |
| A CSS module | The components using it refresh; state kept. |
| A component that uses a new Tailwind class (or a Razor view) | The Tailwind stylesheet is rebuilt and swapped in. |
| The entry (`main.tsx`) or `index.html` | Page reloads. |
| C# that hot reload can apply | Applied in the running app; the browser gets a `nite:server-update` event. |
| C# that needs a restart (new endpoint in `Program.cs`, say) | dotnet watch restarts the app without asking; the page waits for it and reloads (about 10 s). |

Plain `dotnet watch` injects its own browser script, which reloads the page after **every** C#
change. That suits Razor pages, but in a single-page app it throws away client state for an
API-only change. `nite watch` turns it off (`DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1`, which only
works from the environment, not `launchSettings.json`) and NiteFx takes over, reloading after
restarts by itself. For Razor pages, keep dotnet's refresh with `nite watch --browser-refresh`.

It also sets `DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1`. Without it, an interactive dotnet watch handles
some edits that cannot take effect as warnings rather than restarts: add an endpoint to the
top-level code in `Program.cs` and it reports ENC0118 ("might not have any effect until the
application is restarted"), applies an update that changes nothing (that code already ran), and
waits for Ctrl+R, while the page calls an endpoint that does not exist yet. Edits hot reload can
apply are still applied in place. `nite watch --no-auto-restart` keeps dotnet watch's prompt, and
a value you set for either variable yourself is left alone.
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

## Log messages as Markdown (`Nite.Diagnostics`)

A separate package, usable in any .NET project. Write each log message as a Markdown file in
`Diagnostics/`, with its code, event ID, level and message template in the front matter. A source
generator turns it into an `ILogger` extension method backed by `LoggerMessage.Define`, and the
rest of the file documents the message:

```csharp
logger.AgentDisconnected(agentId, 42);   // from Diagnostics/AGENT007.md
```

NiteFx's own warnings are written this way (`src/Nite/Diagnostics`), and the same files are its
[log message reference](https://nitefx.dev/docs/reference/diagnostics/). See
[src/Nite.Diagnostics](src/Nite.Diagnostics/README.md).

## Telemetry and health checks

NiteFx reports what it does as OpenTelemetry traces and metrics, through .NET's own
`ActivitySource` and `Meter` (both named `Nite`), so the library takes no OpenTelemetry dependency
and costs nothing until something listens. Add the names to your OpenTelemetry setup, for
example in the web project of an [Aspire](https://aspire.dev) app, next to `AddServiceDefaults()`:

```csharp
// using Nite.Observability; using OpenTelemetry.Metrics; using OpenTelemetry.Trace;
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(NiteTelemetry.SourceName));
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(NiteTelemetry.MeterName));
```

| Span | When |
|---|---|
| `nite.dev.startup` | The dev server starting: install, initial compiles and pre-bundling, as one trace |
| `nite.dev.compile`, `nite.dev.stylesheet` | A module or stylesheet compiled on request (a child of the request); cache hits make no span |
| `nite.css.sass`, `.less`, `.tailwind`, `.postcss` | Each stylesheet tool, under the stylesheet it ran for |
| `nite.dev.hot_update` | A batch of file changes and what the browser got (`nite.update.kind`: update, css, reload, error) |
| `nite.dev.prebundle` | Pre-bundling dependencies |
| `nite.packages.install`, `.resolve`, `.materialize` | Installing packages; resolve only runs when the lockfile changes |
| `nite.tool.download` | Downloading the esbuild or Dart Sass binary (first run only) |
| `nite.build` | A production build |

Metrics: `nite.dev.compile.duration` and `nite.dev.compiles` (by `nite.kind` and `nite.cache`),
`nite.css.tool.duration`, `nite.dev.hot_updates`, `nite.dev.prebundle.duration`,
`nite.packages.install.duration`, `nite.packages.tarballs` (cache hits and misses) and
`nite.build.duration`. Failed spans carry the error status and an `exception` event.

Requests NiteFx serves under `/_nite` are named after their route (`GET /_nite/src/{**path}`), as
routing names endpoints. [`samples/Nite.Aspire`](samples/Nite.Aspire) shows all of this in the Aspire
dashboard.

A health check reports whether NiteFx can serve the app:

```csharp
builder.Services.AddHealthChecks().AddNite();
```

In development it is Degraded while packages install or dependencies pre-bundle and Unhealthy if
that failed (a typo in `nite.json`) or `UseNite()` never ran; in production it is Unhealthy if
`wwwroot/_nite` is missing, which usually means the publish did not run `nite build`. It is a
readiness check (untagged), so Aspire's `/health` includes it and `/alive` does not.

## CLI

| Command | Aliases | What it does |
|---|---|---|
| `nite init [dir]` | | Scaffold a project: `--template spa` (default) or `islands`, `--name`, `--nite-source <repo>`, `--no-install`. `--from <project>` imports an existing Vite or Create React App project instead (`--dry-run` to preview). |
| `nite install [pkg...]` | `add`, `i` | No arguments: install everything in `nite.json`. With packages (`react`, `react@^19`, `@types/node@latest`): add them, then install. `-D` for devDependencies, `-E` to pin exactly. |
| `nite remove <pkg...>` | `rm`, `delete`, `uninstall` | Remove from `nite.json` and `node_modules`. |
| `nite list` | `ls` | Direct packages with their ranges and installed versions; `--all` adds transitive ones. |
| `nite update [pkg...]` | `up` | Show current, wanted and latest versions, then update within the ranges. `--check` only reports; `--latest` moves the ranges to the latest versions. |
| `nite build` | | Production build into `wwwroot/_nite` (publish runs this). |
| `nite watch` | | `dotnet watch` with NiteFx handling the browser and restarts automatic; `--browser-refresh`, `--no-auto-restart`. See above. |
| `nite config list` | `config ls` | Effective settings; `--show-origin` shows which file or variable each comes from. |
| `nite config get\|set\|unset <key>` | | Read or change the user config (`registry`, `@scope:registry`, `init.template`). `config path` prints its location. |
| `nite cache dir\|clean\|verify` | | Where the cache is; delete it; check cached tarballs and drop corrupt ones. |
| `nite version` | `--version` | The NiteFx version (and esbuild and .NET versions). |
| `nite update-self` | | `dotnet tool update Nite.Cli`, for however NiteFx is installed: local tool manifest, global, or `--tool-path`. |

Project commands work on the current directory; `-C <dir>` points them elsewhere, and
`--verbose` shows debug output. Installs keep the versions already in `nite.lock.json` unless a
range forces a change, so adding one package never upgrades the others; that's what
`nite update` is for.

## Configuration

NiteFx keeps per-user files where the [XDG Base Directory spec](https://specifications.freedesktop.org/basedir-spec/latest/)
says they go:

| | Linux and macOS | Windows | Override |
|---|---|---|---|
| Config (`config.json`) | `~/.config/nite` | `%APPDATA%\nite` | `NITE_CONFIG_DIR`, `XDG_CONFIG_HOME` |
| Cache (tarballs, registry metadata, esbuild, Sass) | `~/.cache/nite` | `%LOCALAPPDATA%\nite\cache` | `NITE_CACHE_DIR`, `XDG_CACHE_HOME` |

`XDG_*` variables apply on every OS when set. The cache is always safe to delete
(`nite cache clean`), which also removes the cache older NiteFx versions left in `~/.local/share/nite`.

Settings come from these places, highest first:

1. `NITE_REGISTRY`
2. `registry` in the project's `nite.json`
3. The project's `.npmrc`
4. The user config, `~/.config/nite/config.json` (edit it with `nite config set`)
5. The user's `~/.npmrc` (or `$NPM_CONFIG_USERCONFIG`)
6. Defaults (the public npm registry)

**Private registries** work from the `.npmrc` you probably already have. Scopes map to registries,
and credentials use npm's `//host/path/:` keys, with `${VAR}` expansion so tokens can stay in the
environment:

```ini
@acme:registry=https://npm.acme.dev/
//npm.acme.dev/:_authToken=${ACME_NPM_TOKEN}
```

A credential is only sent to URLs under its host and path, never to other hosts (a tarball on a
CDN, say). `_authToken`, `_auth`, and `username` with `_password` are supported. NiteFx refuses to
store credentials in its own config; they stay in `.npmrc`.

Registry metadata is cached with its ETag and revalidated, so resolving an unchanged package
costs a 304 instead of a download.

## Limits and non-goals

NiteFx is intentionally smaller than Vite. Things it does not do (yet, or ever):

- **No Vite/Rollup plugins.** Vue SFCs, Svelte, MDX and friends need Vite's plugin pipeline. If
  you need them, use Vite; it's the right tool. (PostCSS plugins do work; see Stylesheets.)
- **PostCSS plugins and Tailwind plugins must be plain JavaScript.** Ones that load native
  addons (`.node` files) cannot run in the interpreter.
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
  are skipped and install scripts never run, so packages that need a postinstall step won't work
  (NiteFx warns about them; see [Install scripts](#install-scripts)).
- **Not yet:** Stylus, `import.meta.glob`, Vite's `?raw`/`?url`/`?worker` imports, CSS modules'
  `localsConvention`, and CSS that a package's own JavaScript imports (import its stylesheet from
  your code instead, as Mantine does). The importer warns about each of these when a project uses
  them. Relative `url()`s inside Sass partials are not rebased (Vite does rebase them).
- Production asset URLs assume the app is served from the site root (`/_nite/...`), except in
  HTML served by `MapNiteSpa`, which adds the request's path base. Hashed bundles are revalidated
  with ETags rather than cached as immutable.

## Development

```bash
dotnet build
dotnet test                                          # includes esbuild and stylesheet integration tests (needs network)
dotnet test --filter "Category!=Integration"         # offline
```

The templates live in `src/Nite.Templates/content` as ordinary files: `dotnet new` reads them
through `.template.config/template.json`, and the CLI embeds them for `nite init`. Placeholders
are `NiteApp` (project file name), `NITE_TITLE`, `NITE_NAMESPACE`, `NITE_PORT` and
`NITE_VERSION`; `TemplateTests` checks that every placeholder a file uses has a symbol in
template.json. To try the pack: `dotnet pack src/Nite.Templates -o /tmp/nite-templates` and
`dotnet new install /tmp/nite-templates/Nite.Templates.<version>.nupkg`.

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) builds, tests and packs `Nite`,
`Nite.Cli` and `Nite.Templates` on every push and pull request. The release tag is the version:
to release 0.6.0, push `v0.6.0` (`git tag v0.6.0 && git push origin v0.6.0`) and the workflow
packs 0.6.0 and pushes it to nuget.org. Other builds are prereleases of the next patch after the
newest tag (`0.6.1-ci.<run>`), kept as a workflow artifact. Local builds are `0.0.0-dev`; to try
one in a project, use `nite init --nite-source`. Publishing uses
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): no stored
API key, just a nuget.org policy for this repository's `ci.yml` and `nuget` environment, and a
`NUGET_USER` secret with the publishing account's profile name.

Environment variables: see [Configuration](#configuration) for the directory and registry
variables; `NITE_ESBUILD_PATH` points to an esbuild binary you supply (it must be version 0.28.2),
and `NITE_SASS_PATH` to a `dart-sass` folder (from Dart Sass's standalone release) to use instead
of downloading one.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Only binaries built from this repository by its release workflow are signed.

- Committers and reviewers: [Tim McQueen](https://github.com/tmcqueen)
- Approvers: [Tim McQueen](https://github.com/tmcqueen)

Read the full [Code signing policy](https://nitefx.dev/docs/code-signing.html) and the
[Privacy policy](https://nitefx.dev/docs/privacy.html). NiteFx collects no information,
and it transfers data to other networked systems only when you ask it to, such as installing
packages from the npm registry.

## License

[Apache License 2.0](LICENSE).
