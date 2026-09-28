using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using LinTv.Mpeg;

namespace LinTv.Core.Domain
{
    /// Categories are A/65 genre names ("Movie", "News", "Comedy"). Premiere and Repeat come
    /// from the broadcast genre codes of the same names. Everything after Description is
    /// optional so guide.json files written before these fields existed still load.
    public sealed record GuideEvent(
        string ChannelId, ushort EventId,
        DateTimeOffset Start, TimeSpan Duration,
        string Title, string? Description,
        IReadOnlyList<string>? Categories = null,
        IReadOnlyList<GuideRating>? Ratings = null,
        bool Premiere = false, bool Repeat = false, bool Captions = false,
        [property: JsonConverter(typeof(JsonStringEnumConverter<AudioLayout>))] AudioLayout? Audio = null);

    /// System is the XMLTV rating system ("VCHIP", "MPAA"), or null for a free-text label.
    public sealed record GuideRating(string? System, string Value);
}
