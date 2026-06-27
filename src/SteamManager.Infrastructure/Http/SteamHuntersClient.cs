using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamManager.Infrastructure.Http;

public class SteamHuntersClient(HttpClient http, ILogger<SteamHuntersClient> logger)
{
    /// <summary>
    /// Returns median completion time (minutes) and perfect-player count for a given app.
    /// Returns (0, 0) when SteamHunters is unavailable or the game has no data.
    /// </summary>
    public async Task<(int MedianCompletionMinutes, int PerfectPlayerCount)> GetAppInfoAsync(
        int appId, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://steamhunters.com/api/apps/{appId}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("SteamHunters {Status} for appId {AppId}", resp.StatusCode, appId);
                return (0, 0);
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(json).RootElement;
            var median = root.TryGetProperty("medianCompletionTime", out var m) ? m.GetInt32() : 0;
            var count  = root.TryGetProperty("playersPerfectedCount", out var c) ? c.GetInt32() : 0;
            return (median, count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SteamHunters unavailable for {AppId}, will use fallback", appId);
            return (0, 0);
        }
    }
}
