using LinTv.Core.Domain;

namespace LinTv.Api.Pages
{
    /// Model for Shared/_ScanCard. Kind matches the /scan/{kind} status API the page polls.
    public sealed record ScanCardModel(
        string Kind, string Title, string Description, ScanStatus Status,
        string FoundLabel, string Handler, string ButtonText, string? Extra = null);
}
