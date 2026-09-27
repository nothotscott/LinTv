using LinTv.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LinTv.Core.Services
{
    /// Samples a channel's signal over a few seconds. A single reading hides the marginal case
    /// that matters most: a signal that locks, drops and relocks. That shows up here as a
    /// LockedPercent under 100 and a wide SNR range.
    public class SignalMeter : ISignalMeter
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

        public ILogger Logger { private get; set; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public SignalMeter(
            ILogger<SignalMeter> logger,
            ITunerArbiterService tunerArbiter)
        {
            Logger = logger;
            TunerArbiter = tunerArbiter;
        }

        public async Task<SignalReport> MeasureAsync(VirtualChannel channel, TimeSpan duration, CancellationToken ct)
        {
            var tune = await TunerArbiter.AcquireForMeasurementAsync(channel.FrequencyHz, ct);
            var samples = new List<SignalStatus> { tune.Signal };
            using var revocable = CancellationTokenSource.CreateLinkedTokenSource(ct, tune.Lease.Revoked);
            try
            {
                var until = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < until)
                {
                    await Task.Delay(SampleInterval, revocable.Token);
                    samples.Add(tune.Lease.Tuner.ReadSignalStatus());
                }
            }
            finally
            {
                // Also when the client gives up mid-measurement.
                await TunerArbiter.ReleaseAsync(tune.Lease);
            }

            var report = new SignalReport(
                channel.Id, channel.Index, channel.RfChannel, channel.FrequencyHz,
                tune.Shared, tune.TimeToLock is { } t ? Math.Round(t.TotalSeconds, 2) : null,
                samples.Count,
                Math.Round(samples.Count(s => s.Locked) * 100.0 / samples.Count, 1),
                samples[^1],
                Range(samples.Select(s => s.StrengthPercent)),
                Range(samples.Select(s => s.SnrDb)),
                channel.SignalStrengthPercent, channel.SignalSnrDb);

            Logger.LogInformation(
                "Signal {Id}/{Index} (RF {Rf}): locked {LockedPercent}% of {Samples} samples, SNR {SnrMin:F1}-{SnrMax:F1} dB (avg {SnrAvg:F1}, scan {ScanSnr:F1}), strength avg {StrengthAvg:F0}%",
                channel.Id, channel.Index, channel.RfChannel, report.LockedPercent, report.Samples,
                report.SnrDb.Min, report.SnrDb.Max, report.SnrDb.Average, channel.SignalSnrDb, report.StrengthPercent.Average);
            return report;
        }

        private static SignalRange Range(IEnumerable<double> values)
        {
            var list = values.ToList();
            return new SignalRange(Math.Round(list.Min(), 1), Math.Round(list.Average(), 1), Math.Round(list.Max(), 1));
        }
    }
}
