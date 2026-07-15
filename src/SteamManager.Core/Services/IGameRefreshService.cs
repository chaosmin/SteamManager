namespace SteamManager.Core.Services;

public interface IGameRefreshService
{
    /// <summary>Refresh achievement schedule using the game's linked reference player.</summary>
    Task RefreshAsync(int gameId, int appId, CancellationToken ct = default);

    /// <summary>Reset all achievements to unlocked=false, then run full RefreshAsync.</summary>
    Task ForceRefreshAsync(int gameId, int appId, CancellationToken ct = default);

    /// <summary>
    /// Pull latest playtime + achievement unlock status from Steam only (no SteamHunters).
    /// If playtime goal met and all achievements unlocked, marks game as Completed.
    /// </summary>
    Task SyncAsync(int gameId, int appId, CancellationToken ct = default);
}
