using Microsoft.Extensions.FileSystemGlobbing;

namespace Nite.Css;

/// <summary>
/// Finds Tailwind class candidates in source files, standing in for Tailwind's native scanner.
/// It over-collects on purpose: anything that might be a class goes to the compiler, which
/// ignores what is not one. Results are cached per file, and candidates are never forgotten during
/// a session (as with Tailwind's own dev servers), so deleting a class never reorders the CSS.
/// </summary>
internal sealed class TailwindScanner
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", ".nite", "bin", "obj", ".vs", ".vscode", ".idea", ".next", ".turbo",
    };

    private static readonly HashSet<string> ExplicitIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git" };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".scss", ".sass", ".less", ".styl", ".map", ".lock", ".log",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".bmp", ".tiff", ".psd",
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".webm", ".wav", ".ogg", ".mov", ".pdf",
        ".zip", ".gz", ".tgz", ".br", ".7z", ".rar", ".dll", ".exe", ".pdb", ".so", ".dylib", ".node", ".wasm",
        ".db", ".sqlite", ".bin", ".snk", ".pfx", ".p12", ".cache", ".suo", ".user",
    };

    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock", "bun.lockb", "nite.lock.json",
    };

    private const long MaxFileSize = 2 * 1024 * 1024;

    private readonly Dictionary<string, ((long, long) Stamp, string[] Candidates)> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _candidates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public IReadOnlyCollection<string> Candidates
    {
        get
        {
            lock (_lock)
            {
                return _candidates.ToList();
            }
        }
    }

    /// <summary>Scans the given sources; returns true if any candidate not seen before turned up.</summary>
    public bool Scan(IEnumerable<TailwindSource> sources)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            (source.Negated ? excluded : files).UnionWith(Expand(source));
        }
        files.ExceptWith(excluded);
        return ScanFiles(files);
    }

    /// <summary>Re-reads specific files (a save during development); true if new candidates appeared.</summary>
    public bool ScanFiles(IEnumerable<string> files)
    {
        var added = false;
        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileSize)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
            lock (_lock)
            {
                if (_files.TryGetValue(file, out var cached) && cached.Stamp == stamp)
                {
                    continue;
                }
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            var found = new HashSet<string>(StringComparer.Ordinal);
            Extract(text, found);

            lock (_lock)
            {
                _files[file] = (stamp, found.ToArray());
                foreach (var candidate in found)
                {
                    added |= _candidates.Add(candidate);
                }
            }
        }
        return added;
    }

    /// <summary>Whether a changed file is one automatic source detection would scan.</summary>
    public static bool IsScannable(string file) =>
        !IgnoredExtensions.Contains(Path.GetExtension(file)) && !IgnoredFiles.Contains(Path.GetFileName(file))
        && !file.Split(Path.DirectorySeparatorChar).Any(IgnoredDirectories.Contains);

    /// <summary>The files a source entry covers: a directory (recursively), a file, or a glob.</summary>
    internal static IEnumerable<string> Expand(TailwindSource source)
    {
        var target = Path.GetFullPath(Path.Combine(source.Base, source.Pattern.Replace('/', Path.DirectorySeparatorChar)));
        if (!source.Pattern.Contains('*') && !source.Pattern.Contains('{'))
        {
            if (File.Exists(target))
            {
                return [target];
            }
            // An explicit directory is scanned even inside an ignored folder (node_modules/@acme/ui);
            // only folders below it are skipped by name.
            return Directory.Exists(target) ? Walk(target, source.Auto ? AutoIgnores(source.Base) : ExplicitIgnoredDirectories) : [];
        }

        // Split off the static leading directories so the walk starts as deep as it can.
        var segments = source.Pattern.Split('/');
        var staticCount = segments.TakeWhile(s => !s.Contains('*') && !s.Contains('{')).Count();
        var root = Path.GetFullPath(Path.Combine(source.Base, string.Join(Path.DirectorySeparatorChar, segments.Take(staticCount))));
        if (!Directory.Exists(root))
        {
            return [];
        }
        var matcher = new Matcher(StringComparison.Ordinal);
        foreach (var pattern in ExpandBraces(string.Join('/', segments.Skip(staticCount))))
        {
            matcher.AddInclude(pattern);
        }
        return Walk(root, source.Auto ? AutoIgnores(source.Base) : ExplicitIgnoredDirectories).Where(f => matcher.Match(root, f).HasMatches);
    }

    /// <summary>The default ignores plus plain folder names from the project's .gitignore (dist/, build).</summary>
    private static HashSet<string> AutoIgnores(string directory)
    {
        var ignored = new HashSet<string>(IgnoredDirectories, StringComparer.OrdinalIgnoreCase);
        var gitignore = Path.Combine(directory, ".gitignore");
        if (File.Exists(gitignore))
        {
            foreach (var raw in File.ReadLines(gitignore))
            {
                var line = raw.Trim().Trim('/');
                if (line.Length > 0 && line[0] != '#' && line[0] != '!' && line.IndexOfAny(['*', '/', '?', '[']) < 0)
                {
                    ignored.Add(line);
                }
            }
        }
        return ignored;
    }

    private static IEnumerable<string> Walk(string root, HashSet<string> ignoredDirectories)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!ignoredDirectories.Contains(name) && !(name == "_nite" && Path.GetFileName(directory) == "wwwroot"))
                    {
                        pending.Push(entry);
                    }
                }
                else if (!IgnoredExtensions.Contains(Path.GetExtension(name)) && !IgnoredFiles.Contains(name))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary><c>*.{js,tsx}</c> → <c>*.js</c>, <c>*.tsx</c>; FileSystemGlobbing has no braces.</summary>
    internal static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        var close = open < 0 ? -1 : pattern.IndexOf('}', open);
        if (open < 0 || close < 0)
        {
            return [pattern];
        }
        return pattern[(open + 1)..close].Split(',')
            .SelectMany(option => ExpandBraces(pattern[..open] + option + pattern[(close + 1)..]));
    }

    /// <summary>
    /// Splits text into things that could be class names. Whitespace always ends one; quotes,
    /// backticks, angle brackets, braces, commas, semicolons and <c>=</c> end one unless inside
    /// square brackets, where arbitrary values (<c>bg-[url('/a.png')]</c>,
    /// <c>grid-cols-[1fr,2fr]</c>) may contain them.
    /// </summary>
    internal static void Extract(string text, ISet<string> into)
    {
        var start = -1;
        var squareDepth = 0;
        var parenDepth = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            var boundary = char.IsWhiteSpace(c)
                || (squareDepth == 0 && c is '"' or '\'' or '`' or '<' or '>' or '{' or '}' or ',' or ';' or '=' or '\\')
                || (squareDepth > 0 && c is '`' or '\\' or '<' or '{' or '}');
            if (boundary)
            {
                if (start >= 0)
                {
                    Add(text.AsSpan(start, i - start), into);
                }
                start = -1;
                squareDepth = 0;
                parenDepth = 0;
                continue;
            }

            if (start < 0)
            {
                start = i;
            }
            if (c == '[') squareDepth++;
            else if (c == ']' && squareDepth > 0) squareDepth--;
            else if (c == '(' && squareDepth == 0) parenDepth++;
            else if (c == ')' && squareDepth == 0 && parenDepth > 0) parenDepth--;
        }
    }

    private static void Add(ReadOnlySpan<char> token, ISet<string> into)
    {
        // Calls and punctuation around a class: cn(…), "flex", trailing periods and colons.
        token = token.TrimStart("(:?&|)");
        token = token.TrimEnd(".,:;?&|");
        while (token.Length > 0 && token[^1] == ')' && Count(token, '(') < Count(token, ')'))
        {
            token = token[..^1];
        }
        if (token.Length is 0 or > 250 || !ContainsLetter(token) || Count(token, '[') != Count(token, ']') || Count(token, '(') != Count(token, ')'))
        {
            return;
        }
        // A function call such as clsx( leaves an opening parenthesis at the end; drop those.
        if (token.Contains("//", StringComparison.Ordinal) || token[^1] == '(' || token.Length < 2)
        {
            return;
        }
        // Class names start lowercase (flex, md:p-4), or with -, !, [, @ or *; digits only for a
        // variant (2xl:flex). Component names, C# identifiers and SVG path data (M12 4.5c…) do
        // not, and the compiler is slow to reject what it is given.
        var first = token[0];
        if (!(char.IsAsciiLetterLower(first) || first is '-' or '!' or '[' or '@' or '*' || (char.IsAsciiDigit(first) && token.Contains(':'))))
        {
            return;
        }
        if (token.Contains("..", StringComparison.Ordinal) || token.Contains("=>", StringComparison.Ordinal) || token.Contains("&&", StringComparison.Ordinal))
        {
            return;
        }
        into.Add(token.ToString());
        if (token[0] == '!' || token[^1] == '!')
        {
            into.Add(token.Trim('!').ToString());
        }
    }

    private static int Count(ReadOnlySpan<char> text, char c)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == c) count++;
        }
        return count;
    }

    private static bool ContainsLetter(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            if (char.IsAsciiLetter(ch)) return true;
        }
        return false;
    }
}

/// <summary>A place to look for class names: a directory, file or glob relative to <see cref="Base"/>.</summary>
/// <param name="Auto">Automatic source detection, which also skips folders named in .gitignore.</param>
internal sealed record TailwindSource(string Base, string Pattern, bool Negated = false, bool Auto = false);
