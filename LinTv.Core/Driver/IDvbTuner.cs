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

        /// Raw 188-byte TS packets for the whole RF multiplex (all subchannels).
        IAsyncEnumerable<ReadOnlyMemory<byte>> ReadTransportStreamAsync(CancellationToken ct);
    }
}
