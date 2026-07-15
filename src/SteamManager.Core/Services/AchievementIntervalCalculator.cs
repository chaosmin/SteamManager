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

    /// <summary>
    /// Distributes achievements evenly across totalMinutes in the provided order.
    /// Use when you have a reliable unlock order from a real player but not reliable timing
    /// (Steam API timestamps are wall-clock time, not in-game session time).
    /// </summary>
    public static List<AchievementIntervalDto> CalculateFromPlayerOrder(
        IReadOnlyList<SteamAchievementDto> achievementsInOrder,
        int totalMinutes)
    {
        if (achievementsInOrder.Count == 0 || totalMinutes <= 0) return [];
        var n = achievementsInOrder.Count;
        return achievementsInOrder
            .Select((a, i) => new AchievementIntervalDto(a.ApiName, (int)Math.Round((double)(i + 1) / n * totalMinutes)))
            .ToList();
    }

    /// <summary>
    /// Like CalculateFromPlayerOrder but detects DLC/update batches by large calendar gaps
    /// (default: >30 days) in the player's unlock timestamps.
    /// Each batch is distributed proportionally across (totalMinutes / batchCount),
    /// and batches are separated by interBatchGapMinutes to represent the DLC release gap.
    /// </summary>
    public static List<AchievementIntervalDto> CalculateFromPlayerOrderBatched(
        IReadOnlyList<SteamAchievementDto> achievementsInOrder,
        IReadOnlyList<long> unlockTimestampsSec,
        int totalMinutes,
        long batchGapThresholdSec = 30L * 24 * 3600,
        int interBatchGapMinutes = 60)
    {
        if (achievementsInOrder.Count == 0 || totalMinutes <= 0) return [];
        if (achievementsInOrder.Count != unlockTimestampsSec.Count)
            return CalculateFromPlayerOrder(achievementsInOrder, totalMinutes);

        // Split into batches wherever consecutive timestamps differ by more than the threshold
        var batches = new List<List<int>>(); // each list = indices into achievementsInOrder
        var current = new List<int> { 0 };
        for (int i = 1; i < unlockTimestampsSec.Count; i++)
        {
            if (unlockTimestampsSec[i] - unlockTimestampsSec[i - 1] > batchGapThresholdSec)
            {
                batches.Add(current);
                current = [];
            }
            current.Add(i);
        }
        batches.Add(current);

        // Only one batch — fall back to even distribution
        if (batches.Count == 1)
            return CalculateFromPlayerOrder(achievementsInOrder, totalMinutes);

        // Distribute totalMinutes across batches proportionally by batch size,
        // then add interBatchGapMinutes between batches.
        var totalGapMinutes = (batches.Count - 1) * interBatchGapMinutes;
        var playMinutes = Math.Max(totalMinutes - totalGapMinutes, batches.Count); // at least 1 min per batch
        var result = new List<AchievementIntervalDto>(achievementsInOrder.Count);
        int batchOffset = 0;

        for (int b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            var batchShare = (int)Math.Round((double)batch.Count / achievementsInOrder.Count * playMinutes);
            if (batchShare < 1) batchShare = 1;

            for (int j = 0; j < batch.Count; j++)
            {
                var idx = batch[j];
                var minuteInBatch = (int)Math.Round((double)(j + 1) / batch.Count * batchShare);
                result.Add(new AchievementIntervalDto(achievementsInOrder[idx].ApiName, batchOffset + minuteInBatch));
            }

            batchOffset += batchShare + interBatchGapMinutes;
        }

        return result;
    }

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

    private static int Median(List<int> values)
    {
        var s = values.OrderBy(x => x).ToList();
        return s.Count % 2 == 0 ? (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2 : s[s.Count / 2];
    }
}
