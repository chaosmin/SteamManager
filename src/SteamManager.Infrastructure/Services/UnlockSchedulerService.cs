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

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public TimeSpan? GetElapsedSessionTime(int appId) =>
        _sessionStart.TryGetValue(appId, out var start) ? DateTime.UtcNow - start : null;

    public Task StartAsync(int appId, CancellationToken ct = default) =>
        StartAsync(appId, resumeFromMinutes: 0, ct);

    public async Task StartAsync(int appId, int resumeFromMinutes, CancellationToken ct = default)
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

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        // Shift session start backwards so already-elapsed minutes are accounted for
        _sessionStart[appId] = DateTime.UtcNow.AddMinutes(-resumeFromMinutes);
        _ = RunSchedulerLoopAsync(appId, cts.Token);
    }

    public async Task StopAsync(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return;
        await cts.CancelAsync();
        _running.Remove(appId);
        _sessionStart.Remove(appId);
    }

    private async Task RunSchedulerLoopAsync(int appId, CancellationToken ct)
    {
        try
        {
            var sessionStart = _sessionStart[appId];

            // Load all pending achievements sorted by offset.
            // Timing is based on elapsed session time (sessionStart + offset),
            // independent of Steam's accumulated TotalPlayMinutes.
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

            foreach (var (id, apiName, offsetMinutes) in pending)
            {
                if (ct.IsCancellationRequested) break;

                // Wait until session elapsed time reaches this achievement's offset
                var targetTime = sessionStart.AddMinutes(offsetMinutes);
                var waitMs = (int)(targetTime - DateTime.UtcNow).TotalMilliseconds;
                if (waitMs > 0)
                {
                    logger.LogInformation("Scheduler: waiting {Wait}s for {AchId} (offset {Off}min) app {AppId}",
                        waitMs / 1000, apiName, offsetMinutes, appId);
                    await Task.Delay(waitMs, ct);
                }

                // 1-100s random jitter before unlock
                await Task.Delay(Random.Shared.Next(1_000, 101_000), ct);

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
                if (ok) await SaveUnlockedAsync(id, ct);
            }
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
        // Fetch the timestamp Steam actually recorded — falls back to UtcNow if unavailable.
        ach.UnlockedAt = await FetchSteamUnlockTimeAsync(scope, ach.AppId, ach.ApiName, ct)
                         ?? DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        notifier.Notify(new UnlockedAchievementInfo(
            GameName: ach.Game.NameI18n ?? ach.Game.Name,
            AchievementName: ach.DisplayNameI18n ?? ach.DisplayName,
            IconUrl: ach.IconUrl));
    }

    /// <summary>
    /// Queries Steam's GetPlayerAchievements API immediately after an unlock to retrieve
    /// the timestamp that Steam's servers recorded. Returns null on any failure.
    /// </summary>
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
