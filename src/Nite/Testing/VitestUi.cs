using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Nite.Testing;

/// <summary>
/// Where a running <c>nite test --ui</c> listens, so the dev server can serve its UI at
/// <c>/_nite/vitest/</c>. <c>nite test</c> writes <c>.nite/vitest-ui.json</c> when Vitest starts
/// and deletes it when Vitest exits; a file left by a process that died is ignored.
/// </summary>
public sealed record VitestUi(int Port, int ProcessId)
{
    /// <summary>The path the UI is served under, in the app and on Vitest's own port alike.</summary>
    public const string BasePath = "/_nite/vitest/";

    /// <summary>The socket the UI talks to Vitest through. Vitest puts it at the site root, whatever the UI's base.</summary>
    public const string ApiPath = "/__vitest_api__";

    /// <summary>Opens a file in the editor when a test's location is clicked; also at the root.</summary>
    public const string EditorPath = "/__open-in-editor";

    public const string FileName = "vitest-ui.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The running UI for <paramref name="project"/>, or null when none is.</summary>
    public static VitestUi? Find(NiteProject project)
    {
        var path = Path.Combine(project.WorkDirectory, FileName);
        try
        {
            var ui = JsonSerializer.Deserialize<VitestUi>(File.ReadAllText(path), Options);
            return ui is { Port: > 0 } && IsRunning(ui.ProcessId) ? ui : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records <paramref name="ui"/> for the dev server; returns the file to delete when Vitest exits.</summary>
    public static string Write(NiteProject project, VitestUi ui)
    {
        Directory.CreateDirectory(project.WorkDirectory);
        var path = Path.Combine(project.WorkDirectory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(ui, Options));
        return path;
    }

    /// <summary>A port nothing listens on now, for Vitest's UI.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
