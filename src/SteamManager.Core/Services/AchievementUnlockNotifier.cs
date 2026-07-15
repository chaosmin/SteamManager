namespace SteamManager.Core.Services;

public record UnlockedAchievementInfo(string GameName, string AchievementName, string? IconUrl);
public record CompletedGameInfo(string GameName, int AchievementCount);

public class AchievementUnlockNotifier
{
    public event Action<UnlockedAchievementInfo>? AchievementUnlocked;
    public event Action<CompletedGameInfo>? GameCompleted;

    public void Notify(UnlockedAchievementInfo info) => AchievementUnlocked?.Invoke(info);
    public void NotifyCompleted(CompletedGameInfo info) => GameCompleted?.Invoke(info);
}
