namespace COMPEL.Utilities;

/// <summary>
///     Bridges the host's logging abstraction to <see cref="Logger"/>, so the hosted services keep using <see cref="ILogger{TCategoryName}"/> while every entry lands in the same console and file format as the start-up messages.
///     Level filtering stays with the host's logging configuration; this provider formats whatever reaches it.
/// </summary>
internal sealed class LoggerProvider(Logger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(logger, LogCategory.Resolve(categoryName));

    public void Dispose()
    {
    }

    private sealed class CategoryLogger(Logger logger, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventID, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);

            // WILLOWMAKER Records A Failure As The Exception's Type And Message On The Same Line, So The Same Shape Is Used Here Rather Than A Stack Trace
            if (exception is not null)
                message = $"{message} :: {exception.GetType().Name} :: {exception.Message}";

            logger.Log(category, message);
        }
    }
}
