using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Driver
{
    public interface IDvbTuner
    {
        Task TuneAsync(long frequencyHz, CancellationToken ct);
        Task<SignalStatus> WaitForLockAsync(TimeSpan timeout, CancellationToken ct);

        /// Current lock state, strength and SNR of the frontend. Unlocked zeros if never tuned.
        SignalStatus ReadSignalStatus();

        /// Raw 188-byte TS packets for the whole RF multiplex (all subchannels). Chunks may be
        /// empty: when no data arrives, an idle tick is yielded at least every ~500 ms so callers
        /// can check deadlines, and cancellation is honoured even when the signal is gone.
        IAsyncEnumerable<ReadOnlyMemory<byte>> ReadTransportStreamAsync(CancellationToken ct);
    }
}
