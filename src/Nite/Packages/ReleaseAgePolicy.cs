using System.Globalization;
using System.Text.RegularExpressions;

namespace Nite.Packages;

/// <summary>
/// <c>minimumReleaseAge</c> from <c>nite.json</c>: versions published more recently than the age
/// are passed over when choosing versions, as pnpm's setting of the same name does. Versions whose
/// publish time the registry does not give are allowed, since refusing them would make some
/// private registries unusable; the installer says so.
/// </summary>
public sealed partial class ReleaseAgePolicy
{
    private ReleaseAgePolicy(TimeSpan minimumAge, DateTimeOffset now, IReadOnlyList<string> exclude, string text)
    {
        MinimumAge = minimumAge;
        Now = now;
        Exclude = exclude;
        Text = text;
    }

    public TimeSpan MinimumAge { get; }

    /// <summary>The moment ages are measured from (fixed per install, so one resolve is consistent).</summary>
    public DateTimeOffset Now { get; }

    public IReadOnlyList<string> Exclude { get; }

    /// <summary>The setting as written, for messages: <c>3d</c>.</summary>
    public string Text { get; }

    /// <summary>The policy <paramref name="config"/> asks for, or null when it sets none (or zero).</summary>
    public static ReleaseAgePolicy? For(NiteConfig config, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(config.MinimumReleaseAge))
        {
            return null;
        }
        var age = ParseAge(config.MinimumReleaseAge);
        return age <= TimeSpan.Zero
            ? null
            : new ReleaseAgePolicy(age, (time ?? TimeProvider.System).GetUtcNow(), config.MinimumReleaseAgeExclude, config.MinimumReleaseAge.Trim());
    }

    /// <summary>Parses <c>90m</c>, <c>12h</c>, <c>3d</c> or <c>1w</c>.</summary>
    public static TimeSpan ParseAge(string text)
    {
        var match = AgePattern().Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            throw new InvalidOperationException($"minimumReleaseAge \"{text}\" in nite.json is not an age. Use a number and a unit: \"90m\", \"12h\", \"3d\" or \"1w\".");
        }
        return match.Groups[2].Value switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            "d" => TimeSpan.FromDays(amount),
            _ => TimeSpan.FromDays(amount * 7.0),
        };
    }

    /// <summary>Whether the age applies to <paramref name="name"/> (it is not excluded).</summary>
    public bool AppliesTo(string name) => !Exclude.Any(pattern =>
        pattern.EndsWith("/*", StringComparison.Ordinal)
            ? name.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(pattern, name, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="version"/> is old enough, or its age is unknown.</summary>
    public bool IsOldEnough(NpmPackument packument, string version) =>
        !AppliesTo(packument.Name) || packument.PublishedAt(version) is not { } published || Now - published >= MinimumAge;

    /// <summary>Whether the registry gave no publish times at all for this package, so the age cannot apply.</summary>
    public bool CannotCheck(NpmPackument packument) => AppliesTo(packument.Name) && packument.Time is null or { Count: 0 };

    /// <summary>The versions of <paramref name="versions"/> that are old enough.</summary>
    public IEnumerable<SemVersion> OldEnough(NpmPackument packument, IEnumerable<SemVersion> versions) =>
        versions.Where(v => IsOldEnough(packument, v.ToString()));

    /// <summary>
    /// The version a dist-tag stands for under this policy: the tagged version if it is old enough,
    /// otherwise the newest old-enough version below it (stable ones only, unless the tag points at a
    /// prerelease). Null when there is none.
    /// </summary>
    public SemVersion? ForTag(NpmPackument packument, string tagged)
    {
        if (IsOldEnough(packument, tagged) || !SemVersion.TryParse(tagged, out var target))
        {
            return SemVersion.TryParse(tagged, out var exact) ? exact : null;
        }
        return OldEnough(packument, Versions(packument))
            .Where(v => v <= target && (target.IsPrerelease || !v.IsPrerelease))
            .OrderDescending()
            .FirstOrDefault();
    }

    /// <summary>"published 5 hours ago" for messages.</summary>
    public string DescribeAge(NpmPackument packument, string version)
    {
        if (packument.PublishedAt(version) is not { } published)
        {
            return "publish time unknown";
        }
        var age = Now - published;
        return age.TotalHours < 1 ? $"published {Math.Max(1, (int)age.TotalMinutes)} minute(s) ago"
            : age.TotalDays < 2 ? $"published {(int)age.TotalHours} hour(s) ago"
            : $"published {(int)age.TotalDays} days ago";
    }

    /// <summary>The package's versions that parse as semver.</summary>
    public static IEnumerable<SemVersion> Versions(NpmPackument packument) =>
        packument.Versions.Keys.Select(v => SemVersion.TryParse(v, out var parsed) ? parsed : null).OfType<SemVersion>();

    [GeneratedRegex(@"^(\d+)\s*([mhdw])$")]
    private static partial Regex AgePattern();
}
