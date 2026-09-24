using LinTv.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Stores
{
    public interface IGuideStore
    {
        Task<IReadOnlyList<GuideEvent>> GetAllAsync();
        Task MergeAsync(IReadOnlyList<GuideEvent> events);
    }
}
