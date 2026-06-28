using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Services;
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
    private int JitterPercent => config.GetValue("AchievementData:IntervalJitterPercent", 10);

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

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

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        _ = RunSchedulerLoopAsync(appId, cts.Token);
    }

    public async Task StopAsync(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return;
        await cts.CancelAsync();
        _running.Remove(appId);
    }

    private async Task RunSchedulerLoopAsync(int appId, CancellationToken ct)
    {
        try
        {
            // Phase 1: catch-up — unlock all overdue achievements with 1-100s random gaps
            await CatchUpOverdueAsync(appId, ct);

            // Phase 2: poll every ~30s for newly-due achievements
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ApplyJitter(30_000, JitterPercent), ct);
                await UnlockNextDueAsync(appId, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Scheduler loop error for {AppId}", appId); }
    }

    private async Task CatchUpOverdueAsync(int appId, CancellationToken ct)
    {
        List<(int Id, string ApiName)> overdue;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId, ct);
            if (game == null) return;

            var rows = await db.Achievements
                .Where(a => a.GameId == game.Id && !a.IsUnlocked
                            && a.UnlockOffsetMinutes <= game.TotalPlayMinutes)
                .OrderBy(a => a.UnlockOffsetMinutes)
                .Select(a => new { a.Id, a.ApiName })
                .ToListAsync(ct);
            overdue = rows.Select(r => (r.Id, r.ApiName)).ToList();
        }

        if (overdue.Count == 0) return;
        logger.LogInformation("Catch-up: {Count} overdue achievements for app {AppId}", overdue.Count, appId);

        foreach (var (id, apiName) in overdue)
        {
            if (ct.IsCancellationRequested) break;
            await Task.Delay(Random.Shared.Next(1_000, 101_000), ct); // 1-100s gap
            var ok = await handler.UnlockAchievementAsync(appId, apiName, ct);
            if (ok) await SaveUnlockedAsync(id, ct);
        }
    }

    private async Task UnlockNextDueAsync(int appId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId, ct);
        if (game == null) return;

        var next = await db.Achievements
            .Where(a => a.GameId == game.Id && !a.IsUnlocked
                        && a.UnlockOffsetMinutes <= game.TotalPlayMinutes)
            .OrderBy(a => a.UnlockOffsetMinutes)
            .FirstOrDefaultAsync(ct);
        if (next == null) return;

        logger.LogInformation("Unlocking {AchId} for app {AppId} at {Min}min",
            next.ApiName, appId, game.TotalPlayMinutes);

        await Task.Delay(Random.Shared.Next(1_000, 101_000), ct); // ±1-100s pre-unlock jitter
        var ok = await handler.UnlockAchievementAsync(appId, next.ApiName, ct);
        if (ok) await SaveUnlockedAsync(next.Id, ct);
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
        ach.UnlockedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        notifier.Notify(new UnlockedAchievementInfo(
            GameName: ach.Game.NameI18n ?? ach.Game.Name,
            AchievementName: ach.DisplayNameI18n ?? ach.DisplayName,
            IconUrl: ach.IconUrl));
    }

    public static int ApplyJitter(int baseMs, int jitterPercent)
    {
        var jitter = (int)(baseMs * jitterPercent / 100.0 * (Random.Shared.NextDouble() * 2 - 1));
        return Math.Max(1000, baseMs + jitter);
    }
}
