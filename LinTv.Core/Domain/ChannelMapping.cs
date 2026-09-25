namespace LinTv.Core.Domain
{
    /// Extra names for a channel, added to the guide as further XMLTV display-names, e.g.
    /// 13.1 → ["FOX"] so Jellyfin/Plex can match the local affiliate to its network.
    /// Channel is the major.minor id (VirtualChannel.Id).
    public sealed record ChannelMapping(string Channel, string[] DisplayNames);
}
