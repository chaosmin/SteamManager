using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class PlayQueueService(
    IServiceScopeFactory scopeFactory,
    IGameIdleService idleService,
    IUnlockSchedulerService schedulerService,
    ISteamSessionService session,
    ILogger<PlayQueueService> logger) : BackgroundService, IPlayQueueService
{
    private int _notInGameStreak = 0;

    // ── IPlayQueueService ────────────────────────────────────────────────────

    public async Task<List<QueueEntry>> GetQueueAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PlayQueueItems
            .Include(q => q.Game)
            .OrderBy(q => q.SortOrder)
            .Select(q => new QueueEntry(
                q.Id, q.GameId, q.Game.AppId,
                q.Game.Name, q.Game.NameI18n,
                q.SortOrder, q.IsActive, q.SavedSessionMinutes))
            .ToListAsync();
    }

    public async Task AddToQueueAsync(int gameId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alreadyQueued = await db.PlayQueueItems.AnyAsync(q => q.GameId == gameId);
        if (alreadyQueued) return;

        var maxOrder = await db.PlayQueueItems.AnyAsync()
            ? await db.PlayQueueItems.MaxAsync(q => q.SortOrder)
            : -1;

        db.PlayQueueItems.Add(new PlayQueueItem
        {
            GameId = gameId,
            SortOrder = maxOrder + 1,
            SavedSessionMinutes = 0,
            IsActive = false,
            AddedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("PlayQueue: game {GameId} added to queue", gameId);
    }

    public async Task RemoveFromQueueAsync(int queueItemId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var item = await db.PlayQueueItems.Include(q => q.Game).FirstOrDefaultAsync(q => q.Id == queueItemId);
        if (item == null) return;

        if (item.IsActive)
        {
            await idleService.StopAsync(item.Game.AppId);
            await schedulerService.StopAsync(item.Game.AppId);
            await ResetGameStatusAsync(db, item.GameId);
        }

        db.PlayQueueItems.Remove(item);
        await db.SaveChangesAsync();
        logger.LogInformation("PlayQueue: queue item {ItemId} removed", queueItemId);
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the app time to finish startup before first poll
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "PlayQueue tick error"); }

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!session.SteamId64.HasValue) return;
        var cfg = await db.SteamConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        if (cfg?.WebApiKey == null) return;

        var steamApi = scope.ServiceProvider.GetRequiredService<SteamWebApiClient>();
        int? currentGameId = await steamApi.GetCurrentGameIdAsync(session.SteamId64.Value, cfg.WebApiKey, ct);

        var activeItem = await db.PlayQueueItems
            .Include(q => q.Game)
            .FirstOrDefaultAsync(q => q.IsActive, ct);

        if (activeItem != null)
        {
            var appId = activeItem.Game.AppId;

            // Check if all achievements are now unlocked → completed
            var hasAny = await db.Achievements.AnyAsync(a => a.GameId == activeItem.GameId, ct);
            var allUnlocked = hasAny &&
                !await db.Achievements.AnyAsync(a => a.GameId == activeItem.GameId && !a.IsUnlocked, ct);

            if (allUnlocked)
            {
                logger.LogInformation("PlayQueue: game {AppId} completed — removing from queue", appId);
                await idleService.StopAsync(appId);
                await schedulerService.StopAsync(appId);
                db.PlayQueueItems.Remove(activeItem);
                await db.SaveChangesAsync(ct);
                _notInGameStreak = 0;
                await StartNextInQueueAsync(db, ct);
                return;
            }

            // Player started a different game → pause idling
            if (currentGameId.HasValue && currentGameId.Value != appId)
            {
                logger.LogInformation("PlayQueue: player in game {Current}, pausing idle for {AppId}", currentGameId, appId);
                var elapsed = schedulerService.GetElapsedSessionTime(appId);
                await idleService.StopAsync(appId);
                await schedulerService.StopAsync(appId);

                activeItem.IsActive = false;
                activeItem.SavedSessionMinutes += (int)(elapsed?.TotalMinutes ?? 0);
                await ResetGameStatusAsync(db, activeItem.GameId);
                await db.SaveChangesAsync(ct);
                _notInGameStreak = 0;
            }
            // else: still idling normally — no action
        }
        else
        {
            // Nothing active — count consecutive not-in-game ticks
            if (currentGameId == null)
            {
                _notInGameStreak++;
                logger.LogDebug("PlayQueue: not in game, streak={Streak}", _notInGameStreak);

                if (_notInGameStreak >= 2)
                {
                    _notInGameStreak = 0;
                    await StartNextInQueueAsync(db, ct);
                }
            }
            else
            {
                _notInGameStreak = 0;
            }
        }
    }

    private async Task StartNextInQueueAsync(AppDbContext db, CancellationToken ct)
    {
        var next = await db.PlayQueueItems
            .Include(q => q.Game)
            .OrderBy(q => q.SortOrder)
            .FirstOrDefaultAsync(ct);

        if (next == null) return;

        var appId = next.Game.AppId;
        var resumeFrom = next.SavedSessionMinutes;

        logger.LogInformation("PlayQueue: starting game {AppId} (resume from {Min}min)", appId, resumeFrom);

        next.IsActive = true;
        await db.SaveChangesAsync(ct);

        await idleService.StartAsync(appId, ct);
        await schedulerService.StartAsync(appId, resumeFrom, ct);

        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == next.GameId, ct);
        if (game != null)
        {
            game.Status = GameStatus.Running;
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task ResetGameStatusAsync(AppDbContext db, int gameId)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return;
        game.Status = GameStatus.Idle;
        await db.SaveChangesAsync();
    }
}
