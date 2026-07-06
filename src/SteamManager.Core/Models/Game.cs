namespace SteamManager.Core.Models;

public enum GameStatus { Idle, Running, Completed }

public class Game
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameI18n { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Idle;

    // Play time tracking (merged from GameProgress)
    public int TotalPlayMinutes { get; set; }
    public DateTime? LastSessionStart { get; set; }   // UTC

    // Reference play minutes: median total playtime of 100%-completion reference players
    public int? ReferencePlayMinutes { get; set; }

    // URL of the SteamHunter (or similar) reference page used to derive achievement unlock order/timing
    public string? ReferenceUrl { get; set; }

    // Persisted idle-session delta: minutes elapsed beyond lastUnlockedOffset at last stop.
    // Restored by UnlockSchedulerService on restart to resume the built-in timer.
    public int SavedIdleDeltaMinutes { get; set; }

    // Achievement cache freshness
    public DateTime? AchievementsCachedAt { get; set; }  // UTC

    // Steam trading card drops remaining (null = not yet synced)
    public int? DropsRemaining { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Achievement> Achievements { get; set; } = [];
}
