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

        var existing = await db.AchievementSchedules.AnyAsync(s => s.AppId == appId, ct);
        if (!existing)
        {
            var intervals = await dataService.GetIntervalsAsync(appId, ct);
            var items = intervals.Select(i => new Core.Models.AchievementScheduleItem
            {
                AppId = appId,
                AchievementId = i.AchievementId,
                OffsetMinutes = i.OffsetMinutes,
                Done = false,
            }).ToList();
            db.AchievementSchedules.AddRange(items);
            await db.SaveChangesAsync(ct);
        }

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
            while (!ct.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var progress = await db.GameProgresses
                    .FirstOrDefaultAsync(p => p.AppId == appId, ct);
                if (progress == null) { await Task.Delay(30_000, ct); continue; }

                var next = await db.AchievementSchedules
                    .Where(s => s.AppId == appId && !s.Done && s.OffsetMinutes <= progress.AccumulatedMinutes)
                    .OrderBy(s => s.OffsetMinutes)
                    .FirstOrDefaultAsync(ct);

                if (next != null)
                {
                    logger.LogInformation("Unlocking {AchId} for app {AppId} at {Min}min",
                        next.AchievementId, appId, progress.AccumulatedMinutes);

                    var ok = await handler.UnlockAchievementAsync(appId, next.AchievementId, ct);
                    if (ok)
                    {
                        next.Done = true;
                        next.UnlockedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                }

                await Task.Delay(ApplyJitter(30_000, JitterPercent), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Scheduler loop error for {AppId}", appId); }
    }

    public static int GetWaitMinutes(int currentMinutes, IReadOnlyList<Core.Models.AchievementScheduleItem> schedule)
    {
        var next = schedule
            .Where(s => !s.Done && s.OffsetMinutes > currentMinutes)
            .OrderBy(s => s.OffsetMinutes)
            .FirstOrDefault();
        return next == null ? -1 : next.OffsetMinutes - currentMinutes;
    }

    public static int ApplyJitter(int baseMs, int jitterPercent)
    {
        var jitter = (int)(baseMs * jitterPercent / 100.0 * (Random.Shared.NextDouble() * 2 - 1));
        return Math.Max(1000, baseMs + jitter);
    }
}
