using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Nite.Cli.Infrastructure;

/// <summary>Renders the library's log messages in the CLI's style instead of the default console logger's.</summary>
internal sealed class SpectreLoggerProvider(LogLevel minimumLevel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SpectreLogger(minimumLevel);

    public void Dispose()
    {
    }

    private sealed class SpectreLogger(LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = Markup.Escape(formatter(state, exception));
            var line = logLevel switch
            {
                LogLevel.Trace or LogLevel.Debug => $"[grey]  {message}[/]",
                LogLevel.Information => $"[grey]›[/] {message}",
                LogLevel.Warning => $"[yellow]warn[/]  {message}",
                _ => $"[red]error[/] {message}",
            };
            AnsiConsole.MarkupLine(line);
        }
    }
}
