using LinTv.Core.Domain;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinTv.Api.Pages
{
    public class ChannelsModel : PageModel
    {
        public IChannelStore ChannelStore { private get; init; }

        public IChannelMapStore ChannelMapStore { private get; init; }

        public ChannelsModel(
            IChannelStore channelStore,
            IChannelMapStore channelMapStore)
        {
            ChannelStore = channelStore;
            ChannelMapStore = channelMapStore;
        }

        public IReadOnlyList<VirtualChannel> Channels { get; private set; } = [];

        /// Channel id → mapped names (channel-map.json).
        public IReadOnlyDictionary<string, string[]> MappedNames { get; private set; } = new Dictionary<string, string[]>();

        public async Task OnGetAsync()
        {
            Channels = (await ChannelStore.GetAllAsync())
                .OrderBy(c => c.Major).ThenBy(c => c.Minor).ThenBy(c => c.Index)
                .ToList();
            MappedNames = (await ChannelMapStore.GetAllAsync())
                .GroupBy(m => m.Channel)
                .ToDictionary(g => g.Key, g => g.SelectMany(m => m.DisplayNames).ToArray());
        }
    }
}
