using System.Reflection;

namespace Nite.Runtime;

/// <summary>The browser runtime shipped inside the Nite assembly.</summary>
internal static class RuntimeSources
{
    public static string IslandLoader { get; } = Read("islands.js");
    public static string ReactRenderer { get; } = Read("react-renderer.js");
    public static string DevClient { get; } = Read("client.js");
    public static string RefreshRuntime { get; } = Read("refresh.js");
    public static string ClientConfig { get; } = Read("config.js");

    /// <summary>The import that gives client code <see cref="NiteOptions.ClientConfig"/>'s values.</summary>
    public const string ClientConfigSpecifier = "nite:config";

    /// <summary>Bare imports the built-in React renderer needs pre-bundled.</summary>
    public static readonly string[] ReactRendererImports = ["react", "react-dom/client"];

    /// <summary>
    /// The renderer module for a production build, which esbuild bundles from <c>.nite/runtime</c>.
    /// A custom renderer is re-exported by absolute path.
    /// </summary>
    public static string RendererForBuild(NiteProject project) =>
        project.CustomRendererPath is { } custom
            ? $"export * from {System.Text.Json.JsonSerializer.Serialize(NitePaths.ToUrlPath(custom))};\n"
            : ReactRenderer;

    /// <summary>The renderer module for the dev server, served at <c>/_nite/runtime/renderer.js</c>.</summary>
    public static string RendererForDev(NiteProject project) =>
        project.CustomRendererPath is { } custom
            ? $"export * from {System.Text.Json.JsonSerializer.Serialize("../src/" + project.RelativeToClientRoot(custom))};\n"
            : ReactRenderer;

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Nite.Runtime.{name}")
            ?? throw new InvalidOperationException($"Nite runtime resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
