using System.Text.Json;
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
    IConfiguration config,
    ILogger<AchievementDataService> logger) : IAchievementDataService
{
    private int CacheExpiryDays => config.GetValue("AchievementData:CacheExpiryDays", 7);
    private int MaxPlayers => config.GetValue("AchievementData:MaxReferencePlayers", 20);
    private int FallbackInterval => config.GetValue("AchievementData:FallbackIntervalPerPercentDiff", 2);

    public async Task<List<AchievementIntervalDto>> GetIntervalsAsync(int appId, CancellationToken ct = default)
    {
        var cache = await db.AchievementCaches.FirstOrDefaultAsync(c => c.AppId == appId, ct);
        if (cache != null && cache.FetchedAt > DateTime.UtcNow.AddDays(-CacheExpiryDays))
        {
            logger.LogDebug("Cache hit for appId {AppId}", appId);
            return JsonSerializer.Deserialize<List<AchievementIntervalDto>>(cache.Data)!;
        }

        await RefreshCacheAsync(appId, ct);
        cache = await db.AchievementCaches.FirstAsync(c => c.AppId == appId, ct);
        return JsonSerializer.Deserialize<List<AchievementIntervalDto>>(cache.Data)!;
    }

    public async Task RefreshCacheAsync(int appId, CancellationToken ct = default)
    {
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Steam config not found");
        var apiKey = cfg.WebApiKey
            ?? throw new InvalidOperationException("Steam Web API key not configured");

        logger.LogInformation("Fetching achievement intervals for appId {AppId}", appId);
        var schema = await steamApi.GetSchemaAchievementsAsync(appId, apiKey, ct);
        var playerIds = await hunters.GetPerfectPlayersAsync(appId, MaxPlayers, ct);

        List<AchievementIntervalDto> intervals;
        if (playerIds.Count > 0)
        {
            var playerData = new List<List<SteamAchievementDto>>();
            foreach (var id in playerIds)
            {
                var data = await steamApi.GetPlayerAchievementsAsync(id, appId, apiKey, ct);
                if (data.Any(a => a.UnlockTime.HasValue)) playerData.Add(data);
            }
            intervals = playerData.Count > 0
                ? AchievementIntervalCalculator.Calculate(playerData)
                : AchievementIntervalCalculator.CalculateFallback(schema, FallbackInterval);
        }
        else
        {
            intervals = AchievementIntervalCalculator.CalculateFallback(schema, FallbackInterval);
        }

        var existing = await db.AchievementCaches.FirstOrDefaultAsync(c => c.AppId == appId, ct);
        if (existing == null)
        {
            db.AchievementCaches.Add(new AchievementCache
            {
                AppId = appId,
                Data = JsonSerializer.Serialize(intervals),
                FetchedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Data = JsonSerializer.Serialize(intervals);
            existing.FetchedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
