using LinTv.Core.Domain;

namespace LinTv.Core.Services
{
    public interface IEpgScanner
    {
        ScanStatus Status { get; }

        /// Starts a guide scan in the background. False if one is already running.
        bool TryStartScan(CancellationToken ct);

        /// Runs a guide scan of every multiplex in the lineup and merges the result into the
        /// guide store. Throws InvalidOperationException if a scan is already running.
        Task<IReadOnlyList<GuideEvent>> ScanAsync(CancellationToken ct);
    }
}
