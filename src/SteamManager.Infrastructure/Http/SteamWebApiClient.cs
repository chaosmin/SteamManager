using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Dto;

namespace SteamManager.Infrastructure.Http;

public class SteamWebApiClient(HttpClient http, ILogger<SteamWebApiClient> logger)
{
    private static readonly TimeSpan CallDelay = TimeSpan.FromSeconds(1);

    public async Task<List<SteamAchievementDto>> GetSchemaAchievementsAsync(
        int appId, string apiKey, CancellationToken ct = default)
    {
        var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l=english";
        var json = await FetchWithRetryAsync(url, ct);
        var achievements = JsonDocument.Parse(json).RootElement
            .GetProperty("game").GetProperty("availableGameStats").GetProperty("achievements")
            .EnumerateArray()
            .Select(a => new SteamAchievementDto(
                a.GetProperty("name").GetString()!,
                a.GetProperty("displayName").GetString()!,
                0.0, null))
            .ToList();

        await Task.Delay(CallDelay, ct);
        var pctJson = await FetchWithRetryAsync(
            $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}", ct);
        var pctMap = JsonDocument.Parse(pctJson).RootElement
            .GetProperty("achievementpercentages").GetProperty("achievements")
            .EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("percent").GetDouble());

        return achievements.Select(a => a with { GlobalPercent = pctMap.GetValueOrDefault(a.ApiName) }).ToList();
    }

    public async Task<List<SteamAchievementDto>> GetPlayerAchievementsAsync(
        long steamId, int appId, string apiKey, CancellationToken ct = default)
    {
        await Task.Delay(CallDelay, ct);
        try
        {
            var url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={apiKey}&steamid={steamId}&appid={appId}";
            var json = await FetchWithRetryAsync(url, ct);
            return JsonDocument.Parse(json).RootElement
                .GetProperty("playerstats").GetProperty("achievements")
                .EnumerateArray()
                .Select(a => new SteamAchievementDto(
                    a.GetProperty("apiname").GetString()!,
                    a.GetProperty("apiname").GetString()!,
                    0.0,
                    a.GetProperty("achieved").GetInt32() == 1
                        ? (long?)a.GetProperty("unlocktime").GetInt64()
                        : null))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get achievements for player {SteamId}", steamId);
            return [];
        }
    }

    private async Task<string> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt >= 3) throw new HttpRequestException("Steam API 429 after 3 retries");
                logger.LogWarning("Steam API 429, waiting 60s (attempt {A}/3)", attempt);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}
