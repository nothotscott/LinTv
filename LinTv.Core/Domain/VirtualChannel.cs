using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    /// One place a virtual channel can be received. The same major.minor can be on several RF
    /// channels (a translator, or a neighbouring market); Index is this entry's position among
    /// them, in scan order. Index 0 is the primary, used by the lineups and the guide.
    public sealed record VirtualChannel(
        int Major, int Minor, string ShortName,
        int RfChannel, long FrequencyHz,
        ushort ProgramNumber, ushort SourceId,
        int Index = 0)
    {
        public string Id => $"{Major}.{Minor}";

        public bool IsPrimary => Index == 0;
    }
}
