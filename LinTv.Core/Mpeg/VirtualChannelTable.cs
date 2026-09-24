using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace LinTv.Core.Mpeg
{
    /// One entry of an ATSC A/65 Virtual Channel Table.
    public sealed record VctChannel(
        string ShortName, int Major, int Minor,
        byte ModulationMode, ushort ChannelTsid, ushort ProgramNumber,
        bool Hidden, byte ServiceType, ushort SourceId);

    public sealed record VctSection(
        byte TableId, ushort TransportStreamId, byte Version,
        byte SectionNumber, byte LastSectionNumber,
        IReadOnlyList<VctChannel> Channels);

    public static class VirtualChannelTable
    {
        /// PSIP base PID carrying the MGT, VCT and STT.
        public const ushort BasePid = 0x1FFB;
        public const byte TerrestrialTableId = 0xC8, CableTableId = 0xC9;

        public const byte ModulationAnalog = 0x01, Modulation8Vsb = 0x04;
        public const byte ServiceTypeDigitalTv = 0x02, ServiceTypeAudio = 0x03;

        /// Parses a CRC-validated TVCT/CVCT section (A/65 section 6.3).
        public static bool TryParse(ReadOnlySpan<byte> s, [NotNullWhen(true)] out VctSection? vct)
        {
            vct = null;
            if (s.Length < 16 || (s[0] != TerrestrialTableId && s[0] != CableTableId)) return false;
            if ((s[5] & 0x01) == 0) return false; // current_next_indicator: not yet in effect

            int end = s.Length - 4; // trailing CRC_32
            int count = s[9];
            int pos = 10;
            var channels = new List<VctChannel>(count);

            for (int i = 0; i < count; i++)
            {
                if (pos + 32 > end) return false;
                var e = s.Slice(pos, 32);

                // Layout of each 32-byte entry:
                //   short_name        7 x UTF-16BE
                //   reserved(4) major(10) minor(10) modulation_mode(8)
                //   carrier_frequency(32) channel_TSID(16) program_number(16)
                //   ETM(2) access(1) hidden(1) rsvd(2) hide_guide(1) rsvd(3) service_type(6)
                //   source_id(16) rsvd(6) descriptors_length(10)
                var name = Encoding.BigEndianUnicode.GetString(e[..14]).Trim('\0', ' ');
                int major = ((e[14] & 0x0F) << 6) | (e[15] >> 2);
                int minor = ((e[15] & 0x03) << 8) | e[16];
                int descriptorsLength = ((e[30] & 0x03) << 8) | e[31];

                channels.Add(new VctChannel(
                    name, major, minor,
                    ModulationMode: e[17],
                    ChannelTsid: (ushort)((e[22] << 8) | e[23]),
                    ProgramNumber: (ushort)((e[24] << 8) | e[25]),
                    Hidden: (e[26] & 0x10) != 0,
                    ServiceType: (byte)(e[27] & 0x3F),
                    SourceId: (ushort)((e[28] << 8) | e[29])));

                pos += 32 + descriptorsLength;
            }

            vct = new VctSection(
                TableId: s[0],
                TransportStreamId: (ushort)((s[3] << 8) | s[4]),
                Version: (byte)((s[5] >> 1) & 0x1F),
                SectionNumber: s[6],
                LastSectionNumber: s[7],
                channels);
            return true;
        }
    }

    /// Accumulates the sections of one VCT until every section of the current version is in.
    public sealed class VctCollector
    {
        private readonly SortedDictionary<byte, VctSection> _sections = new();
        private byte? _tableId;
        private int _version = -1;
        private int _lastSection = -1;

        public bool IsComplete => _lastSection >= 0 && _sections.Count == _lastSection + 1;

        public ushort TransportStreamId { get; private set; }

        public IEnumerable<VctChannel> Channels => _sections.Values.SelectMany(s => s.Channels);

        public void Add(VctSection section)
        {
            // A mux may carry both a TVCT and a CVCT; stick with whichever arrives first.
            _tableId ??= section.TableId;
            if (section.TableId != _tableId) return;

            if (section.Version != _version)
            {
                _sections.Clear();
                _version = section.Version;
                _lastSection = section.LastSectionNumber;
                TransportStreamId = section.TransportStreamId;
            }

            _sections[section.SectionNumber] = section;
        }
    }
}
