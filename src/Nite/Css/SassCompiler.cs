using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nite.Configuration;
using Nite.Packages;

namespace Nite.Css;

/// <summary>
/// Dart Sass through its embedded protocol: one long-lived compiler process (the native binary
/// from the <c>sass-embedded-&lt;platform&gt;</c> npm package, matched to the project's sass or
/// sass-embedded version) serves every compile, the way the sass-embedded package does it.
/// </summary>
internal sealed class SassCompiler(HttpClient http, NiteProject project, ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ProtoMessage>> _pending = new();
    private Process? _process;
    private int _nextId;
    private bool _disposed;

    public async Task<CssStepResult> CompileAsync(string file, SassOptions options, CancellationToken cancellationToken = default)
    {
        var process = await EnsureStartedAsync(cancellationToken);
        var indented = file.EndsWith(".sass", StringComparison.OrdinalIgnoreCase);
        var prefix = options.AdditionalData is { Length: > 0 } extra ? extra + "\n" : "";

        var request = new ProtoWriter();
        if (prefix.Length > 0)
        {
            // additionalData has to be part of the file itself (its @use rules must come first), so
            // the file goes in as a string whose URL makes relative loads resolve next to it.
            request.Message(2, new ProtoWriter()
                .String(1, prefix + await File.ReadAllTextAsync(file, cancellationToken))
                .String(2, new Uri(file).AbsoluteUri)
                .Enum(3, indented ? 1 : 0)
                .Message(4, new ProtoWriter().String(1, Path.GetDirectoryName(file)!)));
        }
        else
        {
            request.String(3, file);
        }
        request.Bool(9, true); // alert_ascii: plain characters in formatted messages
        request.Bool(11, true); // quiet_deps: no warnings from node_modules
        foreach (var loadPath in LoadPaths(options))
        {
            request.Message(6, new ProtoWriter().String(1, loadPath));
        }
        // pkg: URLs (@use "pkg:bootstrap") resolve through package.json, like Node's resolver.
        request.Message(6, new ProtoWriter().Message(5, new ProtoWriter().String(1, project.Directory)));
        foreach (var deprecation in options.SilenceDeprecations)
        {
            request.String(16, deprecation);
        }

        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var completion = new TaskCompletionSource<ProtoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(process, id, new ProtoWriter().Message(2, request).ToArray(), cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var response = await completion.Task;
            return ToResult(response, file, prefix);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private IEnumerable<string> LoadPaths(SassOptions options) =>
        options.LoadPaths.Select(p => Path.GetFullPath(Path.Combine(project.Directory, p)))
            .Append(project.ClientRoot)
            .Append(Path.Combine(project.Directory, "node_modules"))
            .Distinct(StringComparer.Ordinal);

    private CssStepResult ToResult(ProtoMessage response, string file, string prefix)
    {
        var dependencies = response.Strings(4)
            .Where(url => url.StartsWith("file:", StringComparison.Ordinal))
            .Select(url => Path.GetFullPath(new Uri(url).LocalPath))
            .Where(path => path != file)
            .ToList();

        if (response.Message(2) is { } success)
        {
            return new CssStepResult(success.String(1) ?? "", dependencies);
        }

        var failure = response.Message(3) ?? throw new CssException("Sass returned neither CSS nor an error.", file);
        var message = failure.String(1) ?? "Sass error";
        var span = failure.Message(2);
        var start = span?.Message(2);
        var errorFile = span?.String(4) is { } url && url.StartsWith("file:", StringComparison.Ordinal) ? Path.GetFullPath(new Uri(url).LocalPath) : file;
        var line = start is null ? 0 : (int)start.Number(2) + 1;
        var column = start is null ? 0 : (int)start.Number(3);
        if (errorFile == file && prefix.Length > 0)
        {
            // Point past the additionalData lines Nite put in front.
            line -= prefix.Count(c => c == '\n');
            if (line <= 0)
            {
                message += " (in css.preprocessorOptions.scss.additionalData)";
                line = 0;
            }
        }
        var lineText = line > 0 && File.Exists(errorFile) ? CssErrors.LineAt(File.ReadAllText(errorFile), line) : span?.String(5)?.Split('\n').FirstOrDefault();
        throw new CssException(message, errorFile, line, column, lineText);
    }

    private async Task SendAsync(Process process, uint compilationId, byte[] message, CancellationToken cancellationToken)
    {
        var header = new MemoryStream();
        var idBytes = new MemoryStream();
        ProtoWriter.Varint(idBytes, compilationId);
        ProtoWriter.Varint(header, (ulong)(idBytes.Length + message.Length));
        header.Write(idBytes.ToArray());
        header.Write(message);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var stdin = process.StandardInput.BaseStream;
            await stdin.WriteAsync(header.ToArray(), cancellationToken);
            await stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false } running)
        {
            return running;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } started)
            {
                return started;
            }

            var (dart, snapshot) = await EnsureBinaryAsync(cancellationToken);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(dart)
                {
                    ArgumentList = { snapshot, "--embedded" },
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = project.Directory,
                },
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    logger.LogWarning("sass: {Message}", e.Data);
                }
            };
            process.Start();
            process.BeginErrorReadLine();
            _process = process;
            _ = Task.Run(() => ReadLoopAsync(process));
            return process;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var used = 0;
        Exception? failure = null;
        try
        {
            while (true)
            {
                if (used == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
                var read = await stdout.ReadAsync(buffer.AsMemory(used));
                if (read == 0)
                {
                    break;
                }
                used += read;

                var offset = 0;
                while (TryReadPacket(buffer.AsSpan(offset, used - offset), out var consumed, out var compilationId, out var body))
                {
                    offset += consumed;
                    Handle(compilationId, ProtoMessage.Parse(body));
                }
                if (offset > 0)
                {
                    Buffer.BlockCopy(buffer, offset, buffer, 0, used - offset);
                    used -= offset;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var error = new CssException($"The Sass compiler stopped: {failure?.Message ?? "it exited"}");
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(error);
        }
        if (!_disposed)
        {
            logger.LogWarning("{Message}", error.Message);
        }
    }

    private static bool TryReadPacket(ReadOnlySpan<byte> data, out int consumed, out uint compilationId, out byte[] body)
    {
        consumed = 0;
        compilationId = 0;
        body = [];
        var offset = 0;
        ulong length;
        try
        {
            length = ProtoMessage.ReadVarint(data, ref offset);
        }
        catch (FormatException)
        {
            return false; // the length itself is not all here yet
        }
        if ((ulong)(data.Length - offset) < length)
        {
            return false;
        }
        var packet = data.Slice(offset, (int)length);
        var idOffset = 0;
        compilationId = (uint)ProtoMessage.ReadVarint(packet, ref idOffset);
        body = packet[idOffset..].ToArray();
        consumed = offset + (int)length;
        return true;
    }

    private void Handle(uint compilationId, ProtoMessage outbound)
    {
        if (outbound.Message(2) is { } compileResponse)
        {
            if (_pending.TryGetValue(compilationId, out var completion))
            {
                completion.TrySetResult(compileResponse);
            }
        }
        else if (outbound.Message(3) is { } log)
        {
            var type = log.Number(2); // 0 warning, 1 deprecation, 2 @debug
            var text = log.String(6) ?? log.String(3) ?? "";
            logger.Log(type == 2 ? LogLevel.Information : LogLevel.Warning, "sass: {Message}", text.TrimEnd());
        }
        else if (outbound.Message(1) is { } protocolError)
        {
            var error = new CssException($"Sass protocol error: {protocolError.String(3)}");
            if (_pending.TryGetValue(compilationId, out var completion))
            {
                completion.TrySetException(error);
            }
            else
            {
                logger.LogError("{Message}", error.Message);
            }
        }
    }

    /// <summary>The Dart runtime and Sass snapshot for this platform, downloaded on first use.</summary>
    private async Task<(string Dart, string Snapshot)> EnsureBinaryAsync(CancellationToken cancellationToken)
    {
        var overridden = Environment.GetEnvironmentVariable("NITE_SASS_PATH");
        if (!string.IsNullOrEmpty(overridden))
        {
            // A dart-sass folder, as in the standalone release archives (src/dart and src/sass.snapshot).
            return (Path.Combine(overridden, "src", OperatingSystem.IsWindows() ? "dart.exe" : "dart"), Path.Combine(overridden, "src", "sass.snapshot"));
        }

        var version = InstalledVersion()
            ?? throw new CssException("Sass stylesheets need the sass-embedded (or sass) package. Add it to \"devDependencies\" in nite.json.");
        var platform = PlatformName();
        var package = $"sass-embedded-{platform}";
        var directory = Path.Combine(NiteDirectories.Current.Cache, "sass", $"{version}-{platform}");
        var dart = Path.Combine("dart-sass", "src", OperatingSystem.IsWindows() ? "dart.exe" : "dart");
        try
        {
            await PlatformPackage.EnsureAsync(http, NiteSettings.Load(project.Directory).Registry, package, version, directory, dart, cancellationToken);
        }
        catch (NitePackageException ex)
        {
            throw new CssException($"Could not get the Sass compiler ({package}@{version}): {ex.Message}");
        }
        return (Path.Combine(directory, dart), Path.Combine(directory, "dart-sass", "src", "sass.snapshot"));
    }

    private string? InstalledVersion()
    {
        foreach (var name in new[] { "sass-embedded", "sass" })
        {
            var manifest = Path.Combine(project.Directory, "node_modules", name, "package.json");
            if (File.Exists(manifest))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                return document.RootElement.GetProperty("version").GetString();
            }
        }
        return null;
    }

    internal static string PlatformName()
    {
        var os = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsAndroid() ? "android"
            : OperatingSystem.IsLinux() ? (IsMusl() ? "linux-musl" : "linux")
            : throw new PlatformNotSupportedException("There is no Sass binary for this operating system. Set NITE_SASS_PATH to a dart-sass folder.");
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.RiscV64 => "riscv64",
            _ => throw new PlatformNotSupportedException($"There is no Sass binary for {RuntimeInformation.OSArchitecture}. Set NITE_SASS_PATH to a dart-sass folder."),
        };
        return $"{os}-{arch}";
    }

    private static bool IsMusl() =>
        Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_process is { } process)
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
        await Task.CompletedTask;
    }
}
