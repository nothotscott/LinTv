using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinTv.Core.Services
{
    /// Starts an EPG scan at each time in LinTvConfiguration.EpgScanTimes (server local time).
    /// Does nothing if the list is empty.
    public class EpgScheduleService : BackgroundService
    {
        /// Sleep in slices so a clock change (DST, NTP correction) can't push a run hours late.
        private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

        public ILogger Logger { private get; set; }

        public LinTvConfiguration Config { private get; init; }

        public IEpgScanner EpgScanner { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public EpgScheduleService(
            ILogger<EpgScheduleService> logger,
            IOptions<LinTvConfiguration> config,
            IEpgScanner epgScanner,
            IChannelStore channelStore)
        {
            Logger = logger;
            Config = config.Value;
            EpgScanner = epgScanner;
            ChannelStore = channelStore;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Already validated at startup (see Program.cs), so this can't fail here.
            DailySchedule.TryParse(Config.EpgScanTimes, out var schedule, out _);
            if (schedule is null || schedule.IsEmpty)
            {
                Logger.LogInformation("Periodic EPG scanning is off (EpgScanTimes is empty)");
                return;
            }

            Logger.LogInformation("Periodic EPG scanning at {Times} ({TimeZone})", schedule, TimeZoneInfo.Local.Id);

            while (!stoppingToken.IsCancellationRequested)
            {
                var next = schedule.NextAfter(DateTimeOffset.Now);
                Logger.LogDebug("Next scheduled EPG scan at {Next:yyyy-MM-dd HH:mm zzz}", next);

                try
                {
                    for (var wait = next - DateTimeOffset.Now; wait > TimeSpan.Zero; wait = next - DateTimeOffset.Now)
                        await Task.Delay(wait < MaxSleep ? wait : MaxSleep, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await RunScheduledScanAsync(stoppingToken);
            }
        }

        private async Task RunScheduledScanAsync(CancellationToken stoppingToken)
        {
            if ((await ChannelStore.GetAllAsync()).Count == 0)
            {
                Logger.LogWarning("Scheduled EPG scan skipped: the lineup is empty; run a channel scan first");
                return;
            }

            // Busy multiplexes (someone watching another frequency) are skipped by the scanner itself.
            if (EpgScanner.TryStartScan(stoppingToken))
                Logger.LogInformation("Scheduled EPG scan started");
            else
                Logger.LogInformation("Scheduled EPG scan skipped: a scan is already running");
        }
    }
}
