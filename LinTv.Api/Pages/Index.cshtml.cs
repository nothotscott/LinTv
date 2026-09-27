using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using LinTv.Core.Services;
using LinTv.Core.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace LinTv.Api.Pages
{
    public class IndexModel : PageModel
    {
        public LinTvConfiguration Config { get; init; }

        public IChannelScanner ChannelScanner { private get; init; }

        public IEpgScanner EpgScanner { private get; init; }

        public IChannelStore ChannelStore { private get; init; }

        public IGuideStore GuideStore { private get; init; }

        public IHostApplicationLifetime Lifetime { private get; init; }

        public ITunerArbiterService TunerArbiter { private get; init; }

        public IndexModel(
            IOptions<LinTvConfiguration> config,
            IChannelScanner channelScanner,
            IEpgScanner epgScanner,
            IChannelStore channelStore,
            IGuideStore guideStore,
            IHostApplicationLifetime lifetime,
            ITunerArbiterService tunerArbiter)
        {
            Config = config.Value;
            ChannelScanner = channelScanner;
            EpgScanner = epgScanner;
            ChannelStore = channelStore;
            GuideStore = guideStore;
            Lifetime = lifetime;
            TunerArbiter = tunerArbiter;
        }

        [TempData]
        public string? Message { get; set; }

        public ScanStatus ChannelScan { get; private set; } = ScanStatus.Idle;
        public ScanStatus EpgScan { get; private set; } = ScanStatus.Idle;
        public int ChannelCount { get; private set; }
        public int AlternateCount { get; private set; }
        public int MultiplexCount { get; private set; }
        public int GuideEventCount { get; private set; }
        public DateTimeOffset? GuideUntil { get; private set; }
        public DailySchedule? EpgSchedule { get; private set; }
        public DateTimeOffset? NextEpgScan { get; private set; }
        public TunerState Tuner { get; private set; } = new(null, null, 0, null);

        public string BaseUrl => $"{Request.Scheme}://{Request.Host}";

        public async Task OnGetAsync()
        {
            ChannelScan = ChannelScanner.Status;
            EpgScan = EpgScanner.Status;
            Tuner = TunerArbiter.State;

            var channels = await ChannelStore.GetAllAsync();
            ChannelCount = channels.Count(c => c.IsPrimary);
            AlternateCount = channels.Count - ChannelCount;
            MultiplexCount = channels.Select(c => c.FrequencyHz).Distinct().Count();

            var events = await GuideStore.GetAllAsync();
            GuideEventCount = events.Count;
            GuideUntil = events.Count > 0 ? events.Max(e => e.Start + e.Duration) : null;

            // Validated at startup, so this parses.
            if (DailySchedule.TryParse(Config.EpgScanTimes, out var schedule, out _) && !schedule.IsEmpty)
            {
                EpgSchedule = schedule;
                NextEpgScan = schedule.NextAfter(DateTimeOffset.Now);
            }
        }

        public IActionResult OnPostScanChannels()
        {
            Message = ChannelScanner.TryStartScan(Lifetime.ApplicationStopping)
                ? "Channel scan started. It takes a few minutes."
                : "A channel scan is already running.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDisconnectAsync()
        {
            var revoked = await TunerArbiter.ForceReleaseAsync();
            Message = revoked > 0
                ? $"Disconnected: ended {revoked} stream(s)/scan(s) holding the tuner."
                : "The tuner was already idle.";
            return RedirectToPage();
        }

        public IActionResult OnPostScanEpg()
        {
            Message = EpgScanner.TryStartScan(Lifetime.ApplicationStopping)
                ? "EPG scan started."
                : "An EPG scan is already running.";
            return RedirectToPage();
        }
    }
}
