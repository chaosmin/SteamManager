using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Services;

namespace SteamManager.Infrastructure.Http;

/// <summary>
/// Scrapes the Steam Community badge page to find remaining card drops per game.
/// </summary>
public class SteamCommunityClient(HttpClient http, ILogger<SteamCommunityClient> logger, ISteamAuditService audit)
{
    // Matches a badge row block: captures appid and optionally the drops count within ~2000 chars
    private static readonly Regex BadgeRowRegex = new(
        @"data-appid=""(\d+)""[\s\S]{0,2000?}?(\d+)\s+card\s+drops?\s+remaining",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns a dictionary of appId → remaining card drops for the given Steam user.
    /// Only includes games that actually have drops remaining.
    /// </summary>
    public async Task<Dictionary<int, int>> GetCardDropsRemainingAsync(ulong steamId64, CancellationToken ct = default)
    {
        var result = new Dictionary<int, int>();

        for (int page = 1; page <= 20; page++)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/badges/?p={page}";
            string html;
            var sw = Stopwatch.StartNew();
            bool pageSuccess = false;
            string pageSummary = "";
            try
            {
                var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Badge page {Page} returned {Status}", page, response.StatusCode);
                    pageSummary = $"HTTP {(int)response.StatusCode}";
                    _ = audit.LogAsync("SteamCommunity", "GetBadgePage", null,
                        $"steamId={steamId64} page={page}", false, pageSummary, (int)sw.ElapsedMilliseconds);
                    break;
                }
                html = await response.Content.ReadAsStringAsync(ct);
                pageSuccess = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch badge page {Page} for {SteamId}", page, steamId64);
                pageSummary = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                _ = audit.LogAsync("SteamCommunity", "GetBadgePage", null,
                    $"steamId={steamId64} page={page}", false, pageSummary, (int)sw.ElapsedMilliseconds);
                break;
            }

            bool hasNextPage = html.Contains("pagebtn_next", StringComparison.OrdinalIgnoreCase);

            var matches = BadgeRowRegex.Matches(html);
            int pageDropCount = 0;
            foreach (Match m in matches)
            {
                if (int.TryParse(m.Groups[1].Value, out var appId) &&
                    int.TryParse(m.Groups[2].Value, out var drops) &&
                    drops > 0)
                {
                    result[appId] = drops;
                    pageDropCount++;
                }
            }

            sw.Stop();
            _ = audit.LogAsync("SteamCommunity", "GetBadgePage", null,
                $"steamId={steamId64} page={page}",
                pageSuccess,
                $"{pageDropCount} games with drops hasNextPage={hasNextPage}",
                (int)sw.ElapsedMilliseconds);

            if (!hasNextPage) break;
            await Task.Delay(500, ct);
        }

        logger.LogInformation("Found {Count} games with card drops remaining for {SteamId}", result.Count, steamId64);
        return result;
    }
}
