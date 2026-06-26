namespace SteamManager.Core.Models;

public class GameProgress
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public int AccumulatedMinutes { get; set; }
    public DateTime? LastSessionStart { get; set; } // UTC
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public GameConfig Game { get; set; } = null!;
}
