using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace ChestTracker.Server.Infrastructure;

/// <summary>Daily rolling text log (logs/server-yyyyMMdd.log) written by one background writer.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public FileLoggerProvider(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(n, _queue.Writer));

    private async Task WriteLoopAsync()
    {
        string? currentDay = null;
        StreamWriter? writer = null;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync())
            {
                var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (day != currentDay)
                {
                    if (writer is not null) await writer.DisposeAsync();
                    writer = new StreamWriter(new FileStream(Path.Combine(_directory, $"server-{day}.log"), FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
                    currentDay = day;
                    CleanUp();
                }

                await writer!.WriteLineAsync(line);
            }
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
        }
    }

    private void CleanUp()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_directory).GetFiles("server-*.log")
                         .Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-_retentionDays)))
            {
                f.Delete();
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, ChannelWriter<string> queue) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "INFO",
            };
            var text = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{level}] {category}: {formatter(state, exception)}";
            if (exception is not null) text += Environment.NewLine + exception;
            queue.TryWrite(text);
        }
    }
}

/// <summary>One line per HTTP request. Query strings are dropped so SignalR access tokens never reach the log.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var path = context.Request.Path.Value ?? "";
            // Long-polling and health checks are noisy; log them only when they fail.
            var noisy = path.StartsWith(Protocol.Routes.PartyHub, StringComparison.OrdinalIgnoreCase) || path == "/health";
            if (!noisy || context.Response.StatusCode >= 400)
            {
                logger.LogInformation("{Method} {Path} -> {Status} in {Ms:0} ms from {Ip}", context.Request.Method, path,
                    context.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    context.Connection.RemoteIpAddress);
            }
        }
    }
}
