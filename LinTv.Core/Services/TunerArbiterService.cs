using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace LinTv.Core.Services
{
    public class TunerArbiterService : ITunerArbiterService
    {
        /// Tuning + lock normally holds the gate for well under LockWaitSeconds; waiting longer
        /// than this for it points at a stuck tune or a leaked lease.
        private static readonly TimeSpan SlowGateWait = TimeSpan.FromSeconds(2);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private long? _currentFrequency;
        private int _holders;
        private TunerPriority _currentPriority;

        public ILogger Logger { private get; set; }

        public LinTvConfiguration Config { private get; init; }

        public IDvbTuner Tuner { private get; init; }

        public TunerArbiterService(
            ILogger<TunerArbiterService> logger,
            IOptions<LinTvConfiguration> config,
            IDvbTuner tuner)
        {
            Logger = logger;
            Config = config.Value;
            Tuner = tuner;
        }

        public async Task<IDvbTuner> AcquireAsync(long frequencyHz, TunerPriority priority, CancellationToken ct)
        {
            await WaitForGateAsync($"acquire {frequencyHz} Hz ({priority})", ct);
            try
            {
                Logger.LogTrace("Acquire {FrequencyHz} Hz for {Priority}: {Holders} holders on {Current} Hz",
                    frequencyHz, priority, _holders, _currentFrequency);

                if (_holders > 0 && _currentFrequency != frequencyHz)
                {
                    Logger.LogDebug("Tuner busy: {Priority} wants {FrequencyHz} Hz, {Holders} {CurrentPriority} holders on {Current} Hz",
                        priority, frequencyHz, _holders, _currentPriority, _currentFrequency);
                    if (priority <= _currentPriority)
                        throw new TunerBusyException($"Tuner in use on {_currentFrequency} Hz");
                    // Higher priority (e.g. live viewing over background EPG) could preempt here.
                    throw new NotImplementedException("preemption policy");
                }

                if (_holders == 0)
                {
                    await Tuner.TuneAsync(frequencyHz, ct);
                    var status = await Tuner.WaitForLockAsync(TimeSpan.FromSeconds(Config.LockWaitSeconds), ct);
                    if (!status.Locked)
                    {
                        throw new TunerProblemException($"No signal lock on {frequencyHz} Hz");
                    }
                    _currentFrequency = frequencyHz;
                    _currentPriority = priority;
                    Logger.LogInformation("Locked {FrequencyHz} Hz for {Priority} (strength {Strength:F0}%, SNR {Snr:F1} dB)",
                        frequencyHz, priority, status.StrengthPercent, status.SnrDb);
                }
                else
                {
                    Logger.LogDebug("Sharing tuner on {FrequencyHz} Hz with {Holders} holder(s) for {Priority}",
                        frequencyHz, _holders, priority);
                }

                _holders++;
                return Tuner;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ReleaseAsync(CancellationToken ct)
        {
            // Outside the try: if the wait is cancelled we never held the gate, so mustn't release it.
            await WaitForGateAsync("release", ct);
            try
            {
                if (_holders <= 0)
                {
                    // A release without a matching acquire: a caller bug. Don't go negative, or
                    // the next acquire would skip tuning.
                    Logger.LogError("Tuner released with no holders (current {Current} Hz)", _currentFrequency);
                    _holders = 0;
                }
                else if (--_holders == 0)
                {
                    Logger.LogTrace("Released last holder on {Current} Hz; tuner idle", _currentFrequency);
                    _currentFrequency = null;
                }
                else
                {
                    Logger.LogTrace("Released; {Holders} holder(s) remain on {Current} Hz", _holders, _currentFrequency);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task WaitForGateAsync(string operation, CancellationToken ct)
        {
            if (_gate.Wait(0)) return;

            Logger.LogTrace("Waiting for tuner gate to {Operation}", operation);
            var waited = Stopwatch.StartNew();
            await _gate.WaitAsync(ct);

            if (waited.Elapsed > SlowGateWait)
                Logger.LogWarning("Waited {Seconds:F1}s for tuner gate to {Operation}", waited.Elapsed.TotalSeconds, operation);
        }
    }
}
