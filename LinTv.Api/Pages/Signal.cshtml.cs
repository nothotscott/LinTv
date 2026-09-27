using LinTv.Core.Domain;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinTv.Api.Pages
{
    /// Signal meter UI. Measuring itself is done by the page's script calling GET /scan/signal/...
    public class SignalModel : PageModel
    {
        public IChannelStore ChannelStore { private get; init; }

        public IChannelMapStore ChannelMapStore { private get; init; }

        public SignalModel(
            IChannelStore channelStore,
            IChannelMapStore channelMapStore)
        {
            ChannelStore = channelStore;
            ChannelMapStore = channelMapStore;
        }

        /// Preselect from /Signal?channel=13.1&index=0 (the Channels page links here).
        [BindProperty(SupportsGet = true)]
        public string? Channel { get; set; }

        [BindProperty(SupportsGet = true)]
        public int Index { get; set; }

        public IReadOnlyList<(string Value, string Label)> Options { get; private set; } = [];

        public string? Selected { get; private set; }

        public async Task OnGetAsync()
        {
            var mapped = (await ChannelMapStore.GetAllAsync())
                .GroupBy(m => m.Channel)
                .ToDictionary(g => g.Key, g => g.SelectMany(m => m.DisplayNames).FirstOrDefault());

            Options = (await ChannelStore.GetAllAsync())
                .OrderBy(c => c.Major).ThenBy(c => c.Minor).ThenBy(c => c.Index)
                .Select(c => ($"{c.Id}/{c.Index}", Label(c, mapped.GetValueOrDefault(c.Id))))
                .ToList();

            Selected = Channel is null ? null : $"{Channel}/{Index}";
        }

        private static string Label(VirtualChannel c, string? mappedName)
        {
            var name = mappedName is null || mappedName == c.ShortName ? c.ShortName : $"{c.ShortName} / {mappedName}";
            var alternate = c.IsPrimary ? "" : $", alt {c.Index}";
            return $"{c.Id} {name} (RF {c.RfChannel}{alternate})";
        }
    }
}
