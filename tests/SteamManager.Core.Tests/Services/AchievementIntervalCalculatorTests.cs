using SteamManager.Core.Dto;
using SteamManager.Core.Services;
using Xunit;

namespace SteamManager.Core.Tests.Services;

public class AchievementIntervalCalculatorTests
{
    [Fact]
    public void Calculate_TwoPlayers_ReturnsMedianIntervals()
    {
        var players = new List<List<SteamAchievementDto>>
        {
            new List<SteamAchievementDto>
            {
                new("ACH_A", "A", 80.0, 1_000_000_000),
                new("ACH_B", "B", 50.0, 1_000_003_600),
                new("ACH_C", "C", 20.0, 1_000_007_200),
            },
            new List<SteamAchievementDto>
            {
                new("ACH_A", "A", 80.0, 1_000_000_000),
                new("ACH_B", "B", 50.0, 1_000_002_400),
                new("ACH_C", "C", 20.0, 1_000_006_000),
            },
        };

        var result = AchievementIntervalCalculator.Calculate(players);

        Assert.Equal(3, result.Count);
        Assert.Equal("ACH_A", result[0].AchievementId);
        Assert.Equal(0, result[0].OffsetMinutes);
        Assert.Equal("ACH_B", result[1].AchievementId);
        Assert.Equal(50, result[1].OffsetMinutes);
        Assert.Equal("ACH_C", result[2].AchievementId);
        Assert.Equal(110, result[2].OffsetMinutes);
    }

    [Fact]
    public void Calculate_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AchievementIntervalCalculator.Calculate([]));
    }

    [Fact]
    public void CalculateFallback_SortsByGlobalPercent()
    {
        var achievements = new List<SteamAchievementDto>
        {
            new("ACH_HARD", "Hard", 5.0,  null),
            new("ACH_EASY", "Easy", 80.0, null),
            new("ACH_MID",  "Mid",  40.0, null),
        };

        var result = AchievementIntervalCalculator.CalculateFallback(achievements, intervalPerPercent: 2);

        Assert.Equal("ACH_EASY", result[0].AchievementId);
        Assert.Equal(0, result[0].OffsetMinutes);
        Assert.Equal("ACH_MID", result[1].AchievementId);
        Assert.Equal(80, result[1].OffsetMinutes);
        Assert.Equal("ACH_HARD", result[2].AchievementId);
        Assert.Equal(150, result[2].OffsetMinutes);
    }

    [Fact]
    public void CalculateScheduledTimes_PreservesRelativeOffsets()
    {
        // Player unlocked: A at t=0, B at t+3600s (60 min), C at t+7200s (120 min)
        var baseTime = 1_000_000_000L;
        var playerAchs = new List<SteamAchievementDto>
        {
            new("ACH_A", "A", 80.0, baseTime),
            new("ACH_B", "B", 50.0, baseTime + 3600),
            new("ACH_C", "C", 20.0, baseTime + 7200),
        };
        var refreshTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var result = AchievementIntervalCalculator.CalculateScheduledTimes(
            playerAchs, refreshTime, new HashSet<string>());

        Assert.Equal(3, result.Count);
        Assert.Equal(refreshTime,              result["ACH_A"]);
        Assert.Equal(refreshTime.AddMinutes(60),  result["ACH_B"]);
        Assert.Equal(refreshTime.AddMinutes(120), result["ACH_C"]);
    }

    [Fact]
    public void CalculateScheduledTimes_SkipsAlreadyUnlocked()
    {
        var baseTime = 1_000_000_000L;
        var playerAchs = new List<SteamAchievementDto>
        {
            new("ACH_A", "A", 80.0, baseTime),
            new("ACH_B", "B", 50.0, baseTime + 3600),
        };
        var refreshTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var alreadyUnlocked = new HashSet<string> { "ACH_A" };

        var result = AchievementIntervalCalculator.CalculateScheduledTimes(
            playerAchs, refreshTime, alreadyUnlocked);

        Assert.Single(result);
        Assert.False(result.ContainsKey("ACH_A"));
        Assert.True(result.ContainsKey("ACH_B"));
    }

    [Fact]
    public void ValidateBurstThreshold_DetectsBurst()
    {
        var sameTime = 1_000_000_000L;
        var playerAchs = new List<SteamAchievementDto>
        {
            new("A1", "A1", 1.0, sameTime),
            new("A2", "A2", 2.0, sameTime),
            new("A3", "A3", 3.0, sameTime),
            new("A4", "A4", 4.0, sameTime),
            new("A5", "A5", 5.0, sameTime),
            new("A6", "A6", 6.0, sameTime + 60),
        };

        var bursts = AchievementIntervalCalculator.ValidateBurstThreshold(playerAchs, threshold: 5);

        Assert.Single(bursts);
        Assert.Equal(sameTime, bursts[0]);
    }

    [Fact]
    public void ValidateBurstThreshold_NoBurstWhenBelowThreshold()
    {
        var playerAchs = new List<SteamAchievementDto>
        {
            new("A1", "A1", 1.0, 1_000_000_000L),
            new("A2", "A2", 2.0, 1_000_000_000L),
            new("A3", "A3", 3.0, 1_000_001_000L),
        };

        var bursts = AchievementIntervalCalculator.ValidateBurstThreshold(playerAchs, threshold: 5);

        Assert.Empty(bursts);
    }
}
