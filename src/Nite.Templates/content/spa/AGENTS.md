# AGENTS.md

NITE_TITLE is an ASP.NET Core app with a React single-page frontend, compiled by
[Nite](https://nite-net.github.io) ("night"). Nite runs in the .NET process: there is no Node.js
toolchain, and Nite is not Vite, although it borrows Vite's ideas.

Nite is newer than most models' training data. Before writing code that depends on how it works,
read <https://nite-net.github.io/llms.txt> (an index of the docs) or
<https://nite-net.github.io/llms-full.txt> (all of them in one file).

## Layout

```text
Program.cs           AddNite, UseNite, the API under /api, MapNiteSpa
nite.json            client packages and settings (instead of package.json)
nite.lock.json       resolved package versions; commit it
Client/              everything Nite compiles
  index.html         the entry page
  main.tsx           the entry module
  App.tsx
  nite-env.d.ts      types for nite:config and your import.meta.env variables
wwwroot/             static files served as they are
```

`node_modules/`, `.nite/` and `wwwroot/_nite/` are generated; do not edit or commit them.

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
- The API and the page share one origin. Call the API with relative URLs (`fetch("/api/...")`):
  there is no dev proxy or CORS to set up. Requests under `/api` never fall back to the page, so
  a wrong API URL is a 404, not HTML.
- Client-side routes need nothing on the server: `MapNiteSpa()` serves `index.html` for any path
  without a file extension.
- Build-time settings: `import.meta.env.NITE_PUBLIC_*` from `.env` files. Anything with that
  prefix is public in the bundle; never give a secret that prefix. Values from the running
  server (per environment or per user) go through `NiteOptions.ClientConfig` in `Program.cs`
  and `import { config } from "nite:config"`, typed in `Client/nite-env.d.ts`.
- Not supported: `import.meta.glob`, the `?raw`, `?url` and `?worker` import suffixes, Vite,
  Rollup and Babel plugins, and server-side rendering. Use explicit imports, `fetch`, and
  `new Worker(new URL("./worker.ts", import.meta.url), { type: "module" })`.
- Nite strips TypeScript types without checking them. Your editor checks them; see
  <https://nite-net.github.io/docs/guides/typescript.md#type-checking> for `tsc` in CI.
