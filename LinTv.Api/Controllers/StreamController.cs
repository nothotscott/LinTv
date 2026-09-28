using LinTv.Core.Domain;
using LinTv.Core.Exceptions;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        public ILogger Logger { private get; set; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IProgramStreamBroadcaster Broadcaster { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public StreamController(
            ILogger<StreamController> logger,
            ITunerArbiterService tunerArbiter,
            IProgramStreamBroadcaster broadcaster,
            IChannelStore channelStore)
        {
            Logger = logger;
            TunerArbiter = tunerArbiter;
            Broadcaster = broadcaster;
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
            long bytesSent = 0;
            var outcome = "ended (end of stream or no data)";

            // Ends on client disconnect (ct) or on a force release (lease.Revoked).
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Revoked);

            Response.ContentType = "video/mp2t";
            try
            {
                await foreach (var chunk in Broadcaster.StreamProgramAsync(channel.ProgramNumber, stop.Token))
                {
                    await Response.Body.WriteAsync(chunk, stop.Token);
                    bytesSent += chunk.Length;
                }
            }
            catch (ProgramNotFoundException ex)
            {
                // Thrown before anything is written, so a proper error response is still possible.
                outcome = "program not found";
                return Problem($"{channel.Id} on RF {channel.RfChannel}: {ex.Message}. Weak signal, or try a channel scan.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
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
