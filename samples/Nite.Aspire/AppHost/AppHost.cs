var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Nite_Aspire_Api>("api")
    .WithHttpHealthCheck("/health");

// The Nite app is an ordinary project resource: the dev server runs inside it, so there is no
// separate frontend process, port or proxy.
var web = builder.AddProject<Projects.Nite_Aspire_Web>("web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(api)
    .WaitFor(api);

// Optional: Application Insights for both services and the browser. Store a connection string with
//   dotnet user-secrets set ConnectionStrings:appinsights "InstrumentationKey=...;IngestionEndpoint=..." --project AppHost
// and both projects get it as APPLICATIONINSIGHTS_CONNECTION_STRING. The web app hands it to the
// browser through Nite's client config, so one build works with any connection string.
if (!string.IsNullOrEmpty(builder.Configuration["ConnectionStrings:appinsights"]))
{
    var insights = builder.AddConnectionString("appinsights", "APPLICATIONINSIGHTS_CONNECTION_STRING");
    api.WithReference(insights);
    web.WithReference(insights);
}

builder.Build().Run();
