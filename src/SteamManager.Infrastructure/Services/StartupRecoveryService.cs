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

            // Re-anchor scheduled_unlock_at from server start (preserve relative intervals).
            // If schedule is still in the future, leave it untouched.
            // If past-due, compute how much of the reference interval has already elapsed
            // using the last unlocked achievement as the reference point.
            var pending = game.Achievements.Where(a => !a.IsUnlocked && a.ScheduledUnlockAt.HasValue).ToList();
            if (pending.Count > 0)
            {
                var minScheduled = pending.Min(a => a.ScheduledUnlockAt!.Value);
                if (minScheduled > now)
                {
                    // Schedule intact — nothing to do
                    logger.LogInformation("Recovery: game {AppId} schedule intact, first achievement in {Min:F0} min",
                        game.AppId, (minScheduled - now).TotalMinutes);
                }
                else
                {
                    // Schedule lapsed — compute remaining time from reference interval
                    var lastUnlocked = game.Achievements
                        .Where(a => a.IsUnlocked && a.UnlockedAt.HasValue)
                        .OrderByDescending(a => a.UnlockedAt)
                        .FirstOrDefault();

                    TimeSpan anchorOffset = TimeSpan.Zero;
                    if (lastUnlocked?.UnlockedAt != null)
                    {
                        var referenceInterval = minScheduled - lastUnlocked.UnlockedAt!.Value;
                        var elapsed = now - lastUnlocked.UnlockedAt!.Value;
                        var remaining = referenceInterval - elapsed;
                        if (remaining > TimeSpan.Zero)
                            anchorOffset = remaining;
                        // else: elapsed >= referenceInterval → anchor = now (fire at next tick)
                    }

                    var anchor = now.Add(anchorOffset);
                    foreach (var ach in pending)
                    {
                        var relOffset = ach.ScheduledUnlockAt!.Value - minScheduled;
                        ach.ScheduledUnlockAt = anchor.Add(relOffset);
                    }

                    logger.LogInformation(
                        "Recovery: game {AppId} re-anchored {Count} achievements, first fires in {Min:F0} min",
                        game.AppId, pending.Count, anchorOffset.TotalMinutes);
                }
            }

            await db.SaveChangesAsync(ct);
            await idle.StartAsync(game.AppId, ct);

            logger.LogInformation("Recovery: game {AppId} resumed with {Count} pending achievements",
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
