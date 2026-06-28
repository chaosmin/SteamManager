namespace SteamManager.Core.Services;

public record UnlockedAchievementInfo(string GameName, string AchievementName, string? IconUrl);

/// <summary>
/// Singleton event bus: raised by UnlockSchedulerService when an achievement is stored.
/// Blazor components subscribe to show in-UI toast notifications.
/// </summary>
public class AchievementUnlockNotifier
{
    public event Action<UnlockedAchievementInfo>? AchievementUnlocked;

    public void Notify(UnlockedAchievementInfo info) => AchievementUnlocked?.Invoke(info);
}
