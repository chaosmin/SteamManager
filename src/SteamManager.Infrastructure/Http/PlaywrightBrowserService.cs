using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamManager.Infrastructure.Http;

/// <summary>
/// Fetches JavaScript-rendered pages via FlareSolverr (bypasses Cloudflare Bot Management).
/// FlareSolverr runs on the NAS at 192.168.71.39:8191.
/// </summary>
public sealed class PlaywrightBrowserService(ILogger<PlaywrightBrowserService> logger) : IAsyncDisposable
{
    private const string FlareSolverrUrl = "http://192.168.71.39:8191/v1";
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Fetches full page HTML via FlareSolverr. Returns null on error.
    /// </summary>
    public async Task<string?> GetPageContentAsync(string url, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("FlareSolverr: fetching {Url}", url);
            var body = JsonSerializer.Serialize(new { cmd = "request.get", url, maxTimeout = 90000 });
            var resp = await _http.PostAsync(FlareSolverrUrl,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json).RootElement;

            var status = doc.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status != "ok")
            {
                var msg = doc.TryGetProperty("message", out var m) ? m.GetString() : json;
                logger.LogWarning("FlareSolverr: {Url} failed — {Msg}", url, msg);
                return null;
            }

            var html = doc.GetProperty("solution").GetProperty("response").GetString();
            logger.LogInformation("FlareSolverr: {Url} OK ({Len} chars)", url, html?.Length ?? 0);
            return html;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FlareSolverr: failed to fetch {Url}", url);
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
