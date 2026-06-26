using SteamManager.Core.Dto;

namespace SteamManager.Core.Services;

public static class AchievementIntervalCalculator
{
    public static List<AchievementIntervalDto> Calculate(
        IReadOnlyList<List<SteamAchievementDto>> playerAchievements)
    {
        if (playerAchievements.Count == 0) return [];

        var allOffsets = new Dictionary<string, List<int>>();

        foreach (var achievements in playerAchievements)
        {
            var unlocked = achievements
                .Where(a => a.UnlockTime.HasValue)
                .OrderBy(a => a.UnlockTime!.Value)
                .ToList();
            if (unlocked.Count == 0) continue;

            var start = unlocked[0].UnlockTime!.Value;
            foreach (var a in unlocked)
            {
                var offset = (int)((a.UnlockTime!.Value - start) / 60);
                if (!allOffsets.ContainsKey(a.ApiName)) allOffsets[a.ApiName] = [];
                allOffsets[a.ApiName].Add(offset);
            }
        }

        return allOffsets
            .Select(kv => new AchievementIntervalDto(kv.Key, Median(kv.Value)))
            .OrderBy(a => a.OffsetMinutes)
            .ToList();
    }

    public static List<AchievementIntervalDto> CalculateFallback(
        IReadOnlyList<SteamAchievementDto> achievements,
        int intervalPerPercent)
    {
        var sorted = achievements.OrderByDescending(a => a.GlobalPercent).ToList();
        var result = new List<AchievementIntervalDto>(sorted.Count);
        int offset = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            result.Add(new AchievementIntervalDto(sorted[i].ApiName, offset));
            if (i + 1 < sorted.Count)
                offset += Math.Max(1, (int)(sorted[i].GlobalPercent - sorted[i + 1].GlobalPercent) * intervalPerPercent);
        }
        return result;
    }

    private static int Median(List<int> values)
    {
        var s = values.OrderBy(x => x).ToList();
        return s.Count % 2 == 0 ? (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2 : s[s.Count / 2];
    }
}
