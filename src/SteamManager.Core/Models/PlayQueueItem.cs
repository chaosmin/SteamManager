namespace SteamManager.Core.Models;

public class PlayQueueItem
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public int SortOrder { get; set; }

    /// <summary>Accumulated idle minutes saved from previous paused sessions.</summary>
    public int SavedSessionMinutes { get; set; }

    /// <summary>True while this item is currently being actively idled.</summary>
    public bool IsActive { get; set; }

    public DateTime AddedAt { get; set; }
}
