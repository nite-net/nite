using Microsoft.Extensions.Hosting;

namespace Nite.Dev;

/// <summary>
/// Signals that every hosted service has started, OpenTelemetry's included, so the dev server's
/// startup trace has a listener. Requests that arrive first wait for startup as they always have.
/// </summary>
internal sealed class NiteHostStarted : IHostedLifecycleService
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Task => _started.Task;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
