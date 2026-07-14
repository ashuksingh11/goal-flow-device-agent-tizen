using Microsoft.Extensions.Logging;

namespace GoalFlow.Device;

/// <summary>
/// TIZEN EDGE: routes <c>Microsoft.Extensions.Logging</c> to Tizen's dlog
/// (<c>Tizen.Log</c>). A headless <c>ServiceApplication</c> has no stdout
/// console, so the portable core's <c>AddConsole()</c> logging crashes on the
/// Hub — this provider sends every log line to <c>dlogutil</c> under the
/// <see cref="Tag"/> tag instead. View with: <c>dlogutil GOALFLOW</c>.
/// </summary>
public sealed class DlogLoggerProvider : ILoggerProvider
{
    /// <summary>dlog tag — filter with <c>dlogutil GOALFLOW</c>.</summary>
    public const string Tag = "GOALFLOW";

    public ILogger CreateLogger(string categoryName) => new DlogLogger(categoryName);

    public void Dispose()
    {
    }
}

internal sealed class DlogLogger : ILogger
{
    private readonly string _category;

    public DlogLogger(string category)
    {
        // Last namespace segment keeps dlog lines short and readable.
        var dot = category.LastIndexOf('.');
        _category = dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = $"{message}\n{exception}";
        }

        var line = $"{_category}: {message}";
        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                Tizen.Log.Debug(DlogLoggerProvider.Tag, line);
                break;
            case LogLevel.Information:
                Tizen.Log.Info(DlogLoggerProvider.Tag, line);
                break;
            case LogLevel.Warning:
                Tizen.Log.Warn(DlogLoggerProvider.Tag, line);
                break;
            case LogLevel.Error:
                Tizen.Log.Error(DlogLoggerProvider.Tag, line);
                break;
            case LogLevel.Critical:
                Tizen.Log.Fatal(DlogLoggerProvider.Tag, line);
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
