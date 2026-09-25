using LinTv.Core.Domain;

namespace LinTv.Core.Writers
{
    /// One place for public URLs that other documents link to (M3U, lineup.json).
    /// Must match the StreamController and GuideController routes.
    public static class ChannelUrls
    {
        /// /stream/10.1 for the primary, /stream/10.1/{index} for alternates.
        public static string Stream(string baseUrl, VirtualChannel channel) =>
            channel.IsPrimary
                ? $"{baseUrl.TrimEnd('/')}/stream/{channel.Id}"
                : $"{baseUrl.TrimEnd('/')}/stream/{channel.Id}/{channel.Index}";

        public static string Guide(string baseUrl) =>
            $"{baseUrl.TrimEnd('/')}/guide.xml";
    }
}
