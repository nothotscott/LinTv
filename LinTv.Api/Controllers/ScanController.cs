using LinTv.Core.Domain;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using LinTv.Core.Exceptions;
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

        public IChannelStore ChannelStore { private get; init; }

        public ISignalMeter SignalMeter { private get; init; }

        public ScanController(
            IChannelScanner channelScanner,
            IEpgScanner epgScanner,
            IHostApplicationLifetime lifetime,
            IChannelStore channelStore,
            ISignalMeter signalMeter)
        {
            ChannelScanner = channelScanner;
            EpgScanner = epgScanner;
            Lifetime = lifetime;
            ChannelStore = channelStore;
            SignalMeter = signalMeter;
        }

        /// Tunes to a channel and samples its signal for a few seconds, e.g. /scan/signal/13.1 or
        /// /scan/signal/10.1/1?seconds=10. Works on channels too weak to lock. Holds the tuner
        /// while measuring, so it's refused (409) if the tuner is busy on another frequency.
        [HttpGet("signal/{major:int}.{minor:int}/{index:int?}")]
        public async Task<IActionResult> Signal(int major, int minor, int? index, int seconds = 5, CancellationToken ct = default)
        {
            var channel = await ChannelStore.FindAsync(major, minor, index ?? 0);
            if (channel is null) return NotFound();

            try
            {
                return Ok(await SignalMeter.MeasureAsync(channel, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), ct));
            }
            catch (TunerBusyException ex)
            {
                return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Device errors (e.g. /dev/dvb unavailable), same as /stream.
                return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
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
