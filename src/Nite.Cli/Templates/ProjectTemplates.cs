using System.Reflection;

namespace Nite.Cli.Templates;

/// <summary>
/// The files <c>nite init</c> writes: the <c>dotnet new</c> templates in src/Nite.Templates/content,
/// embedded in the CLI so both write the same projects. Placeholders, which template.json maps for
/// <c>dotnet new</c>: NiteApp (the project file name), NITE_TITLE (project name), NITE_NAMESPACE
/// (a valid C# namespace), NITE_PORT and <see cref="PackageReference"/> (how the project gets Nite).
/// </summary>
internal static class ProjectTemplates
{
    public const string Spa = "spa";
    public const string Islands = "islands";
    public static readonly string[] Names = [Spa, Islands];

    /// <summary>The Nite reference in the templates' project files, exactly as written there.</summary>
    public const string PackageReference =
        "  <ItemGroup>\n" +
        "    <PackageReference Include=\"Nite\" Version=\"NITE_VERSION\" />\n" +
        "  </ItemGroup>\n" +
        "\n";

    private const string ResourcePrefix = "templates/";

    public static IReadOnlyDictionary<string, string> For(string template)
    {
        if (!Names.Contains(template))
        {
            throw new ArgumentOutOfRangeException(nameof(template));
        }

        var assembly = typeof(ProjectTemplates).Assembly;
        var prefix = ResourcePrefix + template + "/";
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // MSBuild's RecursiveDir uses the build machine's separator.
            var path = resource.Replace('\\', '/');
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                files[path[prefix.Length..]] = Read(assembly, resource);
            }
        }
        return files;
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        // Checked out on Windows, the files may have CRLF endings; projects get LF either way.
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
