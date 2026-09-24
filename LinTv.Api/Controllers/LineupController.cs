using LinTv.Core.Stores;
using LinTv.Core.Writers;
using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class LineupController : ControllerBase
    {
        public IChannelStore ChannelStore { private get; init; }

        public IM3uWriter M3uWriter { private get; init; }

        public LineupController(
            IChannelStore channelStore,
            IM3uWriter m3uWriter)
        {
            ChannelStore = channelStore;
            M3uWriter = m3uWriter;
        }

        [HttpGet("lineup.m3u")]
        public async Task<IActionResult> M3u()
        {
            var channels = await ChannelStore.GetAllAsync();
            var m3u = M3uWriter.Write(channels, $"{Request.Scheme}://{Request.Host}");
            return Content(m3u, "application/vnd.apple.mpegurl");
        }
    }
}
