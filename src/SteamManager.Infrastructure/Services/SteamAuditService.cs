using Microsoft.Extensions.DependencyInjection;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

/// <summary>
/// Writes one row per Steam interaction (HTTP API call or SteamKit2 protocol call)
/// to the steam_audit_log table. Never throws — audit failures must not crash the app.
/// </summary>
public class SteamAuditService(IServiceScopeFactory scopeFactory) : ISteamAuditService
{
    public async Task LogAsync(string source, string operation, int? appId,
                               string? requestSummary, bool success, string? responseSummary, int durationMs)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SteamAuditLogs.Add(new SteamAuditLog
            {
                Source          = source,
                Operation       = operation,
                AppId           = appId,
                RequestSummary  = Truncate(requestSummary, 500),
                Success         = success,
                ResponseSummary = Truncate(responseSummary, 1000),
                DurationMs      = durationMs,
                CreatedAt       = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Intentionally swallowed — audit must never crash the app.
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}
