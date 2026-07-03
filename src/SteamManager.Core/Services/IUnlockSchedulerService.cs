namespace SteamManager.Core.Services;

public interface IUnlockSchedulerService
{
    Task StartAsync(int appId, CancellationToken ct = default);
    Task StartAsync(int appId, int resumeFromMinutes, CancellationToken ct = default);
    Task StopAsync(int appId);
    bool IsRunning(int appId);
    TimeSpan? GetElapsedSessionTime(int appId);
}
