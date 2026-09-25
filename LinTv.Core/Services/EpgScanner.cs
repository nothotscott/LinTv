using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Exceptions;
using LinTv.Core.Mpeg;
using LinTv.Core.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinTv.Core.Services
{
    /// Reads the over-the-air guide (PSIP EIT/ETT) from each multiplex in the lineup. A
    /// multiplex's EITs cover all of its subchannels, so each RF channel is tuned once.
    public class EpgScanner : IEpgScanner
    {
        private int _running;
        private volatile ScanStatus _status = ScanStatus.Idle;

        public ILogger Logger { private get; set; }

        public LinTvConfiguration Config { private get; init; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public IGuideStore GuideStore { private get; init; }

        public ScanStatus Status => _status;

        public EpgScanner(
            ILogger<EpgScanner> logger,
            IOptions<LinTvConfiguration> config,
            ITunerArbiterService tunerArbiter,
            IChannelStore channelStore,
            IGuideStore guideStore)
        {
            Logger = logger;
            Config = config.Value;
            TunerArbiter = tunerArbiter;
            ChannelStore = channelStore;
            GuideStore = guideStore;
        }

        public bool TryStartScan(CancellationToken ct)
        {
            if (!TryBegin()) return false;

            _ = Task.Run(async () =>
            {
                try { await RunAsync(ct); }
                catch { /* already logged and recorded in Status */ }
            }, CancellationToken.None);
            return true;
        }

        public Task<IReadOnlyList<GuideEvent>> ScanAsync(CancellationToken ct)
        {
            if (!TryBegin()) throw new InvalidOperationException("An EPG scan is already running");
            return RunAsync(ct);
        }

        private bool TryBegin()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
            _status = new ScanStatus(true, 0, 0, _status.LastCompleted, null);
            return true;
        }

        private async Task<IReadOnlyList<GuideEvent>> RunAsync(CancellationToken ct)
        {
            try
            {
                var channels = await ChannelStore.GetAllAsync();
                if (channels.Count == 0)
                    throw new InvalidOperationException("The lineup is empty; run a channel scan first");

                // Guide data comes from each channel's primary entry; alternates carry the same
                // programmes, so tuning them too would just double the scan time.
                var multiplexes = channels.Where(c => c.IsPrimary).GroupBy(c => c.FrequencyHz).ToList();
                var events = new List<GuideEvent>();
                Logger.LogInformation("EPG scan started: {Count} multiplexes", multiplexes.Count);

                for (int i = 0; i < multiplexes.Count; i++)
                {
                    events.AddRange(await ScanMultiplexAsync(multiplexes[i].ToList(), ct));
                    _status = _status with { ProgressPercent = (i + 1) * 100 / multiplexes.Count, Found = events.Count };
                }

                if (events.Count > 0)
                    await GuideStore.MergeAsync(events);
                else
                    Logger.LogWarning("EPG scan found no events; keeping the existing guide");

                Logger.LogInformation("EPG scan complete: {Count} events", events.Count);
                _status = new ScanStatus(false, 100, events.Count, DateTimeOffset.UtcNow, null);
                return events;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) Logger.LogInformation("EPG scan cancelled");
                else Logger.LogError(ex, "EPG scan failed");

                _status = _status with { InProgress = false, LastError = ex.Message };
                throw;
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        private async Task<IReadOnlyList<GuideEvent>> ScanMultiplexAsync(IReadOnlyList<VirtualChannel> channels, CancellationToken ct)
        {
            var rf = channels[0].RfChannel;
            var frequencyHz = channels[0].FrequencyHz;

            IDvbTuner tuner;
            try
            {
                // Lowest priority: shares the tuner if a viewer is already on this multiplex,
                // and backs off if they're on another one.
                tuner = await TunerArbiter.AcquireAsync(frequencyHz, TunerPriority.BackgroundEpg, ct);
            }
            catch (Exception ex) when (ex is TunerProblemException or TunerBusyException)
            {
                Logger.LogWarning("RF {Rf}: skipped EPG ({Reason})", rf, ex.Message);
                return [];
            }

            var timeout = TimeSpan.FromSeconds(Config.EpgScanTimeoutSeconds);
            try
            {
                var bySource = channels.ToDictionary(c => c.SourceId);
                var collector = new PsipGuideCollector(bySource.Keys.ToHashSet());
                bool complete = await CollectAsync(tuner, collector, timeout, ct);

                var events = collector.GetEvents()
                    .Select(x => new GuideEvent(
                        bySource[x.Event.SourceId].Id, x.Event.EventId,
                        x.Start, TimeSpan.FromSeconds(x.Event.LengthSeconds),
                        x.Event.Title!, x.Description))
                    .ToList();

                Logger.LogInformation(
                    "RF {Rf}: {Events} events, {Texts} descriptions from {EitPids} EIT PIDs ({Outcome})",
                    rf, events.Count, collector.TextCount, collector.EitPidCount,
                    complete ? "complete" : $"partial, stopped after {timeout.TotalSeconds}s");
                return events;
            }
            finally
            {
                await TunerArbiter.ReleaseAsync(CancellationToken.None);
            }
        }

        /// Returns true if the collector completed, false if the timeout cut it short. Partial
        /// data is still kept: it usually covers the next few hours, which matter most.
        private static async Task<bool> CollectAsync(IDvbTuner tuner, PsipGuideCollector collector,
            TimeSpan collectTimeout, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(collectTimeout);

            var framer = new TsPacketFramer();
            try
            {
                await foreach (var chunk in tuner.ReadTransportStreamAsync(timeout.Token))
                {
                    framer.Push(chunk.Span, collector.Feed);
                    if (collector.IsComplete) return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // collectTimeout elapsed
            }

            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
