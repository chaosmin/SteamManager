using SteamManager.Core.Models;

namespace SteamManager.Core.Services;

public record QueueEntry(
    int GameId,
    int AppId,
    string Name,
    string? NameI18n,
    int Position,
    GameStatus Status,
    string? HeaderUrl);

public interface IGameQueueService
{
    Task<List<QueueEntry>> GetQueueAsync();
    Task AddToQueueAsync(int gameId);
    Task RemoveFromQueueAsync(int gameId);
    Task StartQueueAsync(CancellationToken ct = default);
    Task AdvanceQueueAsync(int completedAppId, CancellationToken ct = default);
    Task ReorderAsync(int gameId, int newPosition, CancellationToken ct = default);
}
