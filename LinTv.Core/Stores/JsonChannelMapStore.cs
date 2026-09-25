using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using Microsoft.Extensions.Options;

namespace LinTv.Core.Stores
{
    /// User-maintained mappings in {StorageDirectory}/channel-map.json, e.g.
    ///   [ { "Channel": "13.1", "DisplayNames": [ "FOX" ] } ]
    /// Meant to be edited by hand as well as through the API, so it reloads whenever the file's
    /// timestamp changes.
    public class JsonChannelMapStore : IChannelMapStore
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<ChannelMapping> _cache = [];
        private DateTime? _cacheStamp;

        public string FilePath { get; init; }

        public JsonChannelMapStore(IOptions<LinTvConfiguration> config)
        {
            FilePath = Path.Combine(config.Value.StorageDirectory, "channel-map.json");
        }

        public async Task<IReadOnlyList<ChannelMapping>> GetAllAsync()
        {
            await _gate.WaitAsync();
            try
            {
                return await LoadIfChangedAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetAsync(ChannelMapping mapping)
        {
            await _gate.WaitAsync();
            try
            {
                var mappings = (await LoadIfChangedAsync())
                    .Where(m => m.Channel != mapping.Channel)
                    .Append(mapping);
                await SaveAsync(mappings);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> RemoveAsync(string channel)
        {
            await _gate.WaitAsync();
            try
            {
                var mappings = await LoadIfChangedAsync();
                if (!mappings.Any(m => m.Channel == channel)) return false;

                await SaveAsync(mappings.Where(m => m.Channel != channel));
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<IReadOnlyList<ChannelMapping>> LoadIfChangedAsync()
        {
            DateTime? stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null;
            if (stamp == _cacheStamp) return _cache;

            _cache = await JsonFile.ReadAsync<ChannelMapping[]>(FilePath) ?? [];
            _cacheStamp = stamp;
            return _cache;
        }

        private async Task SaveAsync(IEnumerable<ChannelMapping> mappings)
        {
            var sorted = mappings.OrderBy(m => m.Channel, ChannelIdComparer.Instance).ToArray();
            await JsonFile.WriteAtomicAsync(FilePath, sorted);
            _cache = sorted;
            _cacheStamp = File.GetLastWriteTimeUtc(FilePath);
        }

        /// Orders "2.1" before "13.1" (numeric, not string, comparison).
        private sealed class ChannelIdComparer : IComparer<string>
        {
            public static readonly ChannelIdComparer Instance = new();

            public int Compare(string? x, string? y) => Key(x).CompareTo(Key(y));

            private static (int, int, string) Key(string? id)
            {
                var parts = (id ?? "").Split('.');
                return parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
                    ? (major, minor, "")
                    : (int.MaxValue, int.MaxValue, id ?? "");
            }
        }
    }
}
