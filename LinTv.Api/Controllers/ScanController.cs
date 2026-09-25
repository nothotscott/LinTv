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

        public IEpgScanner EpgScanner { private get; init; }

        public IHostApplicationLifetime Lifetime { private get; init; }

        public ScanController(
            IChannelScanner channelScanner,
            IEpgScanner epgScanner,
            IHostApplicationLifetime lifetime)
        {
            ChannelScanner = channelScanner;
            EpgScanner = epgScanner;
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
        public ScanStatus ChannelScanStatus() => ChannelScanner.Status;

        /// Up to EpgScanTimeoutSeconds per multiplex in the lineup; poll GET /scan/epg for progress.
        [HttpPost("epg")]
        public IActionResult StartEpgScan() =>
            EpgScanner.TryStartScan(Lifetime.ApplicationStopping)
                ? Accepted("/scan/epg", EpgScanner.Status)
                : Conflict(EpgScanner.Status);

        [HttpGet("epg")]
        public ScanStatus EpgScanStatus() => EpgScanner.Status;
    }
}
