using LinTv.Core.Domain;
using LinTv.Core.Driver;

namespace LinTv.Core.Services
{
    public interface ITunerArbiterService
    {
        Task<IDvbTuner> AcquireAsync(long frequencyHz, TunerPriority priority, CancellationToken ct);
        Task ReleaseAsync(CancellationToken ct);
    }
}