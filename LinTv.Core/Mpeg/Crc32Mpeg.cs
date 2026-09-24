namespace LinTv.Core.Mpeg
{
    /// CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection, no final XOR).
    /// Computed over a whole section including its trailing CRC_32, a valid section yields 0.
    public static class Crc32Mpeg
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
                crc = (crc << 8) ^ Table[((crc >> 24) ^ b) & 0xFF];
            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int bit = 0; bit < 8; bit++)
                    c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                table[i] = c;
            }
            return table;
        }
    }
}
