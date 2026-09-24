using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Driver;
using LinTv.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Services
{
    public class TunerArbiterService : ITunerArbiterService
    {
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
            await _gate.WaitAsync(ct);
            try
            {
                if (_holders > 0 && _currentFrequency != frequencyHz)
                {
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
                    Logger.LogInformation("Locked {FrequencyHz} Hz (strength {Strength:F0}%, SNR {Snr:F1} dB), streaming", frequencyHz, status.StrengthPercent, status.SnrDb);
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
            try
            {
                await _gate.WaitAsync(ct);
                if (--_holders == 0)
                {
                    _currentFrequency = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
