using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Mpeg;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using System.Buffers;
using System.Diagnostics;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        /// PATs repeat every ~100 ms, so a program missing for this long isn't in the multiplex.
        private static readonly TimeSpan ProgramTimeout = TimeSpan.FromSeconds(5);

        public ILogger Logger { private get; set; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public StreamController(
            ILogger<StreamController> logger,
            ITunerArbiterService tunerArbiter,
            IChannelStore channelStore)
        {
            Logger = logger;
            TunerArbiter = tunerArbiter;
            ChannelStore = channelStore;
        }

        /// Stream a virtual channel as a single-program TS (see ChannelUrls). /stream/10.1 is the
        /// primary entry (best signal at scan time); /stream/10.1/1 is the next best, and so on.
        [HttpGet("stream/{major:int}.{minor:int}/{index:int?}")]
        public async Task<IActionResult> Stream(int major, int minor, int? index, CancellationToken ct)
        {
            var virtualChannel = await ChannelStore.FindAsync(major, minor, index ?? 0);
            if (virtualChannel is null) return NotFound();

            return await StreamProgramAsync(virtualChannel, ct);
        }

        private async Task<IActionResult> StreamProgramAsync(VirtualChannel channel, CancellationToken ct)
        {
            var client = HttpContext.Connection.RemoteIpAddress;
            Logger.LogInformation("Stream {Id}/{Index} (RF {Rf}, program {Program}) requested by {Client}",
                channel.Id, channel.Index, channel.RfChannel, channel.ProgramNumber, client);

            IDvbTuner tuner;
            try
            {
                tuner = await TunerArbiter.AcquireAsync(channel.FrequencyHz, TunerPriority.LiveView, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Stream {Id}/{Index} for {Client} refused: {Reason}", channel.Id, channel.Index, client, ex.Message);
                return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var elapsed = Stopwatch.StartNew();
            long bytesSent = 0;
            var outcome = "ended";

            Response.ContentType = "video/mp2t";
            try
            {
                var framer = new TsPacketFramer();
                var demuxer = new ProgramDemuxer(channel.ProgramNumber);
                var output = new ArrayBufferWriter<byte>(64 * 1024);
                var deadline = DateTime.UtcNow + ProgramTimeout;
                bool loggedStreams = false;

                // ct is RequestAborted: closing the player cancels the read loop and releases the demux.
                await foreach (var chunk in tuner.ReadTransportStreamAsync(ct))
                {
                    framer.Push(chunk.Span, packet => demuxer.Process(packet, output));

                    if (!demuxer.ProgramFound)
                    {
                        // Nothing has been written yet, so a proper error response is still possible.
                        if (DateTime.UtcNow > deadline)
                        {
                            outcome = "program not found";
                            Logger.LogWarning("Program {Program} ({Id}) not in the PAT on RF {Rf} after {Seconds}s",
                                channel.ProgramNumber, channel.Id, channel.RfChannel, ProgramTimeout.TotalSeconds);
                            return Problem(
                                $"Program {channel.ProgramNumber} ({channel.Id}) not found on RF {channel.RfChannel}; try a channel scan",
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                        }
                        continue;
                    }

                    if (!loggedStreams && demuxer.StreamsFound)
                    {
                        loggedStreams = true;
                        Logger.LogDebug("{Id}: PMT after {ElapsedMs} ms, passing PIDs {Pids}", channel.Id,
                            elapsed.ElapsedMilliseconds, string.Join(", ", demuxer.StreamPids.Order().Select(p => $"0x{p:X4}")));
                    }

                    if (output.WrittenCount == 0) continue;
                    await Response.Body.WriteAsync(output.WrittenMemory, ct);
                    bytesSent += output.WrittenCount;
                    output.ResetWrittenCount();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected -- the normal way a stream ends.
                outcome = "client disconnected";
            }
            catch (Exception ex)
            {
                outcome = $"failed: {ex.Message}";
                throw;
            }
            finally
            {
                // Not ct: it's already cancelled here, and the lease must be returned regardless.
                await TunerArbiter.ReleaseAsync(CancellationToken.None);
                Logger.LogInformation("Stream {Id}/{Index} for {Client} {Outcome} after {Seconds:F0}s ({MegaBytes:F1} MB, {Mbps:F1} Mbps)",
                    channel.Id, channel.Index, client, outcome, elapsed.Elapsed.TotalSeconds, bytesSent / 1_000_000.0,
                    elapsed.Elapsed.TotalSeconds > 0 ? bytesSent * 8 / 1_000_000.0 / elapsed.Elapsed.TotalSeconds : 0);
            }

            return new EmptyResult();
        }
    }
}
