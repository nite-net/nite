# Nite under Aspire

An [Aspire](https://aspire.dev) app with two services:

- **web**: a React single-page app compiled by Nite, plus `/api/quote`, which calls the API. The
  Nite dev server runs inside this project, so there is no separate frontend process, port or proxy.
- **api**: a minimal API that hands out quotes. `web` finds it through service discovery
  (`https+http://api`).

```bash
dotnet run --project AppHost --launch-profile http   # or the default https profile, with a dev certificate
```

Open the dashboard link the AppHost prints, then the `web` URL. In the dashboard:

- **Traces**: `nite.dev.startup` is the dev server starting up (package install, compiles,
  pre-bundling); `GET /api/quote` spans `web` and `api`; saving `Client/App.tsx` adds a
  `nite.dev.hot_update` trace with the recompile inside it.
- **Metrics**: the `Nite` meter (compile times, cache hits, hot updates, installs).
- **Resources**: `web` turns unhealthy if the dev server fails to start, for example after a typo in
  `nite.json`.

The Nite-specific wiring is four lines in `Web/Program.cs`; `ServiceDefaults` is the stock Aspire
template, because the API does not use Nite. See
[Telemetry, health checks and Aspire](https://nite-net.github.io/docs/guides/observability.html).
