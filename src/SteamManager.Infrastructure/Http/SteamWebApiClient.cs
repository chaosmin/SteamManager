using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Dto;

namespace SteamManager.Infrastructure.Http;

public class SteamWebApiClient(HttpClient http, ILogger<SteamWebApiClient> logger)
{
    private static readonly TimeSpan CallDelay = TimeSpan.FromSeconds(1);

    public async Task<List<SteamAchievementDto>> GetSchemaAchievementsAsync(
        int appId, string apiKey, string language = "english", CancellationToken ct = default)
    {
        var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l={language}";
        var json = await FetchWithRetryAsync(url, ct);
        var achievements = JsonDocument.Parse(json).RootElement
            .GetProperty("game").GetProperty("availableGameStats").GetProperty("achievements")
            .EnumerateArray()
            .Select(a => new SteamAchievementDto(
                a.GetProperty("name").GetString()!,
                a.GetProperty("displayName").GetString()!,
                0.0, null,
                a.TryGetProperty("icon", out var ic) ? ic.GetString() : null,
                a.TryGetProperty("icongray", out var ig) ? ig.GetString() : null))
            .ToList();

        await Task.Delay(CallDelay, ct);
        var pctJson = await FetchWithRetryAsync(
            $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}", ct);
        var pctMap = JsonDocument.Parse(pctJson).RootElement
            .GetProperty("achievementpercentages").GetProperty("achievements")
            .EnumerateArray()
            .ToDictionary(
                a => a.GetProperty("name").GetString()!,
                a => {
                    var p = a.GetProperty("percent");
                    return p.ValueKind == JsonValueKind.String
                        ? double.Parse(p.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                        : p.GetDouble();
                });

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

    public async Task<List<(int AppId, string Name, int PlaytimeMinutes)>> GetOwnedGamesAsync(
        ulong steamId, string apiKey, string language = "english", CancellationToken ct = default)
    {
        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={apiKey}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&l={language}";
        var json = await FetchWithRetryAsync(url, ct);
        var response = JsonDocument.Parse(json).RootElement.GetProperty("response");
        if (!response.TryGetProperty("games", out var games)) return [];
        return games.EnumerateArray()
            .Select(g => (
                AppId: g.GetProperty("appid").GetInt32(),
                Name: g.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {g.GetProperty("appid").GetInt32()}" : $"App {g.GetProperty("appid").GetInt32()}",
                PlaytimeMinutes: g.TryGetProperty("playtime_forever", out var pt) ? pt.GetInt32() : 0
            ))
            .ToList();
    }

    /// <summary>Returns playtime_forever (minutes) for a specific player+game. Returns 0 on failure or private profile.</summary>
    public async Task<int> GetPlayerGamePlaytimeAsync(long steamId, int appId, string apiKey, CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(CallDelay, ct);
            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={apiKey}&steamid={steamId}&include_appinfo=0&include_played_free_games=1&appids_filter[0]={appId}";
            var json = await FetchWithRetryAsync(url, ct);
            var resp = JsonDocument.Parse(json).RootElement.GetProperty("response");
            if (!resp.TryGetProperty("games", out var games)) return 0;
            var first = games.EnumerateArray().FirstOrDefault();
            return first.ValueKind != JsonValueKind.Undefined && first.TryGetProperty("playtime_forever", out var pt)
                ? pt.GetInt32() : 0;
        }
        catch { return 0; }
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
