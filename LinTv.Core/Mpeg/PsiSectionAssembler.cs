namespace LinTv.Core.Mpeg
{
    /// Reassembles PSI/PSIP sections (PAT, PMT, VCT, EIT...) carried on a single PID.
    /// Sections can span packets, and several can share one packet; the pointer_field in
    /// a payload_unit_start packet marks where the first new section begins.
    public sealed class PsiSectionAssembler(ushort pid)
    {
        private readonly List<byte> _buffer = new(1024);
        private bool _synced;
        private int _lastContinuity = -1;

        public ushort Pid => pid;

        /// Feeds one 188-byte TS packet. Complete, CRC-valid sections are added to <paramref name="sections"/>.
        public void Feed(ReadOnlySpan<byte> packet, List<byte[]> sections)
        {
            if (packet.Length != TsPacketFramer.PacketSize || packet[0] != TsPacketFramer.SyncByte) return;
            if ((packet[1] & 0x80) != 0) return; // transport_error_indicator
            if ((((packet[1] & 0x1F) << 8) | packet[2]) != pid) return;

            int adaptationControl = (packet[3] >> 4) & 0x03;
            if ((adaptationControl & 0x01) == 0) return; // no payload

            int continuity = packet[3] & 0x0F;
            if (_lastContinuity >= 0)
            {
                if (continuity == _lastContinuity) return; // duplicate packet
                if (continuity != ((_lastContinuity + 1) & 0x0F)) ResetSection(); // lost packet(s)
            }
            _lastContinuity = continuity;

            int offset = 4;
            if ((adaptationControl & 0x02) != 0) offset += 1 + packet[4];
            if (offset >= TsPacketFramer.PacketSize) return;
            var payload = packet[offset..];

            bool unitStart = (packet[1] & 0x40) != 0;
            if (unitStart)
            {
                int pointer = payload[0];
                payload = payload[1..];
                if (pointer > payload.Length) { ResetSection(); return; }

                // Bytes before the pointer finish the section already in progress.
                if (_synced)
                {
                    _buffer.AddRange(payload[..pointer]);
                    Drain(sections);
                }

                _buffer.Clear();
                _synced = true;
                _buffer.AddRange(payload[pointer..]);
                Drain(sections);
            }
            else if (_synced)
            {
                _buffer.AddRange(payload);
                Drain(sections);
            }
        }

        private void Drain(List<byte[]> sections)
        {
            while (_synced && _buffer.Count >= 3)
            {
                // 0xFF where a table_id should be means stuffing to the end of the packet.
                if (_buffer[0] == 0xFF) { ResetSection(); return; }

                int length = 3 + (((_buffer[1] & 0x0F) << 8) | _buffer[2]);
                if (_buffer.Count < length) return;

                var section = _buffer.GetRange(0, length).ToArray();
                _buffer.RemoveRange(0, length);

                if (IsValid(section)) sections.Add(section);
            }
        }

        private static bool IsValid(byte[] section)
        {
            bool longForm = (section[1] & 0x80) != 0;
            if (!longForm) return true;
            return section.Length >= 12 && Crc32Mpeg.Compute(section) == 0;
        }

        private void ResetSection()
        {
            _buffer.Clear();
            _synced = false;
        }
    }
}
