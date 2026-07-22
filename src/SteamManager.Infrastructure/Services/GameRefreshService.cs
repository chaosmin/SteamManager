using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public partial class GameRefreshService(
    IServiceScopeFactory scopeFactory,
    SteamWebApiClient steamApi,
    SteamHuntersClient hunters,
    ISteamSessionService session,
    IGameQueueService queue,
    ILogger<GameRefreshService> logger) : IGameRefreshService
{
    public Task RefreshAsync(int gameId, int appId, CancellationToken ct = default)
        => RefreshCoreAsync(gameId, appId, forceReset: false, ct);

    public async Task SyncAsync(int gameId, int appId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Steam config not found");
        var apiKey = cfg.WebApiKey
            ?? throw new InvalidOperationException("Steam Web API key not configured");

        var game = await db.Games.FindAsync([gameId], ct)
            ?? throw new InvalidOperationException($"Game {gameId} not found");

        if (!session.SteamId64.HasValue)
            throw new InvalidOperationException("Not logged in to Steam.");

        // Fetch current playtime
        var currentPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(
            (long)session.SteamId64.Value, appId, apiKey, ct);
        if (currentPlaytime > 0)
            game.SteamPlaytimeAtRefresh = currentPlaytime;

        // Sync achievement unlock status from Steam
        var myAchs = await steamApi.GetPlayerAchievementsAsync(
            (long)session.SteamId64.Value, appId, apiKey, ct);
        var myMap = myAchs.ToDictionary(a => a.ApiName);
        var achievements = await db.Achievements.Where(a => a.GameId == gameId).ToListAsync(ct);
        foreach (var row in achievements)
        {
            if (!row.IsUnlocked && myMap.TryGetValue(row.ApiName, out var p) && p.UnlockTime.HasValue)
            {
                row.IsUnlocked = true;
                row.UnlockedAt = DateTimeOffset.FromUnixTimeSeconds(p.UnlockTime.Value).UtcDateTime;
                row.ScheduledUnlockAt = null;
            }
        }

        // Check completion: playtime goal met AND all achievements unlocked
        var targetMet = game.TargetMinutes.HasValue && game.SteamPlaytimeAtRefresh >= game.TargetMinutes.Value;
        var allUnlocked = achievements.Count > 0 && achievements.All(a => a.IsUnlocked);
        if (targetMet && allUnlocked && game.Status != GameStatus.Completed)
        {
            game.Status = GameStatus.Completed;
            await queue.RemoveFromQueueAsync(gameId);
            logger.LogInformation("Sync: game {AppId} marked Completed", appId);
        }

        game.AchievementsCachedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Sync complete: game {GameId} ({AppId}), playtime={Pt}min",
            gameId, appId, game.SteamPlaytimeAtRefresh);
    }

    public Task ForceRefreshAsync(int gameId, int appId, CancellationToken ct = default)
        => RefreshCoreAsync(gameId, appId, forceReset: true, ct);

    private async Task RefreshCoreAsync(int gameId, int appId, bool forceReset, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. Load config
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Steam config not found");
        var apiKey = cfg.WebApiKey
            ?? throw new InvalidOperationException("Steam Web API key not configured");
        var language = cfg.Language;

        // 2. Load game + reference player
        var game = await db.Games.FindAsync([gameId], ct)
            ?? throw new InvalidOperationException($"Game {gameId} not found");
        var refPlayer = await db.GameReferencePlayers.FirstOrDefaultAsync(r => r.GameId == gameId, ct)
            ?? throw new InvalidOperationException(
                "No reference player configured. Add one in the game detail page first.");

        // 3. Force reset: clear all achievements + reset game state before re-scheduling
        if (forceReset)
        {
            var achToReset = await db.Achievements.Where(a => a.GameId == gameId).ToListAsync(ct);
            foreach (var a in achToReset)
            {
                a.IsUnlocked = false;
                a.UnlockedAt = null;
                a.ScheduledUnlockAt = null;
            }
            // Reset game status so the queue can pick it up again (e.g. was Completed)
            game.Status = GameStatus.Idle;
            game.SessionStartedAt = null;
            await db.SaveChangesAsync(ct);
        }

        // 4. Fetch achievement schema
        var schema = await steamApi.GetSchemaAchievementsAsync(appId, apiKey, language, ct);

        // 5. Fetch MCT from SteamHunters
        var (medianMinutes, _) = await hunters.GetAppInfoAsync(appId, ct);
        if (medianMinutes > 0) game.TargetMinutes = medianMinutes;

        // 6. Resolve reference player Steam64 ID from URL
        long? steamId64 = TryExtractSteamId(refPlayer.PlayerUrl);
        if (steamId64 == null)
        {
            var vanity = TryExtractVanityName(refPlayer.PlayerUrl);
            if (vanity != null)
                steamId64 = await steamApi.ResolveVanityUrlAsync(vanity, apiKey, ct);
        }
        if (!steamId64.HasValue)
            throw new InvalidOperationException(
                $"Cannot resolve Steam ID from: {refPlayer.PlayerUrl}. Use /profiles/{{steamId64}} or /id/{{vanityname}} format.");

        // 7. Fetch reference player's achievements
        var playerAchs = await steamApi.GetPlayerAchievementsAsync(steamId64.Value, appId, apiKey, ct);
        var unlockedByPlayer = playerAchs.Where(a => a.UnlockTime.HasValue).ToList();

        if (unlockedByPlayer.Count == 0)
            throw new InvalidOperationException(
                "Reference player has no unlocked achievements for this game.");

        // 8. Fallback target if no MCT: use reference player's recorded total playtime
        if (game.TargetMinutes == null || game.TargetMinutes == 0)
        {
            var refPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(steamId64.Value, appId, apiKey, ct);
            if (refPlaytime > 0) game.TargetMinutes = refPlaytime;
        }

        // 9. Burst detection
        var burstTimestamps = AchievementIntervalCalculator.ValidateBurstThreshold(unlockedByPlayer);
        if (burstTimestamps.Count > 0 && !refPlayer.OverrideBurstCheck)
            throw new InvalidOperationException(
                $"Burst detected: {burstTimestamps.Count} timestamp(s) with ≥5 achievements share the same unlock time. " +
                "Enable 'Override burst detection' in the reference player panel to proceed.");

        // 10. Build already-unlocked set (these are skipped when scheduling)
        var alreadyUnlockedList = await db.Achievements
            .Where(a => a.GameId == gameId && a.IsUnlocked)
            .Select(a => a.ApiName)
            .ToListAsync(ct);
        var alreadyUnlocked = alreadyUnlockedList.ToHashSet();

        // 11. Calculate scheduled UTC times
        var scheduleMap = AchievementIntervalCalculator.CalculateScheduledTimes(
            unlockedByPlayer, DateTime.UtcNow, alreadyUnlocked);

        // 12. Upsert achievements
        var existing = await db.Achievements
            .Where(a => a.GameId == gameId)
            .ToDictionaryAsync(a => a.ApiName, ct);

        foreach (var ach in schema)
        {
            scheduleMap.TryGetValue(ach.ApiName, out var scheduledAt);

            if (existing.TryGetValue(ach.ApiName, out var row))
            {
                if (language == "english")
                {
                    row.DisplayName = ach.DisplayName;
                    row.DisplayNameI18n = null;
                    row.Description = ach.Description;
                    row.DescriptionI18n = null;
                }
                else
                {
                    row.DisplayNameI18n = ach.DisplayName;
                    row.DescriptionI18n = ach.Description;
                }
                row.GlobalPercent = ach.GlobalPercent;
                row.IconUrl = ach.IconUrl;
                row.IconGrayUrl = ach.IconGrayUrl;
                if (!row.IsUnlocked)
                    row.ScheduledUnlockAt = scheduledAt == default ? null : scheduledAt;
            }
            else
            {
                db.Achievements.Add(new Achievement
                {
                    GameId = gameId, AppId = appId,
                    ApiName = ach.ApiName,
                    DisplayName = ach.DisplayName,
                    DisplayNameI18n = language != "english" ? ach.DisplayName : null,
                    Description = ach.Description,
                    DescriptionI18n = language != "english" ? ach.Description : null,
                    GlobalPercent = ach.GlobalPercent,
                    IconUrl = ach.IconUrl,
                    IconGrayUrl = ach.IconGrayUrl,
                    ScheduledUnlockAt = scheduledAt == default ? null : scheduledAt,
                });
            }
        }

        // 13. Sync own unlock status from Steam + refresh current playtime
        if (session.SteamId64.HasValue)
        {
            var myAchs = await steamApi.GetPlayerAchievementsAsync(
                (long)session.SteamId64.Value, appId, apiKey, ct);
            var myMap = myAchs.ToDictionary(a => a.ApiName);
            var rows = await db.Achievements.Where(a => a.GameId == gameId).ToListAsync(ct);
            foreach (var row in rows)
            {
                if (myMap.TryGetValue(row.ApiName, out var p) && p.UnlockTime.HasValue)
                {
                    row.IsUnlocked = true;
                    row.UnlockedAt = DateTimeOffset.FromUnixTimeSeconds(p.UnlockTime.Value).UtcDateTime;
                    row.ScheduledUnlockAt = null;
                }
            }

            var currentPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(
                (long)session.SteamId64.Value, appId, apiKey, ct);
            if (currentPlaytime > 0)
                game.SteamPlaytimeAtRefresh = currentPlaytime;
        }

        game.AchievementsCachedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // 14. Add to queue (idempotent)
        await queue.AddToQueueAsync(gameId);

        logger.LogInformation("Refresh complete: game {GameId} ({AppId}), {Count} achievements scheduled",
            gameId, appId, scheduleMap.Count);
    }

    // Extract numeric Steam64 ID from URLs like:
    //   https://steamhunters.com/profiles/76561198012345678
    //   https://steamcommunity.com/profiles/76561198012345678
    private static long? TryExtractSteamId(string url)
    {
        var m = SteamIdRegex().Match(url);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    // Extract vanity name from URLs like:
    //   https://steamhunters.com/id/someuser
    //   https://steamcommunity.com/id/someuser
    private static string? TryExtractVanityName(string url)
    {
        var m = VanityRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"/profiles/(\d{17})", RegexOptions.IgnoreCase)]
    private static partial Regex SteamIdRegex();

    [GeneratedRegex(@"/id/([^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VanityRegex();
}
