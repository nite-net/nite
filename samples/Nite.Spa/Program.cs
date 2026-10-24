using System.Collections.Concurrent;
using System.Diagnostics;
using Nite;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNite();
builder.Services.AddSingleton<TodoStore>();

var app = builder.Build();

// Development: compiles Client/ on request and pushes hot updates. Production: no-op.
app.UseNite();

var api = app.MapGroup("/api");

api.MapGet("/todos", (TodoStore store) => store.All());

api.MapGet("/todos/{id:int}", (int id, TodoStore store) =>
    store.Find(id) is { } todo ? Results.Ok(todo) : Results.NotFound());

api.MapPost("/todos", (NewTodo input, TodoStore store) =>
    string.IsNullOrWhiteSpace(input.Title)
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title is required."] })
        : Results.Created($"/api/todos", store.Add(input.Title.Trim())));

api.MapPatch("/todos/{id:int}", (int id, TodoPatch patch, TodoStore store) =>
    store.Update(id, patch.Done) is { } todo ? Results.Ok(todo) : Results.NotFound());

api.MapGet("/stats", (TodoStore store) => new Stats(
    ServerTime: DateTimeOffset.Now,
    Uptime: (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
    Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Total: store.All().Count,
    Done: store.All().Count(t => t.Done)));

app.MapStaticAssets();

// Serves Client/index.html for "/" and for client-side routes like /todos/3 or /stats.
// Unknown /api/* paths still 404 instead of returning the page.
app.MapNiteSpa();

app.Run();

record Todo(int Id, string Title, bool Done, DateTimeOffset Created);
record NewTodo(string Title);
record TodoPatch(bool Done);
record Stats(DateTimeOffset ServerTime, string Uptime, string Runtime, int Total, int Done);

class TodoStore
{
    private readonly ConcurrentDictionary<int, Todo> _todos = new();
    private int _nextId;

    public TodoStore()
    {
        Add("Write the API in C#");
        Add("Write the UI in React");
        Add("Run them as one app");
        Update(1, true);
    }

    public IReadOnlyList<Todo> All() => _todos.Values.OrderBy(t => t.Id).ToList();

    public Todo? Find(int id) => _todos.GetValueOrDefault(id);

    public Todo Add(string title)
    {
        var todo = new Todo(Interlocked.Increment(ref _nextId), title, false, DateTimeOffset.Now);
        _todos[todo.Id] = todo;
        return todo;
    }

    public Todo? Update(int id, bool done) =>
        _todos.TryGetValue(id, out var todo) ? _todos[id] = todo with { Done = done } : null;
}
