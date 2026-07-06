using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Dto;
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

        // Step 1: Try to get actual unlock order/timing from the top SteamHunters player via Steam API
        List<AchievementIntervalDto> intervals;
        string? referenceUrl = null; // null = preserve existing; set below when we have a better value

        // Fast path: extract cached player identity from existing ReferenceUrl.
        // Supports two URL formats:
        //   /id/{username}/apps/{appId}/achievements   (vanity URL — needs ResolveVanityURL)
        //   /profiles/{steamId64}/apps/{appId}/achievements  (numeric Steam64 ID — use directly)
        var existingRef = (await db.Games.FindAsync([gameId], ct))?.ReferenceUrl;
        string? cachedUsername = null;
        long? cachedSteamId = null;
        if (existingRef != null)
        {
            var mId = Regex.Match(existingRef, @"steamhunters\.com/id/([^/]+)/apps/", RegexOptions.IgnoreCase);
            if (mId.Success) cachedUsername = mId.Groups[1].Value;
            else
            {
                var mProfile = Regex.Match(existingRef, @"steamhunters\.com/profiles/(\d+)/apps/", RegexOptions.IgnoreCase);
                if (mProfile.Success && long.TryParse(mProfile.Groups[1].Value, out var sid))
                    cachedSteamId = sid;
            }
        }

        // Resolve a Steam64 ID: prefer cached numeric ID, then cached username, then scrape SteamHunters
        long? resolvedSteamId = cachedSteamId;
        string? username = cachedUsername;
        if (resolvedSteamId == null)
        {
            username ??= await hunters.GetTopPlayerUsernameAsync(appId, ct);
            if (username != null)
                resolvedSteamId = await steamApi.ResolveVanityUrlAsync(username, apiKey, ct);
        }

        if (resolvedSteamId.HasValue)
        {
            var playerAchs = await steamApi.GetPlayerAchievementsAsync(resolvedSteamId.Value, appId, apiKey, ct);
            var unlocked = playerAchs.Where(a => a.UnlockTime.HasValue).ToList();
            if (unlocked.Count > 0)
            {
                // Steam timestamps are wall-clock time (player may stop for weeks between sessions).
                // Preserve only the ORDER; distribute evenly across medianCompletionTime.
                var (medianMinutes, _) = await hunters.GetAppInfoAsync(appId, ct);
                var schemaMap = schema.ToDictionary(a => a.ApiName);
                var orderedPairs = unlocked
                    .OrderBy(a => a.UnlockTime)
                    .Where(a => schemaMap.ContainsKey(a.ApiName))
                    .Select(a => (Schema: schemaMap[a.ApiName], Ts: a.UnlockTime!.Value))
                    .ToList();
                var orderedSchema = orderedPairs.Select(p => p.Schema).ToList();
                var timestamps = orderedPairs.Select(p => p.Ts).ToList();
                var targetMinutes = medianMinutes > 0 ? medianMinutes : orderedSchema.Count * FallbackInterval * 10;
                intervals = AchievementIntervalCalculator.CalculateFromPlayerOrderBatched(orderedSchema, timestamps, targetMinutes);
                referenceUrl = username != null
                    ? $"https://steamhunters.com/id/{username}/apps/{appId}/achievements"
                    : $"https://steamhunters.com/profiles/{resolvedSteamId.Value}/apps/{appId}/achievements";
                logger.LogInformation(
                    "Steam API: player {Player}: {Count} achievements ordered, distributed over {Target}min",
                    username ?? resolvedSteamId.Value.ToString(), unlocked.Count, targetMinutes);
            }
            else
            {
                logger.LogWarning("Steam API: player {Player} has no unlocked achievements for {AppId}, using fallback",
                    username ?? resolvedSteamId.Value.ToString(), appId);
                intervals = await BuildFallbackIntervalsAsync(schema, appId, ct);
            }
        }
        else
        {
            if (username != null)
                logger.LogWarning("Could not resolve Steam ID for {Username}, using fallback", username);
            else
                logger.LogWarning("SteamHunters returned no top player for appId {AppId}, using fallback", appId);
            intervals = await BuildFallbackIntervalsAsync(schema, appId, ct);
        }

        // Step 2: Store reference data on the game row
        {
            var gameRow = await db.Games.FindAsync([gameId], ct);
            if (gameRow != null)
            {
                gameRow.ReferencePlayMinutes = intervals.Count > 0 ? intervals.Max(i => i.OffsetMinutes) : null;
                // Update ReferenceUrl only when we got a fresher player-specific one.
                // Preserve user-set URL on fallback; use generic SteamHunters link when nothing exists.
                if (referenceUrl != null)
                    gameRow.ReferenceUrl = referenceUrl;
                else
                    gameRow.ReferenceUrl ??= $"https://steamhunters.com/apps/{appId}/achievements";
            }
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
                    row.Description = ach.Description;
                    row.DescriptionI18n = null;
                }
                else
                {
                    row.DisplayNameI18n = ach.DisplayName;
                    row.DescriptionI18n = ach.Description;
                    // English DisplayName/Description preserved — not overwritten
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
                    DisplayName = ach.DisplayName,
                    DisplayNameI18n = language != "english" ? ach.DisplayName : null,
                    Description = ach.Description,
                    DescriptionI18n = language != "english" ? ach.Description : null,
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

    /// <summary>
    /// Fallback: use SteamHunters localPercentage (% of completionists) for ordering,
    /// scaled to medianCompletionTime. Better than Steam global% because it reflects
    /// completionist play patterns rather than all players.
    /// </summary>
    private async Task<List<AchievementIntervalDto>> BuildFallbackIntervalsAsync(
        List<SteamAchievementDto> schema, int appId, CancellationToken ct)
    {
        var (medianMinutes, _) = await hunters.GetAppInfoAsync(appId, ct);
        var localPercents = await hunters.GetAppAchievementsLocalPercentAsync(appId, ct);

        List<SteamAchievementDto> orderedSchema;
        if (localPercents.Count > 0)
        {
            // Override GlobalPercent with localPercentage so CalculateFallback sorts by completionist %
            orderedSchema = schema
                .Select(a => localPercents.TryGetValue(a.ApiName, out var lp) ? a with { GlobalPercent = lp } : a)
                .ToList();
            logger.LogInformation("Fallback: using SteamHunters localPercent ordering for appId {AppId}", appId);
        }
        else
        {
            orderedSchema = schema;
        }

        return medianMinutes > 0
            ? AchievementIntervalCalculator.CalculateFallbackScaled(orderedSchema, medianMinutes, FallbackInterval)
            : AchievementIntervalCalculator.CalculateFallback(orderedSchema, FallbackInterval);
    }
}
