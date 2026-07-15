using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class GameQueueService : IGameQueueService
{
    private readonly IGameIdleService _idleService;
    private readonly ISteamSessionService _session;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GameQueueService> _logger;

    public GameQueueService(
        IGameIdleService idleService,
        ISteamSessionService session,
        IServiceScopeFactory scopeFactory,
        ILogger<GameQueueService> logger)
    {
        _idleService = idleService;
        _session = session;
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Auto-advance queue when MCT is reached
        idleService.MctReached += appId =>
            _ = Task.Run(() => AdvanceQueueAsync(appId));
    }

    public async Task<List<QueueEntry>> GetQueueAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status != GameStatus.Completed)
            .OrderBy(q => q.Position)
            .Select(q => new QueueEntry(
                q.GameId, q.Game.AppId,
                q.Game.Name, q.Game.NameI18n,
                q.Position, q.Game.Status,
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{q.Game.AppId}/header.jpg"))
            .ToListAsync();
    }

    public async Task AddToQueueAsync(int gameId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.GameQueue.AnyAsync(q => q.GameId == gameId)) return;
        var maxPos = await db.GameQueue.AnyAsync()
            ? await db.GameQueue.MaxAsync(q => q.Position) : -1;
        db.GameQueue.Add(new GameQueueEntry { GameId = gameId, Position = maxPos + 1, AddedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        _logger.LogInformation("GameQueue: game {GameId} added at position {Pos}", gameId, maxPos + 1);
    }

    public async Task RemoveFromQueueAsync(int gameId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.GameQueue.Include(q => q.Game).FirstOrDefaultAsync(q => q.GameId == gameId);
        if (item == null) return;
        if (item.Game.Status == GameStatus.Playing) await _idleService.StopAsync(item.Game.AppId);
        db.GameQueue.Remove(item);
        await db.SaveChangesAsync();
        _logger.LogInformation("GameQueue: game {GameId} removed", gameId);
    }

    public async Task StartQueueAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        var maxSlots = cfg?.MaxConcurrentGames ?? 1;
        var slotsAvailable = maxSlots - _idleService.PlayingAppIds.Count;
        if (slotsAvailable <= 0) return;

        var idleItems = await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status == GameStatus.Idle)
            .OrderBy(q => q.Position)
            .Take(slotsAvailable)
            .ToListAsync(ct);

        foreach (var item in idleItems)
            await StartGameFromQueueAsync(item.Game, db, ct);
    }

    public async Task AdvanceQueueAsync(int completedAppId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Remove the MCT-completed game from queue
        var completed = await db.GameQueue
            .Include(q => q.Game)
            .FirstOrDefaultAsync(q => q.Game.AppId == completedAppId, ct);
        if (completed != null) { db.GameQueue.Remove(completed); await db.SaveChangesAsync(ct); }

        // Fill available slot
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        var maxSlots = cfg?.MaxConcurrentGames ?? 1;
        if (maxSlots - _idleService.PlayingAppIds.Count <= 0) return;
        var next = await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status == GameStatus.Idle)
            .OrderBy(q => q.Position)
            .FirstOrDefaultAsync(ct);
        if (next != null)
        {
            await StartGameFromQueueAsync(next.Game, db, ct);
            _logger.LogInformation("GameQueue: auto-advanced to game {AppId}", next.Game.AppId);
        }
    }

    public async Task ReorderAsync(int gameId, int newPosition, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Only Idle items can be reordered
        var items = await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status == GameStatus.Idle)
            .OrderBy(q => q.Position)
            .ToListAsync(ct);
        var target = items.FirstOrDefault(q => q.GameId == gameId);
        if (target == null) return;
        items.Remove(target);
        items.Insert(Math.Clamp(newPosition, 0, items.Count), target);
        for (int i = 0; i < items.Count; i++) items[i].Position = i;
        await db.SaveChangesAsync(ct);
    }

    private async Task StartGameFromQueueAsync(Game game, AppDbContext db, CancellationToken ct)
    {
        // Re-anchor Steam playtime snapshot
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        if (cfg?.WebApiKey != null && _session.SteamId64.HasValue)
        {
            using var innerScope = _scopeFactory.CreateScope();
            var steamApi = innerScope.ServiceProvider.GetRequiredService<SteamWebApiClient>();
            var currentPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(
                (long)_session.SteamId64.Value, game.AppId, cfg.WebApiKey, ct);
            game.SteamPlaytimeAtRefresh = currentPlaytime;
        }

        var now = DateTime.UtcNow;
        game.SessionStartedAt = now;

        // Re-anchor scheduled times from now (preserve relative intervals)
        var pending = await db.Achievements
            .Where(a => a.GameId == game.Id && !a.IsUnlocked && a.ScheduledUnlockAt.HasValue)
            .ToListAsync(ct);
        if (pending.Count > 0)
        {
            var minScheduled = pending.Min(a => a.ScheduledUnlockAt!.Value);
            foreach (var ach in pending)
            {
                var relOffset = ach.ScheduledUnlockAt!.Value - minScheduled;
                ach.ScheduledUnlockAt = now.Add(relOffset);
            }
        }

        game.Status = GameStatus.Playing;
        await db.SaveChangesAsync(ct);
        await _idleService.StartAsync(game.AppId, ct);

        _logger.LogInformation("GameQueue: started game {AppId} ({Count} achievements re-anchored)",
            game.AppId, pending.Count);
    }
}
