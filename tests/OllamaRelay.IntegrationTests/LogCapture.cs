using System.Collections.Concurrent;

namespace OllamaRelay.IntegrationTests;

/// <summary>Captures every log line at every level, including exceptions, for the no-content assertions.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Lines);

    public string All => string.Join('\n', Lines);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lines.Enqueue($"[scope] {category}: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (k, v) in pairs)
                {
                    lines.Enqueue($"  {k}={v}");
                }
            }
        }
    }
}
