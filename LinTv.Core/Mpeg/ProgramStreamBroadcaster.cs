using LinTv.Core.Configuration;
using LinTv.Core.Driver;
using LinTv.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LinTv.Core.Mpeg
{
    /// Shares one read of the multiplex between any number of readers, e.g. two viewers on
    /// subchannels of the same RF channel, or an EPG scan riding along with a viewer.
    ///
    /// A single "pump" reads IDvbTuner.ReadTransportStreamAsync and copies each chunk to every
    /// subscriber's bounded channel. The pump starts with the first subscriber and stops when the
    /// last one leaves. Program subscribers then frame and demux their copy independently.
    public sealed class ProgramStreamBroadcaster : IProgramStreamBroadcaster
    {
        /// Per-subscriber backlog: ~64 KB chunks, so about 4 MB or ~1.5 s of a full multiplex. A
        /// subscriber further behind than that loses chunks rather than stalling the others.
        private const int SubscriberBacklog = 64;

        /// PATs repeat every ~100 ms, so a program missing for this long isn't in the multiplex.
        private static readonly TimeSpan ProgramTimeout = TimeSpan.FromSeconds(5);

        private readonly Lock _lock = new();
        private readonly List<Subscriber> _subscribers = [];
        private CancellationTokenSource? _pumpStop;
        private Task? _pump;
        private Task? _stoppingPump;
        private int _pumpGeneration;

        public ILogger Logger { private get; set; }

        public LinTvConfiguration Config { private get; init; }

        public IDvbTuner Tuner { private get; init; }

        public ProgramStreamBroadcaster(
            ILogger<ProgramStreamBroadcaster> logger,
            IOptions<LinTvConfiguration> config,
            IDvbTuner tuner)
        {
            Logger = logger;
            Config = config.Value;
            Tuner = tuner;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamProgramAsync(ushort programNumber,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var framer = new TsPacketFramer();
            var demuxer = new ProgramDemuxer(programNumber);
            var output = new ArrayBufferWriter<byte>(64 * 1024);
            var stallTimeout = TimeSpan.FromSeconds(Config.StreamStallTimeoutSeconds);
            var started = Stopwatch.StartNew();
            var sinceOutput = Stopwatch.StartNew();
            bool loggedStreams = false;

            await foreach (var chunk in ReadMultiplexAsync(ct))
            {
                framer.Push(chunk.Span, packet => demuxer.Process(packet, output));

                if (!demuxer.ProgramFound)
                {
                    if (started.Elapsed > ProgramTimeout)
                    {
                        Logger.LogWarning("Program {Program} not in the PAT after {Seconds}s", programNumber, ProgramTimeout.TotalSeconds);
                        throw new ProgramNotFoundException(programNumber,
                            $"Program {programNumber} not found in the multiplex after {ProgramTimeout.TotalSeconds}s");
                    }
                    continue;
                }

                if (!loggedStreams && demuxer.StreamsFound)
                {
                    loggedStreams = true;
                    Logger.LogDebug("Program {Program}: PMT after {ElapsedMs} ms, passing PIDs {Pids}", programNumber,
                        started.ElapsedMilliseconds, string.Join(", ", demuxer.StreamPids.Order().Select(p => $"0x{p:X4}")));
                }

                if (output.WrittenCount == 0)
                {
                    // Signal lost mid-stream: end rather than hold the tuner for a client that is
                    // only receiving silence.
                    if (sinceOutput.Elapsed > stallTimeout)
                    {
                        Logger.LogWarning("Program {Program}: no data for {Seconds:F0}s, ending stream", programNumber, stallTimeout.TotalSeconds);
                        yield break;
                    }
                    continue;
                }

                // The consumer has finished with this chunk by the time it asks for the next one,
                // so the buffer can be reused rather than copied.
                yield return output.WrittenMemory;
                output.ResetWrittenCount();
                sinceOutput.Restart();
            }
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMultiplexAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            var subscriber = new Subscriber();
            await SubscribeAsync(subscriber, ct);
            try
            {
                await foreach (var chunk in subscriber.Channel.Reader.ReadAllAsync(ct))
                    yield return chunk;
            }
            finally
            {
                Unsubscribe(subscriber);
            }
        }

        private async Task SubscribeAsync(Subscriber subscriber, CancellationToken ct)
        {
            while (true)
            {
                Task stopping;
                lock (_lock)
                {
                    if (_pump is not null)
                    {
                        _subscribers.Add(subscriber);
                        Logger.LogDebug("Joined shared TS read ({Count} readers)", _subscribers.Count);
                        return;
                    }

                    if (_stoppingPump is null || _stoppingPump.IsCompleted)
                    {
                        _subscribers.Add(subscriber);
                        _pumpStop = new CancellationTokenSource();
                        var generation = ++_pumpGeneration;
                        var stop = _pumpStop.Token;
                        _pump = Task.Run(() => PumpAsync(generation, stop), CancellationToken.None);
                        Logger.LogDebug("Started shared TS read");
                        return;
                    }

                    // The previous pump is still closing dvr0 (up to one poll slice). Opening it
                    // again before then fails with EBUSY.
                    stopping = _stoppingPump;
                }

                Logger.LogTrace("Waiting for the previous TS read to close");
                await stopping.WaitAsync(ct).ContinueWith(_ => { }, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
            }
        }

        private void Unsubscribe(Subscriber subscriber)
        {
            lock (_lock)
            {
                if (!_subscribers.Remove(subscriber)) return;
                Logger.LogDebug("Left shared TS read ({Count} readers remain)", _subscribers.Count);

                if (_subscribers.Count == 0 && _pump is not null)
                {
                    _pumpStop!.Cancel();
                    _stoppingPump = _pump;
                    _pump = null;
                    _pumpStop = null;
                }
            }
        }

        private async Task PumpAsync(int generation, CancellationToken stop)
        {
            Exception? failure = null;
            try
            {
                await foreach (var chunk in Tuner.ReadTransportStreamAsync(stop))
                {
                    Subscriber[] targets;
                    lock (_lock) targets = [.. _subscribers];

                    // Chunks are fresh arrays that nobody mutates, so sharing them is safe.
                    // Empty chunks (idle ticks) go to everyone too: consumers use them for deadlines.
                    foreach (var target in targets)
                        if (!target.Channel.Writer.TryWrite(chunk))
                            target.Dropped(Logger);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Last subscriber left.
            }
            catch (Exception ex)
            {
                failure = ex;
                Logger.LogWarning("Shared TS read failed: {Error}", ex.Message);
            }
            finally
            {
                Subscriber[] orphans = [];
                lock (_lock)
                {
                    // Ended on its own (error or end of stream) rather than being stopped: pass
                    // that on to the subscribers, and let the next one start a fresh pump.
                    if (generation == _pumpGeneration && _pump is not null)
                    {
                        orphans = [.. _subscribers];
                        _stoppingPump = _pump;
                        _pump = null;
                        _pumpStop = null;
                    }
                }

                foreach (var orphan in orphans)
                    orphan.Channel.Writer.TryComplete(failure);

                Logger.LogDebug("Stopped shared TS read");
            }
        }

        private sealed class Subscriber
        {
            private long _dropped;

            public Channel<ReadOnlyMemory<byte>> Channel { get; } =
                System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(
                    new BoundedChannelOptions(SubscriberBacklog)
                    {
                        FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails, so drops are counted
                        SingleReader = true,
                        SingleWriter = true
                    });

            public void Dropped(ILogger logger)
            {
                var dropped = Interlocked.Increment(ref _dropped);
                // First drop, then every 100th: enough to notice without flooding.
                if (dropped == 1 || dropped % 100 == 0)
                    logger.LogWarning("A stream reader is falling behind; {Count} chunk(s) dropped for it", dropped);
            }
        }
    }
}
