namespace LinTv.Core.Domain
{
    /// Progress of a channel or EPG scan. Shaped to map onto HDHomeRun's lineup_status.json (ScanInProgress / Progress / Found).
    public sealed record ScanStatus(
        bool InProgress, int ProgressPercent, int Found,
        DateTimeOffset? LastCompleted, string? LastError)
    {
        public static ScanStatus Idle { get; } = new(false, 0, 0, null, null);
    }
}
