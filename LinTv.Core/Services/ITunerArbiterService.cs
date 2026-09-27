using LinTv.Core.Domain;

namespace LinTv.Core.Services
{
    public interface ITunerArbiterService
    {
        /// Tunes (or shares the current tune) and waits for a lock. Always release the lease with
        /// ReleaseAsync in a finally, and link your reads to lease.Revoked.
        Task<TunerLease> AcquireAsync(long frequencyHz, TunerPriority priority, CancellationToken ct);

        /// Like AcquireAsync, but succeeds without a signal lock, so a weak channel can still be
        /// measured.
        Task<TuneResult> AcquireForMeasurementAsync(long frequencyHz, CancellationToken ct);

        /// Returns a lease. Idempotent, and a no-op for leases revoked by ForceReleaseAsync.
        Task ReleaseAsync(TunerLease lease);

        /// Revokes every outstanding lease (cancelling their Revoked tokens) and frees the tuner,
        /// for when a holder is stuck. Returns the number of leases revoked.
        Task<int> ForceReleaseAsync();

        TunerState State { get; }
    }
}
