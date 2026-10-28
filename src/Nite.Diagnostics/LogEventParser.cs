using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Nite.Diagnostics;

/// <summary>
/// Reads one log message file: YAML-style front matter for the machine, Markdown for people.
/// <code>
/// ---
/// code: AGENT007
/// eventId: 7007
/// level: Warning
/// message: Agent {AgentId} disconnected after {Seconds} seconds
/// parameters:
///   AgentId: Guid
///   Seconds: int
/// ---
/// # Agent disconnected
/// </code>
/// The front matter is a small subset of YAML, read by hand: generators cannot easily carry
/// dependencies.
/// </summary>
internal static class LogEventParser
{
    private static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    private static readonly HashSet<string> Keys = ["code", "eventId", "level", "message", "method", "parameters", "exception"];

    public static ParsedFile Parse(string path, string text)
    {
        var problems = new List<Problem>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            problems.Add(new Problem(Descriptors.NoFrontMatter.Id, 0, ""));
            return new ParsedFile(path, null, new EquatableArray<Problem>(problems));
        }
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            problems.Add(new Problem(Descriptors.NoFrontMatter.Id, 0, ""));
            return new ParsedFile(path, null, new EquatableArray<Problem>(problems));
        }

        // key -> (value, line)
        var values = new Dictionary<string, (string Value, int Line)>(StringComparer.Ordinal);
        var parameters = new List<(string Name, string Type, int Line)>();
        string? section = null;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            var indented = char.IsWhiteSpace(line[0]);
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                problems.Add(new Problem(Descriptors.BadFrontMatter.Id, i, line.Trim()));
                continue;
            }
            var key = line.Substring(0, colon).Trim();
            var value = Unquote(line.Substring(colon + 1).Trim());

            if (indented)
            {
                if (section == "parameters")
                {
                    parameters.Add((key, value, i));
                }
                else
                {
                    problems.Add(new Problem(Descriptors.BadFrontMatter.Id, i, line.Trim()));
                }
                continue;
            }

            section = key;
            if (!Keys.Contains(key))
            {
                problems.Add(new Problem(Descriptors.UnknownKey.Id, i, key));
                continue;
            }
            if (values.ContainsKey(key))
            {
                problems.Add(new Problem(Descriptors.BadFrontMatter.Id, i, line.Trim()));
                continue;
            }
            values[key] = (value, i);
        }

        foreach (var required in new[] { "code", "eventId", "level", "message" })
        {
            if (!values.TryGetValue(required, out var v) || v.Value.Length == 0)
            {
                problems.Add(new Problem(Descriptors.MissingKey.Id, 0, required));
            }
        }

        var title = "";
        var titleLine = 0;
        for (var i = end + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("# ", StringComparison.Ordinal))
            {
                title = lines[i].Substring(2).Trim();
                titleLine = i;
                break;
            }
        }

        if (problems.Any(p => p.Id == Descriptors.MissingKey.Id || p.Id == Descriptors.NoFrontMatter.Id))
        {
            return new ParsedFile(path, null, new EquatableArray<Problem>(problems));
        }

        var (code, codeLine) = values["code"];
        if (!code.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            problems.Add(new Problem(Descriptors.BadValue.Id, codeLine, "code", code, "letters, digits, '.', '-' and '_'"));
        }

        var (eventIdText, eventIdLine) = values["eventId"];
        if (!int.TryParse(eventIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            problems.Add(new Problem(Descriptors.BadValue.Id, eventIdLine, "eventId", eventIdText, "a whole number"));
        }

        var (levelText, levelLine) = values["level"];
        var level = Levels.FirstOrDefault(l => string.Equals(l, levelText, StringComparison.OrdinalIgnoreCase));
        if (level is null)
        {
            problems.Add(new Problem(Descriptors.BadValue.Id, levelLine, "level", levelText, string.Join(", ", Levels)));
        }

        var exception = false;
        if (values.TryGetValue("exception", out var exceptionValue) && !bool.TryParse(exceptionValue.Value, out exception))
        {
            problems.Add(new Problem(Descriptors.BadValue.Id, exceptionValue.Line, "exception", exceptionValue.Value, "true or false"));
        }

        string method;
        int methodLine;
        if (values.TryGetValue("method", out var methodValue))
        {
            (method, methodLine) = methodValue;
        }
        else if (title.Length > 0)
        {
            (method, methodLine) = (ToPascalCase(title), titleLine);
        }
        else
        {
            problems.Add(new Problem(Descriptors.NoMethodName.Id, end + 1, ""));
            return new ParsedFile(path, null, new EquatableArray<Problem>(problems));
        }
        if (!SyntaxFacts.IsValidIdentifier(method) || SyntaxFacts.GetKeywordKind(method) != SyntaxKind.None)
        {
            problems.Add(new Problem(Descriptors.BadMethodName.Id, methodLine, method));
        }

        var (message, messageLine) = values["message"];
        var placeholders = Placeholders(message, out var malformed);
        if (malformed)
        {
            problems.Add(new Problem(Descriptors.BadValue.Id, messageLine, "message", message, "a message template with balanced {Placeholder} braces"));
        }
        foreach (var repeated in placeholders.GroupBy(p => p, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add(new Problem(Descriptors.RepeatedPlaceholder.Id, messageLine, repeated.Key));
        }

        foreach (var (name, _, line) in parameters.Where(p => !placeholders.Contains(p.Name)))
        {
            problems.Add(new Problem(Descriptors.ParameterNotInMessage.Id, line, name));
        }
        if (parameters.Count > 0)
        {
            foreach (var name in placeholders.Distinct().Where(p => parameters.All(q => q.Name != p)))
            {
                problems.Add(new Problem(Descriptors.PlaceholderWithoutType.Id, messageLine, name));
            }
        }

        if (problems.Any(p => Descriptors.IsError(p.Id)))
        {
            return new ParsedFile(path, null, new EquatableArray<Problem>(problems));
        }

        var args = placeholders.Distinct().Select(name => new LogParameter(
            name,
            ParameterName(name),
            parameters.FirstOrDefault(p => p.Name == name).Type is { Length: > 0 } type ? type : "object?"));

        var logEvent = new LogEvent(code, eventId, level!, message, method, title, exception,
            new EquatableArray<LogParameter>(args), codeLine, eventIdLine, methodLine);
        return new ParsedFile(path, logEvent, new EquatableArray<Problem>(problems));
    }

    /// <summary>The names of a message template's holes, in order: <c>{Name}</c>, <c>{Name:format}</c>, <c>{Name,10}</c>.</summary>
    internal static List<string> Placeholders(string message, out bool malformed)
    {
        var names = new List<string>();
        malformed = false;
        for (var i = 0; i < message.Length; i++)
        {
            var c = message[i];
            if (c == '{' && i + 1 < message.Length && message[i + 1] == '{' || c == '}' && i + 1 < message.Length && message[i + 1] == '}')
            {
                i++;
                continue;
            }
            if (c == '}')
            {
                malformed = true;
                continue;
            }
            if (c != '{')
            {
                continue;
            }
            var close = message.IndexOf('}', i + 1);
            if (close < 0)
            {
                malformed = true;
                break;
            }
            var hole = message.Substring(i + 1, close - i - 1);
            var name = hole.Split(',', ':')[0].Trim();
            if (name.Length == 0)
            {
                malformed = true;
            }
            else
            {
                names.Add(name);
            }
            i = close;
        }
        return names;
    }

    /// <summary>"Agent disconnected" → AgentDisconnected.</summary>
    internal static string ToPascalCase(string title)
    {
        var builder = new StringBuilder();
        foreach (var word in title.Split(' ', '-', '_', '\t'))
        {
            var letters = new string(word.Where(char.IsLetterOrDigit).ToArray());
            if (letters.Length > 0)
            {
                builder.Append(char.ToUpperInvariant(letters[0])).Append(letters, 1, letters.Length - 1);
            }
        }
        return builder.Length > 0 && char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    /// <summary>A C# parameter name for a placeholder: AgentId → agentId; keywords get an @.</summary>
    internal static string ParameterName(string placeholder)
    {
        var letters = new string(placeholder.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (letters.Length == 0 || char.IsDigit(letters[0]))
        {
            letters = "_" + letters;
        }
        var name = char.ToLowerInvariant(letters[0]) + letters.Substring(1);
        if (name is "logger" or "exception")
        {
            return name + "Value";
        }
        return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
        {
            return value.Substring(1, value.Length - 2).Replace("''", "'");
        }
        return value;
    }
}
