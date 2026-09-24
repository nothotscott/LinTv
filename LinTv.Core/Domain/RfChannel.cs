using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    public sealed record RfChannel(int Number, long FrequencyHz);
}
