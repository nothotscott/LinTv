using LinTv.Core.Domain;

namespace LinTv.Core.Writers
{
    /// One place for public URLs that other documents link to (M3U, lineup.json).
    /// Must match the StreamController and GuideController routes.
    public static class ChannelUrls
    {
        public static string Stream(string baseUrl, VirtualChannel channel) =>
            $"{baseUrl.TrimEnd('/')}/stream/{channel.Id}";

        public static string Guide(string baseUrl) =>
            $"{baseUrl.TrimEnd('/')}/guide.xml";
    }
}
