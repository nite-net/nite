using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Nite.Diagnostics;

// Everything the generator caches between keystrokes compares by value, so an edit to one file
// regenerates only when what it means has changed.

/// <summary>One Markdown file: its event, if it parsed, and what was wrong with it.</summary>
internal sealed record ParsedFile(string Path, LogEvent? Event, EquatableArray<Problem> Problems);

internal sealed record LogEvent(
    string Code,
    int EventId,
    string Level,
    string Message,
    string Method,
    string Title,
    bool Exception,
    EquatableArray<LogParameter> Parameters,
    int CodeLine,
    int EventIdLine,
    int MethodLine);

/// <summary>A message template hole and the C# parameter that fills it.</summary>
internal sealed record LogParameter(string Placeholder, string Name, string Type);

/// <summary>A diagnostic to report at a line of the Markdown file (0-based).</summary>
internal sealed record Problem(string Id, int Line, string Arg0, string Arg1 = "", string Arg2 = "");

internal sealed record GeneratorSettings(string Namespace, string ClassName, bool Public, string HelpUrl);

/// <summary>An immutable array with value equality, for incremental generator caching.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items) => _items = items.ToArray();

    public int Count => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other) => this.SequenceEqual(other);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in this)
        {
            hash = hash * 31 + item.GetHashCode();
        }
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
