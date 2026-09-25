using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Mpeg;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using System.Buffers;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        /// PATs repeat every ~100 ms, so a program missing for this long isn't in the multiplex.
        private static readonly TimeSpan ProgramTimeout = TimeSpan.FromSeconds(5);

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public StreamController(
            ITunerArbiterService tunerArbiter,
            IChannelStore channelStore)
        {
            TunerArbiter = tunerArbiter;
            ChannelStore = channelStore;
        }

        /// Stream a virtual channel as a single-program TS (see ChannelUrls). /stream/10.1 is the
        /// primary entry; /stream/10.1/1 is the second place 10.1 is received, and so on.
        [HttpGet("stream/{major:int}.{minor:int}/{index:int?}")]
        public async Task<IActionResult> Stream(int major, int minor, int? index, CancellationToken ct)
        {
            var virtualChannel = await ChannelStore.FindAsync(major, minor, index ?? 0);
            if (virtualChannel is null) return NotFound();

            return await StreamProgramAsync(virtualChannel, ct);
        }

        private async Task<IActionResult> StreamProgramAsync(VirtualChannel channel, CancellationToken ct)
        {
            IDvbTuner tuner;
            try
            {
                tuner = await TunerArbiter.AcquireAsync(channel.FrequencyHz, TunerPriority.LiveView, ct);
            }
            catch (Exception ex)
            {
                return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                var framer = new TsPacketFramer();
                var demuxer = new ProgramDemuxer(channel.ProgramNumber);
                var output = new ArrayBufferWriter<byte>(64 * 1024);
                var deadline = DateTime.UtcNow + ProgramTimeout;

                // ct is RequestAborted: closing the player cancels the read loop and releases the demux.
                await foreach (var chunk in tuner.ReadTransportStreamAsync(ct))
                {
                    framer.Push(chunk.Span, packet => demuxer.Process(packet, output));

                    if (!demuxer.ProgramFound)
                    {
                        // Nothing has been written yet, so a proper error response is still possible.
                        if (DateTime.UtcNow > deadline)
                            return Problem(
                                $"Program {channel.ProgramNumber} ({channel.Id}) not found on RF {channel.RfChannel}; try a channel scan",
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                        continue;
                    }

                    if (output.WrittenCount == 0) continue;
                    Response.ContentType = "video/mp2t";
                    await Response.Body.WriteAsync(output.WrittenMemory, ct);
                    output.ResetWrittenCount();
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
