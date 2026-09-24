using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Stores
{
    public interface IChannelStore
    {
        Task<IReadOnlyList<VirtualChannel>> GetAllAsync();
        Task<VirtualChannel?> FindAsync(int major, int minor);
        Task ReplaceAllAsync(IReadOnlyList<VirtualChannel> channels);
    }
}
