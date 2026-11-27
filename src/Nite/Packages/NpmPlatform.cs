using System.Runtime.InteropServices;

namespace Nite.Packages;

/// <summary>
/// A machine in npm's terms (<c>process.platform</c>, <c>process.arch</c> and the C library), for
/// choosing which platform-specific packages to install: the <c>os</c>, <c>cpu</c> and
/// <c>libc</c> fields of a package manifest are matched against it.
/// </summary>
public sealed record NpmPlatform(string? Os, string? Cpu, string? Libc)
{
    /// <summary>This machine. A null part is one npm has no name for.</summary>
    public static NpmPlatform Current { get; } = new(CurrentOs(), CurrentCpu(), OperatingSystem.IsLinux() ? (IsMusl() ? "musl" : "glibc") : null);

    /// <summary>
    /// Whether a package with these constraints can run here. Each list is npm's: names allowed, or
    /// names with <c>!</c> excluded; a missing or empty list allows anything.
    /// </summary>
    public bool Supports(IReadOnlyList<string>? os, IReadOnlyList<string>? cpu, IReadOnlyList<string>? libc) =>
        Allows(os, Os) && Allows(cpu, Cpu) && (Os != "linux" || Allows(libc, Libc));

    /// <summary>
    /// The <c>libc</c> a manifest declares, or one read from its name (<c>-musl</c>, <c>-gnu</c>):
    /// the abbreviated registry document leaves the field out, and Linux binary packages follow
    /// that naming (<c>@rollup/rollup-linux-x64-musl</c>, <c>lightningcss-linux-x64-gnu</c>).
    /// </summary>
    public static IReadOnlyList<string>? LibcOf(string name, IReadOnlyList<string>? declared) =>
        declared is { Count: > 0 } ? declared
        : name.EndsWith("-musl", StringComparison.Ordinal) || name.Contains("-musl-", StringComparison.Ordinal) ? ["musl"]
        : name.EndsWith("-gnu", StringComparison.Ordinal) || name.Contains("-gnu-", StringComparison.Ordinal) ? ["glibc"]
        : null;

    private static bool Allows(IReadOnlyList<string>? list, string? current)
    {
        if (list is null || list.Count == 0)
        {
            return true;
        }
        if (current is null)
        {
            // A machine npm has no name for runs no package that names where it runs.
            return list.All(entry => entry.StartsWith('!'));
        }
        if (list.Contains("!" + current))
        {
            return false;
        }
        var positive = list.Where(entry => !entry.StartsWith('!')).ToList();
        return positive.Count == 0 || positive.Contains(current) || positive.Contains("any");
    }

    private static string? CurrentOs() =>
        OperatingSystem.IsWindows() ? "win32"
        : OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsAndroid() ? "android"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsFreeBSD() ? "freebsd"
        : null;

    private static string? CurrentCpu() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "ia32",
        Architecture.Arm => "arm",
        Architecture.RiscV64 => "riscv64",
        Architecture.S390x => "s390x",
        Architecture.Ppc64le => "ppc64",
        Architecture.LoongArch64 => "loong64",
        _ => null,
    };

    internal static bool IsMusl() =>
        Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
}
