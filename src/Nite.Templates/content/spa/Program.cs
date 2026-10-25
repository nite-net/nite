using Nite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNite();

var app = builder.Build();

// Development: compiles Client/ on request with hot updates. Production: no-op.
app.UseNite();

app.MapGet("/api/hello", () => new { message = "Hello from ASP.NET Core", time = DateTimeOffset.Now });

app.MapStaticAssets();

// Serves Client/index.html for "/" and client-side routes; /api/* never falls back.
app.MapNiteSpa();

app.Run();
