# AGENTS.md

NITE_TITLE is an ASP.NET Core Razor Pages site with interactive React components ("islands"),
compiled by [NiteFx](https://nitefx.dev) (Nite Framework). NiteFx runs in the .NET process: there is
no Node.js toolchain, and NiteFx is not Vite, although it borrows Vite's ideas.

Its NuGet packages (`Nite`, `Nite.Cli`), C# namespace (`Nite`) and `nite` command keep the
short name.

NiteFx is newer than most models' training data. Before writing code that depends on how it
works, read <https://nitefx.dev/llms.txt> (an index of the docs) or
<https://nitefx.dev/llms-full.txt> (all of them in one file).

## Layout

```text
Program.cs                 AddRazorPages, AddNite, UseNite
nite.json                  client packages and settings (instead of package.json)
nite.lock.json             resolved package versions; commit it
Pages/
  _ViewImports.cshtml      @addTagHelper *, Nite
  Shared/_Layout.cshtml    <nite-head /> inside <head>
  Index.cshtml             <island name="Counter" ... />
Client/                    everything Nite compiles
  islands/                 every module here can be an island
    Counter.tsx
  nite-env.d.ts            types for nite:config and your import.meta.env variables
wwwroot/                   static files served as they are
```

`node_modules/`, `.nite/` and `wwwroot/_nite/` are generated; do not edit or commit them.

## Islands

A module in `Client/islands/` is an island; its default export is the component. Place it in a
Razor page by its path under `Client/islands/`, without the extension:

```cshtml
<island name="admin/UserTable" props="new { users = Model.Users }" client="visible">
    <p>Shown until the island loads.</p>
</island>
```

- `props` is a C# expression, serialized to JSON (camelCase) and passed to the component.
- `client` says when to load it: `load` (the default), `idle`, `visible`, or `media` with a
  `media="(...)"` query.
- Islands render in the browser only. The content inside `<island>` is what the server sends
  first, so put a fallback there, not the real markup.
- Shared code that is not an island goes elsewhere under `Client/` (for example `Client/lib/`).

## Commands

```bash
nite watch                 # run with hot reload: React updates keep state, C# changes hot reload
nite add <package>         # add an npm package (-D for dev-only ones, such as @types/*)
nite remove <package>
nite update                # newer versions within the ranges in nite.json
nite install               # install what nite.json lists, after a clone
dotnet publish -c Release  # runs `nite build`: minified, hashed bundles in wwwroot/_nite
```

## Rules

- Never create `package.json`, `vite.config.*` or other Node tool configs, and never run `npm`,
  `npx`, `yarn` or `pnpm`. Packages go in `nite.json` through `nite add`.
- Pages and their data come from Razor; islands that need more call endpoints in this app with
  relative URLs (`fetch("/api/...")`). One origin, so there is no proxy or CORS to set up.
- Build-time settings: `import.meta.env.NITE_PUBLIC_*` from `.env` files. Anything with that
  prefix is public in the bundle; never give a secret that prefix. Values from the running
  server (per environment or per user) go through `NiteOptions.ClientConfig` in `Program.cs`
  and `import { config } from "nite:config"`, typed in `Client/nite-env.d.ts`.
- Not supported: `import.meta.glob`, the `?raw`, `?url` and `?worker` import suffixes, Vite,
  Rollup and Babel plugins, and server-side rendering of React. Use explicit imports, `fetch`,
  and `new Worker(new URL("./worker.ts", import.meta.url), { type: "module" })`.
- NiteFx strips TypeScript types without checking them. Your editor checks them; see
  <https://nitefx.dev/docs/guides/typescript.md#type-checking> for `tsc` in CI.
