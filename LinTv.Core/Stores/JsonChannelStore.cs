using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using Microsoft.Extensions.Options;

namespace LinTv.Core.Stores
{
    /// Lineup persisted as {StorageDirectory}/channels.json, cached in memory after first read.
    public class JsonChannelStore : IChannelStore
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<VirtualChannel>? _cache;

        public string FilePath { get; init; }

        public JsonChannelStore(IOptions<LinTvConfiguration> config)
        {
            FilePath = Path.Combine(config.Value.StorageDirectory, "channels.json");
        }

        public async Task<IReadOnlyList<VirtualChannel>> GetAllAsync()
        {
            await _gate.WaitAsync();
            try
            {
                return _cache ??= await JsonFile.ReadAsync<VirtualChannel[]>(FilePath) ?? [];
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<VirtualChannel?> FindAsync(int major, int minor)
        {
            var channels = await GetAllAsync();
            return channels.FirstOrDefault(c => c.Major == major && c.Minor == minor);
        }

        public async Task ReplaceAllAsync(IReadOnlyList<VirtualChannel> channels)
        {
            var snapshot = channels.ToArray();

            await _gate.WaitAsync();
            try
            {
                await JsonFile.WriteAtomicAsync(FilePath, snapshot);
                _cache = snapshot;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
