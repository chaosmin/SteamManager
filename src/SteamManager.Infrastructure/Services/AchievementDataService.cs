using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class AchievementDataService(
    AppDbContext db,
    SteamWebApiClient steamApi,
    SteamHuntersClient hunters,
    ISteamSessionService session,
    IConfiguration config,
    ILogger<AchievementDataService> logger) : IAchievementDataService
{
    private int FallbackInterval => config.GetValue("AchievementData:FallbackIntervalPerPercentDiff", 2);

    public async Task LoadAchievementsAsync(int gameId, int appId, CancellationToken ct = default)
    {
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Steam config not found");
        var apiKey = cfg.WebApiKey
            ?? throw new InvalidOperationException("Steam Web API key not configured");

        var language = cfg.Language ?? "english";
        logger.LogInformation("Loading achievements for appId {AppId} (gameId {GameId}, lang {Lang})", appId, gameId, language);
        var schema = await steamApi.GetSchemaAchievementsAsync(appId, apiKey, language, ct);
        var (medianMinutes, _) = await hunters.GetAppInfoAsync(appId, ct);

        var intervals = medianMinutes > 0
            ? AchievementIntervalCalculator.CalculateFallbackScaled(schema, medianMinutes, FallbackInterval)
            : AchievementIntervalCalculator.CalculateFallback(schema, FallbackInterval);

        // Store reference completion time on the game for display purposes
        {
            var gameRow = await db.Games.FindAsync([gameId], ct);
            if (gameRow != null)
                gameRow.ReferencePlayMinutes = medianMinutes > 0 ? medianMinutes
                    : intervals.Count > 0 ? intervals.Max(i => i.OffsetMinutes) : null;
        }

        var intervalMap = intervals.ToDictionary(i => i.AchievementId, i => i.OffsetMinutes);

        var existing = await db.Achievements
            .Where(a => a.GameId == gameId)
            .ToDictionaryAsync(a => a.ApiName, ct);

        foreach (var ach in schema)
        {
            var offsetMinutes = intervalMap.GetValueOrDefault(ach.ApiName, 0);
            if (existing.TryGetValue(ach.ApiName, out var row))
            {
                if (language == "english")
                {
                    row.DisplayName = ach.DisplayName;
                    row.DisplayNameI18n = null;
                }
                else
                {
                    row.DisplayNameI18n = ach.DisplayName;
                    // DisplayName (English) preserved — not overwritten
                }
                row.GlobalPercent = ach.GlobalPercent;
                row.IconUrl = ach.IconUrl;
                row.IconGrayUrl = ach.IconGrayUrl;
                row.UnlockOffsetMinutes = offsetMinutes;
            }
            else
            {
                db.Achievements.Add(new Achievement
                {
                    GameId = gameId,
                    AppId = appId,
                    ApiName = ach.ApiName,
                    DisplayName = ach.DisplayName,  // best-effort fallback on first insert
                    DisplayNameI18n = language != "english" ? ach.DisplayName : null,
                    GlobalPercent = ach.GlobalPercent,
                    IconUrl = ach.IconUrl,
                    IconGrayUrl = ach.IconGrayUrl,
                    UnlockOffsetMinutes = offsetMinutes,
                    IsUnlocked = false,
                });
            }
        }

        // Sync the current user's own unlock status
        if (session.SteamId64.HasValue)
        {
            var playerAchs = await steamApi.GetPlayerAchievementsAsync(
                (long)session.SteamId64.Value, appId, apiKey, ct);
            var playerMap = playerAchs.ToDictionary(a => a.ApiName);

            var rows = await db.Achievements.Where(a => a.GameId == gameId).ToListAsync(ct);
            foreach (var row in rows)
            {
                if (playerMap.TryGetValue(row.ApiName, out var p))
                {
                    row.IsUnlocked = p.UnlockTime.HasValue;
                    row.UnlockedAt = p.UnlockTime.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(p.UnlockTime.Value).UtcDateTime
                        : null;
                }
            }
        }

        var game = await db.Games.FindAsync([gameId], ct);
        if (game != null) game.AchievementsCachedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

}
