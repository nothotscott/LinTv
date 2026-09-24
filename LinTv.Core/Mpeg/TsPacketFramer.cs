namespace LinTv.Core.Mpeg
{
    public delegate void TsPacketHandler(ReadOnlySpan<byte> packet);

    /// Splits an arbitrary byte stream (dvr0 reads aren't guaranteed to be packet-aligned)
    /// into 188-byte TS packets, resyncing on the 0x47 sync byte.
    public sealed class TsPacketFramer
    {
        public const int PacketSize = 188;
        public const byte SyncByte = 0x47;

        private readonly byte[] _partial = new byte[PacketSize];
        private int _partialLength;

        public void Push(ReadOnlySpan<byte> data, TsPacketHandler onPacket)
        {
            while (!data.IsEmpty)
            {
                if (_partialLength > 0)
                {
                    int take = Math.Min(PacketSize - _partialLength, data.Length);
                    data[..take].CopyTo(_partial.AsSpan(_partialLength));
                    _partialLength += take;
                    data = data[take..];

                    if (_partialLength == PacketSize)
                    {
                        onPacket(_partial);
                        _partialLength = 0;
                    }
                    continue;
                }

                if (data[0] != SyncByte)
                {
                    int sync = data.IndexOf(SyncByte);
                    if (sync < 0) return;
                    data = data[sync..];
                }

                if (data.Length >= PacketSize)
                {
                    onPacket(data[..PacketSize]);
                    data = data[PacketSize..];
                }
                else
                {
                    data.CopyTo(_partial);
                    _partialLength = data.Length;
                    return;
                }
            }
        }
    }
}
