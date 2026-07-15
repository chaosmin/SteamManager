namespace SteamManager.Core.Services;

public interface IGameIdleService
{
    IReadOnlyList<int> PlayingAppIds { get; }
    bool IsRunning(int appId);
    Task StartAsync(int appId, CancellationToken ct = default);
    Task StopAsync(int appId);
    event Action<int>? MctReached;           // fires when MCT reached → game transitions to Scheduled
    event Action<int, int>? ProgressUpdated; // (appId, elapsedSessionMinutes)
}
