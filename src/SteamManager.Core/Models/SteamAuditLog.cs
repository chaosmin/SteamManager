namespace SteamManager.Core.Models;

public class SteamAuditLog
{
    public long Id { get; set; }

    /// <summary>"WebApi" | "SteamHunters" | "SteamCommunity" | "SteamKit2"</summary>
    public string Source { get; set; } = "";

    /// <summary>e.g. "GetPlayerAchievements", "StoreUserStats"</summary>
    public string Operation { get; set; } = "";

    public int? AppId { get; set; }

    /// <summary>Key request parameters; API keys are stripped.</summary>
    public string? RequestSummary { get; set; }

    public bool Success { get; set; }

    /// <summary>Key response fields or error message.</summary>
    public string? ResponseSummary { get; set; }

    public int DurationMs { get; set; }

    public DateTime CreatedAt { get; set; }
}
