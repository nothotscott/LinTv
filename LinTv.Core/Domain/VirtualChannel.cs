using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Domain
{
    /// One place a virtual channel can be received. The same major.minor can be on several RF
    /// channels (a translator, or a neighbouring market); Index ranks them by signal quality at
    /// scan time. Index 0 is the primary: the best connection, used by the lineups and the guide.
    public sealed record VirtualChannel(
        int Major, int Minor, string ShortName,
        int RfChannel, long FrequencyHz,
        ushort ProgramNumber, ushort SourceId,
        int Index = 0,
        double SignalStrengthPercent = 0, double SignalSnrDb = 0)
    {
        public string Id => $"{Major}.{Minor}";

        public bool IsPrimary => Index == 0;
    }
}
