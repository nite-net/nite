using System.Formats.Tar;
using System.IO.Compression;

namespace Nite.Packages;

internal static class Tarball
{
    /// <summary>
    /// Extracts an npm tarball into <paramref name="destination"/>, dropping the top-level folder
    /// (usually <c>package/</c>) the way npm does.
    /// </summary>
    public static void Extract(byte[] tgz, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        using var gzip = new GZipStream(new MemoryStream(tgz), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                // Directories are implied by files; links and devices have no business in a package.
                continue;
            }

            var name = entry.Name.Replace('\\', '/');
            var slash = name.IndexOf('/');
            if (slash < 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, name[(slash + 1)..]));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new NitePackageException($"Refusing to extract '{entry.Name}' outside the package folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = File.Create(target))
            {
                entry.DataStream?.CopyTo(output);
            }

            if (!OperatingSystem.IsWindows())
            {
                // Keep execute bits (esbuild's binary needs them) but never more than rwxr-xr-x.
                var mode = (UnixFileMode)entry.Mode & (UnixFileMode)0b111_101_101 | UnixFileMode.UserRead | UnixFileMode.UserWrite;
                File.SetUnixFileMode(target, mode);
            }
        }
    }
}
