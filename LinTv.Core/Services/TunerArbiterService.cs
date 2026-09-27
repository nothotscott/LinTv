using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace LinTv.Core.Services
{
    /// One physical tuner, shared by callers on the same frequency. A "session" is one tune, from
    /// the first lease to the last release (or a force release). Leases carry their session, so a
    /// lease revoked by ForceReleaseAsync can't later decrement a newer session's holder count.
    public class TunerArbiterService : ITunerArbiterService
    {
        /// Tuning + lock normally holds the gate for well under LockWaitSeconds; waiting longer
        /// than this for it points at a stuck tune or a leaked lease.
        private static readonly TimeSpan SlowGateWait = TimeSpan.FromSeconds(2);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private long? _currentFrequency;
        private int _holders;
        private TunerPriority _currentPriority;
        private int _session;
        private CancellationTokenSource? _sessionRevoked;
        private DateTimeOffset? _sessionStarted;

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

        public TunerState State
        {
            get
            {
                // Racy snapshot is fine for display.
                return _holders > 0
                    ? new TunerState(_currentFrequency, _currentPriority, _holders, _sessionStarted)
                    : new TunerState(null, null, 0, null);
            }
        }

        public async Task<TunerLease> AcquireAsync(long frequencyHz, TunerPriority priority, CancellationToken ct) =>
            (await AcquireCoreAsync(frequencyHz, priority, requireLock: true, ct)).Lease;

        /// Diagnostic, user-initiated: same standing as a channel scan.
        public Task<TuneResult> AcquireForMeasurementAsync(long frequencyHz, CancellationToken ct) =>
            AcquireCoreAsync(frequencyHz, TunerPriority.ChannelScan, requireLock: false, ct);

        private async Task<TuneResult> AcquireCoreAsync(long frequencyHz, TunerPriority priority, bool requireLock, CancellationToken ct)
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

                if (_holders > 0)
                {
                    Logger.LogDebug("Sharing tuner on {FrequencyHz} Hz with {Holders} holder(s) for {Priority}",
                        frequencyHz, _holders, priority);
                    _holders++;
                    return new TuneResult(NewLease(frequencyHz, priority), Tuner.ReadSignalStatus(), TimeToLock: null, Shared: true);
                }

                var tuning = Stopwatch.StartNew();
                await Tuner.TuneAsync(frequencyHz, ct);
                var status = await Tuner.WaitForLockAsync(TimeSpan.FromSeconds(Config.LockWaitSeconds), ct);
                if (!status.Locked && requireLock)
                {
                    throw new TunerProblemException($"No signal lock on {frequencyHz} Hz");
                }

                // New session.
                _currentFrequency = frequencyHz;
                _currentPriority = priority;
                _session++;
                _sessionRevoked = new CancellationTokenSource();
                _sessionStarted = DateTimeOffset.Now;

                if (status.Locked)
                    Logger.LogInformation("Locked {FrequencyHz} Hz for {Priority} in {ElapsedMs} ms (strength {Strength:F0}%, SNR {Snr:F1} dB)",
                        frequencyHz, priority, tuning.ElapsedMilliseconds, status.StrengthPercent, status.SnrDb);
                else
                    Logger.LogInformation("Tuned {FrequencyHz} Hz for {Priority} without lock (strength {Strength:F0}%, SNR {Snr:F1} dB)",
                        frequencyHz, priority, status.StrengthPercent, status.SnrDb);

                _holders++;
                return new TuneResult(NewLease(frequencyHz, priority), status, status.Locked ? tuning.Elapsed : null, Shared: false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private TunerLease NewLease(long frequencyHz, TunerPriority priority) =>
            new(Tuner, frequencyHz, priority, _session, _sessionRevoked!.Token);

        public async Task ReleaseAsync(TunerLease lease)
        {
            if (!lease.MarkReleased())
            {
                Logger.LogTrace("Lease on {FrequencyHz} Hz released twice; ignored", lease.FrequencyHz);
                return;
            }

            // Uncancellable: the lease must be returned even when the caller's token is dead.
            await WaitForGateAsync("release", CancellationToken.None);
            try
            {
                if (lease.Session != _session || _holders <= 0)
                {
                    // Revoked by a force release; the session it belonged to is already gone.
                    Logger.LogDebug("Released a revoked {Priority} lease on {FrequencyHz} Hz; ignored",
                        lease.Priority, lease.FrequencyHz);
                }
                else if (--_holders == 0)
                {
                    Logger.LogTrace("Released last holder on {Current} Hz; tuner idle", _currentFrequency);
                    EndSession();
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

        public async Task<int> ForceReleaseAsync()
        {
            CancellationTokenSource? revoked;
            int holders;

            await WaitForGateAsync("force release", CancellationToken.None);
            try
            {
                holders = _holders;
                revoked = _sessionRevoked;
                if (holders > 0)
                    Logger.LogWarning("Force-releasing tuner on {Current} Hz: revoking {Holders} {Priority} lease(s) held since {Since:T}",
                        _currentFrequency, holders, _currentPriority, _sessionStarted);
                else
                    Logger.LogInformation("Force release requested, but the tuner is idle");

                _holders = 0;
                EndSession();
                _session++; // outstanding leases now belong to a dead session
            }
            finally
            {
                _gate.Release();
            }

            // Outside the gate: cancellation runs holders' callbacks, which may want the gate.
            revoked?.Cancel();
            return holders;
        }

        private void EndSession()
        {
            _currentFrequency = null;
            _sessionRevoked = null;
            _sessionStarted = null;
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
