namespace LinTv.Core.Domain
{
    public static class AtscChannelPlan
    {
        /// US/Canada terrestrial RF 2-36 (post-2020 repack; 37 is reserved, 38+ were auctioned off).
        public static IReadOnlyList<RfChannel> UsBroadcast { get; } =
            Enumerable.Range(2, 35).Select(rf => new RfChannel(rf, CenterFrequencyHz(rf))).ToArray();

        /// 6 MHz channels, tuned at their center. The VHF bands aren't contiguous.
        public static long CenterFrequencyHz(int rf) => rf switch
        {
            >= 2 and <= 4 => 57 + (rf - 2) * 6,     // 54-72 MHz
            >= 5 and <= 6 => 79 + (rf - 5) * 6,     // 76-88 MHz
            >= 7 and <= 13 => 177 + (rf - 7) * 6,   // 174-216 MHz
            >= 14 and <= 51 => 473 + (rf - 14) * 6, // 470-698 MHz
            _ => throw new ArgumentOutOfRangeException(nameof(rf), rf, "Not an ATSC RF channel")
        } * 1_000_000L;
    }
}
