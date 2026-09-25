using LinTv.Core.Domain;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    /// Extra guide display-names per channel (see ChannelMapping). The same data lives in
    /// {StorageDirectory}/channel-map.json, which can also be edited by hand.
    [ApiController]
    [Route("channel-map")]
    public class ChannelMapController : ControllerBase
    {
        public IChannelMapStore ChannelMapStore { private get; init; }

        public ChannelMapController(IChannelMapStore channelMapStore)
        {
            ChannelMapStore = channelMapStore;
        }

        [HttpGet]
        public Task<IReadOnlyList<ChannelMapping>> GetAll() => ChannelMapStore.GetAllAsync();

        /// e.g. PUT /channel-map/13.1 with body ["FOX"]. Replaces any existing names for 13.1.
        [HttpPut("{major:int}.{minor:int}")]
        public async Task<ChannelMapping> Set(int major, int minor, [FromBody] string[] displayNames)
        {
            var mapping = new ChannelMapping($"{major}.{minor}",
                displayNames.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToArray());
            await ChannelMapStore.SetAsync(mapping);
            return mapping;
        }

        [HttpDelete("{major:int}.{minor:int}")]
        public async Task<IActionResult> Remove(int major, int minor) =>
            await ChannelMapStore.RemoveAsync($"{major}.{minor}") ? NoContent() : NotFound();
    }
}
