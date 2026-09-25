using System.Buffers;

namespace LinTv.Core.Mpeg
{
    /// Cuts one program (virtual channel) out of a multiplex as a standalone single-program TS.
    /// Follows the PAT to the program's PMT, then to its elementary-stream and PCR PIDs.
    /// Those pass through unmodified. The PAT is rewritten to list only this program, because
    /// clients start by reading it. Everything else is dropped: other programs, PSIP, and nulls.
    public sealed class ProgramDemuxer
    {
        private const ushort PatPid = 0x0000, NullPid = 0x1FFF;
        private const byte PatTableId = 0x00, PmtTableId = 0x02;

        private readonly ushort _programNumber;
        private readonly PsiSectionAssembler _patAssembler = new(PatPid);
        private readonly List<byte[]> _sections = new();
        private readonly HashSet<ushort> _streamPids = new();
        private readonly byte[] _patPacket = new byte[TsPacketFramer.PacketSize];

        private PsiSectionAssembler? _pmtAssembler;
        private int _pmtPid = -1;
        private int _patContinuity;

        /// The PAT listed the program; output has started.
        public bool ProgramFound => _pmtPid >= 0;

        /// The PMT arrived; elementary streams are flowing.
        public bool StreamsFound => _streamPids.Count > 0;

        public ProgramDemuxer(ushort programNumber)
        {
            _programNumber = programNumber;
        }

        public void Process(ReadOnlySpan<byte> packet, IBufferWriter<byte> output)
        {
            if (packet.Length != TsPacketFramer.PacketSize) return;
            var pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);

            if (pid == PatPid)
            {
                _patAssembler.Feed(packet, _sections);
                foreach (var section in _sections) HandlePat(section, output);
                _sections.Clear();
            }
            else if (pid == _pmtPid)
            {
                _pmtAssembler!.Feed(packet, _sections);
                foreach (var section in _sections) HandlePmt(section);
                _sections.Clear();
                output.Write(packet);
            }
            else if (_streamPids.Contains(pid))
            {
                output.Write(packet);
            }
        }

        /// PAT (ISO 13818-1 2.4.4.3): after the 8-byte header, 4-byte entries of
        /// program_number(16) rsvd(3) PID(13), then CRC_32.
        private void HandlePat(byte[] s, IBufferWriter<byte> output)
        {
            if (s[0] != PatTableId || s.Length < 12 || (s[5] & 0x01) == 0) return;

            for (int pos = 8; pos + 4 <= s.Length - 4; pos += 4)
            {
                var program = (ushort)((s[pos] << 8) | s[pos + 1]);
                if (program != _programNumber) continue;

                int pmtPid = ((s[pos + 2] & 0x1F) << 8) | s[pos + 3];
                if (pmtPid != _pmtPid)
                {
                    // New (or moved) PMT: stop passing the old streams until it arrives.
                    _pmtPid = pmtPid;
                    _pmtAssembler = new PsiSectionAssembler((ushort)pmtPid);
                    _streamPids.Clear();
                }

                var transportStreamId = (ushort)((s[3] << 8) | s[4]);
                var version = (byte)((s[5] >> 1) & 0x1F);
                WritePat(transportStreamId, version, output);
                return;
            }
        }

        /// PMT (2.4.4.8): PCR_PID at 8, program_info_length at 10, then entries of
        /// stream_type(8) rsvd(3) PID(13) rsvd(4) ES_info_length(12) descriptors.
        private void HandlePmt(byte[] s)
        {
            if (s[0] != PmtTableId || s.Length < 16 || (s[5] & 0x01) == 0) return;
            if (((s[3] << 8) | s[4]) != _programNumber) return;

            _streamPids.Clear();

            var pcrPid = (ushort)(((s[8] & 0x1F) << 8) | s[9]);
            if (pcrPid != NullPid) _streamPids.Add(pcrPid);

            int pos = 12 + (((s[10] & 0x0F) << 8) | s[11]);
            int end = s.Length - 4;
            while (pos + 5 <= end)
            {
                _streamPids.Add((ushort)(((s[pos + 1] & 0x1F) << 8) | s[pos + 2]));
                pos += 5 + (((s[pos + 3] & 0x0F) << 8) | s[pos + 4]);
            }
        }

        /// A single-program PAT, sent every time the source PAT repeats (~100 ms), with its own
        /// continuity counter. It keeps the source's TSID and version, so PMT moves still propagate.
        private void WritePat(ushort transportStreamId, byte version, IBufferWriter<byte> output)
        {
            Span<byte> section = stackalloc byte[16];
            section[0] = PatTableId;
            section[1] = 0xB0;              // section_syntax_indicator, '0', reserved
            section[2] = 13;                // section_length: 5 header + 4 entry + 4 CRC
            section[3] = (byte)(transportStreamId >> 8);
            section[4] = (byte)transportStreamId;
            section[5] = (byte)(0xC1 | (version << 1)); // reserved, version, current_next
            section[6] = 0;                 // section_number
            section[7] = 0;                 // last_section_number
            section[8] = (byte)(_programNumber >> 8);
            section[9] = (byte)_programNumber;
            section[10] = (byte)(0xE0 | (_pmtPid >> 8));
            section[11] = (byte)_pmtPid;
            uint crc = Crc32Mpeg.Compute(section[..12]);
            section[12] = (byte)(crc >> 24);
            section[13] = (byte)(crc >> 16);
            section[14] = (byte)(crc >> 8);
            section[15] = (byte)crc;

            var packet = _patPacket.AsSpan();
            packet.Fill(0xFF);
            packet[0] = TsPacketFramer.SyncByte;
            packet[1] = 0x40;               // payload_unit_start, PID 0
            packet[2] = 0x00;
            packet[3] = (byte)(0x10 | _patContinuity); // payload only
            packet[4] = 0x00;               // pointer_field
            section.CopyTo(packet[5..]);
            _patContinuity = (_patContinuity + 1) & 0x0F;

            output.Write(packet);
        }
    }
}
