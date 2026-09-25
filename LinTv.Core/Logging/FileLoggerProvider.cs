using LinTv.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LinTv.Core.Logging
{
    /// Daily log files in {StorageDirectory}/logs, written on a background task so logging
    /// never blocks the tuner or stream loops. Level filtering comes from the normal
    /// "Logging" configuration; "Logging:File:LogLevel" overrides it for this sink only.
    [ProviderAlias("File")]
    public sealed partial class FileLoggerProvider : ILoggerProvider, ILogStore
    {
        /// If the disk stalls, drop the oldest lines rather than grow without bound or block callers.
        private const int QueueCapacity = 10_000;

        private readonly Channel<string> _queue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Task _writer;
        private readonly int _retentionDays;

        public string LogDirectory { get; }

        public FileLoggerProvider(IOptions<LinTvConfiguration> config)
        {
            LogDirectory = Path.Combine(config.Value.StorageDirectory, "logs");
            _retentionDays = Math.Max(1, config.Value.LogRetentionDays);
            _writer = Task.Run(WriteLoopAsync);
        }

        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

        internal void Enqueue(string entry) => _queue.Writer.TryWrite(entry);

        private async Task WriteLoopAsync()
        {
            StreamWriter? writer = null;
            string? currentPath = null;
            try
            {
                while (await _queue.Reader.WaitToReadAsync())
                {
                    try
                    {
                        var path = PathFor(DateTime.Now);
                        if (path != currentPath)
                        {
                            writer?.Dispose();
                            Directory.CreateDirectory(LogDirectory);
                            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                            currentPath = path;
                            DeleteExpired();
                        }

                        // Drain whatever is queued, then flush once per batch.
                        while (_queue.Reader.TryRead(out var entry))
                            writer!.Write(entry);
                        writer!.Flush();
                    }
                    catch (Exception ex)
                    {
                        // Can't log a logging failure; stderr ends up in journald under systemd.
                        Console.Error.WriteLine($"LinTv file log: {ex.Message}");
                        writer?.Dispose();
                        writer = null;
                        currentPath = null;
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                }
            }
            finally
            {
                writer?.Dispose();
            }
        }

        private string PathFor(DateTime local) =>
            Path.Combine(LogDirectory, $"lintv-{local.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

        private void DeleteExpired()
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "lintv-*.log"))
            {
                var match = LogFileName().Match(Path.GetFileName(file));
                if (match.Success
                    && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }

        public IReadOnlyList<LogFileInfo> ListFiles()
        {
            if (!Directory.Exists(LogDirectory)) return [];

            return new DirectoryInfo(LogDirectory).EnumerateFiles("lintv-*.log")
                .Where(f => LogFileName().IsMatch(f.Name))
                .OrderByDescending(f => f.Name)
                .Select(f => new LogFileInfo(f.Name, f.Length, f.LastWriteTimeUtc))
                .ToList();
        }

        public async Task<IReadOnlyList<string>?> ReadTailAsync(string? file, int lines, CancellationToken ct)
        {
            file ??= ListFiles().FirstOrDefault()?.Name;
            // The name pattern also rules out path traversal.
            if (file is null || !LogFileName().IsMatch(file)) return null;

            var path = Path.Combine(LogDirectory, file);
            if (!File.Exists(path)) return null;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // Read backwards in growing blocks until there are enough lines: a Trace-level day can
            // be large, and only the tail is wanted.
            long block = 64 * 1024;
            while (true)
            {
                long start = Math.Max(0, stream.Length - block);
                var buffer = new byte[stream.Length - start];
                stream.Seek(start, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(buffer, ct);

                var all = Encoding.UTF8.GetString(buffer).Split('\n');
                if (start > 0) all = all[1..]; // first line is probably partial
                var complete = all.Length > 0 && all[^1].Length == 0 ? all[..^1] : all;

                if (start == 0 || complete.Length >= lines)
                    return complete.TakeLast(lines).Select(l => l.TrimEnd('\r')).ToList();

                block *= 4;
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            _writer.Wait(TimeSpan.FromSeconds(2));
        }

        [GeneratedRegex(@"^lintv-(\d{8})\.log$")]
        private static partial Regex LogFileName();

        private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            // Level filtering is done by the logging framework's rules before Log is called.
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var sb = new StringBuilder(256);
                sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                  .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
                  .Append(category).Append(": ")
                  .Append(formatter(state, exception))
                  .Append('\n');

                if (exception is not null)
                {
                    // Indent so a tail or grep can tell continuation lines from new entries.
                    foreach (var line in exception.ToString().Split('\n'))
                        sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
                }

                provider.Enqueue(sb.ToString());
            }

            private static string Abbreviate(LogLevel level) => level switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };
        }
    }
}
