using LinTv.Core.Domain;

namespace LinTv.Core.Services
{
    public interface ISignalMeter
    {
        /// Tunes to the channel (or shares the tuner if it's already there), samples the signal
        /// for <paramref name="duration"/>, then releases the tuner. Works without a lock.
        /// Throws TunerBusyException if the tuner is in use on another frequency.
        Task<SignalReport> MeasureAsync(VirtualChannel channel, TimeSpan duration, CancellationToken ct);
    }
}
