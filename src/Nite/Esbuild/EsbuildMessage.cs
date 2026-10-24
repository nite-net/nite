using System.Text;

namespace Nite.Esbuild;

/// <summary>An error or warning reported by esbuild.</summary>
public sealed record EsbuildMessage(string Text, string? File, int Line, int Column, string? LineText)
{
    public override string ToString() =>
        File is null ? Text : $"{File}:{Line}:{Column}: {Text}";

    internal static List<EsbuildMessage> ListFrom(object? value)
    {
        var messages = new List<EsbuildMessage>();
        if (value is not List<object?> list)
        {
            return messages;
        }

        foreach (var item in list.OfType<Dictionary<string, object?>>())
        {
            var text = item.GetValueOrDefault("text") as string ?? "Unknown esbuild error";
            if (item.GetValueOrDefault("location") is Dictionary<string, object?> location)
            {
                messages.Add(new EsbuildMessage(
                    text,
                    location.GetValueOrDefault("file") as string,
                    location.GetValueOrDefault("line") as int? ?? 0,
                    location.GetValueOrDefault("column") as int? ?? 0,
                    location.GetValueOrDefault("lineText") as string));
            }
            else
            {
                messages.Add(new EsbuildMessage(text, null, 0, 0, null));
            }
        }
        return messages;
    }
}

public sealed class EsbuildException(string message, IReadOnlyList<EsbuildMessage> errors)
    : Exception(Format(message, errors))
{
    public IReadOnlyList<EsbuildMessage> Errors { get; } = errors;

    private static string Format(string message, IReadOnlyList<EsbuildMessage> errors)
    {
        if (errors.Count == 0)
        {
            return message;
        }

        var builder = new StringBuilder(message).Append(':');
        foreach (var error in errors)
        {
            builder.AppendLine().Append("  ").Append(error);
        }
        return builder.ToString();
    }
}
