using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TimePlayer.Wpf.Logging;

/// <summary>
/// Writes the log to logs\timeplayer-YYYYMMDD.log (one file per day, the last 7 kept), so a problem
/// can be reported with the log attached. Writes happen on a background thread.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int DaysToKeep = 7;
    private readonly string _folder;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string folder)
    {
        _folder = folder;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "TimePlayer log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            DeleteOldLogs();
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    File.AppendAllText(Path.Combine(_folder, $"timeplayer-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // A locked or full disk must never take the player down.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No writable log folder: logging silently stays in memory only.
        }
    }

    private void DeleteOldLogs()
    {
        foreach (var old in Directory.GetFiles(_folder, "timeplayer-*.log").OrderByDescending(f => f).Skip(DaysToKeep))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel,-11} {_shortCategory}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception is not null)
                line += exception + Environment.NewLine;
            provider.Enqueue(line);
        }
    }
}
