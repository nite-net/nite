using Nite.Packages;

namespace Nite.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.11", 1)]
    [InlineData("v1.0.0+build.5", "1.0.0", 0)]
    public void Compares_per_semver_spec(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVersion.Parse(a).CompareTo(SemVersion.Parse(b))));
    }

    [Theory]
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^1.2.3", "1.2.2", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("^1.2", "1.9.9", true)]
    [InlineData("^1", "1.0.0", true)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("~1", "1.9.0", true)]
    [InlineData("1.x", "1.5.0", true)]
    [InlineData("1.2.*", "1.3.0", false)]
    [InlineData("*", "4.5.6", true)]
    [InlineData("", "0.0.1", true)]
    [InlineData(">=1.2 <2", "1.9.9", true)]
    [InlineData(">=1.2 <2", "2.0.0", false)]
    [InlineData(">= 1.2.0", "1.2.0", true)]
    [InlineData(">1.2", "1.2.9", false)]
    [InlineData(">1.2", "1.3.0", true)]
    [InlineData("<=1.2", "1.2.9", true)]
    [InlineData("<=1.2", "1.3.0", false)]
    [InlineData("1.2.3 - 2.3", "2.3.9", true)]
    [InlineData("1.2.3 - 2.3", "2.4.0", false)]
    [InlineData("1.2.3 - 2.3.4", "2.3.4", true)]
    [InlineData("^1 || ^3", "3.1.0", true)]
    [InlineData("^1 || ^3", "2.1.0", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    public void Matches_npm_ranges(string range, string version, bool expected)
    {
        Assert.Equal(expected, SemVerRange.Parse(range).IsSatisfiedBy(SemVersion.Parse(version)));
    }

    [Theory]
    [InlineData("^1.2.3", "1.3.0-beta.1", false)] // prereleases need an opt-in on the same tuple
    [InlineData("^1.2.3-beta.1", "1.2.3-beta.2", true)]
    [InlineData("^1.2.3-beta.1", "1.2.4-beta.1", false)]
    [InlineData("*", "1.0.0-rc.1", false)]
    [InlineData("^19.0.0", "20.0.0-canary", false)]
    public void Excludes_prereleases_unless_asked(string range, string version, bool expected)
    {
        Assert.Equal(expected, SemVerRange.Parse(range).IsSatisfiedBy(SemVersion.Parse(version)));
    }

    [Fact]
    public void Picks_the_highest_satisfying_version()
    {
        var versions = new[] { "18.3.1", "19.0.0", "19.3.1", "19.4.0-canary.1", "20.0.0" }.Select(SemVersion.Parse);
        Assert.Equal("19.3.1", SemVerRange.Parse("^19.0.0").MaxSatisfying(versions)?.ToString());
    }

    [Theory]
    [InlineData("git+https://github.com/a/b.git")]
    [InlineData("file:../local")]
    [InlineData("npm:other@1")]
    public void Rejects_non_registry_specifiers(string range)
    {
        Assert.False(SemVerRange.TryParse(range, out _));
    }
}
