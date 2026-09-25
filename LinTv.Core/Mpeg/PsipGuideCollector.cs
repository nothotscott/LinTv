namespace LinTv.Core.Mpeg
{
    /// Collects guide data from one multiplex. It starts on the PSIP base PID and, once the
    /// MGT arrives, also follows the EIT/ETT PIDs it lists. Feed it every TS packet.
    public sealed class PsipGuideCollector
    {
        /// Current leap-second count (2017 onwards). Replaced by the STT's value when one arrives.
        private const int DefaultGpsUtcOffset = 18;

        private readonly IReadOnlySet<ushort> _sourceIds;
        private readonly Dictionary<ushort, PsiSectionAssembler> _assemblers = new();
        private readonly HashSet<ushort> _eitPids = new();
        private readonly Dictionary<(ushort Pid, ushort SourceId), SectionSet> _eitTables = new();
        private readonly Dictionary<(ushort SourceId, ushort EventId), EitEvent> _events = new();
        private readonly Dictionary<uint, string> _texts = new();
        private readonly List<byte[]> _sections = new();

        private bool _haveMgt;

        public int GpsUtcOffset { get; private set; } = DefaultGpsUtcOffset;

        public int EitPidCount => _eitPids.Count;
        public int EventCount => _events.Count;
        public int TextCount => _texts.Count;

        /// All EIT tables listed in the MGT are complete for every wanted source, and every event
        /// that points to an ETT has its text.
        public bool IsComplete =>
            _haveMgt
            && _eitPids.All(pid => _sourceIds.All(src =>
                _eitTables.TryGetValue((pid, src), out var table) && table.IsComplete))
            && _events.Values
                .Where(e => e.EtmLocation is 1 or 2)
                .All(e => _texts.ContainsKey(PsipTables.EventEtmId(e.SourceId, e.EventId)));

        /// <param name="sourceIds">PSIP source_ids of the lineup's channels on this multiplex.</param>
        public PsipGuideCollector(IReadOnlySet<ushort> sourceIds)
        {
            _sourceIds = sourceIds;
            _assemblers[VirtualChannelTable.BasePid] = new PsiSectionAssembler(VirtualChannelTable.BasePid);
        }

        public void Feed(ReadOnlySpan<byte> packet)
        {
            if (packet.Length != TsPacketFramer.PacketSize) return;

            var pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
            if (!_assemblers.TryGetValue(pid, out var assembler)) return;

            assembler.Feed(packet, _sections);
            foreach (var section in _sections) Handle(pid, section);
            _sections.Clear();
        }

        /// Events with a decodable title, times converted from GPS to UTC.
        public IEnumerable<(EitEvent Event, DateTimeOffset Start, string? Description)> GetEvents()
        {
            foreach (var e in _events.Values)
            {
                if (e.Title is null) continue;

                var start = PsipTables.GpsEpoch.AddSeconds((long)e.StartGps - GpsUtcOffset);
                _texts.TryGetValue(PsipTables.EventEtmId(e.SourceId, e.EventId), out var description);
                yield return (e, start, description);
            }
        }

        private void Handle(ushort pid, byte[] section)
        {
            switch (section[0])
            {
                case PsipTables.MgtTableId when pid == VirtualChannelTable.BasePid && !_haveMgt:
                    if (!PsipTables.TryParseMgt(section, out var entries)) return;
                    foreach (var entry in entries)
                    {
                        bool eit = entry.TableType is >= PsipTables.EitTypeFirst and <= PsipTables.EitTypeLast;
                        bool ett = entry.TableType is >= PsipTables.EttTypeFirst and <= PsipTables.EttTypeLast;
                        if (!eit && !ett) continue;

                        if (eit) _eitPids.Add(entry.Pid);
                        if (!_assemblers.ContainsKey(entry.Pid))
                            _assemblers[entry.Pid] = new PsiSectionAssembler(entry.Pid);
                    }
                    _haveMgt = true;
                    break;

                case PsipTables.SttTableId when pid == VirtualChannelTable.BasePid:
                    if (PsipTables.TryParseSttGpsUtcOffset(section, out var offset)) GpsUtcOffset = offset;
                    break;

                case PsipTables.EitTableId:
                    if (!PsipTables.TryParseEit(section, out var eitSection)) return;
                    if (!_sourceIds.Contains(eitSection.SourceId)) return;

                    var key = (pid, eitSection.SourceId);
                    if (!_eitTables.TryGetValue(key, out var table)) _eitTables[key] = table = new SectionSet();
                    table.Add(eitSection.Version, eitSection.SectionNumber, eitSection.LastSectionNumber);

                    foreach (var e in eitSection.Events)
                        _events[(e.SourceId, e.EventId)] = e;
                    break;

                case PsipTables.EttTableId:
                    if (PsipTables.TryParseEtt(section, out var etmId, out var text))
                        _texts[etmId] = text;
                    break;
            }
        }

        /// Tracks which sections of one table version have arrived.
        private sealed class SectionSet
        {
            private readonly HashSet<byte> _received = new();
            private int _version = -1;
            private int _lastSection = -1;

            public bool IsComplete => _lastSection >= 0 && _received.Count == _lastSection + 1;

            public void Add(byte version, byte sectionNumber, byte lastSectionNumber)
            {
                if (version != _version)
                {
                    _received.Clear();
                    _version = version;
                    _lastSection = lastSectionNumber;
                }
                _received.Add(sectionNumber);
            }
        }
    }
}
