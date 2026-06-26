namespace SteamManager.Core.Models;

public enum GameStatus { Idle, Running, Completed }

public class GameConfig
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal TargetHours { get; set; }
    public bool EnableAchievements { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Idle;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public GameProgress? Progress { get; set; }
    public ICollection<AchievementScheduleItem> AchievementSchedule { get; set; } = [];
}
