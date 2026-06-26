namespace SteamManager.Core.Dto;

public record AchievementIntervalDto(
    string AchievementId,
    int OffsetMinutes   // median minutes from start of playthrough
);
