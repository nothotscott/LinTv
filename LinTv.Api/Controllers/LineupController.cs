using LinTv.Core.Domain;
using LinTv.Core.Services;
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

        public IHdHomeRunLineupService HdHomeRunLineupService { private get; init; }

        public LineupController(
            IChannelStore channelStore,
            IM3uWriter m3uWriter,
            IHdHomeRunLineupService hdHomeRunLineupService)
        {
            ChannelStore = channelStore;
            M3uWriter = m3uWriter;
            HdHomeRunLineupService = hdHomeRunLineupService;
        }

        private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

        [HttpGet("lineup.m3u")]
        public async Task<IActionResult> M3u()
        {
            var channels = await ChannelStore.GetAllAsync();
            var m3u = M3uWriter.Write(channels, BaseUrl);
            return Content(m3u, "application/vnd.apple.mpegurl");
        }

        [HttpGet("lineup.json")]
        public async Task<IActionResult> Lineup()
        {
            return new JsonResult(await HdHomeRunLineupService.GetLineupAsync(BaseUrl), HdHomeRunJson.Options);
        }

        [HttpGet("lineup_status.json")]
        public async Task<IActionResult> LineupStatus()
        {
            return new JsonResult(await HdHomeRunLineupService.LineupStatusAsync(), HdHomeRunJson.Options);
        }
    }
}
