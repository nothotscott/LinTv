using LinTv.Core.Domain;

namespace LinTv.Core.Services
{
    public interface IHdHomeRunLineupService
    {
        Task<IEnumerable<HdHomeRunLineupEntry>> GetLineupAsync(string baseUrl);
        Task<HdHomeRunLineupStatus> LineupStatusAsync();
    }
}