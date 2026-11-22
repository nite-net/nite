using Nite.Packages;

namespace Nite.Tests;

/// <summary>Several processes share the esbuild and Sass cache, so installing into it must not clobber a working copy.</summary>
public class PlatformPackageTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-platform-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Staging(string content)
    {
        var staging = Path.Combine(_root.FullName, "pkg." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "bin"));
        File.WriteAllText(Path.Combine(staging, "bin", "tool"), content);
        return staging;
    }

    [Fact]
    public void Moves_the_extraction_into_place()
    {
        var directory = Path.Combine(_root.FullName, "pkg");
        var staging = Staging("new");

        PlatformPackage.SwapIn(staging, directory, "bin/tool");

        Assert.Equal("new", File.ReadAllText(Path.Combine(directory, "bin", "tool")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Keeps_a_copy_another_process_installed_first()
    {
        var directory = Path.Combine(_root.FullName, "pkg");
        PlatformPackage.SwapIn(Staging("first"), directory, "bin/tool");
        var second = Staging("second");

        PlatformPackage.SwapIn(second, directory, "bin/tool");

        Assert.Equal("first", File.ReadAllText(Path.Combine(directory, "bin", "tool")));
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public void Replaces_a_folder_left_without_the_binary()
    {
        var directory = Path.Combine(_root.FullName, "pkg");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "partial"), "");

        PlatformPackage.SwapIn(Staging("new"), directory, "bin/tool");

        Assert.Equal("new", File.ReadAllText(Path.Combine(directory, "bin", "tool")));
        Assert.False(File.Exists(Path.Combine(directory, "partial")));
    }

    [Fact]
    public async Task The_file_lock_is_exclusive_until_released()
    {
        var path = Path.Combine(_root.FullName, "pkg.lock");
        var first = await PlatformPackage.AcquireFileLockAsync(path, CancellationToken.None);

        var second = PlatformPackage.AcquireFileLockAsync(path, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
