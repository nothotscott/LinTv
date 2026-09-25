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

        /// Raw 188-byte TS packets for the whole RF multiplex (all subchannels).
        IAsyncEnumerable<ReadOnlyMemory<byte>> ReadTransportStreamAsync(CancellationToken ct);
    }
}
