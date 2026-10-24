using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Nite.Packages;

/// <summary>A semantic version, compared per semver 2.0 (build metadata is ignored).</summary>
public sealed partial class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public SemVersion(int major, int minor, int patch, IReadOnlyList<string>? prerelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? [];
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a valid semantic version.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        var match = VersionPattern().Match(text?.Trim() ?? "");
        if (!match.Success)
        {
            return false;
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        version = new SemVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            prerelease);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        // A version without a prerelease outranks the same version with one.
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;

        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var a = Prerelease[i];
            var b = other.Prerelease[i];
            var aNumeric = int.TryParse(a, out var aNumber);
            var bNumeric = int.TryParse(b, out var bNumber);
            result = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (result != 0) return result;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}" : $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"^[v=]?\s*(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-.]+)?$")]
    private static partial Regex VersionPattern();
}

/// <summary>
/// An npm version range: <c>^1.2.3</c>, <c>~1.2</c>, <c>&gt;=1 &lt;2</c>, <c>1.x</c>, <c>1.2.3 - 2</c>,
/// <c>*</c> and <c>||</c> unions of those. Prerelease versions only match when a comparator in the same
/// set names a prerelease on the same major.minor.patch, as in node-semver.
/// </summary>
public sealed partial class SemVerRange
{
    private readonly List<List<Comparator>> _sets;

    private SemVerRange(string text, List<List<Comparator>> sets)
    {
        Text = text;
        _sets = sets;
    }

    public string Text { get; }

    public static SemVerRange Parse(string text) =>
        TryParse(text, out var range) ? range : throw new FormatException($"'{text}' is not a supported version range.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVerRange? range)
    {
        range = null;
        text ??= "";
        var sets = new List<List<Comparator>>();

        foreach (var rawSet in text.Split("||"))
        {
            var set = new List<Comparator>();
            var part = rawSet.Trim();

            var hyphen = HyphenPattern().Match(part);
            if (hyphen.Success)
            {
                if (!TryParsePartial(hyphen.Groups[1].Value, out var low) || !TryParsePartial(hyphen.Groups[2].Value, out var high))
                {
                    return false;
                }
                set.Add(new Comparator(">=", low.Floor()));
                set.AddRange(high.UpperBound(inclusiveWhenComplete: true));
                sets.Add(set);
                continue;
            }

            // Allow "> = 1.2" style spacing between operators and versions.
            var tokens = OperatorSpacing().Replace(part, "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                tokens = ["*"];
            }

            foreach (var token in tokens)
            {
                if (!TryParseComparator(token, set))
                {
                    return false;
                }
            }
            sets.Add(set);
        }

        range = new SemVerRange(text, sets);
        return true;
    }

    public bool IsSatisfiedBy(SemVersion version)
    {
        foreach (var set in _sets)
        {
            if (!set.All(c => c.Matches(version)))
            {
                continue;
            }
            if (!version.IsPrerelease)
            {
                return true;
            }
            if (set.Any(c => c.Version.IsPrerelease && c.Version.Major == version.Major && c.Version.Minor == version.Minor && c.Version.Patch == version.Patch))
            {
                return true;
            }
        }
        return false;
    }

    public SemVersion? MaxSatisfying(IEnumerable<SemVersion> versions) =>
        versions.Where(IsSatisfiedBy).Max();

    public override string ToString() => Text;

    private static bool TryParseComparator(string token, List<Comparator> set)
    {
        var match = ComparatorPattern().Match(token);
        if (!match.Success || !TryParsePartial(match.Groups["version"].Value, out var partial))
        {
            return false;
        }

        var op = match.Groups["op"].Value;
        switch (op)
        {
            case "^":
                set.Add(new Comparator(">=", partial.Floor()));
                set.Add(new Comparator("<", partial.CaretCeiling()));
                break;
            case "~" or "~>":
                set.Add(new Comparator(">=", partial.Floor()));
                set.Add(new Comparator("<", partial.TildeCeiling()));
                break;
            case "" or "=":
                if (partial.IsWildcardMajor)
                {
                    set.Add(Comparator.Any);
                }
                else if (partial.IsComplete)
                {
                    set.Add(new Comparator("=", partial.Floor()));
                }
                else
                {
                    set.Add(new Comparator(">=", partial.Floor()));
                    set.AddRange(partial.UpperBound(inclusiveWhenComplete: false));
                }
                break;
            case ">=":
                set.Add(partial.IsWildcardMajor ? Comparator.Any : new Comparator(">=", partial.Floor()));
                break;
            case ">":
                set.Add(partial.IsWildcardMajor
                    ? new Comparator("<", new SemVersion(0, 0, 0)) // nothing is greater than everything
                    : partial.IsComplete ? new Comparator(">", partial.Floor()) : new Comparator(">=", partial.NextAfterPartial()));
                break;
            case "<":
                set.Add(partial.IsWildcardMajor ? new Comparator("<", new SemVersion(0, 0, 0)) : new Comparator("<", partial.Floor()));
                break;
            case "<=":
                if (partial.IsWildcardMajor)
                {
                    set.Add(Comparator.Any);
                }
                else
                {
                    set.AddRange(partial.UpperBound(inclusiveWhenComplete: true));
                }
                break;
            default:
                return false;
        }
        return true;
    }

    private static bool TryParsePartial(string text, [NotNullWhen(true)] out PartialVersion? partial)
    {
        partial = null;
        var match = PartialPattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        static int? Part(Group group) =>
            !group.Success || group.Value is "x" or "X" or "*" ? null : int.Parse(group.Value);

        var major = Part(match.Groups["major"]);
        var minor = major is null ? null : Part(match.Groups["minor"]);
        var patch = minor is null ? null : Part(match.Groups["patch"]);
        var prerelease = patch is not null && match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        partial = new PartialVersion(major, minor, patch, prerelease);
        return true;
    }

    private sealed record PartialVersion(int? Major, int? Minor, int? Patch, string[] Prerelease)
    {
        public bool IsWildcardMajor => Major is null;
        public bool IsComplete => Patch is not null;

        public SemVersion Floor() => new(Major ?? 0, Minor ?? 0, Patch ?? 0, Prerelease);

        public SemVersion CaretCeiling()
        {
            if (Major is null) return new SemVersion(int.MaxValue, 0, 0);
            if (Major > 0 || Minor is null) return new SemVersion(Major.Value + 1, 0, 0, ["0"]);
            if (Minor > 0 || Patch is null) return new SemVersion(0, Minor.Value + 1, 0, ["0"]);
            return new SemVersion(0, 0, Patch.Value + 1, ["0"]);
        }

        public SemVersion TildeCeiling()
        {
            if (Major is null) return new SemVersion(int.MaxValue, 0, 0);
            if (Minor is null) return new SemVersion(Major.Value + 1, 0, 0, ["0"]);
            return new SemVersion(Major.Value, Minor.Value + 1, 0, ["0"]);
        }

        public SemVersion NextAfterPartial() =>
            Minor is null ? new SemVersion(Major!.Value + 1, 0, 0) : new SemVersion(Major!.Value, Minor.Value + 1, 0);

        public IEnumerable<Comparator> UpperBound(bool inclusiveWhenComplete)
        {
            if (Major is null) yield break;
            if (IsComplete)
            {
                yield return new Comparator(inclusiveWhenComplete ? "<=" : "=", Floor());
            }
            else
            {
                // "<=1.2" and "1.2" both mean "anything in 1.2.x".
                var next = NextAfterPartial();
                yield return new Comparator("<", new SemVersion(next.Major, next.Minor, next.Patch, ["0"]));
            }
        }
    }

    private sealed record Comparator(string Operator, SemVersion Version)
    {
        public static readonly Comparator Any = new(">=", new SemVersion(0, 0, 0, ["0"]));

        public bool Matches(SemVersion candidate)
        {
            var comparison = candidate.CompareTo(Version);
            return Operator switch
            {
                "=" => comparison == 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                _ => false,
            };
        }
    }

    [GeneratedRegex(@"^\s*(\S+)\s+-\s+(\S+)\s*$")]
    private static partial Regex HyphenPattern();

    [GeneratedRegex(@"(<=|>=|<|>|=|\^|~>|~)\s+")]
    private static partial Regex OperatorSpacing();

    [GeneratedRegex(@"^(?<op><=|>=|<|>|=|\^|~>|~)?(?<version>.+)$")]
    private static partial Regex ComparatorPattern();

    [GeneratedRegex(@"^[v=]?(?<major>\d+|[xX*])(?:\.(?<minor>\d+|[xX*])(?:\.(?<patch>\d+|[xX*])(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?)?)?(?:\+[0-9A-Za-z-.]+)?$")]
    private static partial Regex PartialPattern();
}
