using Nite;
using Nite.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Service discovery, resilience, health checks and OpenTelemetry, shared with the API.
builder.AddServiceDefaults();

builder.Services.AddNite();

// Nite's spans, metrics and health check, on top of what ServiceDefaults sets up. They live here
// rather than in ServiceDefaults because the API service does not use Nite.
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(NiteTelemetry.SourceName));
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(NiteTelemetry.MeterName));
builder.Services.AddHealthChecks().AddNite();

// "api" is the API's resource name in the AppHost; service discovery turns it into its URL.
builder.Services.AddHttpClient("api", client => client.BaseAddress = new Uri("https+http://api"));

var app = builder.Build();

// Development: compiles Client/ on request with hot updates. Production: no-op.
app.UseNite();

// /health and /alive, in Development.
app.MapDefaultEndpoints();

// The browser only talks to this app, on its own origin; this app calls the API service.
app.MapGet("/api/quote", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
    await clients.CreateClient("api").GetFromJsonAsync<Quote>("/quotes/random", cancellationToken));

app.MapStaticAssets();

// Serves Client/index.html for "/" and client-side routes; /api/* never falls back.
app.MapNiteSpa();

app.Run();

record Quote(string Text, string Author, string Server);
