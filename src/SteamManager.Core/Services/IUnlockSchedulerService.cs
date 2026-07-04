namespace SteamManager.Core.Services;

public interface IUnlockSchedulerService
{
    /// <summary>Starts the idle timer for appId, restoring saved delta from game.SavedIdleDeltaMinutes in DB.</summary>
    Task StartAsync(int appId, CancellationToken ct = default);
    /// <summary>Stops the idle timer and persists the current delta to game.SavedIdleDeltaMinutes.</summary>
    Task StopAsync(int appId);
    bool IsRunning(int appId);
    /// <summary>Returns idle minutes elapsed since last unlocked achievement (pure delta, excludes base offset).</summary>
    TimeSpan? GetElapsedSessionTime(int appId);
    /// <summary>Returns how long until the achievement at targetOffsetMinutes unlocks, using the live session timer.</summary>
    TimeSpan? GetTimeUntilNext(int appId, int targetOffsetMinutes);
}
