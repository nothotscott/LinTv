using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        public ITunerArbiterService TunerArbiter { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public StreamController(
            ITunerArbiterService tunerArbiter,
            IChannelStore channelStore)
        {
            TunerArbiter = tunerArbiter;
            ChannelStore = channelStore;
        }

        /// Whole RF multiplex as MPEG-TS; open in VLC via Media > Open Network Stream:
        ///   http://&lt;host&gt;:5249/stream?frequencyHz=189000000
        /// Pick a subchannel in VLC's Playback > Program menu.
        [HttpGet("stream")]
        public Task<IActionResult> Stream(long frequencyHz, CancellationToken ct) =>
            StreamMultiplexAsync(frequencyHz, ct);

        /// HDHomeRun-style channel URL, e.g. /auto/v9.1. Streams the channel's whole RF multiplex
        /// for now; VLC picks the subchannel via the #EXTVLCOPT:program line in /lineup.m3u.
        [HttpGet("auto/v{channel}")]
        public async Task<IActionResult> Channel(string channel, CancellationToken ct)
        {
            var parts = channel.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
                return NotFound();

            var virtualChannel = await ChannelStore.FindAsync(major, minor);
            if (virtualChannel is null) return NotFound();

            return await StreamMultiplexAsync(virtualChannel.FrequencyHz, ct);
        }

        private async Task<IActionResult> StreamMultiplexAsync(long frequencyHz, CancellationToken ct)
        {
            IDvbTuner tuner;
            try
            {
                tuner = await TunerArbiter.AcquireAsync(frequencyHz, TunerPriority.LiveView, ct);
            }
            catch (Exception ex)
            {
                return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                // ct is RequestAborted: closing VLC cancels the read loop and releases the demux.
                Response.ContentType = "video/mp2t";
                await foreach (var chunk in tuner.ReadTransportStreamAsync(ct))
                {
                    await Response.Body.WriteAsync(chunk, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected -- the normal way a stream ends.
            }
            finally
            {
                // Not ct: it's already cancelled here, and the lease must be returned regardless.
                await TunerArbiter.ReleaseAsync(CancellationToken.None);
            }

            return new EmptyResult();
        }
    }
}
