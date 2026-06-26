namespace SteamManager.Core.Models;

public class AchievementCache
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Data { get; set; } = "[]";  // JSON: AchievementIntervalDto[]
    public DateTime FetchedAt { get; set; }   // UTC
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
