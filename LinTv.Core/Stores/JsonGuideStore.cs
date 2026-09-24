using LinTv.Core.Domain;
using LinTv.Core.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Stores
{
    public class JsonGuideStore : IGuideStore
    {
        public Task<IReadOnlyList<GuideEvent>> GetAllAsync()
        {
            throw new NotImplementedException();
        }

        public Task MergeAsync(IReadOnlyList<GuideEvent> events)
        {
            throw new NotImplementedException();
        }
    }
}
