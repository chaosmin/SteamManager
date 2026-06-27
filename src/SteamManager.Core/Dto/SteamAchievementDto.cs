namespace SteamManager.Core.Dto;

public record SteamAchievementDto(
    string ApiName,
    string DisplayName,
    double GlobalPercent,
    long? UnlockTime,       // Unix timestamp; null if locked
    string? IconUrl = null,
    string? IconGrayUrl = null
);
