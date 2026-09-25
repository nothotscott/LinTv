using LinTv.Core.Domain;

namespace LinTv.Core.Stores
{
    public interface IChannelMapStore
    {
        Task<IReadOnlyList<ChannelMapping>> GetAllAsync();

        /// Adds or replaces the mapping for mapping.Channel.
        Task SetAsync(ChannelMapping mapping);

        /// False if there was no mapping for the channel.
        Task<bool> RemoveAsync(string channel);
    }
}
