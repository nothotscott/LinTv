using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Writers
{
    public interface IM3uWriter
    {
        string Write(IReadOnlyList<VirtualChannel> channels, string baseUrl);
    }
}
