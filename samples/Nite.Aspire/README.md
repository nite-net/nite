# NiteFx under Aspire

An [Aspire](https://aspire.dev) app with two services:

- **web**: a React single-page app compiled by NiteFx, plus `/api/quote`, which calls the API. The
  NiteFx dev server runs inside this project, so there is no separate frontend process, port or proxy.
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

The page also shows **client config**: values `NiteOptions.ClientConfig` in `Web/Program.cs`
works out for each request (environment, machine, the request's trace id), read in the browser with
`import { config } from "nite:config"`.

## Application Insights

Optional. Give the AppHost a connection string and both services and the browser send telemetry to
it:

```bash
dotnet user-secrets set ConnectionStrings:appinsights "InstrumentationKey=...;IngestionEndpoint=..." --project AppHost
```

The AppHost passes it to both projects as `APPLICATIONINSIGHTS_CONNECTION_STRING`; ServiceDefaults
turns on the Azure Monitor exporter when it is set, and the web app hands it to the browser through
client config, so the same client build works with any connection string. `Client/telemetry.ts`
loads the SDK as a separate chunk after the page renders, and joins the trace of the request that
served the page: a click on "Another one" shows in Application Insights as one transaction, from the
page view through `web` to `api`.

The Nite-specific wiring is a few lines in `Web/Program.cs`; `ServiceDefaults` is the stock Aspire
template, because the API does not use NiteFx. See
[Telemetry, health checks and Aspire](https://nitefx.dev/docs/guides/observability.html).
