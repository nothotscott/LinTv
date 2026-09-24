using LinTv.Core.Domain;

namespace LinTv.Core.Services
{
    public interface IChannelScanner
    {
        ChannelScanStatus Status { get; }

        /// Starts a scan in the background. False if one is already running.
        bool TryStartScan(CancellationToken ct);

        /// Runs a scan to completion and saves the result to the channel store.
        /// Throws InvalidOperationException if a scan is already running.
        Task<IReadOnlyList<VirtualChannel>> ScanAsync(CancellationToken ct);
    }
}
