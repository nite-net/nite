using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Nite.Diagnostics;

/// <summary>
/// Turns each log message file (an <c>AdditionalFiles</c> item with <c>NiteDiagnostic="true"</c>,
/// which build/Nite.Diagnostics.targets adds for Diagnostics/**/*.md) into an ILogger extension
/// method backed by <c>LoggerMessage.Define</c>: cached, checked with IsEnabled, and logged as
/// structured properties with the file's code as the EventId name.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class LogEventGenerator : IIncrementalGenerator
{
    // LoggerMessage.Define has overloads for up to six values; more go through ILogger.Log.
    private const int MaxDefineParameters = 6;

    private const string Logging = "global::Microsoft.Extensions.Logging";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(pair => IsLogMessageFile(pair.Right.GetOptions(pair.Left)))
            .Select((pair, cancellationToken) =>
                LogEventParser.Parse(pair.Left.Path, pair.Left.GetText(cancellationToken)?.ToString() ?? ""));

        var settings = context.AnalyzerConfigOptionsProvider.Select((options, _) => ReadSettings(options.GlobalOptions));

        context.RegisterSourceOutput(files.Collect().Combine(settings), (spc, input) => Emit(spc, input.Left, input.Right));
    }

    private static bool IsLogMessageFile(AnalyzerConfigOptions options) =>
        options.TryGetValue("build_metadata.AdditionalFiles.NiteDiagnostic", out var value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static GeneratorSettings ReadSettings(AnalyzerConfigOptions options)
    {
        string Get(string name) => options.TryGetValue("build_property." + name, out var value) ? value.Trim() : "";

        var ns = Get("NiteDiagnosticsNamespace");
        if (ns.Length == 0)
        {
            ns = Get("RootNamespace");
        }
        var className = Get("NiteDiagnosticsClass");
        return new GeneratorSettings(
            ns,
            className.Length > 0 ? className : "Log",
            string.Equals(Get("NiteDiagnosticsPublic"), "true", StringComparison.OrdinalIgnoreCase),
            Get("NiteDiagnosticsHelpUrl"));
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<ParsedFile> files, GeneratorSettings settings)
    {
        foreach (var file in files)
        {
            foreach (var problem in file.Problems)
            {
                var arg0 = problem.Id == Descriptors.NoFrontMatter.Id ? Path.GetFileName(file.Path) : problem.Arg0;
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ById(problem.Id), At(file.Path, problem.Line), arg0, problem.Arg1, problem.Arg2));
            }
        }

        // Files in a stable order, so "also used by" always names the same file.
        var events = files.Where(f => f.Event is not null).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var accepted = new List<(string Path, LogEvent Event)>();
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        var eventIds = new Dictionary<int, string>();
        var methods = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in events)
        {
            var e = file.Event!;
            var name = Path.GetFileName(file.Path);
            var clash = false;
            if (codes.TryGetValue(e.Code, out var other))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DuplicateCode, At(file.Path, e.CodeLine), e.Code, other));
                clash = true;
            }
            if (eventIds.TryGetValue(e.EventId, out other))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DuplicateEventId, At(file.Path, e.EventIdLine), e.EventId, other));
                clash = true;
            }
            if (methods.TryGetValue(e.Method, out other))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.DuplicateMethod, At(file.Path, e.MethodLine), e.Method, other));
                clash = true;
            }
            if (clash)
            {
                continue;
            }
            codes[e.Code] = name;
            eventIds[e.EventId] = name;
            methods[e.Method] = name;
            accepted.Add((file.Path, e));
        }

        if (accepted.Count > 0)
        {
            context.AddSource(settings.ClassName + ".LogMessages.g.cs", SourceText.From(Generate(accepted, settings), Encoding.UTF8));
        }
    }

    internal static string Generate(IReadOnlyList<(string Path, LogEvent Event)> events, GeneratorSettings settings)
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/> by Nite.Diagnostics from Markdown log message files.");
        code.AppendLine("#nullable enable");
        code.AppendLine("using System;");
        code.AppendLine();
        if (settings.Namespace.Length > 0)
        {
            code.AppendLine($"namespace {settings.Namespace};");
            code.AppendLine();
        }
        code.AppendLine($"[global::System.CodeDom.Compiler.GeneratedCode(\"Nite.Diagnostics\", \"{typeof(LogEventGenerator).Assembly.GetName().Version}\")]");
        code.AppendLine($"{(settings.Public ? "public" : "internal")} static partial class {settings.ClassName}");
        code.AppendLine("{");

        foreach (var (path, e) in events.OrderBy(x => x.Event.EventId))
        {
            var level = $"{Logging}.LogLevel.{e.Level}";
            var eventId = $"new {Logging}.EventId({e.EventId}, {Literal(e.Code)})";
            var exception = e.Exception ? "exception" : "null";
            var parameters = string.Concat(e.Parameters.Select(p => $", {p.Type} {p.Name}"));
            var signature = $"this {Logging}.ILogger logger{(e.Exception ? ", global::System.Exception? exception" : "")}{parameters}";

            code.AppendLine($"    /// <summary><b>{Xml(e.Code)}</b>{(e.Title.Length > 0 ? ": " + Xml(e.Title) : "")}. Logs at {e.Level}: <c>{Xml(e.Message)}</c></summary>");
            if (settings.HelpUrl.Length > 0)
            {
                code.AppendLine($"    /// <remarks><see href=\"{Xml(settings.HelpUrl.Replace("{code}", e.Code))}\">{Xml(e.Code)}</see></remarks>");
            }
            code.AppendLine($"    // From {Path.GetFileName(path)}");

            if (e.Parameters.Count <= MaxDefineParameters)
            {
                var field = "__" + e.Method;
                var types = string.Join(", ", e.Parameters.Select(p => p.Type));
                var actionTypes = $"{Logging}.ILogger{(types.Length > 0 ? ", " + types : "")}, global::System.Exception?";
                var define = types.Length > 0 ? $"Define<{types}>" : "Define";
                code.AppendLine($"    private static readonly global::System.Action<{actionTypes}> {field} =");
                code.AppendLine($"        {Logging}.LoggerMessage.{define}({level}, {eventId}, {Literal(e.Message)});");
                code.AppendLine();
                var args = string.Concat(e.Parameters.Select(p => ", " + p.Name));
                code.AppendLine($"    public static void {e.Method}({signature}) => {field}(logger{args}, {exception});");
            }
            else
            {
                var values = string.Join(", ", e.Parameters.Select(p => p.Name));
                code.AppendLine($"    public static void {e.Method}({signature})");
                code.AppendLine("    {");
                code.AppendLine($"        if (logger.IsEnabled({level}))");
                code.AppendLine("        {");
                code.AppendLine($"            {Logging}.LoggerExtensions.Log(logger, {level}, {eventId}, {exception}, {Literal(e.Message)}, new object?[] {{ {values} }});");
                code.AppendLine("        }");
                code.AppendLine("    }");
            }
            code.AppendLine();
        }

        code.AppendLine("}");
        return code.ToString();
    }

    private static Location At(string path, int line) =>
        Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(new LinePosition(line, 0), new LinePosition(line, 0)));

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Xml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
