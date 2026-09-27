using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Mpeg;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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

        public LinTvConfiguration Config { private get; init; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public StreamController(
            ILogger<StreamController> logger,
            IOptions<LinTvConfiguration> config,
            ITunerArbiterService tunerArbiter,
            IChannelStore channelStore)
        {
            Logger = logger;
            Config = config.Value;
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

        /// Who holds the tuner right now.
        [HttpGet("stream/status")]
        public TunerState Status() => TunerArbiter.State;

        /// Ends whatever holds the tuner: streams, scans, signal measurements. For a stream a client
        /// abandoned without disconnecting cleanly. Returns the number of leases revoked.
        [HttpPost("stream/disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            var before = TunerArbiter.State;
            var revoked = await TunerArbiter.ForceReleaseAsync();
            Logger.LogWarning("Tuner force-disconnected by {Client}", HttpContext.Connection.RemoteIpAddress);
            return Ok(new { revoked, before.FrequencyHz, before.Priority });
        }

        private async Task<IActionResult> StreamProgramAsync(VirtualChannel channel, CancellationToken ct)
        {
            var client = HttpContext.Connection.RemoteIpAddress;
            Logger.LogInformation("Stream {Id}/{Index} (RF {Rf}, program {Program}) requested by {Client}",
                channel.Id, channel.Index, channel.RfChannel, channel.ProgramNumber, client);

            TunerLease lease;
            try
            {
                lease = await TunerArbiter.AcquireAsync(channel.FrequencyHz, TunerPriority.LiveView, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Stream {Id}/{Index} for {Client} refused: {Reason}", channel.Id, channel.Index, client, ex.Message);
                return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var elapsed = Stopwatch.StartNew();
            var sinceLastWrite = Stopwatch.StartNew();
            var stallTimeout = TimeSpan.FromSeconds(Config.StreamStallTimeoutSeconds);
            long bytesSent = 0;
            var outcome = "ended";

            // Ends on client disconnect (ct) or on a force release (lease.Revoked).
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Revoked);

            Response.ContentType = "video/mp2t";
            try
            {
                var framer = new TsPacketFramer();
                var demuxer = new ProgramDemuxer(channel.ProgramNumber);
                var output = new ArrayBufferWriter<byte>(64 * 1024);
                var deadline = DateTime.UtcNow + ProgramTimeout;
                bool loggedStreams = false;

                // Chunks may be empty: the tuner yields an idle tick at least every ~500 ms when no
                // data arrives, which is what lets the deadline and stall checks below run.
                await foreach (var chunk in lease.Tuner.ReadTransportStreamAsync(stop.Token))
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
                                $"Program {channel.ProgramNumber} ({channel.Id}) not found on RF {channel.RfChannel}; weak signal, or try a channel scan",
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

                    if (output.WrittenCount == 0)
                    {
                        // Signal lost mid-stream: end it rather than hold the tuner for a client
                        // that is only receiving silence.
                        if (sinceLastWrite.Elapsed > stallTimeout)
                        {
                            outcome = $"stalled (no data for {stallTimeout.TotalSeconds:F0}s)";
                            break;
                        }
                        continue;
                    }

                    await Response.Body.WriteAsync(output.WrittenMemory, stop.Token);
                    bytesSent += output.WrittenCount;
                    output.ResetWrittenCount();
                    sinceLastWrite.Restart();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The normal ways a stream ends.
                outcome = ct.IsCancellationRequested ? "client disconnected" : "force-disconnected";
            }
            catch (Exception ex)
            {
                outcome = $"failed: {ex.Message}";
                throw;
            }
            finally
            {
                await TunerArbiter.ReleaseAsync(lease);
                Logger.LogInformation("Stream {Id}/{Index} for {Client} {Outcome} after {Seconds:F0}s ({MegaBytes:F1} MB, {Mbps:F1} Mbps)",
                    channel.Id, channel.Index, client, outcome, elapsed.Elapsed.TotalSeconds, bytesSent / 1_000_000.0,
                    elapsed.Elapsed.TotalSeconds > 0 ? bytesSent * 8 / 1_000_000.0 / elapsed.Elapsed.TotalSeconds : 0);
            }

            // A force release after data was sent can't become an error response; just end the body.
            if (lease.Revoked.IsCancellationRequested) HttpContext.Abort();
            return new EmptyResult();
        }
    }
}
