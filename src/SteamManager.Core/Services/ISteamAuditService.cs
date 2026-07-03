namespace SteamManager.Core.Services;

public interface ISteamAuditService
{
    Task LogAsync(string source, string operation, int? appId,
                  string? requestSummary, bool success, string? responseSummary, int durationMs);
}
