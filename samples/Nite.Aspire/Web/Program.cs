using System.Diagnostics;
using Nite;
using Nite.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Service discovery, resilience, health checks and OpenTelemetry, shared with the API.
builder.AddServiceDefaults();

builder.Services.AddNite(options => options.ClientConfig = context => new
{
    // Set by the AppHost when Application Insights is configured. Read when each page is served,
    // not when the client was built, so one build runs anywhere.
    AppInsights = context.RequestServices.GetRequiredService<IConfiguration>()["APPLICATIONINSIGHTS_CONNECTION_STRING"],

    // Context about this request, as the server saw it. The trace id lets the browser's telemetry
    // join the trace of the request that served the page.
    Server = new
    {
        Environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName,
        Machine = Environment.MachineName,
        TraceId = Activity.Current?.TraceId.ToString(),
        RenderedAt = DateTimeOffset.UtcNow,
    },
});

// Nite's spans, metrics and health check, on top of what ServiceDefaults sets up. They live here
// rather than in ServiceDefaults because the API service does not use Nite.
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(NiteTelemetry.SourceName));
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(NiteTelemetry.MeterName));
builder.Services.AddHealthChecks().AddNite();

// "api" is the API's resource name in the AppHost; service discovery turns it into its URL.
builder.Services.AddHttpClient("api", client => client.BaseAddress = new Uri("https+http://api"));

var app = builder.Build();

// Development: compiles Client/ on request with hot updates. Production: caches hashed bundles for good.
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
