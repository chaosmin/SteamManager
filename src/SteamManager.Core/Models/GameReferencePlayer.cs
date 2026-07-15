namespace SteamManager.Core.Models;

public class GameReferencePlayer
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public string PlayerUrl { get; set; } = string.Empty;  // SteamHunters profile URL, max 512 chars
    public bool OverrideBurstCheck { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
