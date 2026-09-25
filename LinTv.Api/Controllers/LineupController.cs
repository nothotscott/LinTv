using LinTv.Api.HdHomeRun;
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

        public IChannelScanner ChannelScanner { private get; init; }

        public LineupController(
            IChannelStore channelStore,
            IM3uWriter m3uWriter,
            IChannelScanner channelScanner)
        {
            ChannelStore = channelStore;
            M3uWriter = m3uWriter;
            ChannelScanner = channelScanner;
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
            var channels = await ChannelStore.GetAllAsync();
            var lineup = channels
                .OrderBy(c => c.Major).ThenBy(c => c.Minor)
                .Select(c => new HdHomeRunLineupEntry(c.Id, c.ShortName, ChannelUrls.Stream(BaseUrl, c)));
            return new JsonResult(lineup, HdHomeRunJson.Options);
        }

        [HttpGet("lineup_status.json")]
        public IActionResult LineupStatus()
        {
            var scan = ChannelScanner.Status;
            var status = scan.InProgress
                ? new HdHomeRunLineupStatus(1, Progress: scan.ProgressPercent, Found: scan.Found)
                : new HdHomeRunLineupStatus(0, ScanPossible: 1, Source: "Antenna", SourceList: ["Antenna"]);
            return new JsonResult(status, HdHomeRunJson.Options);
        }
    }
}
