using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Exceptions
{
    public class TunerProblemException(string message) : Exception(message);
}
