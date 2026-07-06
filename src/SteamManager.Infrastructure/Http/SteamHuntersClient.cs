using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Services;

namespace SteamManager.Infrastructure.Http;

public class SteamHuntersClient(HttpClient http, ILogger<SteamHuntersClient> logger, ISteamAuditService audit, PlaywrightBrowserService playwright)
{

    /// <summary>
    /// Returns median completion time (minutes) and perfect-player count for a given app.
    /// Used as fallback when player scraping fails.
    /// Returns (0, 0) when SteamHunters is unavailable.
    /// </summary>
    public async Task<(int MedianCompletionMinutes, int PerfectPlayerCount)> GetAppInfoAsync(
        int appId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool success = false;
        string responseSummary = "";
        try
        {
            var url = $"https://steamhunters.com/api/apps/{appId}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("SteamHunters {Status} for appId {AppId}", resp.StatusCode, appId);
                responseSummary = $"HTTP {(int)resp.StatusCode}";
                return (0, 0);
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(json).RootElement;
            var median = root.TryGetProperty("medianCompletionTime", out var m) ? m.GetInt32() : 0;
            var count  = root.TryGetProperty("playersPerfectedCount", out var c) ? c.GetInt32() : 0;
            success = true;
            responseSummary = $"median={median}min count={count}";
            return (median, count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SteamHunters unavailable for {AppId}, will use fallback", appId);
            responseSummary = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            return (0, 0);
        }
        finally
        {
            sw.Stop();
            _ = audit.LogAsync("SteamHunters", "GetAppInfo", appId,
                $"appId={appId}", success, responseSummary, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Scrapes https://steamhunters.com/apps/{appId}/users?sort=achievements and returns
    /// the username of the top-ranked player (most achievements unlocked).
    /// Returns null when the page is unavailable or no players are listed.
    /// </summary>
    public async Task<string?> GetTopPlayerUsernameAsync(int appId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool success = false;
        string responseSummary = "";
        try
        {
            var url = $"https://steamhunters.com/apps/{appId}/users?sort=achievements";
            var html = await playwright.GetPageContentAsync(url, ct);
            if (html == null) { responseSummary = "failed to fetch"; return null; }

            // Each player row links to /id/{username}/apps/{appId}/achievements
            var m = Regex.Match(html, $@"/id/([^/""\s]+)/apps/{appId}/achievements", RegexOptions.IgnoreCase);
            if (!m.Success) { responseSummary = "no player link found"; return null; }

            var username = m.Groups[1].Value;
            success = true;
            responseSummary = $"username={username}";
            return username;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SteamHunters GetTopPlayer failed for appId {AppId}", appId);
            responseSummary = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            return null;
        }
        finally
        {
            sw.Stop();
            _ = audit.LogAsync("SteamHunters", "GetTopPlayer", appId,
                $"appId={appId}", success, responseSummary, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Returns per-achievement local completion percentages from the SteamHunters JSON API.
    /// localPercentage = % of SteamHunters completionists who have each achievement (better ordering signal than Steam global%).
    /// Returns empty dict on failure.
    /// </summary>
    public async Task<Dictionary<string, double>> GetAppAchievementsLocalPercentAsync(
        int appId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool success = false;
        string responseSummary = "";
        try
        {
            var url = $"https://steamhunters.com/api/apps/{appId}/achievements";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                responseSummary = $"HTTP {(int)resp.StatusCode}";
                return [];
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(json).RootElement
                .EnumerateArray()
                .Where(a => a.TryGetProperty("apiName", out _) && a.TryGetProperty("localPercentage", out _))
                .ToDictionary(
                    a => a.GetProperty("apiName").GetString()!,
                    a => a.GetProperty("localPercentage").GetDouble());
            success = result.Count > 0;
            responseSummary = $"count={result.Count}";
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SteamHunters GetAppAchievementsLocalPercent failed for {AppId}", appId);
            responseSummary = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            return [];
        }
        finally
        {
            sw.Stop();
            _ = audit.LogAsync("SteamHunters", "GetAppAchievementsLocalPercent", appId,
                $"appId={appId}", success, responseSummary, (int)sw.ElapsedMilliseconds);
        }
    }
}
