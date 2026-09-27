using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    /// Signal is the reading at tune time. TimeToLock is null if there was no lock, or if the
    /// tune was shared (Shared = another holder already had the tuner on this frequency).
    public sealed record TuneResult(TunerLease Lease, SignalStatus Signal, TimeSpan? TimeToLock, bool Shared);
}
