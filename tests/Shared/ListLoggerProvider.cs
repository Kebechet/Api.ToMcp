using Microsoft.Extensions.Logging;

namespace Api.ToMcp.Tests.Shared;

/// <summary>
/// Collects every log entry the logging filters let through into a list the test reads. Linked into each test project
/// that needs it rather than copied.
/// Adds under a lock on the list, since a running host logs from several threads; a test that reads while the host is
/// still logging has to take the same lock.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _logs;

    public ListLoggerProvider(List<(LogLevel Level, string Message)> logs)
    {
        _logs = logs;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ListLogger(_logs);
    }

    public void Dispose()
    {
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _logs;

        public ListLogger(List<(LogLevel Level, string Message)> logs)
        {
            _logs = logs;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_logs)
            {
                _logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
