using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Nite.Build;
using Nite.Diagnostics;
using Nite.Esbuild;

namespace Nite.Tests;

public class DiagnosticsGeneratorTests
{
    private const string AgentDisconnected = """
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

        The agent stopped sending heartbeats.
        """;

    [Fact]
    public void Generates_a_structured_logging_method_named_after_the_title()
    {
        var result = Run(("Diagnostics/AGENT007.md", AgentDisconnected));

        Assert.Empty(result.Diagnostics);
        var logger = new RecordingLogger();
        var id = Guid.NewGuid();
        Invoke(result, "AgentDisconnected", logger, id, 42);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(7007, entry.EventId.Id);
        Assert.Equal("AGENT007", entry.EventId.Name);
        Assert.Equal($"Agent {id} disconnected after 42 seconds", entry.Message);
        Assert.Equal(id, entry.State["AgentId"]);
        Assert.Equal(42, entry.State["Seconds"]);
        Assert.Equal("Agent {AgentId} disconnected after {Seconds} seconds", entry.State["{OriginalFormat}"]);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void Does_nothing_when_the_level_is_off()
    {
        var result = Run(("Diagnostics/AGENT007.md", AgentDisconnected));
        var logger = new RecordingLogger { MinimumLevel = LogLevel.Error };

        Invoke(result, "AgentDisconnected", logger, Guid.NewGuid(), 1);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Untyped_placeholders_are_objects_and_method_overrides_the_title()
    {
        var result = Run(("Diagnostics/A.md", """
            ---
            code: APP1
            eventId: 1
            level: information
            method: Started
            message: "Started {Name}: {Count} items"
            ---
            # This title is not used for the name
            """));

        Assert.Empty(result.Diagnostics);
        var method = Method(result, "Started");
        Assert.Equal([typeof(ILogger), typeof(object), typeof(object)], method.GetParameters().Select(p => p.ParameterType));
        var logger = new RecordingLogger();
        method.Invoke(null, [logger, "app", 3]);
        Assert.Equal("Started app: 3 items", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void Exception_true_adds_an_exception_parameter_after_the_logger()
    {
        var result = Run(("Diagnostics/F.md", """
            ---
            code: APP2
            eventId: 2
            level: Error
            message: Saving {File} failed
            exception: true
            parameters:
              File: string
            ---
            # Save failed
            """));

        var logger = new RecordingLogger();
        var error = new IOException("disk full");
        Invoke(result, "SaveFailed", logger, error, "a.txt");

        var entry = Assert.Single(logger.Entries);
        Assert.Same(error, entry.Exception);
        Assert.Equal("Saving a.txt failed", entry.Message);
    }

    [Fact]
    public void A_message_without_placeholders_has_no_parameters()
    {
        var result = Run(("Diagnostics/R.md", "---\ncode: APP3\neventId: 3\nlevel: Debug\nmessage: Ready\n---\n# Ready\n"));

        var logger = new RecordingLogger();
        Invoke(result, "Ready", logger);
        Assert.Equal("Ready", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void More_than_six_placeholders_still_log_with_every_property()
    {
        var result = Run(("Diagnostics/M.md", """
            ---
            code: APP4
            eventId: 4
            level: Information
            message: "{A} {B} {C} {D} {E} {F} {G}"
            ---
            # Many
            """));

        Assert.Empty(result.Diagnostics);
        var logger = new RecordingLogger();
        Invoke(result, "Many", logger, 1, 2, 3, 4, 5, 6, 7);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("1 2 3 4 5 6 7", entry.Message);
        Assert.Equal(7, entry.State["G"]);
        Assert.Equal(new EventId(4, "APP4"), entry.EventId);
    }

    [Fact]
    public void Uses_the_configured_namespace_class_and_visibility_and_links_the_help_url()
    {
        var result = Run([("Diagnostics/AGENT007.md", AgentDisconnected)], new()
        {
            ["build_property.NiteDiagnosticsNamespace"] = "My.Logs",
            ["build_property.NiteDiagnosticsClass"] = "AgentLog",
            ["build_property.NiteDiagnosticsPublic"] = "false",
            ["build_property.NiteDiagnosticsHelpUrl"] = "https://example.com/logs/{code}.html",
        });

        Assert.Empty(result.Diagnostics);
        var type = result.Assembly.GetType("My.Logs.AgentLog")!;
        Assert.False(type.IsPublic);
        Assert.Contains("<see href=\"https://example.com/logs/AGENT007.html\">", result.Source);
        Assert.Contains("// From AGENT007.md", result.Source);
    }

    [Fact]
    public void Generates_the_class_even_when_every_file_has_errors()
    {
        // The build imports the class's namespace whenever there are message files, so the
        // namespace must exist even before any file is valid.
        var result = Run(("Diagnostics/bad.md", "---\ncode: X\neventId: 1\nlevel: Loud\nmessage: m\n---\n# T\n"));

        Assert.Equal("NDIAG004", Assert.Single(result.Diagnostics).Id);
        Assert.NotNull(result.Assembly.GetType("Test.Log"));
    }

    [Fact]
    public void Ignores_additional_files_without_the_metadata()
    {
        var result = Run([("README.md", AgentDisconnected)], [], marked: false);

        Assert.Empty(result.Diagnostics);
        Assert.Null(result.Source);
    }

    [Theory]
    [InlineData("no front matter here", "NDIAG001", 0)]
    [InlineData("---\ncode: X\n", "NDIAG001", 0)]
    [InlineData("---\ncode X\neventId: 1\nlevel: Debug\nmessage: m\n---\n# T", "NDIAG002", 1)]
    [InlineData("---\ncode: X\nlevel: Debug\nmessage: m\n---\n# T", "NDIAG003", 0)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Loud\nmessage: m\n---\n# T", "NDIAG004", 3)]
    [InlineData("---\ncode: X\neventId: one\nlevel: Debug\nmessage: m\n---\n# T", "NDIAG004", 2)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m {Oops\n---\n# T", "NDIAG004", 4)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m\n---\nNo heading", "NDIAG005", 6)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m\nmethod: class\n---\n# T", "NDIAG006", 5)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: \"{A} {A}\"\n---\n# T", "NDIAG007", 4)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: \"{A}\"\nparameters:\n  A: int\n  B: int\n---\n# T", "NDIAG008", 7)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: \"{A} {B}\"\nparameters:\n  A: int\n---\n# T", "NDIAG009", 4)]
    [InlineData("---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m\nlevle: Debug\n---\n# T", "NDIAG013", 5)]
    public void Reports_problems_at_the_file_and_line(string markdown, string id, int line)
    {
        var result = Run(("Diagnostics/bad.md", markdown));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == id);
        var span = diagnostic.Location.GetLineSpan();
        Assert.Equal("Diagnostics/bad.md", span.Path);
        Assert.Equal(line, span.StartLinePosition.Line);
    }

    [Fact]
    public void Unknown_keys_warn_but_still_generate()
    {
        var result = Run(("Diagnostics/w.md", "---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m\ncolour: red\n---\n# Warned\n"));

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(result.Diagnostics).Severity);
        Assert.NotNull(Method(result, "Warned"));
    }

    [Theory]
    [InlineData("code: X\neventId: 2", "NDIAG010")]
    [InlineData("code: Y\neventId: 1", "NDIAG011")]
    public void Duplicate_codes_and_event_ids_are_errors(string clash, string id)
    {
        var result = Run(
            ("Diagnostics/a.md", "---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m\n---\n# First\n"),
            ("Diagnostics/b.md", $"---\n{clash}\nlevel: Debug\nmessage: m\n---\n# Second\n"));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal("Diagnostics/b.md", diagnostic.Location.GetLineSpan().Path);
        Assert.Contains("a.md", diagnostic.GetMessage());
        Assert.NotNull(Method(result, "First"));
    }

    [Fact]
    public void Two_files_with_the_same_title_need_a_method_name()
    {
        var result = Run(
            ("Diagnostics/a.md", "---\ncode: X\neventId: 1\nlevel: Debug\nmessage: m\n---\n# Same\n"),
            ("Diagnostics/b.md", "---\ncode: Y\neventId: 2\nlevel: Debug\nmessage: m\n---\n# Same\n"));

        Assert.Equal("NDIAG012", Assert.Single(result.Diagnostics).Id);
    }

    [Theory]
    [InlineData("Agent disconnected", "AgentDisconnected")]
    [InlineData("'x' is not in the build", "XIsNotInTheBuild")]
    [InlineData("peer-version mismatch", "PeerVersionMismatch")]
    [InlineData("404 page", "_404Page")]
    public void Titles_become_pascal_case_method_names(string title, string method) =>
        Assert.Equal(method, LogEventParser.ToPascalCase(title));

    [Theory]
    [InlineData("AgentId", "agentId")]
    [InlineData("Class", "@class")]
    [InlineData("Logger", "loggerValue")]
    [InlineData("value", "value")]
    public void Placeholders_become_parameter_names(string placeholder, string name) =>
        Assert.Equal(name, LogEventParser.ParameterName(placeholder));

    [Fact]
    public void Reads_placeholder_names_with_formats_alignment_and_escaped_braces()
    {
        var names = LogEventParser.Placeholders("{{literal}} {A} {B:0.0} {C,10} done", out var malformed);

        Assert.False(malformed);
        Assert.Equal(["A", "B", "C"], names);
    }

    private sealed record Result(ImmutableArray<Diagnostic> Diagnostics, string? Source, Assembly Assembly);

    private static Result Run(params (string Path, string Text)[] files) => Run(files, []);

    private static Result Run((string Path, string Text)[] files, Dictionary<string, string> global, bool marked = true)
    {
        global.TryAdd("build_property.NiteDiagnosticsPublic", "true");
        global.TryAdd("build_property.RootNamespace", "Test");
        var texts = files.Select(f => (AdditionalText)new TestText(f.Path, f.Text)).ToImmutableArray();
        var options = new TestOptionsProvider(new TestOptions(global),
            marked ? new TestOptions(new() { ["build_metadata.AdditionalFiles.NiteDiagnostic"] = "true" }) : new TestOptions([]));

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Generated" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText("")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver.Create([new LogEventGenerator().AsSourceGenerator()], texts, optionsProvider: options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        // The generated code itself must compile cleanly.
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning));
        using var stream = new MemoryStream();
        Assert.True(output.Emit(stream).Success);
        var source = output.SyntaxTrees.Skip(1).FirstOrDefault()?.ToString();
        return new Result(diagnostics, source, Assembly.Load(stream.ToArray()));
    }

    private static MethodInfo Method(Result result, string name) =>
        result.Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)).Single(m => m.Name == name);

    private static void Invoke(Result result, string name, params object?[] args) => Method(result, name).Invoke(null, args);

    private sealed class TestText(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class TestOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    private sealed class TestOptionsProvider(AnalyzerConfigOptions global, AnalyzerConfigOptions perFile) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestOptions([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => perFile;
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

        public List<(LogLevel Level, EventId EventId, string Message, Dictionary<string, object?> State, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
            Entries.Add((logLevel, eventId, formatter(state, exception), values, exception));
        }
    }
}

/// <summary>NiteFx's own log messages come from src/Nite/Diagnostics through the generator.</summary>
public class NiteLogMessagesTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-log-messages-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task An_empty_project_logs_NITE1002_with_its_code_and_structured_value()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "nite.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Client"));
        var logger = new EventLogger();

        await using var esbuild = new EsbuildService(await EsbuildBinary.EnsureAsync(new HttpClient()));
        var project = new NiteProject(_root.FullName);
        await new NiteBuilder(esbuild, logger).BuildAsync(project);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == 1002);
        Assert.Equal("NITE1002", entry.EventId.Name);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"Nothing to build: no islands in {project.IslandsDirectory}, no index.html and no \"entries\" in nite.json", entry.Message);
        Assert.Equal(project.IslandsDirectory, entry.State["Islands"]);
    }

    private sealed class EventLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message, Dictionary<string, object?> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception),
                state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : []));
    }
}
