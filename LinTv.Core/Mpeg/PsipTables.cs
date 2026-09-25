using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace LinTv.Core.Mpeg
{
    /// One table_types entry of the Master Guide Table.
    public sealed record MgtEntry(ushort TableType, ushort Pid);

    /// One event of an Event Information Table. StartGps is GPS seconds since 1980-01-06.
    public sealed record EitEvent(
        ushort SourceId, ushort EventId,
        uint StartGps, int LengthSeconds,
        int EtmLocation, string? Title);

    public sealed record EitSection(
        ushort SourceId, byte Version,
        byte SectionNumber, byte LastSectionNumber,
        IReadOnlyList<EitEvent> Events);

    /// ATSC A/65 PSIP tables used for the guide. All parsers expect CRC-validated sections
    /// from PsiSectionAssembler.
    public static class PsipTables
    {
        public const byte MgtTableId = 0xC7, EitTableId = 0xCB, EttTableId = 0xCC, SttTableId = 0xCD;

        /// MGT table_type ranges: EIT-0..127 and event ETT-0..127.
        public const ushort EitTypeFirst = 0x0100, EitTypeLast = 0x017F;
        public const ushort EttTypeFirst = 0x0200, EttTypeLast = 0x027F;

        public static readonly DateTimeOffset GpsEpoch = new(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);

        /// ETM_id linking an event to its ETT: source_id(16) | event_id(14) | 0b10.
        public static uint EventEtmId(ushort sourceId, ushort eventId) =>
            ((uint)sourceId << 16) | ((uint)eventId << 2) | 0x2;

        /// A/65 6.2. Entry: table_type(16) rsvd(3) PID(13) rsvd(3) version(5)
        /// number_bytes(32) rsvd(4) descriptors_length(12) descriptors.
        public static bool TryParseMgt(ReadOnlySpan<byte> s, [NotNullWhen(true)] out IReadOnlyList<MgtEntry>? entries)
        {
            entries = null;
            if (s.Length < 17 || s[0] != MgtTableId) return false;

            int end = s.Length - 4;
            int count = (s[9] << 8) | s[10];
            int pos = 11;
            var list = new List<MgtEntry>(count);

            for (int i = 0; i < count; i++)
            {
                if (pos + 11 > end) return false;
                var type = (ushort)((s[pos] << 8) | s[pos + 1]);
                var pid = (ushort)(((s[pos + 2] & 0x1F) << 8) | s[pos + 3]);
                int descriptorsLength = ((s[pos + 9] & 0x0F) << 8) | s[pos + 10];
                list.Add(new MgtEntry(type, pid));
                pos += 11 + descriptorsLength;
            }

            entries = list;
            return true;
        }

        /// A/65 6.1: GPS_UTC_offset, the leap seconds to subtract from GPS time to get UTC.
        public static bool TryParseSttGpsUtcOffset(ReadOnlySpan<byte> s, out int gpsUtcOffset)
        {
            gpsUtcOffset = 0;
            if (s.Length < 20 || s[0] != SttTableId) return false;
            gpsUtcOffset = s[13];
            return true;
        }

        /// A/65 6.5. Event: rsvd(2) event_id(14) start_time(32) rsvd(2) ETM_location(2)
        /// length_in_seconds(20) title_length(8) title_text rsvd(4) descriptors_length(12) descriptors.
        public static bool TryParseEit(ReadOnlySpan<byte> s, [NotNullWhen(true)] out EitSection? eit)
        {
            eit = null;
            if (s.Length < 14 || s[0] != EitTableId) return false;
            if ((s[5] & 0x01) == 0) return false; // current_next_indicator

            var sourceId = (ushort)((s[3] << 8) | s[4]);
            int end = s.Length - 4;
            int count = s[9];
            int pos = 10;
            var events = new List<EitEvent>(count);

            for (int i = 0; i < count; i++)
            {
                if (pos + 10 > end) return false;
                var eventId = (ushort)(((s[pos] & 0x3F) << 8) | s[pos + 1]);
                uint start = BinaryPrimitives.ReadUInt32BigEndian(s[(pos + 2)..]);
                int etmLocation = (s[pos + 6] >> 4) & 0x03;
                int length = ((s[pos + 6] & 0x0F) << 16) | (s[pos + 7] << 8) | s[pos + 8];
                int titleLength = s[pos + 9];
                pos += 10;

                if (pos + titleLength + 2 > end) return false;
                var title = MultipleStringStructure.Decode(s.Slice(pos, titleLength));
                pos += titleLength;

                int descriptorsLength = ((s[pos] & 0x0F) << 8) | s[pos + 1];
                pos += 2 + descriptorsLength;

                events.Add(new EitEvent(sourceId, eventId, start, length, etmLocation, title));
            }

            eit = new EitSection(sourceId, (byte)((s[5] >> 1) & 0x1F), s[6], s[7], events);
            return true;
        }

        /// A/65 6.6: ETM_id(32) then extended_text_message (a multiple_string_structure).
        public static bool TryParseEtt(ReadOnlySpan<byte> s, out uint etmId, [NotNullWhen(true)] out string? text)
        {
            etmId = 0;
            text = null;
            if (s.Length < 18 || s[0] != EttTableId) return false;

            etmId = BinaryPrimitives.ReadUInt32BigEndian(s[9..]);
            text = MultipleStringStructure.Decode(s[13..^4]);
            return text is not null;
        }
    }

    /// A/65 6.10 multiple_string_structure: one or more languages, each made of segments.
    public static class MultipleStringStructure
    {
        private const byte ModeUtf16 = 0x3F;
        private const byte ModeLastUnicodePage = 0x33;

        /// The English string if present, otherwise the first decodable one. Returns null for
        /// Huffman-compressed text (A/65 Annex C), which isn't supported yet.
        public static string? Decode(ReadOnlySpan<byte> mss)
        {
            if (mss.IsEmpty) return null;

            int count = mss[0];
            int pos = 1;
            string? first = null;

            for (int i = 0; i < count; i++)
            {
                if (pos + 4 > mss.Length) break;
                bool english = mss[pos] == 'e' && mss[pos + 1] == 'n' && mss[pos + 2] == 'g';
                int segments = mss[pos + 3];
                pos += 4;

                var sb = new StringBuilder();
                bool decodable = true;

                for (int j = 0; j < segments; j++)
                {
                    if (pos + 3 > mss.Length) return first;
                    byte compression = mss[pos], mode = mss[pos + 1];
                    int length = mss[pos + 2];
                    pos += 3;

                    if (pos + length > mss.Length) return first;
                    var bytes = mss.Slice(pos, length);
                    pos += length;

                    if (compression != 0)
                        decodable = false;
                    else if (mode == ModeUtf16)
                        sb.Append(Encoding.BigEndianUnicode.GetString(bytes));
                    else if (mode <= ModeLastUnicodePage)
                        // Mode N: each byte is the low byte of a code point in Unicode page N
                        // (mode 0 is Latin-1).
                        foreach (var b in bytes) sb.Append((char)((mode << 8) | b));
                    else
                        decodable = false;
                }

                var text = decodable ? sb.ToString().Trim() : null;
                if (string.IsNullOrEmpty(text)) continue;
                if (english) return text;
                first ??= text;
            }

            return first;
        }
    }
}
