using SteamManager.Core.Dto;

namespace SteamManager.Core.Services;

public static class AchievementIntervalCalculator
{
    /// <summary>
    /// Calculates per-achievement unlock offsets (minutes from game start).
    /// When playerPlaytimesMinutes is provided, offsets are absolute (from game start).
    /// When omitted, offsets are relative to each player's first achievement (backward-compatible).
    /// </summary>
    public static List<AchievementIntervalDto> Calculate(
        IReadOnlyList<List<SteamAchievementDto>> playerAchievements,
        IReadOnlyList<int>? playerPlaytimesMinutes = null)
    {
        if (playerAchievements.Count == 0) return [];

        var allOffsets = new Dictionary<string, List<int>>();

        for (int p = 0; p < playerAchievements.Count; p++)
        {
            var achievements = playerAchievements[p];
            var playtime = playerPlaytimesMinutes != null && p < playerPlaytimesMinutes.Count
                ? playerPlaytimesMinutes[p] : 0;

            var unlocked = achievements
                .Where(a => a.UnlockTime.HasValue)
                .OrderBy(a => a.UnlockTime!.Value)
                .ToList();
            if (unlocked.Count == 0) continue;

            var firstTime = unlocked[0].UnlockTime!.Value;
            var span      = (int)((unlocked[^1].UnlockTime!.Value - firstTime) / 60);
            // pre-achievement cushion: total playtime minus the first-to-last ach span.
            // Falls back to 0 (relative offsets) when playtime is unavailable.
            var preAchTime = playtime > span ? playtime - span : 0;

            foreach (var a in unlocked)
            {
                var relOffset = (int)((a.UnlockTime!.Value - firstTime) / 60);
                var absOffset = preAchTime + relOffset;
                if (!allOffsets.ContainsKey(a.ApiName)) allOffsets[a.ApiName] = [];
                allOffsets[a.ApiName].Add(absOffset);
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

    /// <summary>
    /// Like CalculateFallback but scales all offsets so the last achievement lands at totalMinutes.
    /// Use when SteamHunters provides a medianCompletionTime to anchor the spread.
    /// </summary>
    public static List<AchievementIntervalDto> CalculateFallbackScaled(
        IReadOnlyList<SteamAchievementDto> achievements,
        int totalMinutes,
        int intervalPerPercent)
    {
        var raw = CalculateFallback(achievements, intervalPerPercent);
        if (raw.Count == 0 || totalMinutes <= 0) return raw;

        var maxRaw = raw.Max(a => a.OffsetMinutes);
        if (maxRaw <= 0) return raw;

        double scale = (double)totalMinutes / maxRaw;
        return raw.Select(a => new AchievementIntervalDto(a.AchievementId, (int)Math.Round(a.OffsetMinutes * scale)))
                  .ToList();
    }

    private static int Median(List<int> values)
    {
        var s = values.OrderBy(x => x).ToList();
        return s.Count % 2 == 0 ? (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2 : s[s.Count / 2];
    }
}
