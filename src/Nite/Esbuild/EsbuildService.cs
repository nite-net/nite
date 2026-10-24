using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nite.Esbuild;

/// <summary>
/// A client for a long-lived <c>esbuild --service</c> process. This is the same channel esbuild's
/// own JS API uses, so one process serves every transform and build for the life of the app.
/// The process is (re)started lazily, so a crash only costs the requests that were in flight.
/// </summary>
public sealed class EsbuildService : IAsyncDisposable
{
    private readonly string _binaryPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Dictionary<string, object?>>> _pending = new();
    private Process? _process;
    private TaskCompletionSource? _handshake;
    private int _nextId;
    private int _nextBuildKey;
    private bool _disposed;

    public EsbuildService(string binaryPath, ILogger<EsbuildService>? logger = null)
    {
        _binaryPath = binaryPath;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Transforms a single file's contents, like <c>esbuild.transform</c>.</summary>
    public async Task<EsbuildTransformResult> TransformAsync(string input, IEnumerable<string> flags, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new Dictionary<string, object?>
        {
            ["command"] = "transform",
            ["flags"] = flags.Append("--log-level=silent").Append("--color=false").ToList(),
            ["inputFS"] = false,
            ["input"] = Encoding.UTF8.GetBytes(input),
        }, cancellationToken);

        var errors = EsbuildMessage.ListFrom(response.GetValueOrDefault("errors"));
        var warnings = EsbuildMessage.ListFrom(response.GetValueOrDefault("warnings"));
        if (errors.Count > 0)
        {
            throw new EsbuildException("Transform failed", errors);
        }

        if (response.GetValueOrDefault("codeFS") is true || response.GetValueOrDefault("mapFS") is true)
        {
            // esbuild only spills results to disk when the host asks it to, which we never do.
            throw new InvalidOperationException("esbuild returned a file-system result for an in-memory transform.");
        }

        return new EsbuildTransformResult(
            AsString(response.GetValueOrDefault("code")),
            AsString(response.GetValueOrDefault("map")),
            warnings);
    }

    /// <summary>Runs a one-shot build, like <c>esbuild.build</c> without plugins.</summary>
    public async Task<EsbuildBuildResult> BuildAsync(EsbuildBuildOptions options, CancellationToken cancellationToken = default)
    {
        var flags = options.Flags.ToList();
        flags.Add("--log-level=silent");
        flags.Add("--color=false");
        if (options.Metafile)
        {
            flags.Add("--metafile");
        }

        var response = await SendAsync(new Dictionary<string, object?>
        {
            ["command"] = "build",
            ["key"] = Interlocked.Increment(ref _nextBuildKey),
            ["entries"] = options.Entries.Select(e => new List<object?> { e.OutputName ?? "", e.Path }).ToList(),
            ["flags"] = flags,
            ["write"] = options.Write,
            ["stdinContents"] = null,
            ["stdinResolveDir"] = null,
            ["absWorkingDir"] = options.WorkingDirectory,
            ["nodePaths"] = new List<object?>(),
            ["context"] = false,
        }, cancellationToken);

        var errors = EsbuildMessage.ListFrom(response.GetValueOrDefault("errors"));
        var warnings = EsbuildMessage.ListFrom(response.GetValueOrDefault("warnings"));
        if (errors.Count > 0)
        {
            throw new EsbuildException("Build failed", errors);
        }

        var outputFiles = new List<EsbuildOutputFile>();
        if (response.GetValueOrDefault("outputFiles") is List<object?> files)
        {
            foreach (var file in files.OfType<Dictionary<string, object?>>())
            {
                outputFiles.Add(new EsbuildOutputFile(
                    AsString(file.GetValueOrDefault("path")),
                    file.GetValueOrDefault("contents") as byte[] ?? Encoding.UTF8.GetBytes(AsString(file.GetValueOrDefault("contents")))));
            }
        }

        var metafile = AsString(response.GetValueOrDefault("metafile"));
        return new EsbuildBuildResult(warnings, outputFiles, metafile.Length > 0 ? metafile : null);
    }

    private async Task<Dictionary<string, object?>> SendAsync(Dictionary<string, object?> request, CancellationToken cancellationToken)
    {
        var process = await EnsureStartedAsync(cancellationToken);
        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var completion = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await WriteAsync(process, EsbuildPacket.Encode(id, isRequest: true, request), cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var response = await completion.Task;
            if (response.GetValueOrDefault("error") is string error && error.Length > 0)
            {
                throw new EsbuildException(error, []);
            }
            return response;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(Process process, byte[] packet, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var stdin = process.StandardInput.BaseStream;
            await stdin.WriteAsync(packet, cancellationToken);
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
        if (_process is { HasExited: false } running && _handshake is { Task.IsCompletedSuccessfully: true })
        {
            return running;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is not { HasExited: false })
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(_binaryPath)
                    {
                        ArgumentList = { $"--service={EsbuildBinary.Version}" },
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                    EnableRaisingEvents = true,
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger.LogWarning("esbuild: {Message}", e.Data);
                    }
                };
                process.Start();
                process.BeginErrorReadLine();

                _handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _process = process;
                _ = Task.Run(() => ReadLoopAsync(process, _handshake));
                _logger.LogDebug("Started esbuild {Version} service (pid {Pid})", EsbuildBinary.Version, process.Id);
            }

            await _handshake!.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return _process;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, TaskCompletionSource handshake)
    {
        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var used = 0;
        var isFirstPacket = true;
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
                while (offset + 4 <= used)
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
                    if (offset + 4 + length > used)
                    {
                        // Grow eagerly so a single large packet (a big build's output files) fits.
                        if (4 + length > buffer.Length)
                        {
                            Array.Resize(ref buffer, 4 + length);
                        }
                        break;
                    }

                    var body = buffer.AsSpan(offset + 4, length);
                    offset += 4 + length;

                    if (isFirstPacket)
                    {
                        // The service announces its version before anything else.
                        isFirstPacket = false;
                        var version = Encoding.ASCII.GetString(body);
                        if (version != EsbuildBinary.Version)
                        {
                            throw new InvalidOperationException($"esbuild binary version {version} does not match the expected {EsbuildBinary.Version}.");
                        }
                        handshake.TrySetResult();
                        continue;
                    }

                    var (id, isRequest, value) = EsbuildPacket.Decode(body);
                    if (isRequest)
                    {
                        // Without plugins or watch mode the only thing the service asks is "ping".
                        var reply = EsbuildPacket.Encode(id, isRequest: false, new Dictionary<string, object?>());
                        _ = WriteAsync(process, reply, CancellationToken.None);
                    }
                    else if (_pending.TryGetValue(id, out var completion))
                    {
                        completion.TrySetResult(value as Dictionary<string, object?> ?? []);
                    }
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

        var reason = failure?.Message ?? "the esbuild service exited";
        if (!_disposed)
        {
            _logger.LogWarning("esbuild service stopped: {Reason}", reason);
        }

        var error = new EsbuildException($"The esbuild service stopped: {reason}", []);
        handshake.TrySetException(error);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(error);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string AsString(object? value) => value switch
    {
        string s => s,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => "",
    };

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
                // Closing stdin is esbuild's signal to shut down cleanly.
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

public sealed record EsbuildEntry(string Path, string? OutputName = null);

public sealed record EsbuildBuildOptions
{
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyList<EsbuildEntry> Entries { get; init; }
    public IReadOnlyList<string> Flags { get; init; } = [];
    public bool Write { get; init; } = true;
    public bool Metafile { get; init; }
}

public sealed record EsbuildTransformResult(string Code, string Map, IReadOnlyList<EsbuildMessage> Warnings);

public sealed record EsbuildOutputFile(string Path, byte[] Contents);

public sealed record EsbuildBuildResult(IReadOnlyList<EsbuildMessage> Warnings, IReadOnlyList<EsbuildOutputFile> OutputFiles, string? Metafile);
