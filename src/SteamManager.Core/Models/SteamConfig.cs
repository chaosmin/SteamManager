namespace SteamManager.Core.Models;

public class SteamConfig
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordEnc { get; set; }        // AES-256; cleared after first login
    public string? WebApiKey { get; set; }
    public string? SessionToken { get; set; }        // AES-256 encrypted RefreshToken
    public DateTime? SessionUpdatedAt { get; set; }  // UTC
    public string DisplayTimezone { get; set; } = "UTC";
    public string SyncCron { get; set; } = "0 0 * * *"; // default: daily at midnight
    public string Language { get; set; } = "english";   // steam API language: english / schinese
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
