namespace SteamManager.Core.Models;

public class GameQueueEntry
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public int Position { get; set; }   // 0-based; lower = higher priority
    public DateTime AddedAt { get; set; }
}
