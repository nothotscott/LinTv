namespace LinTv.Core.Logging
{
    public sealed record LogFileInfo(string Name, long SizeBytes, DateTimeOffset LastWritten);

    /// Read side of the file log sink.
    public interface ILogStore
    {
        /// Log files, newest first.
        IReadOnlyList<LogFileInfo> ListFiles();

        /// The last <paramref name="lines"/> lines of <paramref name="file"/> (default: the newest).
        /// Null if the file doesn't exist or isn't a log file name.
        Task<IReadOnlyList<string>?> ReadTailAsync(string? file, int lines, CancellationToken ct);
    }
}
