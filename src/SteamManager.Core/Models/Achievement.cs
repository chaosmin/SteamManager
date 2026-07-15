namespace SteamManager.Core.Models;

public class Achievement
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int AppId { get; set; }
    public string ApiName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DisplayNameI18n { get; set; }
    public string? Description { get; set; }
    public string? DescriptionI18n { get; set; }
    public double GlobalPercent { get; set; }
    public string? IconUrl { get; set; }
    public string? IconGrayUrl { get; set; }

    // Scheduling
    public DateTime? ScheduledUnlockAt { get; set; }  // UTC absolute time to unlock; null = not scheduled
    public bool IsUnlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Game Game { get; set; } = null!;
}
