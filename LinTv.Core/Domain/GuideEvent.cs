using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    public sealed record GuideEvent(
        string ChannelId, ushort EventId,
        DateTimeOffset Start, TimeSpan Duration,
        string Title, string? Description);
}
