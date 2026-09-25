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
    /// Walks the ATSC RF plan. For each channel that locks, reads the PSIP Virtual Channel
    /// Table from the multiplex, which maps major.minor numbers to MPEG program numbers.
    public class ChannelScanner : IChannelScanner
    {
        private int _running;
        private volatile ScanStatus _status = ScanStatus.Idle;

        public ILogger Logger { private get; set; }

        public LinTvConfiguration Config { private get; init; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public ScanStatus Status => _status;

        public ChannelScanner(
            ILogger<ChannelScanner> logger,
            IOptions<LinTvConfiguration> config,
            ITunerArbiterService tunerArbiter,
            IChannelStore channelStore)
        {
            Logger = logger;
            Config = config.Value;
            TunerArbiter = tunerArbiter;
            ChannelStore = channelStore;
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

        public Task<IReadOnlyList<VirtualChannel>> ScanAsync(CancellationToken ct)
        {
            if (!TryBegin()) throw new InvalidOperationException("A channel scan is already running");
            return RunAsync(ct);
        }

        private bool TryBegin()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
            _status = new ScanStatus(true, 0, 0, _status.LastCompleted, null);
            return true;
        }

        private async Task<IReadOnlyList<VirtualChannel>> RunAsync(CancellationToken ct)
        {
            var plan = AtscChannelPlan.UsBroadcast;
            var found = new List<VirtualChannel>();

            try
            {
                Logger.LogInformation("Channel scan started: RF {First}-{Last}", plan[0].Number, plan[^1].Number);

                for (int i = 0; i < plan.Count; i++)
                {
                    found.AddRange(await ScanRfChannelAsync(plan[i], ct));
                    _status = _status with { ProgressPercent = (i + 1) * 100 / plan.Count, Found = found.Count };
                }

                var channels = RankBySignal(found);

                // Don't wipe a good lineup because the antenna was unplugged.
                if (channels.Count > 0)
                    await ChannelStore.ReplaceAllAsync(channels);
                else
                    Logger.LogWarning("Channel scan found nothing; keeping the existing lineup");

                Logger.LogInformation("Channel scan complete: {Count} channels", channels.Count);
                _status = new ScanStatus(false, 100, channels.Count, DateTimeOffset.UtcNow, null);
                return channels;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) Logger.LogInformation("Channel scan cancelled");
                else Logger.LogError(ex, "Channel scan failed");

                _status = _status with { InProgress = false, LastError = ex.Message };
                throw;
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        /// The same virtual channel on several RFs (a translator, or a neighbouring market's
        /// signal) keeps every copy. Index 0 goes to the best SNR, then strength, then lowest RF.
        private List<VirtualChannel> RankBySignal(IEnumerable<VirtualChannel> found)
        {
            var ranked = new List<VirtualChannel>();
            foreach (var group in found.GroupBy(c => c.Id))
            {
                var ordered = group
                    .OrderByDescending(c => c.SignalSnrDb)
                    .ThenByDescending(c => c.SignalStrengthPercent)
                    .ThenBy(c => c.RfChannel)
                    .Select((c, index) => c with { Index = index })
                    .ToList();

                if (ordered.Count > 1)
                    Logger.LogInformation("{Id} received on {Count} RFs, best first: {Candidates}", group.Key, ordered.Count,
                        string.Join("; ", ordered.Select(c => $"RF {c.RfChannel} SNR {c.SignalSnrDb:F1} dB, strength {c.SignalStrengthPercent:F0}%")));

                ranked.AddRange(ordered);
            }

            return ranked.OrderBy(c => c.Major).ThenBy(c => c.Minor).ThenBy(c => c.Index).ToList();
        }

        private async Task<IReadOnlyList<VirtualChannel>> ScanRfChannelAsync(RfChannel rf, CancellationToken ct)
        {
            IDvbTuner tuner;
            try
            {
                tuner = await TunerArbiter.AcquireAsync(rf.FrequencyHz, TunerPriority.ChannelScan, ct);
            }
            catch (TunerProblemException)
            {
                Logger.LogDebug("RF {Rf} ({FrequencyHz} Hz): no lock", rf.Number, rf.FrequencyHz);
                return [];
            }
            // TunerBusyException propagates: someone with higher priority holds the tuner.

            var vctTimeout = TimeSpan.FromSeconds(Config.ChannelScanTimeoutSeconds);   
            try
            {
                Logger.LogTrace("RF {Rf}: scanning Vct within {Timeout}s", rf.Number, vctTimeout.TotalSeconds);
                var signalAtLock = tuner.ReadSignalStatus();
                var vct = await ReadVctAsync(tuner, vctTimeout, ct);

                // Two samples a moment apart smooth out a single noisy reading.
                var signalAfterVct = tuner.ReadSignalStatus();
                var strength = (signalAtLock.StrengthPercent + signalAfterVct.StrengthPercent) / 2;
                var snr = (signalAtLock.SnrDb + signalAfterVct.SnrDb) / 2;
                Logger.LogDebug("RF {Rf}: signal strength {Strength:F0}%, SNR {Snr:F1} dB", rf.Number, strength, snr);
                if (vct is null)
                {
                    Logger.LogWarning("RF {Rf}: locked, but no VCT within {Timeout}s", rf.Number, vctTimeout.TotalSeconds);
                    return [];
                }
                if (!vct.Channels.Any())
                {
                    Logger.LogWarning("RF {Rf}: locked, VCT empty within {Timeout}s", rf.Number, vctTimeout.TotalSeconds);
                    return [];
                }

                var channels = vct.Channels
                    .Where(c => !c.Hidden
                        && c.ModulationMode == VirtualChannelTable.Modulation8Vsb
                        && c.ServiceType is VirtualChannelTable.ServiceTypeDigitalTv or VirtualChannelTable.ServiceTypeAudio
                        // A VCT may also describe channels carried in other multiplexes.
                        && c.ChannelTsid == vct.TransportStreamId)
                    .Select(c => new VirtualChannel(
                        c.Major, c.Minor, c.ShortName,
                        rf.Number, rf.FrequencyHz,
                        c.ProgramNumber, c.SourceId,
                        SignalStrengthPercent: Math.Round(strength, 1), SignalSnrDb: Math.Round(snr, 1)))
                    .ToList();

                Logger.LogInformation("RF {Rf}: {Channels}", rf.Number,
                    channels.Count == 0 ? "no usable channels" : string.Join(", ", channels.Select(c => $"{c.Id} {c.ShortName}")));
                return channels;
            }
            finally
            {
                await TunerArbiter.ReleaseAsync(CancellationToken.None);
            }
        }

        private static async Task<VctCollector?> ReadVctAsync(IDvbTuner tuner, TimeSpan vctTimeout, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(vctTimeout);

            var framer = new TsPacketFramer();
            var assembler = new PsiSectionAssembler(VirtualChannelTable.BasePid);
            var collector = new VctCollector();
            var sections = new List<byte[]>();

            try
            {
                await foreach (var chunk in tuner.ReadTransportStreamAsync(timeout.Token))
                {
                    framer.Push(chunk.Span, packet => assembler.Feed(packet, sections));

                    foreach (var section in sections)
                        if (VirtualChannelTable.TryParse(section, out var vct)) collector.Add(vct);
                    sections.Clear();

                    if (collector.IsComplete) return collector;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // VctTimeout elapsed
            }

            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}
