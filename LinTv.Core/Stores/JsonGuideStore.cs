using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using Microsoft.Extensions.Options;

namespace LinTv.Core.Stores
{
    /// Guide persisted as {StorageDirectory}/guide.json, cached in memory after first read.
    public class JsonGuideStore : IGuideStore
    {
        /// Recently ended programmes stay around for clients that look back a little.
        private static readonly TimeSpan RetainPast = TimeSpan.FromHours(6);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<GuideEvent>? _cache;

        public string FilePath { get; init; }

        public JsonGuideStore(IOptions<LinTvConfiguration> config)
        {
            FilePath = Path.Combine(config.Value.StorageDirectory, "guide.json");
        }

        public async Task<IReadOnlyList<GuideEvent>> GetAllAsync()
        {
            await _gate.WaitAsync();
            try
            {
                return _cache ??= await JsonFile.ReadAsync<GuideEvent[]>(FilePath) ?? [];
            }
            finally
            {
                _gate.Release();
            }
        }

        /// For each channel in <paramref name="events"/>, the new events replace every stored
        /// event that starts in the time window they cover. That handles reschedules and
        /// cancellations, not just title changes. Other channels and times are kept, apart from
        /// programmes that ended more than RetainPast ago.
        public async Task MergeAsync(IReadOnlyList<GuideEvent> events)
        {
            await _gate.WaitAsync();
            try
            {
                var existing = _cache ??= await JsonFile.ReadAsync<GuideEvent[]>(FilePath) ?? [];
                var cutoff = DateTimeOffset.UtcNow - RetainPast;

                var windows = events
                    .GroupBy(e => e.ChannelId)
                    .ToDictionary(g => g.Key, g => (Start: g.Min(e => e.Start), End: g.Max(e => e.Start + e.Duration)));

                var merged = existing
                    .Where(e => e.Start + e.Duration >= cutoff)
                    .Where(e => !(windows.TryGetValue(e.ChannelId, out var w) && e.Start >= w.Start && e.Start < w.End))
                    .Concat(events.Where(e => e.Start + e.Duration >= cutoff))
                    .OrderBy(e => e.ChannelId).ThenBy(e => e.Start)
                    .ToArray();

                await JsonFile.WriteAtomicAsync(FilePath, merged);
                _cache = merged;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
