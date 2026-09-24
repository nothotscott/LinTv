using LinTv.Core.Configuration;
using LinTv.Core.Domain;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LinTv.Core.Stores
{
    /// Lineup persisted as {StorageDirectory}/channels.json, cached in memory after first read.
    public class JsonChannelStore : IChannelStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
                return _cache ??= await LoadAsync();
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
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Write-then-rename so a crash mid-write never leaves a truncated lineup.
                var temp = FilePath + ".tmp";
                await using (var stream = File.Create(temp))
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions);
                File.Move(temp, FilePath, overwrite: true);

                _cache = snapshot;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<IReadOnlyList<VirtualChannel>> LoadAsync()
        {
            if (!File.Exists(FilePath)) return [];

            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<VirtualChannel[]>(stream, JsonOptions) ?? [];
        }
    }
}
