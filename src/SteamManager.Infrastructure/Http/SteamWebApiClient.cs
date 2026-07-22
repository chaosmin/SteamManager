using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Dto;
using SteamManager.Core.Services;

namespace SteamManager.Infrastructure.Http;

public class SteamWebApiClient(HttpClient http, ILogger<SteamWebApiClient> logger, ISteamAuditService audit)
{
    private static readonly TimeSpan CallDelay = TimeSpan.FromSeconds(1);

    // Strip ?key=... or &key=... from URLs before logging.
    private static readonly Regex KeyParam = new(@"[?&]key=[^&]*", RegexOptions.Compiled);
    private static string StripKey(string url)
    {
        var s = KeyParam.Replace(url, m => m.Value[0] == '?' ? "?" : "");
        return s.TrimEnd('?').TrimEnd('&');
    }

    public async Task<List<SteamAchievementDto>> GetSchemaAchievementsAsync(
        int appId, string apiKey, string language = "english", CancellationToken ct = default)
    {
        var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l={language}";
        var json = await FetchWithRetryAsync(url, ct, "GetSchemaForGame", appId);
        var achievements = JsonDocument.Parse(json).RootElement
            .GetProperty("game").GetProperty("availableGameStats").GetProperty("achievements")
            .EnumerateArray()
            .Select(a => new SteamAchievementDto(
                a.GetProperty("name").GetString()!,
                a.GetProperty("displayName").GetString()!,
                0.0, null,
                a.TryGetProperty("icon", out var ic) ? ic.GetString() : null,
                a.TryGetProperty("icongray", out var ig) ? ig.GetString() : null,
                a.TryGetProperty("description", out var desc) ? desc.GetString() : null))
            .ToList();

        await Task.Delay(CallDelay, ct);
        var pctUrl = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}";
        var pctJson = await FetchWithRetryAsync(pctUrl, ct, "GetGlobalAchievementPercentages", appId);
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
            var json = await FetchWithRetryAsync(url, ct, "GetPlayerAchievements", appId);
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
        var json = await FetchWithRetryAsync(url, ct, "GetOwnedGames");
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

    /// <summary>Returns the AppId the player is currently playing, or null if not in a game / profile private.</summary>
    public async Task<int?> GetCurrentGameIdAsync(ulong steamId, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={steamId}";
            var json = await FetchWithRetryAsync(url, ct, "GetPlayerSummaries");
            var player = JsonDocument.Parse(json).RootElement
                .GetProperty("response").GetProperty("players")
                .EnumerateArray().FirstOrDefault();
            if (player.ValueKind == JsonValueKind.Undefined) return null;
            return player.TryGetProperty("gameid", out var gid) && int.TryParse(gid.GetString(), out var id)
                ? id : null;
        }
        catch { return null; }
    }

    /// <summary>Resolves a Steam vanity URL to a Steam64 ID. Returns null on failure or private/not-found.</summary>
    public async Task<long?> ResolveVanityUrlAsync(string vanityUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(CallDelay, ct);
            var url = $"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v0001/?key={apiKey}&vanityurl={vanityUrl}";
            var json = await FetchWithRetryAsync(url, ct, "ResolveVanityURL");
            var resp = JsonDocument.Parse(json).RootElement.GetProperty("response");
            if (resp.GetProperty("success").GetInt32() == 1 &&
                long.TryParse(resp.GetProperty("steamid").GetString(), out var id))
                return id;
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ResolveVanityURL failed for {VanityUrl}", vanityUrl);
            return null;
        }
    }

    /// <summary>Returns playtime_forever (minutes) for a specific player+game. Returns 0 on failure or private profile.</summary>
    public async Task<int> GetPlayerGamePlaytimeAsync(long steamId, int appId, string apiKey, CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(CallDelay, ct);
            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={apiKey}&steamid={steamId}&include_appinfo=0&include_played_free_games=1&appids_filter[0]={appId}";
            var json = await FetchWithRetryAsync(url, ct, "GetPlayerGamePlaytime", appId);
            var resp = JsonDocument.Parse(json).RootElement.GetProperty("response");
            if (!resp.TryGetProperty("games", out var games)) return 0;
            var first = games.EnumerateArray().FirstOrDefault();
            return first.ValueKind != JsonValueKind.Undefined && first.TryGetProperty("playtime_forever", out var pt)
                ? pt.GetInt32() : 0;
        }
        catch { return 0; }
    }

    public record AppStoreDetails(string? ShortDescription, string[] Tags);

    public async Task<AppStoreDetails> GetAppStoreDetailsAsync(int appId, string language = "english", CancellationToken ct = default)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=short_description,genres,categories&cc=us&l={language}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new AppStoreDetails(null, []);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty(appId.ToString(), out var appNode) ||
                !appNode.TryGetProperty("success", out var succ) || !succ.GetBoolean() ||
                !appNode.TryGetProperty("data", out var data))
                return new AppStoreDetails(null, []);

            var shortDesc = data.TryGetProperty("short_description", out var sd) ? sd.GetString() : null;
            var genres = data.TryGetProperty("genres", out var g)
                ? g.EnumerateArray()
                    .Select(x => x.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "")
                    .Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : Array.Empty<string>();
            var skipCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Steam Cloud", "Remote Play Together", "Remote Play on TV", "Remote Play on Phone",
                  "Remote Play on Tablet", "Steam Leaderboards", "Valve Anti-Cheat enabled", "Stats",
                  "Steam Trading Cards", "Steam Workshop" };
            var categories = data.TryGetProperty("categories", out var c)
                ? c.EnumerateArray()
                    .Select(x => x.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "")
                    .Where(s => !string.IsNullOrEmpty(s) && !skipCategories.Contains(s))
                    .Take(5).ToArray()
                : Array.Empty<string>();

            return new AppStoreDetails(shortDesc, genres.Concat(categories).Distinct().ToArray());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch store details for AppId {AppId}", appId);
            return new AppStoreDetails(null, []);
        }
    }

    private async Task<string> FetchWithRetryAsync(string url, CancellationToken ct,
        string operation = "Unknown", int? appId = null)
    {
        var sw = Stopwatch.StartNew();
        var logUrl = StripKey(url);
        bool success = false;
        string responseSummary = "";
        try
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
                var content = await resp.Content.ReadAsStringAsync(ct);
                success = true;
                responseSummary = $"{content.Length}B";
                return content;
            }
        }
        catch (Exception ex)
        {
            responseSummary = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            _ = audit.LogAsync("WebApi", operation, appId, logUrl, success, responseSummary, (int)sw.ElapsedMilliseconds);
        }
    }
}
