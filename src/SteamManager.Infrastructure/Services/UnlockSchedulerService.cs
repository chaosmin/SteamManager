using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Infrastructure.Services;

public class UnlockSchedulerService(
    AchievementHandler handler,
    AchievementUnlockNotifier notifier,
    IServiceScopeFactory scopeFactory,
    ILogger<UnlockSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct); // startup grace period

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await ScanAndUnlockAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "UnlockScheduler scan error"); }
        }
    }

    private async Task ScanAndUnlockAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var due = await db.Achievements
            .Include(a => a.Game)
            .Where(a => !a.IsUnlocked
                && a.ScheduledUnlockAt.HasValue
                && a.ScheduledUnlockAt.Value <= now)
            .OrderBy(a => a.ScheduledUnlockAt)
            .ToListAsync(ct);

        if (due.Count > 0)
            logger.LogInformation("UnlockScheduler: {Count} achievements due", due.Count);

        foreach (var ach in due)
        {
            if (ct.IsCancellationRequested) break;

            var ok = await handler.UnlockAchievementAsync(ach.AppId, ach.ApiName, ct);
            if (!ok)
            {
                logger.LogWarning("Failed to unlock {ApiName} for app {AppId}", ach.ApiName, ach.AppId);
                continue;
            }

            ach.IsUnlocked = true;
            ach.ScheduledUnlockAt = null;
            ach.UnlockedAt = await FetchSteamUnlockTimeAsync(scope, ach.AppId, ach.ApiName, ct) ?? now;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Unlocked {ApiName} for game {AppId}", ach.ApiName, ach.AppId);

            notifier.Notify(new UnlockedAchievementInfo(
                GameName: ach.Game.NameI18n ?? ach.Game.Name,
                AchievementName: ach.DisplayNameI18n ?? ach.DisplayName,
                IconUrl: ach.IconUrl));

            // Check if Scheduled game is now fully complete
            if (ach.Game.Status == GameStatus.Scheduled)
            {
                var anyPending = await db.Achievements
                    .AnyAsync(a => a.GameId == ach.GameId && !a.IsUnlocked, ct);
                if (!anyPending)
                {
                    ach.Game.Status = GameStatus.Completed;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Game {AppId} fully completed", ach.AppId);

                    var totalCount = await db.Achievements
                        .CountAsync(a => a.GameId == ach.GameId, ct);
                    notifier.NotifyCompleted(new CompletedGameInfo(
                        GameName: ach.Game.NameI18n ?? ach.Game.Name,
                        AchievementCount: totalCount));
                }
            }
        }
    }

    private async Task<DateTime?> FetchSteamUnlockTimeAsync(
        IServiceScope scope, int appId, string apiName, CancellationToken ct)
    {
        try
        {
            var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = scope.ServiceProvider.GetRequiredService<ISteamSessionService>();
            if (!session.SteamId64.HasValue) return null;

            var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
            if (cfg?.WebApiKey == null) return null;

            var steamApi   = scope.ServiceProvider.GetRequiredService<SteamWebApiClient>();
            var playerAchs = await steamApi.GetPlayerAchievementsAsync(
                (long)session.SteamId64.Value, appId, cfg.WebApiKey, ct);

            var found = playerAchs.FirstOrDefault(a =>
                string.Equals(a.ApiName, apiName, StringComparison.OrdinalIgnoreCase));

            return found?.UnlockTime.HasValue == true
                ? DateTimeOffset.FromUnixTimeSeconds(found.UnlockTime.Value).UtcDateTime
                : null;
        }
        catch { return null; }
    }
}
