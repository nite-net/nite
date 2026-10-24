using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nite.Css;

/// <summary>Less, from the project's own <c>less</c> package, running in <see cref="JsEngine"/>.</summary>
internal sealed class LessCompiler(ToolBundler bundler, NiteProject project, ILogger logger) : IDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private JsEngine? _engine;

    public async Task<CssStepResult> CompileAsync(string file, LessOptions options)
    {
        var engine = await LoadAsync();
        var source = (options.AdditionalData is { Length: > 0 } extra ? extra + "\n" : "") + await File.ReadAllTextAsync(file);
        var paths = new[] { Path.GetDirectoryName(file)!, project.ClientRoot, Path.Combine(project.Directory, "node_modules") }
            .Concat(options.Paths.Select(p => Path.GetFullPath(Path.Combine(project.Directory, p))))
            .Select(JsEngine.ToJsPath);
        var settings = JsonSerializer.Serialize(new
        {
            paths,
            javascriptEnabled = options.JavascriptEnabled,
            globalVars = options.GlobalVars,
            modifyVars = options.ModifyVars,
        });

        var json = await engine.CallAsync("__niteLess", source, JsEngine.ToJsPath(file), settings);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw CssErrors.FromJs(error, file);
        }
        return new CssStepResult(
            root.GetProperty("css").GetString()!,
            root.GetProperty("imports").EnumerateArray().Select(i => JsEngine.FromJsPath(i.GetString()!)).ToList());
    }

    private async Task<JsEngine> LoadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_engine is not null)
            {
                return _engine;
            }
            if (!File.Exists(Path.Combine(project.Directory, "node_modules", "less", "package.json")))
            {
                throw new CssException("Less stylesheets need the less package. Add it to \"devDependencies\" in nite.json.");
            }

            var bundle = await bundler.BundleAsync("less", Entry);
            var engine = new JsEngine(project.Directory, logger);
            await engine.ExecuteFileAsync(bundle);
            return _engine = engine;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    // syncImport makes Less read imports with readFileSync, so a render completes in one call.
    private const string Entry = """
        const loaded = require("less");
        const less = loaded.default || loaded;
        globalThis.__niteLess = (source, filename, settings) => {
          const options = JSON.parse(settings);
          let output, failure;
          less.render(source, { filename, syncImport: true, ...options }, (error, result) => {
            failure = error;
            output = result;
          });
          if (failure) {
            return JSON.stringify({ error: { message: failure.message, file: failure.filename, line: failure.line || 0, column: (failure.column || 0) + 1 } });
          }
          if (!output) {
            return JSON.stringify({ error: { message: "Less did not finish synchronously (an async plugin?)", file: filename } });
          }
          return JSON.stringify({ css: output.css, imports: output.imports || [] });
        };
        """;

    public void Dispose() => _engine?.Dispose();
}
