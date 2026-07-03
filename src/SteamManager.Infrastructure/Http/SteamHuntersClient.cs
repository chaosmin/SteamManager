using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Services;

namespace SteamManager.Infrastructure.Http;

public class SteamHuntersClient(HttpClient http, ILogger<SteamHuntersClient> logger, ISteamAuditService audit)
{
    /// <summary>
    /// Returns median completion time (minutes) and perfect-player count for a given app.
    /// Returns (0, 0) when SteamHunters is unavailable or the game has no data.
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
}
