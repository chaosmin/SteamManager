namespace SteamManager.Core.Services;

public interface IAchievementDataService
{
    /// <summary>Fetch schema + intervals and upsert Achievement rows for the given game.</summary>
    Task LoadAchievementsAsync(int gameId, int appId, CancellationToken ct = default);
}
