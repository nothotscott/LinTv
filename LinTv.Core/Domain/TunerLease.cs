using LinTv.Core.Driver;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    /// A hold on the tuner. Revoked is cancelled if the tuner is force-released; holders should
    /// stop using Tuner when it fires.
    public sealed class TunerLease
    {
        private int _released;

        internal TunerLease(IDvbTuner tuner, long frequencyHz, TunerPriority priority, int session, CancellationToken revoked)
        {
            Tuner = tuner;
            FrequencyHz = frequencyHz;
            Priority = priority;
            Session = session;
            Revoked = revoked;
        }

        public IDvbTuner Tuner { get; }
        public long FrequencyHz { get; }
        public TunerPriority Priority { get; }
        public CancellationToken Revoked { get; }

        internal int Session { get; }

        /// True the first time only.
        internal bool MarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;
    }
}
