using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    public sealed record SignalStatus(bool Locked, double StrengthPercent, double SnrDb);
}
