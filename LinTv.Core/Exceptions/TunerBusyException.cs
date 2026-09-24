using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Exceptions
{
    public sealed class TunerBusyException(string message) : Exception(message);
}
