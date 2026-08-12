using SteamManager.Core.Dto;

namespace SteamManager.Core.Services;

public static class AchievementIntervalCalculator
{
    /// <summary>
    /// Method A scheduling: maps each reference player achievement to an absolute UTC time.
    /// scheduled = refreshTime + (playerUnlockTimestamp − playerFirstUnlockTimestamp).
    /// Skips achievements listed in alreadyUnlockedApiNames.
    /// </summary>
    public static Dictionary<string, DateTime> CalculateScheduledTimes(
        IList<SteamAchievementDto> playerAchs,
        DateTime refreshTime,
        ISet<string> alreadyUnlockedApiNames)
    {
        var unlocked = playerAchs
            .Where(a => a.UnlockTime.HasValue && !alreadyUnlockedApiNames.Contains(a.ApiName))
            .OrderBy(a => a.UnlockTime!.Value)
            .ToList();

        if (unlocked.Count == 0) return [];

        var firstUnlockSec = unlocked[0].UnlockTime!.Value;
        var result = new Dictionary<string, DateTime>(unlocked.Count);
        foreach (var ach in unlocked)
        {
            var offsetSeconds = ach.UnlockTime!.Value - firstUnlockSec;
            result[ach.ApiName] = refreshTime.AddSeconds(offsetSeconds);
        }
        return result;
    }

    /// <summary>
    /// Returns unix-second timestamps where ≥ threshold achievements share the same unlock time.
    /// Non-empty result = burst detected → block Refresh unless OverrideBurstCheck is set.
    /// </summary>
    public static List<long> ValidateBurstThreshold(
        IList<SteamAchievementDto> playerAchs,
        int threshold = 5)
    {
        return playerAchs
            .Where(a => a.UnlockTime.HasValue)
            .GroupBy(a => a.UnlockTime!.Value)
            .Where(g => g.Count() >= threshold)
            .Select(g => g.Key)
            .ToList();
    }
}
