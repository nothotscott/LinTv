using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    public sealed record VirtualChannel(
        int Major, int Minor, string ShortName,
        int RfChannel, long FrequencyHz,
        ushort ProgramNumber, ushort SourceId)
    {
        public string Id => $"{Major}.{Minor}";
    }
}
