namespace LinTv.Core.Domain
{
    /// Shaped to map onto HDHomeRun's lineup_status.json (ScanInProgress / Progress / Found).
    public sealed record ChannelScanStatus(
        bool InProgress, int ProgressPercent, int Found,
        DateTimeOffset? LastCompleted, string? LastError)
    {
        public static ChannelScanStatus Idle { get; } = new(false, 0, 0, null, null);
    }
}
