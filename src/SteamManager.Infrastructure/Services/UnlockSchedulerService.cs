using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Infrastructure.Services;

public class UnlockSchedulerService(
    AchievementHandler handler,
    AchievementUnlockNotifier notifier,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<UnlockSchedulerService> logger) : IUnlockSchedulerService
{
    private readonly Dictionary<int, CancellationTokenSource> _running = [];
    private readonly Dictionary<int, DateTime> _sessionStart = [];
    // Tracks the offset of the last unlocked achievement for this session.
    // Updated in-memory each time an achievement is unlocked so GetElapsedSessionTime
    // always returns the delta relative to the most recent unlock.
    private readonly Dictionary<int, int> _lastUnlockedOffset = [];

    public event Action<int>? ScheduleCompleted;

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public TimeSpan? GetTimeUntilNext(int appId, int targetOffsetMinutes)
    {
        if (!_sessionStart.TryGetValue(appId, out var start)) return null;
        var remaining = start.AddMinutes(targetOffsetMinutes) - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Returns how many idle minutes have elapsed since the last unlocked achievement
    /// (pure delta — excludes the lastUnlockedOffset base). This is what gets persisted
    /// to game.SavedIdleDeltaMinutes on stop and restored on the next start.
    /// </summary>
    public TimeSpan? GetElapsedSessionTime(int appId)
    {
        if (!_sessionStart.TryGetValue(appId, out var start)) return null;
        var total = DateTime.UtcNow - start;
        var offset = _lastUnlockedOffset.TryGetValue(appId, out var o) ? o : 0;
        return total - TimeSpan.FromMinutes(offset);
    }

    /// <summary>
    /// Starts the idle scheduler for appId.
    /// Reads game.SavedIdleDeltaMinutes from DB to restore the timer after a restart.
    /// Timer base = lastUnlockedOffset (from DB) + savedDelta, so the first pending
    /// achievement unlocks after (its offset − lastUnlockedOffset − savedDelta) minutes.
    /// </summary>
    public async Task StartAsync(int appId, CancellationToken ct = default)
    {
        if (_running.ContainsKey(appId)) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dataService = scope.ServiceProvider.GetRequiredService<IAchievementDataService>();

        var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId, ct);
        if (game == null) return;

        var hasAchievements = await db.Achievements.AnyAsync(a => a.GameId == game.Id, ct);
        if (!hasAchievements)
            await dataService.LoadAchievementsAsync(game.Id, appId, ct);

        // Last unlocked achievement's offset = the built-in timer's base.
        var lastUnlockedOffset = await db.Achievements
            .Where(a => a.GameId == game.Id && a.IsUnlocked)
            .MaxAsync(a => (int?)a.UnlockOffsetMinutes, ct) ?? 0;

        // savedDelta = idle minutes already accumulated beyond lastUnlockedOffset.
        var savedDelta = game.SavedIdleDeltaMinutes;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        _lastUnlockedOffset[appId] = lastUnlockedOffset;
        // sessionStart is set so that (NOW − sessionStart) = lastUnlockedOffset + savedDelta.
        _sessionStart[appId] = DateTime.UtcNow.AddMinutes(-(lastUnlockedOffset + savedDelta));

        logger.LogInformation(
            "Scheduler: starting app {AppId} — lastUnlockedOffset={Base}min savedDelta={Delta}min",
            appId, lastUnlockedOffset, savedDelta);

        _ = RunSchedulerLoopAsync(appId, cts.Token);
    }

    /// <summary>
    /// Stops the scheduler and persists the current idle delta to game.SavedIdleDeltaMinutes
    /// so it can be restored on the next StartAsync call.
    /// </summary>
    public async Task StopAsync(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return;

        // Capture elapsed before cancelling so the value is accurate.
        var deltaMinutes = (int)(GetElapsedSessionTime(appId)?.TotalMinutes ?? 0);

        await cts.CancelAsync();
        _running.Remove(appId);
        _sessionStart.Remove(appId);
        _lastUnlockedOffset.Remove(appId);

        // Persist timer so a restart can resume from the same point.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId);
            if (game != null)
            {
                game.SavedIdleDeltaMinutes = deltaMinutes;
                await db.SaveChangesAsync();
                logger.LogInformation(
                    "Scheduler: stopped app {AppId} — persisted delta={Delta}min", appId, deltaMinutes);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduler: failed to persist idle delta for app {AppId}", appId);
        }
    }

    private async Task RunSchedulerLoopAsync(int appId, CancellationToken ct)
    {
        try
        {
            var sessionStart = _sessionStart[appId];

            List<(int Id, string ApiName, int OffsetMinutes)> pending;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId, ct);
                if (game == null) return;

                var rows = await db.Achievements
                    .Where(a => a.GameId == game.Id && !a.IsUnlocked)
                    .OrderBy(a => a.UnlockOffsetMinutes)
                    .Select(a => new { a.Id, a.ApiName, a.UnlockOffsetMinutes })
                    .ToListAsync(ct);
                pending = rows.Select(r => (r.Id, r.ApiName, r.UnlockOffsetMinutes)).ToList();
            }

            if (pending.Count == 0) return;
            logger.LogInformation("Scheduler: {Count} achievements pending for app {AppId}", pending.Count, appId);

            int prevOffset = _lastUnlockedOffset.TryGetValue(appId, out var initialOffset) ? initialOffset : 0;

            foreach (var (id, apiName, offsetMinutes) in pending)
            {
                if (ct.IsCancellationRequested) break;

                // Achievement unlocks when sessionStart + offset is reached,
                // i.e. after (offset − lastUnlockedOffset − savedDelta) idle minutes.
                var targetTime = sessionStart.AddMinutes(offsetMinutes);
                var waitMs = (long)(targetTime - DateTime.UtcNow).TotalMilliseconds;
                if (waitMs > 0)
                {
                    var deltaLog = offsetMinutes - prevOffset;
                    logger.LogInformation(
                        "Scheduler: waiting {Wait}s for {AchId} (delta {Delta}min) app {AppId}",
                        waitMs / 1000, apiName, deltaLog, appId);
                    await Task.Delay((int)Math.Min(waitMs, int.MaxValue), ct);
                }
                else
                {
                    // Catch-up: achievement is overdue. Wait the same gap as the original
                    // schedule so the cadence feels natural (offsetDelta minutes).
                    var catchUpMs = Math.Max(offsetMinutes - prevOffset, 1) * 60_000;
                    logger.LogInformation(
                        "Scheduler: catch-up {AchId} — waiting {Wait}s (offset delta) app {AppId}",
                        apiName, catchUpMs / 1000, appId);
                    await Task.Delay(catchUpMs, ct);
                }
                prevOffset = offsetMinutes;

                // Skip if already unlocked by a concurrent sync
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var alreadyDone = await db.Achievements
                        .Where(a => a.Id == id)
                        .Select(a => a.IsUnlocked)
                        .FirstOrDefaultAsync(ct);
                    if (alreadyDone) continue;
                }

                var ok = await handler.UnlockAchievementAsync(appId, apiName, ct);
                if (ok)
                {
                    await SaveUnlockedAsync(id, ct);
                    // Update in-memory base so GetElapsedSessionTime stays accurate
                    // relative to this newly unlocked achievement.
                    _lastUnlockedOffset[appId] = offsetMinutes;
                }
            }
            // All achievements processed — notify queue service to advance immediately
            if (!ct.IsCancellationRequested)
                ScheduleCompleted?.Invoke(appId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Scheduler loop error for {AppId}", appId); }
    }

    private async Task SaveUnlockedAsync(int achievementId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ach = await db.Achievements
            .Include(a => a.Game)
            .FirstOrDefaultAsync(a => a.Id == achievementId, ct);
        if (ach == null) return;
        ach.IsUnlocked = true;
        ach.UnlockedAt = await FetchSteamUnlockTimeAsync(scope, ach.AppId, ach.ApiName, ct)
                         ?? DateTime.UtcNow;
        // Reset persisted delta — the new base is this achievement's offset.
        ach.Game.SavedIdleDeltaMinutes = 0;
        await db.SaveChangesAsync(ct);

        notifier.Notify(new UnlockedAchievementInfo(
            GameName: ach.Game.NameI18n ?? ach.Game.Name,
            AchievementName: ach.DisplayNameI18n ?? ach.DisplayName,
            IconUrl: ach.IconUrl));
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
        catch
        {
            return null;
        }
    }
}
