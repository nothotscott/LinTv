using LinTv.Core.Domain;
using System.Text;

namespace LinTv.Core.Writers
{
    /// Extended M3U as consumed by Jellyfin/Emby M3U tuners and VLC.
    /// tvg-id is the major.minor channel ID, which the XMLTV guide will use as its channel id.
    public class M3uWriter : IM3uWriter
    {
        public string Write(IReadOnlyList<VirtualChannel> channels, string baseUrl)
        {
            baseUrl = baseUrl.TrimEnd('/');
            // x-tvg-url points guide-aware players at the XMLTV guide.
            var sb = new StringBuilder($"#EXTM3U x-tvg-url=\"{ChannelUrls.Guide(baseUrl)}\"\n");

            // Primaries only: tvg-id must be unique for clients to map the guide.
            foreach (var c in channels.Where(c => c.IsPrimary).OrderBy(c => c.Major).ThenBy(c => c.Minor))
            {
                var name = c.ShortName.Replace("\"", "'").Replace(",", " ");
                sb.Append($"#EXTINF:-1 tvg-id=\"{c.Id}\" tvg-chno=\"{c.Id}\" tvg-name=\"{name}\",{c.Id} {name}\n");
                sb.Append($"{ChannelUrls.Stream(baseUrl, c)}\n");
            }

            return sb.ToString();
        }
    }
}
