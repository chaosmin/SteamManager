using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamManager.Infrastructure.Http;

public class SteamHuntersClient(HttpClient http, ILogger<SteamHuntersClient> logger)
{
    public async Task<List<long>> GetPerfectPlayersAsync(int appId, int maxCount, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://steamhunters.com/api/apps/{appId}/players?perfectOnly=true&limit={maxCount}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("SteamHunters {Status} for appId {AppId}", resp.StatusCode, appId);
                return [];
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement
                .EnumerateArray()
                .Select(e => e.GetProperty("steamId").GetInt64())
                .Take(maxCount)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SteamHunters unavailable for {AppId}, will use fallback", appId);
            return [];
        }
    }
}
