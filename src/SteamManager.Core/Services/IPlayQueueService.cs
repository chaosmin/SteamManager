namespace SteamManager.Core.Services;

public record QueueEntry(
    int QueueItemId,
    int GameId,
    int AppId,
    string Name,
    string? NameI18n,
    int SortOrder,
    bool IsActive,
    int SavedSessionMinutes);

public interface IPlayQueueService
{
    Task<List<QueueEntry>> GetQueueAsync();
    Task AddToQueueAsync(int gameId);
    Task RemoveFromQueueAsync(int queueItemId);
}
