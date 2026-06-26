namespace SteamManager.Core.Services;

public interface IGameIdleService
{
    bool IsRunning(int appId);
    Task StartAsync(int appId, CancellationToken ct = default);
    Task StopAsync(int appId);
    event Action<int, int>? ProgressUpdated; // (appId, accumulatedMinutes)
}
