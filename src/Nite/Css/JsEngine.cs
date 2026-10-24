using System.Reflection;
using Jint;
using Jint.Native;
using Microsoft.Extensions.Logging;

namespace Nite.Css;

/// <summary>
/// A JavaScript engine (Jint, a managed interpreter) for build tools that are plain JavaScript:
/// PostCSS and its plugins, Tailwind's compiler and Less. The tools come from the project's own
/// node_modules, bundled by esbuild (<see cref="ToolBundler"/>), and see just enough of Node's
/// built-ins (<c>node-lite.js</c>) to run. An engine is single-threaded, so calls queue up.
/// </summary>
internal sealed class JsEngine : IDisposable
{
    private static readonly string Prelude = ReadResource("node-lite.js");

    private readonly Engine _engine;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsEngine(string workingDirectory, ILogger logger, Func<string, string, string>? loadModule = null)
    {
        _engine = new Engine(options => options.Strict(false));
        _engine.SetValue("__host", new NodeLiteHost(workingDirectory, logger, loadModule));
        _engine.Execute(Prelude, "nite:node-lite.js");
    }

    /// <summary>Runs code on the engine, one caller at a time, off the request thread.</summary>
    public async Task<T> RunAsync<T>(Func<Engine, T> work, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => work(_engine), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Evaluates a script file, such as a bundled tool.</summary>
    public Task ExecuteFileAsync(string path) =>
        RunAsync(engine => engine.Execute(File.ReadAllText(path), Path.GetFileName(path)));

    /// <summary>Calls a global function and waits for the promise it returns, if any.</summary>
    public Task<string> CallAsync(string function, params object?[] arguments) =>
        RunAsync(engine =>
        {
            var values = arguments.Select(a => a is null ? JsValue.Null : JsValue.FromObject(engine, a)).ToArray();
            var result = engine.Invoke(function, values).UnwrapIfPromise();
            return result.IsString() ? result.AsString() : result.ToString();
        });

    public void Dispose()
    {
        _engine.Dispose();
        _lock.Dispose();
    }

    internal static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Nite.Css.{name}")
            ?? throw new InvalidOperationException($"Nite resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>JavaScript paths use forward slashes everywhere, including Windows drive paths.</summary>
    internal static string ToJsPath(string path) => path.Replace('\\', '/');

    internal static string FromJsPath(string path) =>
        Path.GetFullPath(OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path);
}

/// <summary>The C# side of node-lite.js. Method names are what the JavaScript calls.</summary>
#pragma warning disable IDE1006 // lower-case to read naturally from JavaScript
internal sealed class NodeLiteHost(string workingDirectory, ILogger logger, Func<string, string, string>? loadModule)
{
    public string cwd() => JsEngine.ToJsPath(workingDirectory);

    public bool isWindows() => OperatingSystem.IsWindows();

    public bool isFile(string path) => File.Exists(JsEngine.FromJsPath(path));

    public bool isDirectory(string path) => Directory.Exists(JsEngine.FromJsPath(path));

    public string readFile(string path) => File.ReadAllText(JsEngine.FromJsPath(path));

    public string stat(string path)
    {
        var full = JsEngine.FromJsPath(path);
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            return $"file|{info.Length}|{new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()}";
        }
        return Directory.Exists(full)
            ? $"dir|0|{new DateTimeOffset(Directory.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds()}"
            : "";
    }

    /// <summary>One entry per line: "f name" for files, "d name" for directories.</summary>
    public string readDirectory(string path)
    {
        var full = JsEngine.FromJsPath(path);
        var entries = Directory.EnumerateDirectories(full).Select(d => "d " + Path.GetFileName(d))
            .Concat(Directory.EnumerateFiles(full).Select(f => "f " + Path.GetFileName(f)))
            .Order(StringComparer.Ordinal);
        return string.Join('\n', entries);
    }

    public string? env(string name) => name == "NODE_ENV" ? null : Environment.GetEnvironmentVariable(name);

    public void log(string level, string message)
    {
        var logLevel = level switch { "error" => LogLevel.Error, "warn" => LogLevel.Warning, "debug" => LogLevel.Debug, _ => LogLevel.Information };
        logger.Log(logLevel, "{Message}", message.TrimEnd());
    }

    /// <summary>A stylesheet import resolved to a file (see <see cref="CssResolver"/>), or "" when there is none.</summary>
    public string resolveStylesheet(string id, string baseDirectory) =>
        CssResolver.Resolve(id, JsEngine.FromJsPath(baseDirectory)) is { } path ? JsEngine.ToJsPath(path) : "";

    /// <summary>Bundles a module (a Tailwind plugin, say) and returns a script that defines it.</summary>
    public string bundleModule(string id, string baseDirectory) =>
        loadModule?.Invoke(id, JsEngine.FromJsPath(baseDirectory)) ?? throw new InvalidOperationException($"Cannot load '{id}' here.");
}
#pragma warning restore IDE1006
