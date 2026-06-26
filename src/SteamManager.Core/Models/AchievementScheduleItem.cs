namespace SteamManager.Core.Models;

public class AchievementScheduleItem
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string AchievementId { get; set; } = string.Empty;
    public int OffsetMinutes { get; set; }   // relative to AccumulatedMinutes
    public bool Done { get; set; }
    public DateTime? UnlockedAt { get; set; } // UTC
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public GameConfig Game { get; set; } = null!;
}
