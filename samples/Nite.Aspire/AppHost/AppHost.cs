var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Nite_Aspire_Api>("api")
    .WithHttpHealthCheck("/health");

// The Nite app is an ordinary project resource: the dev server runs inside it, so there is no
// separate frontend process, port or proxy.
builder.AddProject<Projects.Nite_Aspire_Web>("web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
