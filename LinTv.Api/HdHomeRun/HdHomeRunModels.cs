using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinTv.Api.HdHomeRun
{
    /// HDHomeRun's HTTP API uses PascalCase keys and 0/1 for booleans; clients such as
    /// Plex and Jellyfin match on exact names, so these bypass ASP.NET's camelCase default.
    public static class HdHomeRunJson
    {
        public static JsonSerializerOptions Options { get; } = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// /discover.json: how Plex/Jellyfin identify the device and find its lineup.
    public sealed record HdHomeRunDiscover(
        string FriendlyName,
        string ModelNumber,
        string FirmwareName,
        string FirmwareVersion,
        string DeviceID,
        string DeviceAuth,
        string BaseURL,
        string LineupURL,
        int TunerCount);

    /// One entry of /lineup.json.
    public sealed record HdHomeRunLineupEntry(string GuideNumber, string GuideName, string URL);

    /// /lineup_status.json. A real tuner sends Progress/Found while scanning and
    /// ScanPossible/Source/SourceList otherwise; null fields are omitted.
    public sealed record HdHomeRunLineupStatus(
        int ScanInProgress,
        int? Progress = null,
        int? Found = null,
        int? ScanPossible = null,
        string? Source = null,
        string[]? SourceList = null);
}
