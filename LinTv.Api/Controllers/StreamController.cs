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

        /// Stream a virtual channel, e.g. /stream/8.1 (see ChannelUrls). Sends the channel's whole
        /// RF multiplex for now; VLC picks the subchannel via the #EXTVLCOPT:program line in
        /// /lineup.m3u, or manually via Playback > Program.
        [HttpGet("stream/{major:int}.{minor:int}")]
        public async Task<IActionResult> Stream(int major, int minor, CancellationToken ct)
        {
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
