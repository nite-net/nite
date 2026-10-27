using Microsoft.CodeAnalysis;

namespace Nite.Diagnostics;

/// <summary>What the generator reports about log message files. Errors stop that file's method from being generated.</summary>
internal static class Descriptors
{
    private const string Category = "Nite.Diagnostics";

    public static readonly DiagnosticDescriptor NoFrontMatter = Error("NDIAG001",
        "Log message file has no front matter",
        "'{0}' must start with front matter between --- lines, with at least code, eventId, level and message");

    public static readonly DiagnosticDescriptor BadFrontMatter = Error("NDIAG002",
        "Front matter line not understood",
        "Front matter line not understood: '{0}'. Use 'key: value', with parameters indented under 'parameters:'");

    public static readonly DiagnosticDescriptor MissingKey = Error("NDIAG003",
        "Log message file is missing a required key",
        "The front matter needs '{0}'");

    public static readonly DiagnosticDescriptor BadValue = Error("NDIAG004",
        "Front matter value not valid",
        "'{1}' is not a valid {0}: use {2}");

    public static readonly DiagnosticDescriptor NoMethodName = Error("NDIAG005",
        "No method name",
        "Give the file a '# Title' heading or a 'method:' key: the generated method is named after it");

    public static readonly DiagnosticDescriptor BadMethodName = Error("NDIAG006",
        "Method name is not a C# identifier",
        "'{0}' is not a valid C# method name: set 'method:' in the front matter");

    public static readonly DiagnosticDescriptor RepeatedPlaceholder = Error("NDIAG007",
        "Placeholder used more than once",
        "The message uses {{{0}}} more than once: give each value its own name");

    public static readonly DiagnosticDescriptor ParameterNotInMessage = Error("NDIAG008",
        "Parameter is not in the message",
        "'{0}' is under parameters but the message has no {{{0}}}");

    public static readonly DiagnosticDescriptor PlaceholderWithoutType = Error("NDIAG009",
        "Placeholder has no type",
        "The message has {{{0}}} but parameters does not give it a type: list every placeholder, or none to make them all object?");

    public static readonly DiagnosticDescriptor DuplicateCode = Error("NDIAG010",
        "Duplicate code",
        "Code '{0}' is also used by {1}");

    public static readonly DiagnosticDescriptor DuplicateEventId = Error("NDIAG011",
        "Duplicate event ID",
        "Event ID {0} is also used by {1}");

    public static readonly DiagnosticDescriptor DuplicateMethod = Error("NDIAG012",
        "Duplicate method name",
        "Method '{0}' is also generated from {1}: set 'method:' in one of them");

    public static readonly DiagnosticDescriptor UnknownKey = new("NDIAG013",
        "Unknown front matter key",
        "Unknown front matter key '{0}': the generator reads code, eventId, level, message, method, parameters and exception",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "NDIAG001" => NoFrontMatter,
        "NDIAG002" => BadFrontMatter,
        "NDIAG003" => MissingKey,
        "NDIAG004" => BadValue,
        "NDIAG005" => NoMethodName,
        "NDIAG006" => BadMethodName,
        "NDIAG007" => RepeatedPlaceholder,
        "NDIAG008" => ParameterNotInMessage,
        "NDIAG009" => PlaceholderWithoutType,
        "NDIAG010" => DuplicateCode,
        "NDIAG011" => DuplicateEventId,
        "NDIAG012" => DuplicateMethod,
        _ => UnknownKey,
    };

    public static bool IsError(string id) => ById(id).DefaultSeverity == DiagnosticSeverity.Error;

    private static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
