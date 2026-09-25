using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Stores
{
    public interface IChannelStore
    {
        Task<IReadOnlyList<VirtualChannel>> GetAllAsync();
        /// The index-th place major.minor is received (see VirtualChannel.Index); null if none.
        Task<VirtualChannel?> FindAsync(int major, int minor, int index = 0);
        Task ReplaceAllAsync(IReadOnlyList<VirtualChannel> channels);
    }
}
