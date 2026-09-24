using LinTv.Core.Domain;
using LinTv.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    [ApiController]
    [Route("scan")]
    public class ScanController : ControllerBase
    {
        public IChannelScanner ChannelScanner { private get; init; }

        public IHostApplicationLifetime Lifetime { private get; init; }

        public ScanController(
            IChannelScanner channelScanner,
            IHostApplicationLifetime lifetime)
        {
            ChannelScanner = channelScanner;
            Lifetime = lifetime;
        }

        /// A scan takes a few minutes (each dead RF channel costs LockWaitSeconds), so it runs in
        /// the background; poll GET /scan/channels for progress.
        [HttpPost("channels")]
        public IActionResult StartChannelScan() =>
            ChannelScanner.TryStartScan(Lifetime.ApplicationStopping)
                ? Accepted("/scan/channels", ChannelScanner.Status)
                : Conflict(ChannelScanner.Status);

        [HttpGet("channels")]
        public ChannelScanStatus ChannelScanStatus() => ChannelScanner.Status;
    }
}
