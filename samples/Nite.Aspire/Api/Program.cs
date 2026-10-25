var builder = WebApplication.CreateBuilder(args);

// Service discovery, resilience, health checks and OpenTelemetry, shared with the web app.
builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

Quote[] quotes =
[
    new("Simplicity is prerequisite for reliability.", "Edsger W. Dijkstra"),
    new("Make it work, make it right, make it fast.", "Kent Beck"),
    new("The cheapest, fastest and most reliable components are those that aren't there.", "Gordon Bell"),
    new("Programs must be written for people to read, and only incidentally for machines to execute.", "Harold Abelson"),
    new("Deleted code is debugged code.", "Jeff Sickel"),
];

app.MapGet("/quotes/random", () => quotes[Random.Shared.Next(quotes.Length)] with { Server = Environment.MachineName });

app.Run();

record Quote(string Text, string Author, string Server = "");
