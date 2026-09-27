using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    /// Snapshot for the UI and /stream/status. Since is when the current tune started.
    public sealed record TunerState(long? FrequencyHz, TunerPriority? Priority, int Holders, DateTimeOffset? Since);
}
