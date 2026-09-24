using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Writers
{
    public interface IXmlTvWriter
    {
        string Write(IReadOnlyList<VirtualChannel> channels, IReadOnlyList<GuideEvent> events);
    }
}
