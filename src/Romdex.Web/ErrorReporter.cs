using Microsoft.JSInterop;

namespace Romdex.Web;

/// <summary>
/// The errors the app couldn't handle, which Blazor logs as critical and shows its error banner for: said in that
/// banner, with their details, and sent to the server's log (js/errors.js), so they can be told apart and found later.
/// </summary>
public sealed class ErrorReporter : ILoggerProvider
{
    /// <summary>Set once the app is built; nothing is said before that.</summary>
    public static IJSInProcessRuntime? Js { get; set; }

    public ILogger CreateLogger(string categoryName) => new Logger();

    public void Dispose()
    {
    }

    /// <summary>What to say about it: a line, and the whole of it.</summary>
    public static (string Message, string Details) Describe(Exception exception)
    {
        var inner = exception;
        while (inner is AggregateException { InnerExceptions.Count: 1 } or System.Reflection.TargetInvocationException && inner.InnerException is not null)
            inner = inner.InnerException!;
        var line = inner.Message.Split('\n', 2)[0].Trim();
        var details = exception.ToString();
        return ($"{inner.GetType().Name}: {line}", details.Length > 4000 ? details[..4000] + "…" : details);
    }

    private sealed class Logger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Critical;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || Js is not { } js)
                return;
            var (message, details) = exception is null ? (formatter(state, null), formatter(state, null)) : Describe(exception);
            try
            {
                js.InvokeVoid("romdexError.show", message, details);
            }
            catch (JSException)
            {
                // The banner says its plain words without it.
            }
        }
    }
}
