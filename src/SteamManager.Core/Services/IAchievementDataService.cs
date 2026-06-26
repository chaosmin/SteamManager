using SteamManager.Core.Dto;

namespace SteamManager.Core.Services;

public interface IAchievementDataService
{
    Task<List<AchievementIntervalDto>> GetIntervalsAsync(int appId, CancellationToken ct = default);
    Task RefreshCacheAsync(int appId, CancellationToken ct = default);
}
