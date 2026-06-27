namespace SteamManager.Core.Models;

public class Achievement
{
    public int Id { get; set; }
    public int GameId { get; set; }       // FK to Game.Id
    public int AppId { get; set; }        // denormalized for quick Steam API lookups
    public string ApiName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DisplayNameI18n { get; set; }
    public double GlobalPercent { get; set; }
    public string? IconUrl { get; set; }
    public string? IconGrayUrl { get; set; }

    // Unlock scheduling
    public int UnlockOffsetMinutes { get; set; }  // relative to accumulated play time
    public bool IsUnlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }     // UTC

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Game Game { get; set; } = null!;
}
