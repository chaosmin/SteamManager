using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class SyncBackgroundService(
    ISteamSessionService session,
    IServiceScopeFactory scopeFactory,
    ILogger<SyncBackgroundService> logger) : BackgroundService, ISyncService
{
    public bool IsSyncing { get; private set; }
    public int SyncProgress { get; private set; }
    public string? SyncStatusMessage { get; private set; }
    public DateTime? NextRunUtc { get; private set; }
    public event Action? SyncStateChanged;

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private const string FallbackCron = "0 0 * * *";

    public Task TriggerAsync(CancellationToken ct = default)
    {
        // Ignore if trigger already pending or sync already running
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && session.State != LoginState.LoggedIn)
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            var cronExpr = await GetCronAsync(ct);
            try
            {
                var schedule = CronExpression.Parse(cronExpr, CronFormat.Standard);
                NextRunUtc = schedule.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
                Notify();

                if (NextRunUtc.HasValue)
                {
                    var delay = NextRunUtc.Value - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.WhenAny(Task.Delay(delay, ct), _trigger.WaitAsync(ct));
                }
            }
            catch (CronFormatException ex)
            {
                logger.LogWarning(ex, "Invalid cron '{Expr}', falling back to daily midnight", cronExpr);
                await Task.WhenAny(Task.Delay(TimeSpan.FromHours(24), ct), _trigger.WaitAsync(ct));
            }

            if (!ct.IsCancellationRequested)
                await RunSyncAsync(ct);
        }
    }

    private async Task<string> GetCronAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
            return string.IsNullOrWhiteSpace(cfg?.SyncCron) ? FallbackCron : cfg.SyncCron;
        }
        catch { return FallbackCron; }
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        if (IsSyncing) return;
        IsSyncing = true;
        SyncProgress = 0;
        SyncStatusMessage = "Syncing library...";
        Notify();
        logger.LogInformation("Sync started");

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var steamApi = scope.ServiceProvider.GetRequiredService<SteamWebApiClient>();
            var achievementService = scope.ServiceProvider.GetRequiredService<IAchievementDataService>();

            var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
            if (cfg?.WebApiKey == null)
            {
                SyncStatusMessage = "Skipped: Web API key not configured";
                Notify();
                return;
            }

            var language = cfg.Language ?? "english";

            // Step 1: sync library — updates playtime for all games
            if (session.SteamId64.HasValue)
                await SyncLibraryAsync(db, steamApi, cfg.WebApiKey, session.SteamId64.Value, language, ct);

            // Step 2: iterate ALL games for achievement sync
            var games = await db.Games.ToListAsync(ct);
            int total = games.Count;

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) break;
                var game = games[i];
                SyncProgress = (i + 1) * 100 / Math.Max(total, 1);
                SyncStatusMessage = $"{game.Name} ({i + 1}/{total})";
                Notify();

                try { await achievementService.LoadAchievementsAsync(game.Id, game.AppId, ct); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Achievement sync failed for appId {AppId}", game.AppId);
                }
            }

            SyncProgress = 100;
            SyncStatusMessage = $"Done — {total} games synced";
            logger.LogInformation("Sync completed ({Total} games)", total);
        }
        catch (Exception ex)
        {
            SyncStatusMessage = $"Sync failed: {ex.Message}";
            logger.LogError(ex, "Sync failed");
        }
        finally
        {
            IsSyncing = false;
            Notify();
        }
    }

    private async Task SyncLibraryAsync(AppDbContext db, SteamWebApiClient steamApi,
        string apiKey, ulong steamId, string language, CancellationToken ct)
    {
        var owned = await steamApi.GetOwnedGamesAsync(steamId, apiKey, language, ct);
        var existingGames = await db.Games.ToListAsync(ct);
        var existingMap = existingGames.ToDictionary(g => g.AppId);
        int added = 0, updated = 0;
        foreach (var (appId, name, playtimeMinutes) in owned)
        {
            if (existingMap.TryGetValue(appId, out var existing))
            {
                if (language == "english")
                {
                    existing.Name = name;
                    existing.NameI18n = null;
                }
                else
                    existing.NameI18n = name;

                if (existing.TotalPlayMinutes != playtimeMinutes)
                { existing.TotalPlayMinutes = playtimeMinutes; updated++; }
            }
            else
            {
                db.Games.Add(new Game
                {
                    AppId = appId,
                    Name = name,
                    NameI18n = language != "english" ? name : null,
                    TargetHours = 10,
                    TotalPlayMinutes = playtimeMinutes,
                });
                added++;
            }
        }
        // Always save — name/nameI18n may have changed even if playtime didn't
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Library: +{Added} new, {Updated} playtime updated", added, updated);
    }

    private void Notify() => SyncStateChanged?.Invoke();
}
