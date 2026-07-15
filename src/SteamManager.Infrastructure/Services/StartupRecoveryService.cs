using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class StartupRecoveryService(
    ISteamSessionService session,
    IGameIdleService idle,
    SteamWebApiClient steamApi,
    AppDbContext db,
    ILogger<StartupRecoveryService> logger)
{
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Running startup recovery");

        var restored = await session.TryRestoreSessionAsync(ct);
        if (!restored)
        {
            logger.LogWarning("Could not restore Steam session — manual login required");
            return;
        }

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;

        // Recover Playing games: re-anchor schedule from server start
        var playingGames = await db.Games
            .Include(g => g.Achievements)
            .Where(g => g.Status == GameStatus.Playing)
            .ToListAsync(ct);

        foreach (var game in playingGames)
        {
            logger.LogInformation("Recovery: re-anchoring Playing game {AppId}", game.AppId);

            // Re-read Steam playtime in case it changed before the restart
            if (cfg?.WebApiKey != null && session.SteamId64.HasValue)
            {
                var currentPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(
                    (long)session.SteamId64.Value, game.AppId, cfg.WebApiKey, ct);
                game.SteamPlaytimeAtRefresh = currentPlaytime;
            }

            game.SessionStartedAt = now;

            // Re-anchor scheduled_unlock_at from server start (preserve relative intervals)
            var pending = game.Achievements.Where(a => !a.IsUnlocked && a.ScheduledUnlockAt.HasValue).ToList();
            if (pending.Count > 0)
            {
                var minScheduled = pending.Min(a => a.ScheduledUnlockAt!.Value);
                foreach (var ach in pending)
                {
                    var relOffset = ach.ScheduledUnlockAt!.Value - minScheduled;
                    ach.ScheduledUnlockAt = now.Add(relOffset);
                }
            }

            await db.SaveChangesAsync(ct);
            await idle.StartAsync(game.AppId, ct);

            logger.LogInformation("Recovery: game {AppId} resumed, {Count} achievements re-anchored",
                game.AppId, pending.Count);
        }

        // Scheduled games: global scanner picks up past-due achievements automatically.
        // Detect any that are already complete (no pending achievements).
        var scheduledGames = await db.Games
            .Include(g => g.Achievements)
            .Where(g => g.Status == GameStatus.Scheduled)
            .ToListAsync(ct);

        foreach (var game in scheduledGames)
        {
            var anyPending = game.Achievements.Any(a => !a.IsUnlocked);
            if (!anyPending)
            {
                game.Status = GameStatus.Completed;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Recovery: game {AppId} promoted to Completed (no pending achievements)", game.AppId);
            }
        }
    }
}
