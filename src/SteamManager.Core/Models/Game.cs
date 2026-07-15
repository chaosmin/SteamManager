namespace SteamManager.Core.Models;

public enum GameStatus { Idle, Playing, Scheduled, Completed }

public class Game
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameI18n { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Idle;

    // Playtime tracking (re-anchored at each Start via Steam API)
    public int SteamPlaytimeAtRefresh { get; set; }   // Steam-recorded minutes at last Start/Refresh
    public int? TargetMinutes { get; set; }            // MCT from SteamHunters (or reference player duration)
    public DateTime? SessionStartedAt { get; set; }   // UTC — when current Playing session began

    // Achievement cache freshness
    public DateTime? AchievementsCachedAt { get; set; }

    // Steam trading card drops remaining (null = not yet synced)
    public int? DropsRemaining { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Achievement> Achievements { get; set; } = [];
    public GameReferencePlayer? ReferencePlayer { get; set; }
}
