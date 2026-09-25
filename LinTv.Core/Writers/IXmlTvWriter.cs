using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Writers
{
    public interface IXmlTvWriter
    {
        /// <param name="mappings">User channel-map entries, added as extra display-names.</param>
        string Write(IReadOnlyList<VirtualChannel> channels, IReadOnlyList<GuideEvent> events,
            IReadOnlyList<ChannelMapping> mappings);
    }
}
