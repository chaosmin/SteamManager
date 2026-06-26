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
}
