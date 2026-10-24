using System.Text.Json;
using Nite.Esbuild;

namespace Nite.Css;

/// <summary>A stylesheet failed to compile. Carries the location so the error overlay can show it.</summary>
public sealed class CssException(string message, string? file = null, int line = 0, int column = 0, string? lineText = null)
    : EsbuildPluginException(message, file, line, column, lineText);

/// <summary>The CSS one step produced and the files besides its input that went into it.</summary>
internal sealed record CssStepResult(string Css, IReadOnlyList<string> Dependencies);

internal static class CssErrors
{
    /// <summary>
    /// Builds an exception from the <c>{ message, file, line, column, source }</c> objects the
    /// JavaScript entries return; <c>source</c> is the file's text, used for the line excerpt.
    /// </summary>
    public static CssException FromJs(JsonElement error, string fallbackFile)
    {
        var message = error.GetProperty("message").GetString() ?? "CSS error";
        var file = error.TryGetProperty("file", out var f) && f.GetString() is { Length: > 0 } path ? JsEngine.FromJsPath(path) : fallbackFile;
        var line = error.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        var column = error.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        string? source = error.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (source is null && line > 0 && File.Exists(file))
        {
            source = File.ReadAllText(file);
        }
        if (error.TryGetProperty("plugin", out var plugin) && plugin.GetString() is { Length: > 0 } pluginName)
        {
            message = $"[{pluginName}] {message}";
        }
        // PostCSS and Less count columns from 1; esbuild's overlay counts from 0.
        return new CssException(message, file, line, Math.Max(0, column - 1), LineAt(source, line));
    }

    public static string? LineAt(string? source, int line)
    {
        if (source is null || line <= 0)
        {
            return null;
        }
        var lines = source.Split('\n');
        return line <= lines.Length ? lines[line - 1].TrimEnd('\r') : null;
    }
}
