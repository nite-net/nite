# Nite.Diagnostics

Write each log message as a Markdown file. A source generator turns it into a fast, structured
`ILogger` extension method, and the same file is the documentation for that message.

Part of [NiteFx](https://nitefx.dev), but it has nothing to do with the frontend: it works in any
.NET project that logs with `Microsoft.Extensions.Logging`.

```bash
dotnet add package Nite.Diagnostics
```

## A log message

`Diagnostics/AGENT007.md`:

```markdown
---
code: AGENT007
eventId: 7007
level: Warning
message: Agent {AgentId} disconnected after {Seconds} seconds
parameters:
  AgentId: Guid
  Seconds: int
---
# Agent disconnected

The agent stopped sending heartbeats. Usually the host went to sleep or lost its network...
```

Becomes:

```csharp
logger.AgentDisconnected(agentId, 42);
// warn: MyApp[7007] Agent 6f1c… disconnected after 42 seconds
```

The methods are in a class named `Log` in your project's root namespace, and the package imports
that namespace in every file as a global using, so they work everywhere, top-level statements in
`Program.cs` included, whether or not `ImplicitUsings` is on. Set `NiteDiagnosticsGlobalUsing` to
`false` to write the `using` yourself.

- The method is named after the `# Title`, or `method:` if you set one.
- Each `{Placeholder}` becomes a parameter, logged as a structured property. `parameters:` gives
  their types; without it they are `object?`. If you list any, list them all.
- `exception: true` adds an `Exception?` parameter, first after the logger.
- The event's ID is `eventId`, and its name is `code`, so log tools can filter on either.
- Methods use `LoggerMessage.Define`: the template is parsed once, and nothing is formatted or
  allocated when the level is off. Messages with more than six placeholders use `ILogger.Log`.

Everything after the front matter is yours: explain the cause and the fix. Publish the folder with
your docs site, and set `NiteDiagnosticsHelpUrl` to link each method's IntelliSense to its page.

## Settings

Set these in your project file:

| Property | Default | |
|---|---|---|
| `NiteDiagnosticsDirectory` | `Diagnostics` | Where the Markdown files are, under the project. |
| `NiteDiagnosticsNamespace` | `$(RootNamespace)` | Namespace of the generated class. |
| `NiteDiagnosticsClass` | `Log` | Name of the generated class. |
| `NiteDiagnosticsPublic` | `false` | `true` makes the class public instead of internal. |
| `NiteDiagnosticsGlobalUsing` | `true` | Import the class's namespace in every file. `false` leaves that to you. |
| `NiteDiagnosticsHelpUrl` | none | A URL with `{code}` in it, linked from each method's docs. |

## Errors at build time

Problems in a file are compiler errors pointing at that file and line:
- missing or malformed front matter, or a missing `code`, `eventId`, `level` or `message`
- an unknown level, an event ID that is not a number, or a title that is not a valid method name
- a placeholder used twice, a `parameters:` entry that is not in the message, or the reverse
- two files with the same code, event ID or method name

An unknown front matter key is a warning, to catch typos.
