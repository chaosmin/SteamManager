# v0.4.0 Achievement Scheduling Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace offset-based achievement scheduling with absolute-timestamp scheduling, add game queue with max-2-concurrent enforcement, and introduce MCT-based playtime tracking.

**Architecture:** `RefreshAsync` (new `GameRefreshService`) computes concrete UTC unlock times from a linked reference player's real Steam unlock timestamps and inserts the game into a simple queue. `GameIdleService` now enforces ≤2 concurrent games and fires `MctReached` when elapsed time satisfies MCT. A global 60-second scanner (`UnlockSchedulerService`) replaces per-game timers.

**Tech Stack:** ASP.NET Core 8 / Blazor Server / MudBlazor, EF Core 8 + Pomelo MySQL, SteamKit2, xUnit

---

## File Map

| Action | Path |
|--------|------|
| Modify | `src/SteamManager.Core/Models/Game.cs` |
| Modify | `src/SteamManager.Core/Models/Achievement.cs` |
| Delete | `src/SteamManager.Core/Models/PlayQueueItem.cs` |
| Create | `src/SteamManager.Core/Models/GameQueueEntry.cs` |
| Create | `src/SteamManager.Core/Models/GameReferencePlayer.cs` |
| Modify | `src/SteamManager.Core/Services/IGameIdleService.cs` |
| Delete | `src/SteamManager.Core/Services/IUnlockSchedulerService.cs` |
| Delete  | `src/SteamManager.Core/Services/IAchievementDataService.cs` |
| Create | `src/SteamManager.Core/Services/IGameRefreshService.cs` |
| Delete | `src/SteamManager.Core/Services/IPlayQueueService.cs` |
| Create | `src/SteamManager.Core/Services/IGameQueueService.cs` |
| Modify | `src/SteamManager.Core/Services/AchievementIntervalCalculator.cs` |
| Modify | `src/SteamManager.Core/Services/AchievementUnlockNotifier.cs` |
| Modify | `src/SteamManager.Infrastructure/Persistence/AppDbContext.cs` |
| Create | EF migration `src/SteamManager.Infrastructure/Migrations/*_v040_…` |
| Modify | `src/SteamManager.Infrastructure/Services/GameIdleService.cs` |
| Modify | `src/SteamManager.Infrastructure/Services/UnlockSchedulerService.cs` |
| Delete  | `src/SteamManager.Infrastructure/Services/AchievementDataService.cs` |
| Create | `src/SteamManager.Infrastructure/Services/GameRefreshService.cs` |
| Delete | `src/SteamManager.Infrastructure/Services/PlayQueueService.cs` |
| Create | `src/SteamManager.Infrastructure/Services/GameQueueService.cs` |
| Modify | `src/SteamManager.Infrastructure/Services/StartupRecoveryService.cs` |
| Modify | `src/SteamManager.Web/Program.cs` |
| Modify | `src/SteamManager.Web/Components/Pages/Dashboard.razor` |
| Modify | `src/SteamManager.Web/Components/Pages/GameDetail.razor` |
| Modify | `src/SteamManager.Web/Components/Pages/Games.razor` |
| Modify | `tests/SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests.cs` |

---

## Task 1: Update Domain Models

**Files:**
- Modify: `src/SteamManager.Core/Models/Game.cs`
- Modify: `src/SteamManager.Core/Models/Achievement.cs`
- Create: `src/SteamManager.Core/Models/GameQueueEntry.cs`
- Create: `src/SteamManager.Core/Models/GameReferencePlayer.cs`
- Delete: `src/SteamManager.Core/Models/PlayQueueItem.cs`

- [ ] **Step 1: Replace Game.cs**

```csharp
// src/SteamManager.Core/Models/Game.cs
namespace SteamManager.Core.Models;

public enum GameStatus { Idle, Playing, Scheduled, Completed }

public class Game
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameI18n { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Idle;

    // Playtime tracking (re-anchored at each Start via Steam API)
    public int SteamPlaytimeAtRefresh { get; set; }   // Steam-recorded minutes at last Start/Refresh
    public int? TargetMinutes { get; set; }            // MCT from SteamHunters (or reference player duration)
    public DateTime? SessionStartedAt { get; set; }   // UTC — when current Playing session began

    // Achievement cache freshness
    public DateTime? AchievementsCachedAt { get; set; }

    // Steam trading card drops remaining (null = not yet synced)
    public int? DropsRemaining { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Achievement> Achievements { get; set; } = [];
    public GameReferencePlayer? ReferencePlayer { get; set; }
}
```

- [ ] **Step 2: Replace Achievement.cs**

```csharp
// src/SteamManager.Core/Models/Achievement.cs
namespace SteamManager.Core.Models;

public class Achievement
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public int AppId { get; set; }
    public string ApiName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DisplayNameI18n { get; set; }
    public string? Description { get; set; }
    public string? DescriptionI18n { get; set; }
    public double GlobalPercent { get; set; }
    public string? IconUrl { get; set; }
    public string? IconGrayUrl { get; set; }

    // Scheduling
    public DateTime? ScheduledUnlockAt { get; set; }  // UTC absolute time to unlock; null = not scheduled
    public bool IsUnlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Game Game { get; set; } = null!;
}
```

- [ ] **Step 3: Create GameQueueEntry.cs**

```csharp
// src/SteamManager.Core/Models/GameQueueEntry.cs
namespace SteamManager.Core.Models;

public class GameQueueEntry
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public int Position { get; set; }   // 0-based; lower = higher priority
    public DateTime AddedAt { get; set; }
}
```

- [ ] **Step 4: Create GameReferencePlayer.cs**

```csharp
// src/SteamManager.Core/Models/GameReferencePlayer.cs
namespace SteamManager.Core.Models;

public class GameReferencePlayer
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public string PlayerUrl { get; set; } = string.Empty;  // SteamHunters profile URL, max 512 chars
    public bool OverrideBurstCheck { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 5: Delete PlayQueueItem.cs**

```bash
rm src/SteamManager.Core/Models/PlayQueueItem.cs
```

- [ ] **Step 6: Build Core**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Core/SteamManager.Core.csproj 2>&1 | tail -5
```

Expected: `Build succeeded` (errors in other projects from removed PlayQueueItem are expected — fixed in later tasks).

- [ ] **Step 7: Commit**

```bash
git add src/SteamManager.Core/Models/
git commit -m "feat: v0.4.0 domain models — GameStatus, ScheduledUnlockAt, GameQueueEntry, GameReferencePlayer"
```

---

## Task 2: AppDbContext + EF Migration

**Files:**
- Modify: `src/SteamManager.Infrastructure/Persistence/AppDbContext.cs`
- Create: EF migration

- [ ] **Step 1: Replace AppDbContext.cs**

```csharp
// src/SteamManager.Infrastructure/Persistence/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using SteamManager.Core.Models;

namespace SteamManager.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SteamConfig> SteamConfigs => Set<SteamConfig>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<SteamAuditLog> SteamAuditLogs => Set<SteamAuditLog>();
    public DbSet<GameQueueEntry> GameQueue => Set<GameQueueEntry>();
    public DbSet<GameReferencePlayer> GameReferencePlayers => Set<GameReferencePlayer>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SteamConfig>(e =>
        {
            e.ToTable("steam_config");
            e.Property(x => x.DisplayTimezone).HasDefaultValue("UTC");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<Game>(e =>
        {
            e.ToTable("game");
            e.HasIndex(x => x.AppId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<Achievement>(e =>
        {
            e.ToTable("achievement");
            e.HasIndex(x => new { x.GameId, x.ApiName }).IsUnique();
            e.HasIndex(x => new { x.IsUnlocked, x.ScheduledUnlockAt });  // for global scanner
            e.HasOne(x => x.Game).WithMany(g => g.Achievements)
             .HasForeignKey(x => x.GameId);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<SteamAuditLog>(e =>
        {
            e.ToTable("steam_audit_log");
            e.Property(x => x.Source).HasMaxLength(50);
            e.Property(x => x.Operation).HasMaxLength(100);
            e.Property(x => x.RequestSummary).HasMaxLength(500);
            e.Property(x => x.ResponseSummary).HasMaxLength(1000);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Source, x.Operation });
        });

        mb.Entity<GameQueueEntry>(e =>
        {
            e.ToTable("game_queue");
            e.HasOne(x => x.Game).WithMany()
             .HasForeignKey(x => x.GameId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.GameId).IsUnique();
            e.HasIndex(x => x.Position);
        });

        mb.Entity<GameReferencePlayer>(e =>
        {
            e.ToTable("game_reference_player");
            e.HasOne(x => x.Game).WithOne(g => g.ReferencePlayer)
             .HasForeignKey<GameReferencePlayer>(x => x.GameId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.GameId).IsUnique();
            e.Property(x => x.PlayerUrl).HasMaxLength(512);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        foreach (var entity in mb.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLower(name[i]));
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Create EF migration**

```bash
cd /Users/hugomin/Developer/Repository/chaosmin/SteamManager
/Users/hugomin/.dotnet/dotnet ef migrations add v040_achievement_scheduling_redesign \
  --project src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj \
  --startup-project src/SteamManager.Web/SteamManager.Web.csproj
```

Expected: new file in `src/SteamManager.Infrastructure/Migrations/` created.

- [ ] **Step 3: Verify migration builds**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj 2>&1 | tail -5
```

Expected: `Build succeeded`

- [ ] **Step 4: Commit**

```bash
git add src/SteamManager.Infrastructure/Persistence/ src/SteamManager.Infrastructure/Migrations/
git commit -m "feat: AppDbContext v0.4.0 — game_queue, game_reference_player, scheduled_unlock_at index"
```

---

## Task 3: Interfaces

**Files:**
- Modify: `src/SteamManager.Core/Services/IGameIdleService.cs`
- Delete: `src/SteamManager.Core/Services/IUnlockSchedulerService.cs`
- Delete: `src/SteamManager.Core/Services/IAchievementDataService.cs`
- Create: `src/SteamManager.Core/Services/IGameRefreshService.cs`
- Delete: `src/SteamManager.Core/Services/IPlayQueueService.cs`
- Create: `src/SteamManager.Core/Services/IGameQueueService.cs`
- Modify: `src/SteamManager.Core/Services/AchievementUnlockNotifier.cs`

- [ ] **Step 1: Replace IGameIdleService.cs**

```csharp
// src/SteamManager.Core/Services/IGameIdleService.cs
namespace SteamManager.Core.Services;

public interface IGameIdleService
{
    IReadOnlyList<int> PlayingAppIds { get; }
    bool IsRunning(int appId);
    Task StartAsync(int appId, CancellationToken ct = default);
    Task StopAsync(int appId);
    event Action<int>? MctReached;           // fires when MCT reached → game transitions to Scheduled
    event Action<int, int>? ProgressUpdated; // (appId, elapsedSessionMinutes)
}
```

- [ ] **Step 2: Delete old interfaces**

```bash
rm src/SteamManager.Core/Services/IUnlockSchedulerService.cs
rm src/SteamManager.Core/Services/IAchievementDataService.cs
rm src/SteamManager.Core/Services/IPlayQueueService.cs
```

- [ ] **Step 3: Create IGameRefreshService.cs**

```csharp
// src/SteamManager.Core/Services/IGameRefreshService.cs
namespace SteamManager.Core.Services;

public interface IGameRefreshService
{
    /// <summary>Refresh achievement schedule using the game's linked reference player.</summary>
    Task RefreshAsync(int gameId, int appId, CancellationToken ct = default);

    /// <summary>Reset all achievements to unlocked=false, then run full RefreshAsync.</summary>
    Task ForceRefreshAsync(int gameId, int appId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create IGameQueueService.cs**

```csharp
// src/SteamManager.Core/Services/IGameQueueService.cs
using SteamManager.Core.Models;

namespace SteamManager.Core.Services;

public record QueueEntry(
    int GameId,
    int AppId,
    string Name,
    string? NameI18n,
    int Position,
    GameStatus Status,
    string? HeaderUrl);

public interface IGameQueueService
{
    Task<List<QueueEntry>> GetQueueAsync();
    Task AddToQueueAsync(int gameId);
    Task RemoveFromQueueAsync(int gameId);
    Task StartQueueAsync(CancellationToken ct = default);
    Task AdvanceQueueAsync(int completedAppId, CancellationToken ct = default);
    Task ReorderAsync(int gameId, int newPosition, CancellationToken ct = default);
}
```

- [ ] **Step 5: Update AchievementUnlockNotifier.cs**

```csharp
// src/SteamManager.Core/Services/AchievementUnlockNotifier.cs
namespace SteamManager.Core.Services;

public record UnlockedAchievementInfo(string GameName, string AchievementName, string? IconUrl);
public record CompletedGameInfo(string GameName, int AchievementCount);

public class AchievementUnlockNotifier
{
    public event Action<UnlockedAchievementInfo>? AchievementUnlocked;
    public event Action<CompletedGameInfo>? GameCompleted;

    public void Notify(UnlockedAchievementInfo info) => AchievementUnlocked?.Invoke(info);
    public void NotifyCompleted(CompletedGameInfo info) => GameCompleted?.Invoke(info);
}
```

- [ ] **Step 6: Build Core**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Core/SteamManager.Core.csproj 2>&1 | tail -5
```

Expected: `Build succeeded`

- [ ] **Step 7: Commit**

```bash
git add src/SteamManager.Core/Services/
git commit -m "feat: v0.4.0 interfaces — IGameRefreshService, IGameQueueService, updated IGameIdleService, CompletedGameInfo"
```

---

## Task 4: AchievementIntervalCalculator New Methods + Tests

**Files:**
- Modify: `src/SteamManager.Core/Services/AchievementIntervalCalculator.cs`
- Modify: `tests/SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests.cs`

- [ ] **Step 1: Write failing tests**

Append after the last existing `[Fact]` in `AchievementIntervalCalculatorTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to confirm they FAIL**

```bash
/Users/hugomin/.dotnet/dotnet test tests/SteamManager.Core.Tests/ \
  --filter "CalculateScheduledTimes|ValidateBurstThreshold" 2>&1 | tail -10
```

Expected: build or test errors (methods don't exist yet).

- [ ] **Step 3: Add methods to AchievementIntervalCalculator.cs** (before the private `Median` method)

```csharp
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
```

- [ ] **Step 4: Run all tests — confirm they PASS**

```bash
/Users/hugomin/.dotnet/dotnet test tests/SteamManager.Core.Tests/ 2>&1 | tail -5
```

Expected: `7 passed`

- [ ] **Step 5: Commit**

```bash
git add src/SteamManager.Core/Services/AchievementIntervalCalculator.cs \
        tests/SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests.cs
git commit -m "feat: AchievementIntervalCalculator — CalculateScheduledTimes + ValidateBurstThreshold (TDD)"
```

---

## Task 5: Rewrite GameIdleService

**Files:**
- Modify: `src/SteamManager.Infrastructure/Services/GameIdleService.cs`

- [ ] **Step 1: Replace GameIdleService.cs**

```csharp
// src/SteamManager.Infrastructure/Services/GameIdleService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Infrastructure.Services;

public class GameIdleService : IGameIdleService
{
    private readonly SteamClientWrapper _steam;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GameIdleService> _logger;
    private readonly Dictionary<int, CancellationTokenSource> _running = [];

    public event Action<int>? MctReached;
    public event Action<int, int>? ProgressUpdated;

    public IReadOnlyList<int> PlayingAppIds => [.. _running.Keys];

    public GameIdleService(SteamClientWrapper steam, IServiceScopeFactory scopeFactory,
        ILogger<GameIdleService> logger)
    {
        _steam = steam;
        _scopeFactory = scopeFactory;
        _logger = logger;
        steam.OnLoggedOn += ResumeRunningGames;
    }

    private void ResumeRunningGames()
    {
        foreach (var appId in _running.Keys)
        {
            _logger.LogInformation("Resuming game {AppId} after reconnect", appId);
            SendGamesPlayed(appId);
        }
    }

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public async Task StartAsync(int appId, CancellationToken ct = default)
    {
        if (_running.ContainsKey(appId)) return;
        if (_running.Count >= 2)
            throw new InvalidOperationException(
                "Max 2 concurrent games. Stop a playing game before starting another.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId, ct)
            ?? throw new InvalidOperationException($"Game {appId} not found");

        // SessionStartedAt, SteamPlaytimeAtRefresh, and Status = Playing must be set by
        // caller (GameQueueService.StartGameFromQueueAsync or StartupRecoveryService) before calling here.
        var targetMinutes = game.TargetMinutes;
        var steamPlaytimeAtRefresh = game.SteamPlaytimeAtRefresh;

        SendGamesPlayed(appId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        _ = RunIdleLoopAsync(appId, targetMinutes, steamPlaytimeAtRefresh, cts.Token);
    }

    public async Task StopAsync(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return;
        await cts.CancelAsync();
        _running.Remove(appId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var game = await db.Games
            .Include(g => g.Achievements)
            .FirstOrDefaultAsync(g => g.AppId == appId);
        if (game != null)
        {
            game.Status = GameStatus.Idle;
            game.SessionStartedAt = null;
            // Clear pending schedules — must re-Refresh before queuing again
            foreach (var ach in game.Achievements.Where(a => !a.IsUnlocked))
                ach.ScheduledUnlockAt = null;
            await db.SaveChangesAsync();
        }
        SendGamesPlayed(0);
    }

    private async Task RunIdleLoopAsync(
        int appId, int? targetMinutes, int steamPlaytimeAtRefresh, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var game = await db.Games.FirstAsync(g => g.AppId == appId, ct);
                if (!game.SessionStartedAt.HasValue) continue;

                var elapsedMinutes = (int)(DateTime.UtcNow - game.SessionStartedAt.Value).TotalMinutes;
                ProgressUpdated?.Invoke(appId, elapsedMinutes);

                if (!targetMinutes.HasValue) continue;

                var requiredMinutes = targetMinutes.Value - steamPlaytimeAtRefresh;
                if (elapsedMinutes < requiredMinutes) continue;

                // MCT reached — stop sending games played, transition to Scheduled
                game.Status = GameStatus.Scheduled;
                await db.SaveChangesAsync(ct);

                SendGamesPlayed(0);
                _running.Remove(appId);
                _logger.LogInformation("Game {AppId}: MCT reached ({Elapsed}/{Required}min) → Scheduled",
                    appId, elapsedMinutes, requiredMinutes);
                MctReached?.Invoke(appId);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Idle loop error for {AppId}", appId); }
    }

    private void SendGamesPlayed(int appId)
    {
        var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        if (appId != 0)
            msg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = (ulong)appId });
        _steam.Client.Send(msg);
    }
}
```

- [ ] **Step 2: Build**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj 2>&1 | tail -10
```

Expected: `GameIdleService` clean; other service files have errors (fixed in later tasks).

- [ ] **Step 3: Commit**

```bash
git add src/SteamManager.Infrastructure/Services/GameIdleService.cs
git commit -m "feat: GameIdleService rewrite — max 2 concurrent, MCT-based Scheduled transition, MctReached event"
```

---

## Task 6: Rewrite UnlockSchedulerService (Global Scanner)

**Files:**
- Modify: `src/SteamManager.Infrastructure/Services/UnlockSchedulerService.cs`

- [ ] **Step 1: Replace UnlockSchedulerService.cs**

```csharp
// src/SteamManager.Infrastructure/Services/UnlockSchedulerService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Infrastructure.Services;

public class UnlockSchedulerService(
    AchievementHandler handler,
    AchievementUnlockNotifier notifier,
    IServiceScopeFactory scopeFactory,
    ILogger<UnlockSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct); // startup grace period

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await ScanAndUnlockAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "UnlockScheduler scan error"); }
        }
    }

    private async Task ScanAndUnlockAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var due = await db.Achievements
            .Include(a => a.Game)
            .Where(a => !a.IsUnlocked
                && a.ScheduledUnlockAt.HasValue
                && a.ScheduledUnlockAt.Value <= now)
            .OrderBy(a => a.ScheduledUnlockAt)
            .ToListAsync(ct);

        if (due.Count > 0)
            logger.LogInformation("UnlockScheduler: {Count} achievements due", due.Count);

        foreach (var ach in due)
        {
            if (ct.IsCancellationRequested) break;

            var ok = await handler.UnlockAchievementAsync(ach.AppId, ach.ApiName, ct);
            if (!ok)
            {
                logger.LogWarning("Failed to unlock {ApiName} for app {AppId}", ach.ApiName, ach.AppId);
                continue;
            }

            ach.IsUnlocked = true;
            ach.ScheduledUnlockAt = null;
            ach.UnlockedAt = await FetchSteamUnlockTimeAsync(scope, ach.AppId, ach.ApiName, ct) ?? now;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Unlocked {ApiName} for game {AppId}", ach.ApiName, ach.AppId);

            notifier.Notify(new UnlockedAchievementInfo(
                GameName: ach.Game.NameI18n ?? ach.Game.Name,
                AchievementName: ach.DisplayNameI18n ?? ach.DisplayName,
                IconUrl: ach.IconUrl));

            // Check if Scheduled game is now fully complete
            if (ach.Game.Status == GameStatus.Scheduled)
            {
                var anyPending = await db.Achievements
                    .AnyAsync(a => a.GameId == ach.GameId && !a.IsUnlocked, ct);
                if (!anyPending)
                {
                    ach.Game.Status = GameStatus.Completed;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Game {AppId} fully completed", ach.AppId);

                    var totalCount = await db.Achievements
                        .CountAsync(a => a.GameId == ach.GameId, ct);
                    notifier.NotifyCompleted(new CompletedGameInfo(
                        GameName: ach.Game.NameI18n ?? ach.Game.Name,
                        AchievementCount: totalCount));
                }
            }
        }
    }

    private async Task<DateTime?> FetchSteamUnlockTimeAsync(
        IServiceScope scope, int appId, string apiName, CancellationToken ct)
    {
        try
        {
            var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = scope.ServiceProvider.GetRequiredService<ISteamSessionService>();
            if (!session.SteamId64.HasValue) return null;

            var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
            if (cfg?.WebApiKey == null) return null;

            var steamApi   = scope.ServiceProvider.GetRequiredService<SteamWebApiClient>();
            var playerAchs = await steamApi.GetPlayerAchievementsAsync(
                (long)session.SteamId64.Value, appId, cfg.WebApiKey, ct);

            var found = playerAchs.FirstOrDefault(a =>
                string.Equals(a.ApiName, apiName, StringComparison.OrdinalIgnoreCase));

            return found?.UnlockTime.HasValue == true
                ? DateTimeOffset.FromUnixTimeSeconds(found.UnlockTime.Value).UtcDateTime
                : null;
        }
        catch { return null; }
    }
}
```

- [ ] **Step 2: Build**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj 2>&1 | tail -10
```

Expected: `UnlockSchedulerService` clean; `PlayQueueService`, `AchievementDataService`, `StartupRecoveryService` still have errors.

- [ ] **Step 3: Commit**

```bash
git add src/SteamManager.Infrastructure/Services/UnlockSchedulerService.cs
git commit -m "feat: UnlockSchedulerService — global 60s scanner, Completed detection, CompletedGameInfo notify"
```

---

## Task 7: New GameRefreshService

**Files:**
- Create: `src/SteamManager.Infrastructure/Services/GameRefreshService.cs`
- Delete: `src/SteamManager.Infrastructure/Services/AchievementDataService.cs`

- [ ] **Step 1: Delete AchievementDataService.cs**

```bash
rm src/SteamManager.Infrastructure/Services/AchievementDataService.cs
```

- [ ] **Step 2: Create GameRefreshService.cs**

```csharp
// src/SteamManager.Infrastructure/Services/GameRefreshService.cs
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public partial class GameRefreshService(
    AppDbContext db,
    SteamWebApiClient steamApi,
    SteamHuntersClient hunters,
    ISteamSessionService session,
    IGameQueueService queue,
    ILogger<GameRefreshService> logger) : IGameRefreshService
{
    public Task RefreshAsync(int gameId, int appId, CancellationToken ct = default)
        => RefreshCoreAsync(gameId, appId, forceReset: false, ct);

    public Task ForceRefreshAsync(int gameId, int appId, CancellationToken ct = default)
        => RefreshCoreAsync(gameId, appId, forceReset: true, ct);

    private async Task RefreshCoreAsync(int gameId, int appId, bool forceReset, CancellationToken ct)
    {
        // 1. Load config
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Steam config not found");
        var apiKey = cfg.WebApiKey
            ?? throw new InvalidOperationException("Steam Web API key not configured");
        var language = cfg.Language ?? "english";

        // 2. Load game + reference player
        var game = await db.Games.FindAsync([gameId], ct)
            ?? throw new InvalidOperationException($"Game {gameId} not found");
        var refPlayer = await db.GameReferencePlayers.FirstOrDefaultAsync(r => r.GameId == gameId, ct)
            ?? throw new InvalidOperationException(
                "No reference player configured. Add one in the game detail page first.");

        // 3. Force reset: clear all achievements before re-scheduling
        if (forceReset)
        {
            var achToReset = await db.Achievements.Where(a => a.GameId == gameId).ToListAsync(ct);
            foreach (var a in achToReset)
            {
                a.IsUnlocked = false;
                a.UnlockedAt = null;
                a.ScheduledUnlockAt = null;
            }
            await db.SaveChangesAsync(ct);
        }

        // 4. Fetch achievement schema
        var schema = await steamApi.GetSchemaAchievementsAsync(appId, apiKey, language, ct);

        // 5. Fetch MCT from SteamHunters
        var (medianMinutes, _) = await hunters.GetAppInfoAsync(appId, ct);
        if (medianMinutes > 0) game.TargetMinutes = medianMinutes;

        // 6. Resolve reference player Steam64 ID from URL
        long? steamId64 = TryExtractSteamId(refPlayer.PlayerUrl);
        if (steamId64 == null)
        {
            var vanity = TryExtractVanityName(refPlayer.PlayerUrl);
            if (vanity != null)
                steamId64 = await steamApi.ResolveVanityUrlAsync(vanity, apiKey, ct);
        }
        if (!steamId64.HasValue)
            throw new InvalidOperationException(
                $"Cannot resolve Steam ID from: {refPlayer.PlayerUrl}. Use /profiles/{{steamId64}} or /id/{{vanityname}} format.");

        // 7. Fetch reference player's achievements
        var playerAchs = await steamApi.GetPlayerAchievementsAsync(steamId64.Value, appId, apiKey, ct);
        var unlockedByPlayer = playerAchs.Where(a => a.UnlockTime.HasValue).ToList();

        if (unlockedByPlayer.Count == 0)
            throw new InvalidOperationException(
                "Reference player has no unlocked achievements for this game.");

        // 8. Fallback target if no MCT: use reference player's recorded total playtime
        if (game.TargetMinutes == null || game.TargetMinutes == 0)
        {
            var refPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(steamId64.Value, appId, apiKey, ct);
            if (refPlaytime > 0) game.TargetMinutes = refPlaytime;
        }

        // 9. Burst detection
        var burstTimestamps = AchievementIntervalCalculator.ValidateBurstThreshold(unlockedByPlayer);
        if (burstTimestamps.Count > 0 && !refPlayer.OverrideBurstCheck)
            throw new InvalidOperationException(
                $"Burst detected: {burstTimestamps.Count} timestamp(s) with ≥5 achievements share the same unlock time. " +
                "Enable 'Override burst detection' in the reference player panel to proceed.");

        // 10. Build already-unlocked set (these are skipped when scheduling)
        var alreadyUnlocked = await db.Achievements
            .Where(a => a.GameId == gameId && a.IsUnlocked)
            .Select(a => a.ApiName)
            .ToHashSetAsync(ct);

        // 11. Calculate scheduled UTC times
        var scheduleMap = AchievementIntervalCalculator.CalculateScheduledTimes(
            unlockedByPlayer, DateTime.UtcNow, alreadyUnlocked);

        // 12. Upsert achievements
        var existing = await db.Achievements
            .Where(a => a.GameId == gameId)
            .ToDictionaryAsync(a => a.ApiName, ct);

        foreach (var ach in schema)
        {
            scheduleMap.TryGetValue(ach.ApiName, out var scheduledAt);

            if (existing.TryGetValue(ach.ApiName, out var row))
            {
                if (language == "english")
                {
                    row.DisplayName = ach.DisplayName;
                    row.DisplayNameI18n = null;
                    row.Description = ach.Description;
                    row.DescriptionI18n = null;
                }
                else
                {
                    row.DisplayNameI18n = ach.DisplayName;
                    row.DescriptionI18n = ach.Description;
                }
                row.GlobalPercent = ach.GlobalPercent;
                row.IconUrl = ach.IconUrl;
                row.IconGrayUrl = ach.IconGrayUrl;
                if (!row.IsUnlocked)
                    row.ScheduledUnlockAt = scheduledAt == default ? null : scheduledAt;
            }
            else
            {
                db.Achievements.Add(new Achievement
                {
                    GameId = gameId, AppId = appId,
                    ApiName = ach.ApiName,
                    DisplayName = ach.DisplayName,
                    DisplayNameI18n = language != "english" ? ach.DisplayName : null,
                    Description = ach.Description,
                    DescriptionI18n = language != "english" ? ach.Description : null,
                    GlobalPercent = ach.GlobalPercent,
                    IconUrl = ach.IconUrl,
                    IconGrayUrl = ach.IconGrayUrl,
                    ScheduledUnlockAt = scheduledAt == default ? null : scheduledAt,
                });
            }
        }

        // 13. Sync own unlock status from Steam
        if (session.SteamId64.HasValue)
        {
            var myAchs = await steamApi.GetPlayerAchievementsAsync(
                (long)session.SteamId64.Value, appId, apiKey, ct);
            var myMap = myAchs.ToDictionary(a => a.ApiName);
            var rows = await db.Achievements.Where(a => a.GameId == gameId).ToListAsync(ct);
            foreach (var row in rows)
            {
                if (myMap.TryGetValue(row.ApiName, out var p) && p.UnlockTime.HasValue)
                {
                    row.IsUnlocked = true;
                    row.UnlockedAt = DateTimeOffset.FromUnixTimeSeconds(p.UnlockTime.Value).UtcDateTime;
                    row.ScheduledUnlockAt = null;
                }
            }
        }

        game.AchievementsCachedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // 14. Add to queue (idempotent)
        await queue.AddToQueueAsync(gameId);

        logger.LogInformation("Refresh complete: game {GameId} ({AppId}), {Count} achievements scheduled",
            gameId, appId, scheduleMap.Count);
    }

    // Extract numeric Steam64 ID from URLs like:
    //   https://steamhunters.com/profiles/76561198012345678
    //   https://steamcommunity.com/profiles/76561198012345678
    private static long? TryExtractSteamId(string url)
    {
        var m = SteamIdRegex().Match(url);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    // Extract vanity name from URLs like:
    //   https://steamhunters.com/id/someuser
    //   https://steamcommunity.com/id/someuser
    private static string? TryExtractVanityName(string url)
    {
        var m = VanityRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"/profiles/(\d{17})", RegexOptions.IgnoreCase)]
    private static partial Regex SteamIdRegex();

    [GeneratedRegex(@"/id/([^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VanityRegex();
}
```

- [ ] **Step 3: Build Infrastructure**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj 2>&1 | tail -10
```

Expected: `GameRefreshService` clean; `PlayQueueService` and `StartupRecoveryService` still have errors.

- [ ] **Step 4: Commit**

```bash
git add src/SteamManager.Infrastructure/Services/GameRefreshService.cs
git commit -m "feat: GameRefreshService — 14-step Refresh, burst detection, absolute schedule, queue enqueue"
```

---

## Task 8: New GameQueueService

**Files:**
- Create: `src/SteamManager.Infrastructure/Services/GameQueueService.cs`
- Delete: `src/SteamManager.Infrastructure/Services/PlayQueueService.cs`

- [ ] **Step 1: Delete PlayQueueService.cs**

```bash
rm src/SteamManager.Infrastructure/Services/PlayQueueService.cs
```

- [ ] **Step 2: Create GameQueueService.cs**

```csharp
// src/SteamManager.Infrastructure/Services/GameQueueService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class GameQueueService : IGameQueueService
{
    private readonly IGameIdleService _idleService;
    private readonly ISteamSessionService _session;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GameQueueService> _logger;

    public GameQueueService(
        IGameIdleService idleService,
        ISteamSessionService session,
        IServiceScopeFactory scopeFactory,
        ILogger<GameQueueService> logger)
    {
        _idleService = idleService;
        _session = session;
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Auto-advance queue when MCT is reached
        idleService.MctReached += appId =>
            _ = Task.Run(() => AdvanceQueueAsync(appId));
    }

    public async Task<List<QueueEntry>> GetQueueAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GameQueue
            .Include(q => q.Game)
            .OrderBy(q => q.Position)
            .Select(q => new QueueEntry(
                q.GameId, q.Game.AppId,
                q.Game.Name, q.Game.NameI18n,
                q.Position, q.Game.Status,
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{q.Game.AppId}/header.jpg"))
            .ToListAsync();
    }

    public async Task AddToQueueAsync(int gameId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.GameQueue.AnyAsync(q => q.GameId == gameId)) return;
        var maxPos = await db.GameQueue.AnyAsync()
            ? await db.GameQueue.MaxAsync(q => q.Position) : -1;
        db.GameQueue.Add(new GameQueueEntry { GameId = gameId, Position = maxPos + 1, AddedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        _logger.LogInformation("GameQueue: game {GameId} added at position {Pos}", gameId, maxPos + 1);
    }

    public async Task RemoveFromQueueAsync(int gameId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.GameQueue.Include(q => q.Game).FirstOrDefaultAsync(q => q.GameId == gameId);
        if (item == null) return;
        if (item.Game.Status == GameStatus.Playing) await _idleService.StopAsync(item.Game.AppId);
        db.GameQueue.Remove(item);
        await db.SaveChangesAsync();
        _logger.LogInformation("GameQueue: game {GameId} removed", gameId);
    }

    public async Task StartQueueAsync(CancellationToken ct = default)
    {
        var slotsAvailable = 2 - _idleService.PlayingAppIds.Count;
        if (slotsAvailable <= 0) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var idleItems = await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status == GameStatus.Idle)
            .OrderBy(q => q.Position)
            .Take(slotsAvailable)
            .ToListAsync(ct);

        foreach (var item in idleItems)
            await StartGameFromQueueAsync(item.Game, db, ct);
    }

    public async Task AdvanceQueueAsync(int completedAppId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Remove the MCT-completed game from queue
        var completed = await db.GameQueue
            .Include(q => q.Game)
            .FirstOrDefaultAsync(q => q.Game.AppId == completedAppId, ct);
        if (completed != null) { db.GameQueue.Remove(completed); await db.SaveChangesAsync(ct); }

        // Fill available slot
        if (2 - _idleService.PlayingAppIds.Count <= 0) return;
        var next = await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status == GameStatus.Idle)
            .OrderBy(q => q.Position)
            .FirstOrDefaultAsync(ct);
        if (next != null)
        {
            await StartGameFromQueueAsync(next.Game, db, ct);
            _logger.LogInformation("GameQueue: auto-advanced to game {AppId}", next.Game.AppId);
        }
    }

    public async Task ReorderAsync(int gameId, int newPosition, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Only Idle items can be reordered
        var items = await db.GameQueue
            .Include(q => q.Game)
            .Where(q => q.Game.Status == GameStatus.Idle)
            .OrderBy(q => q.Position)
            .ToListAsync(ct);
        var target = items.FirstOrDefault(q => q.GameId == gameId);
        if (target == null) return;
        items.Remove(target);
        items.Insert(Math.Clamp(newPosition, 0, items.Count), target);
        for (int i = 0; i < items.Count; i++) items[i].Position = i;
        await db.SaveChangesAsync(ct);
    }

    private async Task StartGameFromQueueAsync(Game game, AppDbContext db, CancellationToken ct)
    {
        // Re-anchor Steam playtime snapshot
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        if (cfg?.WebApiKey != null && _session.SteamId64.HasValue)
        {
            using var innerScope = _scopeFactory.CreateScope();
            var steamApi = innerScope.ServiceProvider.GetRequiredService<SteamWebApiClient>();
            var currentPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(
                (long)_session.SteamId64.Value, game.AppId, cfg.WebApiKey, ct);
            game.SteamPlaytimeAtRefresh = currentPlaytime;
        }

        var now = DateTime.UtcNow;
        game.SessionStartedAt = now;

        // Re-anchor scheduled times from now (preserve relative intervals)
        var pending = await db.Achievements
            .Where(a => a.GameId == game.Id && !a.IsUnlocked && a.ScheduledUnlockAt.HasValue)
            .ToListAsync(ct);
        if (pending.Count > 0)
        {
            var minScheduled = pending.Min(a => a.ScheduledUnlockAt!.Value);
            foreach (var ach in pending)
            {
                var relOffset = ach.ScheduledUnlockAt!.Value - minScheduled;
                ach.ScheduledUnlockAt = now.Add(relOffset);
            }
        }

        game.Status = GameStatus.Playing;
        await db.SaveChangesAsync(ct);
        await _idleService.StartAsync(game.AppId, ct);

        _logger.LogInformation("GameQueue: started game {AppId} ({Count} achievements re-anchored)",
            game.AppId, pending.Count);
    }
}
```

- [ ] **Step 3: Build Infrastructure**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj 2>&1 | tail -10
```

Expected: only `StartupRecoveryService` has errors.

- [ ] **Step 4: Commit**

```bash
git add src/SteamManager.Infrastructure/Services/GameQueueService.cs
git commit -m "feat: GameQueueService — StartQueueAsync, AdvanceQueueAsync on MctReached, ReorderAsync"
```

---

## Task 9: Update StartupRecoveryService

**Files:**
- Modify: `src/SteamManager.Infrastructure/Services/StartupRecoveryService.cs`

- [ ] **Step 1: Replace StartupRecoveryService.cs**

```csharp
// src/SteamManager.Infrastructure/Services/StartupRecoveryService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class StartupRecoveryService(
    ISteamSessionService session,
    IGameIdleService idle,
    SteamWebApiClient steamApi,
    AppDbContext db,
    ILogger<StartupRecoveryService> logger)
{
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Running startup recovery");

        var restored = await session.TryRestoreSessionAsync(ct);
        if (!restored)
        {
            logger.LogWarning("Could not restore Steam session — manual login required");
            return;
        }

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;

        // Recover Playing games: re-anchor schedule from server start
        var playingGames = await db.Games
            .Include(g => g.Achievements)
            .Where(g => g.Status == GameStatus.Playing)
            .ToListAsync(ct);

        foreach (var game in playingGames)
        {
            logger.LogInformation("Recovery: re-anchoring Playing game {AppId}", game.AppId);

            // Re-read Steam playtime in case it changed before the restart
            if (cfg?.WebApiKey != null && session.SteamId64.HasValue)
            {
                var currentPlaytime = await steamApi.GetPlayerGamePlaytimeAsync(
                    (long)session.SteamId64.Value, game.AppId, cfg.WebApiKey, ct);
                game.SteamPlaytimeAtRefresh = currentPlaytime;
            }

            game.SessionStartedAt = now;

            // Re-anchor scheduled_unlock_at from server start (preserve relative intervals)
            var pending = game.Achievements.Where(a => !a.IsUnlocked && a.ScheduledUnlockAt.HasValue).ToList();
            if (pending.Count > 0)
            {
                var minScheduled = pending.Min(a => a.ScheduledUnlockAt!.Value);
                foreach (var ach in pending)
                {
                    var relOffset = ach.ScheduledUnlockAt!.Value - minScheduled;
                    ach.ScheduledUnlockAt = now.Add(relOffset);
                }
            }

            await db.SaveChangesAsync(ct);
            await idle.StartAsync(game.AppId, ct);

            logger.LogInformation("Recovery: game {AppId} resumed, {Count} achievements re-anchored",
                game.AppId, pending.Count);
        }

        // Scheduled games: global scanner picks up past-due achievements automatically.
        // Detect any that are already complete (no pending achievements).
        var scheduledGames = await db.Games
            .Include(g => g.Achievements)
            .Where(g => g.Status == GameStatus.Scheduled)
            .ToListAsync(ct);

        foreach (var game in scheduledGames)
        {
            var anyPending = game.Achievements.Any(a => !a.IsUnlocked);
            if (!anyPending)
            {
                game.Status = GameStatus.Completed;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Recovery: game {AppId} promoted to Completed (no pending achievements)", game.AppId);
            }
        }
    }
}
```

- [ ] **Step 2: Build Infrastructure — expect clean**

```bash
/Users/hugomin/.dotnet/dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj 2>&1 | tail -5
```

Expected: `Build succeeded`

- [ ] **Step 3: Commit**

```bash
git add src/SteamManager.Infrastructure/Services/StartupRecoveryService.cs
git commit -m "feat: StartupRecoveryService — re-anchor ScheduledUnlockAt from server start, re-read Steam playtime"
```

---

## Task 10: Update Program.cs DI

**Files:**
- Modify: `src/SteamManager.Web/Program.cs`

- [ ] **Step 1: Replace the Steam + Core services block and PlayQueue block**

In Program.cs, find and replace the block from `// Steam + Core services` to the end of the PlayQueue registration. New content:

```csharp
// Steam + Core services
builder.Services.AddSingleton<SteamClientWrapper>();
builder.Services.AddSingleton<AchievementHandler>();
builder.Services.AddSingleton<AchievementUnlockNotifier>();
builder.Services.AddSingleton<ISteamSessionService, SteamSessionService>();
builder.Services.AddSingleton<IGameIdleService, GameIdleService>();
builder.Services.AddScoped<StartupRecoveryService>();
builder.Services.AddSingleton<SyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());
builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<SyncBackgroundService>());
builder.Services.AddHostedService<UnlockSchedulerService>();

// Queue and refresh services
// GameQueueService is singleton so it can hold MctReached subscription across scopes
builder.Services.AddSingleton<IGameQueueService, GameQueueService>();
builder.Services.AddScoped<IGameRefreshService, GameRefreshService>();
```

- [ ] **Step 2: Update inline SQL guards** — replace the `play_queue` guard block with the new tables:

```csharp
await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS steam_audit_log (
        id BIGINT NOT NULL AUTO_INCREMENT,
        source VARCHAR(50) NOT NULL,
        operation VARCHAR(100) NOT NULL,
        app_id INT NULL,
        request_summary VARCHAR(500) NULL,
        success TINYINT(1) NOT NULL,
        response_summary VARCHAR(1000) NULL,
        duration_ms INT NOT NULL,
        created_at DATETIME(6) NOT NULL,
        PRIMARY KEY (id),
        KEY IX_steam_audit_log_created_at (created_at),
        KEY IX_steam_audit_log_source_operation (source, operation)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS game_queue (
        id INT NOT NULL AUTO_INCREMENT,
        game_id INT NOT NULL,
        position INT NOT NULL DEFAULT 0,
        added_at DATETIME(6) NOT NULL,
        PRIMARY KEY (id),
        UNIQUE KEY IX_game_queue_game_id (game_id),
        KEY IX_game_queue_position (position),
        CONSTRAINT FK_game_queue_game_game_id
            FOREIGN KEY (game_id) REFERENCES game (id) ON DELETE CASCADE
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

await db.Database.ExecuteSqlRawAsync(@"
    CREATE TABLE IF NOT EXISTS game_reference_player (
        id INT NOT NULL AUTO_INCREMENT,
        game_id INT NOT NULL,
        player_url VARCHAR(512) NOT NULL,
        override_burst_check TINYINT(1) NOT NULL DEFAULT 0,
        created_at DATETIME(6) NOT NULL,
        updated_at DATETIME(6) NOT NULL,
        PRIMARY KEY (id),
        UNIQUE KEY IX_game_reference_player_game_id (game_id),
        CONSTRAINT FK_game_reference_player_game_game_id
            FOREIGN KEY (game_id) REFERENCES game (id) ON DELETE CASCADE
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
```

- [ ] **Step 3: Build the full solution**

```bash
/Users/hugomin/.dotnet/dotnet build /Users/hugomin/Developer/Repository/chaosmin/SteamManager/SteamManager.sln 2>&1 | tail -10
```

Expected: `Build succeeded` for all projects.

- [ ] **Step 4: Run all tests**

```bash
/Users/hugomin/.dotnet/dotnet test tests/SteamManager.Core.Tests/ 2>&1 | tail -5
```

Expected: `7 passed`

- [ ] **Step 5: Start server and confirm it starts**

```bash
pkill -f "SteamManager.Web" 2>/dev/null; sleep 1
export $(grep -v '^#' /Users/hugomin/Developer/Repository/chaosmin/SteamManager/.env | xargs)
/Users/hugomin/.dotnet/dotnet run --project /Users/hugomin/Developer/Repository/chaosmin/SteamManager/src/SteamManager.Web/SteamManager.Web.csproj &
sleep 10 && curl -s -o /dev/null -w "%{http_code}" http://localhost:5066/
pkill -f "SteamManager.Web" 2>/dev/null
```

Expected: `200` or `302`

- [ ] **Step 6: Commit**

```bash
git add src/SteamManager.Web/Program.cs
git commit -m "feat: Program.cs — DI wired for v0.4.0, updated inline SQL guards"
```

---

## Task 11: Dashboard.razor — Queue Panel + Dual Playing Cards

**Files:**
- Modify: `src/SteamManager.Web/Components/Pages/Dashboard.razor`

Read the current file before editing:

```bash
wc -l src/SteamManager.Web/Components/Pages/Dashboard.razor
```

- [ ] **Step 1: Add @inject and queue state to @code block**

Add at the top of the razor directives:

```razor
@inject IGameQueueService QueueService
@inject AchievementUnlockNotifier Notifier
```

Add to `@code` block:

```csharp
private List<QueueEntry> _queue = [];
private bool _queueLoading = true;
```

- [ ] **Step 2: Load queue and subscribe to events in OnInitializedAsync**

In `OnInitializedAsync` (or `OnAfterRenderAsync` for first render), add:

```csharp
_queue = await QueueService.GetQueueAsync();
_queueLoading = false;
Notifier.GameCompleted += OnGameCompleted;
```

Add disposal in `IDisposable.Dispose` or `DisposeAsync`:

```csharp
Notifier.GameCompleted -= OnGameCompleted;
```

- [ ] **Step 3: Add queue methods to @code**

```csharp
private async Task StartQueue()
{
    await QueueService.StartQueueAsync();
    _queue = await QueueService.GetQueueAsync();
    StateHasChanged();
}

private async Task RemoveFromQueue(int gameId)
{
    await QueueService.RemoveFromQueueAsync(gameId);
    _queue = await QueueService.GetQueueAsync();
    StateHasChanged();
}

private async Task OnQueueItemDropped(MudItemDropInfo<QueueEntry> drop)
{
    if (drop.Item == null) return;
    await QueueService.ReorderAsync(drop.Item.GameId, drop.IndexInZone);
    _queue = await QueueService.GetQueueAsync();
    StateHasChanged();
}

private static Color GetQueueStatusColor(GameStatus status) => status switch
{
    GameStatus.Playing   => Color.Info,
    GameStatus.Scheduled => Color.Primary,
    _                    => Color.Default,
};

private static string GetQueueRowBorderStyle(GameStatus status) => status switch
{
    GameStatus.Playing   => "border-left:3px solid #66C0F4;",
    GameStatus.Scheduled => "border-left:3px solid #3B82F6;",
    _                    => "",
};

private void OnGameCompleted(CompletedGameInfo info)
{
    InvokeAsync(() => {
        Snackbar.Add(
            $"Completed — {info.GameName}: all {info.AchievementCount} achievements unlocked",
            Severity.Success,
            cfg => { cfg.VisibleStateDuration = 8000; });
        StateHasChanged();
    });
}
```

- [ ] **Step 4: Insert Queue Control Panel markup** (between stat cards and existing Currently Playing section)

```razor
@* ── Queue Control Panel ── *@
<MudPaper Class="pa-4 mb-4" Style="background:rgba(255,255,255,0.03);" Elevation="0">
    <div class="d-flex align-center justify-space-between mb-3">
        <MudText Typo="Typo.h6">Queue</MudText>
        <MudButton Variant="Variant.Filled" Color="Color.Primary"
                   StartIcon="@Icons.Material.Filled.PlayArrow"
                   Disabled="@(!_queue.Any(q => q.Status == GameStatus.Idle))"
                   OnClick="StartQueue">
            Start Queue
        </MudButton>
    </div>

    @if (_queueLoading)
    {
        <MudSkeleton Height="48px" Class="mb-1" />
        <MudSkeleton Height="48px" />
    }
    else if (!_queue.Any())
    {
        <MudText Typo="Typo.body2" Style="color:rgba(255,255,255,0.45)">
            Queue is empty. Refresh a game with a reference player configured.
        </MudText>
    }
    else
    {
        <MudDropContainer T="QueueEntry" Items="_queue" ItemDropped="OnQueueItemDropped"
                          ItemsSelector="@((item, _) => true)">
            <ChildContent>
                <MudDropZone T="QueueEntry" Identifier="queue" Class="d-flex flex-column gap-1">
                    @foreach (var item in _queue)
                    {
                        <MudPaper Elevation="0" Class="d-flex align-center gap-3 pa-2"
                                  Style="@($"border-radius:6px;background:rgba(255,255,255,0.04);{GetQueueRowBorderStyle(item.Status)}")">
                            <MudIcon Icon="@Icons.Material.Filled.DragIndicator"
                                     Style="@(item.Status == GameStatus.Idle
                                         ? "color:rgba(255,255,255,0.3);cursor:grab"
                                         : "color:rgba(255,255,255,0.1);cursor:default")" />
                            <MudImage Src="@item.HeaderUrl" Width="48" Height="22"
                                      ObjectFit="ObjectFit.Cover" Style="border-radius:3px" />
                            <MudText Typo="Typo.body2" Class="flex-grow-1">
                                @(item.NameI18n ?? item.Name)
                            </MudText>
                            <MudChip T="string" Size="Size.Small"
                                     Color="@GetQueueStatusColor(item.Status)">
                                @item.Status.ToString()
                            </MudChip>
                            <MudIconButton Icon="@Icons.Material.Filled.Close" Size="Size.Small"
                                           Disabled="@(item.Status != GameStatus.Idle)"
                                           OnClick="@(() => RemoveFromQueue(item.GameId))" />
                        </MudPaper>
                    }
                </MudDropZone>
            </ChildContent>
        </MudDropContainer>
    }
</MudPaper>
```

- [ ] **Step 5: Replace single Currently Playing card with dual-card grid**

Replace the existing "Currently Playing" section with:

```razor
@* ── Playing + Scheduled Cards ── *@
@{
    var playingItems   = _queue.Where(q => q.Status == GameStatus.Playing).ToList();
    var scheduledItems = _queue.Where(q => q.Status == GameStatus.Scheduled).ToList();
}

@if (playingItems.Any())
{
    <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:16px;margin-bottom:16px">
        @foreach (var item in playingItems)
        {
            <MudCard Style="background:rgba(255,255,255,0.04)">
                <MudCardMedia Image="@item.HeaderUrl" Height="110" />
                <MudCardContent>
                    <div class="d-flex align-center justify-space-between mb-1">
                        <MudText Typo="Typo.h6">@(item.NameI18n ?? item.Name)</MudText>
                        <MudChip T="string" Size="Size.Small" Color="Color.Info">Playing</MudChip>
                    </div>
                    <MudText Typo="Typo.caption" Style="color:rgba(255,255,255,0.45)">
                        Idling…
                    </MudText>
                </MudCardContent>
                <MudCardActions>
                    <MudButton Variant="Variant.Outlined" Color="Color.Error" Size="Size.Small"
                               OnClick="@(() => RemoveFromQueue(item.GameId))">Stop</MudButton>
                </MudCardActions>
            </MudCard>
        }
    </div>
}

@if (scheduledItems.Any())
{
    <MudText Typo="Typo.caption" Style="color:rgba(255,255,255,0.45)" Class="mb-2">Scheduled</MudText>
    <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:16px;margin-bottom:16px">
        @foreach (var item in scheduledItems)
        {
            <MudCard Style="background:rgba(255,255,255,0.03);border-left:3px solid #3B82F6">
                <MudCardContent>
                    <div class="d-flex align-center justify-space-between">
                        <MudText Typo="Typo.body1">@(item.NameI18n ?? item.Name)</MudText>
                        <MudChip T="string" Size="Size.Small" Color="Color.Primary">Scheduled</MudChip>
                    </div>
                    <MudText Typo="Typo.caption" Style="color:rgba(255,255,255,0.45)" Class="mt-1">
                        Playtime goal reached · achievements still unlocking
                    </MudText>
                </MudCardContent>
            </MudCard>
        }
    </div>
}
```

- [ ] **Step 6: Build and start server**

```bash
pkill -f "SteamManager.Web" 2>/dev/null; sleep 1
export $(grep -v '^#' /Users/hugomin/Developer/Repository/chaosmin/SteamManager/.env | xargs)
/Users/hugomin/.dotnet/dotnet run --project /Users/hugomin/Developer/Repository/chaosmin/SteamManager/src/SteamManager.Web/SteamManager.Web.csproj &
sleep 10 && curl -s -o /dev/null -w "%{http_code}" http://localhost:5066/
pkill -f "SteamManager.Web" 2>/dev/null
```

Expected: `200` or `302`

- [ ] **Step 7: Commit**

```bash
git add src/SteamManager.Web/Components/Pages/Dashboard.razor
git commit -m "feat: Dashboard — queue panel with drag-and-drop, dual playing cards, Scheduled section"
```

---

## Task 12: GameDetail.razor — Reference Player Panel + Achievement Table

**Files:**
- Modify: `src/SteamManager.Web/Components/Pages/GameDetail.razor`

Read current file length first:

```bash
wc -l src/SteamManager.Web/Components/Pages/GameDetail.razor
```

- [ ] **Step 1: Add @inject for new service**

```razor
@inject IGameRefreshService RefreshService
```

- [ ] **Step 2: Add reference player state and methods to @code**

```csharp
// Reference player panel
private bool _hasPlayer;
private string _playerUrl = "";
private string _playerUrlShort = "";
private bool _overrideBurst;
private bool _playerPanelExpanded;
private string? _playerError;

private async Task LoadReferencePlayerAsync()
{
    var refPlayer = await Db.GameReferencePlayers
        .FirstOrDefaultAsync(r => r.GameId == _game!.Id);
    _hasPlayer = refPlayer != null;
    if (refPlayer != null)
    {
        _playerUrl = refPlayer.PlayerUrl;
        _playerUrlShort = _playerUrl.Length > 50 ? _playerUrl[..50] + "…" : _playerUrl;
        _overrideBurst = refPlayer.OverrideBurstCheck;
    }
}

private async Task SavePlayer()
{
    if (string.IsNullOrWhiteSpace(_playerUrl)) return;
    var refPlayer = await Db.GameReferencePlayers.FirstOrDefaultAsync(r => r.GameId == _game!.Id);
    if (refPlayer == null)
        Db.GameReferencePlayers.Add(new GameReferencePlayer
            { GameId = _game!.Id, PlayerUrl = _playerUrl.Trim(), OverrideBurstCheck = _overrideBurst });
    else
        { refPlayer.PlayerUrl = _playerUrl.Trim(); refPlayer.OverrideBurstCheck = _overrideBurst; }
    await Db.SaveChangesAsync();
    await LoadReferencePlayerAsync();
    Snackbar.Add("Reference player saved.", Severity.Success);
}

private async Task RemovePlayer()
{
    var refPlayer = await Db.GameReferencePlayers.FirstOrDefaultAsync(r => r.GameId == _game!.Id);
    if (refPlayer != null) { Db.GameReferencePlayers.Remove(refPlayer); await Db.SaveChangesAsync(); }
    _hasPlayer = false; _playerUrl = ""; _playerUrlShort = "";
}

private async Task Refresh()
{
    _playerError = null;
    try
    {
        await RefreshService.RefreshAsync(_game!.Id, _game.AppId);
        await ReloadAchievementsAsync();   // existing method that reloads achievements from DB
        Snackbar.Add("Refresh complete.", Severity.Success);
    }
    catch (Exception ex) { _playerError = ex.Message; }
}

private static string GetAchRowClass(Achievement ach)
{
    if (ach.IsUnlocked) return "row-unlocked";
    if (ach.ScheduledUnlockAt.HasValue && ach.ScheduledUnlockAt.Value > DateTime.UtcNow) return "row-next";
    if (ach.ScheduledUnlockAt.HasValue) return "row-pending";
    return "";
}
```

- [ ] **Step 3: Call LoadReferencePlayerAsync from OnInitializedAsync (or OnParametersSetAsync)**

```csharp
await LoadReferencePlayerAsync();
```

- [ ] **Step 4: Insert Reference Player panel markup** (between game header row and achievement table)

```razor
<MudExpansionPanel @bind-IsExpanded="_playerPanelExpanded" Dense="true" Class="mb-4">
    <TitleContent>
        <div class="d-flex align-center gap-2">
            <MudIcon Icon="@Icons.Material.Filled.Person" Size="Size.Small" />
            <MudText Typo="Typo.body2">Reference Player</MudText>
            @if (_hasPlayer)
            {
                <MudIcon Icon="@Icons.Material.Filled.Circle" Size="Size.Small"
                         Style="color:#22C55E;font-size:8px" />
                <MudText Typo="Typo.caption" Style="color:rgba(255,255,255,0.5)">@_playerUrlShort</MudText>
            }
        </div>
    </TitleContent>
    <ChildContent>
        @if (!_hasPlayer)
        {
            <MudAlert Severity="Severity.Info" Dense="true" Class="mb-3">
                No reference player configured. Scheduled unlocking will use estimated intervals.
            </MudAlert>
        }
        @if (_playerError != null)
        {
            <MudAlert Severity="@(_playerError.Contains("Burst") ? Severity.Warning : Severity.Error)"
                      Dense="true" Class="mb-3">@_playerError</MudAlert>
        }
        <MudTextField @bind-Value="_playerUrl" Label="SteamHunters Player URL"
                      Variant="Variant.Outlined" FullWidth="true" Class="mb-3"
                      Placeholder="https://steamhunters.com/profiles/76561198012345678"
                      Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Link" />
        <MudCheckBox @bind-Value="_overrideBurst" Dense="true" Class="mb-3"
                     Label="Override burst detection" />
        <div class="d-flex gap-2">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="SavePlayer">Save</MudButton>
            <MudButton Variant="Variant.Filled" Color="Color.Secondary"
                       Disabled="@(!_hasPlayer)" OnClick="Refresh">Refresh</MudButton>
            @if (_hasPlayer)
            {
                <MudButton Variant="Variant.Outlined" Color="Color.Error" OnClick="RemovePlayer">
                    Remove Player
                </MudButton>
            }
        </div>
    </ChildContent>
</MudExpansionPanel>
```

- [ ] **Step 5: Update achievement table** — add Scheduled At, Status chip, and Time columns

Replace/update the `<MudTableHead>`:

```razor
<MudTableHead>
    <MudTr>
        <MudTh Style="width:48px"></MudTh>
        <MudTh>Achievement</MudTh>
        <MudTh Style="width:150px">Scheduled At</MudTh>
        <MudTh Style="width:90px">Global %</MudTh>
        <MudTh Style="width:110px">Status</MudTh>
        <MudTh Style="width:120px">Time</MudTh>
    </MudTr>
</MudTableHead>
```

In each `<MudTr>` (table row for an achievement), add the new cells and apply row class:

```razor
<MudTr Class="@GetAchRowClass(ach)">
    @* ... existing icon and name cells ... *@

    @* Scheduled At *@
    <MudTd Style="color:rgba(255,255,255,0.55);font-size:0.8rem">
        @(ach.ScheduledUnlockAt.HasValue
            ? ach.ScheduledUnlockAt.Value.ToLocalTime().ToString("MM-dd HH:mm")
            : "—")
    </MudTd>

    @* Global % — existing cell *@

    @* Status chip *@
    <MudTd>
        @{
            var (label, hex) = ach.IsUnlocked ? ("Unlocked", "#22C55E")
                : ach.ScheduledUnlockAt.HasValue ? ("Scheduled", "#3B82F6")
                : ("Locked", "#6B7280");
        }
        <MudChip T="string" Size="Size.Small" Style="@($"background:{hex};color:#fff")">@label</MudChip>
    </MudTd>

    @* Time *@
    <MudTd>
        @if (ach.IsUnlocked && ach.UnlockedAt.HasValue)
        {
            <span style="color:#22C55E;font-size:0.8rem">
                @ach.UnlockedAt.Value.ToLocalTime().ToString("MM-dd HH:mm")
            </span>
        }
        else if (ach.ScheduledUnlockAt.HasValue)
        {
            var rem = ach.ScheduledUnlockAt.Value - DateTime.UtcNow;
            @if (rem > TimeSpan.Zero)
            {
                <span style="color:#3B82F6;font-size:0.8rem">
                    in @((int)rem.TotalHours)h @rem.Minutes%m
                </span>
            }
            else
            {
                <span style="color:#F59E0B;font-size:0.8rem">
                    Pending <span class="pending-dot"
                        style="display:inline-block;width:6px;height:6px;border-radius:50%;background:#F59E0B"></span>
                </span>
            }
        }
        else
        {
            <span style="color:rgba(255,255,255,0.3)">—</span>
        }
    </MudTd>
</MudTr>
```

- [ ] **Step 6: Add CSS** (in a `<style>` block at the bottom of the razor file)

```css
<style>
    .row-unlocked { opacity: 0.55; }
    .row-next { border-left: 3px solid #3B82F6; background: rgba(59,130,246,0.05); }
    .row-pending { border-left: 3px solid #F59E0B; background: rgba(245,158,11,0.04); }
    @@keyframes pulse-dot { 0%, 100% { opacity: 1; } 50% { opacity: 0.3; } }
    .pending-dot { animation: pulse-dot 1.5s ease-in-out infinite; }
</style>
```

- [ ] **Step 7: Build and test**

```bash
pkill -f "SteamManager.Web" 2>/dev/null; sleep 1
export $(grep -v '^#' /Users/hugomin/Developer/Repository/chaosmin/SteamManager/.env | xargs)
/Users/hugomin/.dotnet/dotnet run --project /Users/hugomin/Developer/Repository/chaosmin/SteamManager/src/SteamManager.Web/SteamManager.Web.csproj &
sleep 10 && curl -s -o /dev/null -w "%{http_code}" http://localhost:5066/
pkill -f "SteamManager.Web" 2>/dev/null
```

Expected: `200` or `302`

- [ ] **Step 8: Commit**

```bash
git add src/SteamManager.Web/Components/Pages/GameDetail.razor
git commit -m "feat: GameDetail — reference player panel, achievement table Scheduled At + status chips + Time"
```

---

## Task 13: Games.razor — Button State Machine + Queue Badge

**Files:**
- Modify: `src/SteamManager.Web/Components/Pages/Games.razor`

Read current file first:

```bash
wc -l src/SteamManager.Web/Components/Pages/Games.razor
```

- [ ] **Step 1: Add @inject for new services**

```razor
@inject IGameQueueService QueueService
@inject IGameRefreshService RefreshService
```

- [ ] **Step 2: Add queue state and methods to @code**

```csharp
private List<QueueEntry> _queue = [];

// Call this in OnInitializedAsync after loading games:
//   _queue = await QueueService.GetQueueAsync();

private int? GetQueuePosition(int gameId)
    => _queue.FirstOrDefault(q => q.GameId == gameId)?.Position;

private async Task RefreshGame(int gameId, int appId)
{
    try
    {
        await RefreshService.RefreshAsync(gameId, appId);
        _queue = await QueueService.GetQueueAsync();
        await ReloadGamesAsync();
        Snackbar.Add("Refresh complete — game added to queue.", Severity.Success);
    }
    catch (Exception ex) { Snackbar.Add(ex.Message, Severity.Error); }
}

private async Task ForceRefreshGame(int gameId, int appId)
{
    try
    {
        await RefreshService.ForceRefreshAsync(gameId, appId);
        _queue = await QueueService.GetQueueAsync();
        await ReloadGamesAsync();
        Snackbar.Add("Force refresh complete.", Severity.Success);
    }
    catch (Exception ex) { Snackbar.Add(ex.Message, Severity.Error); }
}

private async Task StopGame(int appId)
{
    await IdleService.StopAsync(appId);
    _queue = await QueueService.GetQueueAsync();
    await ReloadGamesAsync();
}
```

- [ ] **Step 3: Update Games query to include ReferencePlayer**

In the method that loads games, add `.Include(g => g.ReferencePlayer)`:

```csharp
var games = await Db.Games
    .Include(g => g.ReferencePlayer)   // add this
    .OrderBy(g => g.Name)
    .ToListAsync();
```

- [ ] **Step 4: Replace action button with state machine**

For each game card, replace the existing Refresh/Stop button with:

```razor
@{
    var queuePos = GetQueuePosition(game.Id);
    var hasRefPlayer = game.ReferencePlayer != null;
}

@if (game.Status == GameStatus.Playing)
{
    <MudButton Variant="Variant.Outlined" Color="Color.Error" Size="Size.Small"
               OnClick="@(() => StopGame(game.AppId))">Stop</MudButton>
}
else if (game.Status == GameStatus.Scheduled)
{
    <MudChip T="string" Size="Size.Small" Color="Color.Primary">Scheduled</MudChip>
}
else if (game.Status == GameStatus.Completed)
{
    <MudButton Variant="Variant.Outlined" Color="Color.Warning" Size="Size.Small"
               OnClick="@(() => ForceRefreshGame(game.Id, game.AppId))">Force Refresh</MudButton>
}
else if (queuePos.HasValue)
{
    <MudButton Variant="Variant.Outlined" Color="Color.Secondary" Size="Size.Small"
               OnClick="@(() => RefreshGame(game.Id, game.AppId))">Re-Refresh</MudButton>
}
else if (!hasRefPlayer)
{
    <MudTooltip Text="Configure a reference player in game detail first">
        <MudButton Variant="Variant.Outlined" Size="Size.Small" Disabled="true">Refresh</MudButton>
    </MudTooltip>
}
else
{
    <MudButton Variant="Variant.Outlined" Color="Color.Primary" Size="Size.Small"
               OnClick="@(() => RefreshGame(game.Id, game.AppId))">Refresh</MudButton>
}
```

- [ ] **Step 5: Add queue position badge on game thumbnail**

Wrap the existing game thumbnail `<MudImage>` with a conditional badge:

```razor
@if (queuePos.HasValue && game.Status == GameStatus.Idle)
{
    <MudBadge Content="@((queuePos.Value + 1).ToString())"
              Overlap="true" Bordered="true"
              BadgeColor="Color.Info">
        <MudImage Src="@($"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.AppId}/header.jpg")"
                  Width="184" Height="69" ObjectFit="ObjectFit.Cover" />
    </MudBadge>
}
else
{
    <MudImage Src="@($"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.AppId}/header.jpg")"
              Width="184" Height="69" ObjectFit="ObjectFit.Cover" />
}
```

- [ ] **Step 6: Build and final integration test**

```bash
pkill -f "SteamManager.Web" 2>/dev/null; sleep 1
export $(grep -v '^#' /Users/hugomin/Developer/Repository/chaosmin/SteamManager/.env | xargs)
/Users/hugomin/.dotnet/dotnet run --project /Users/hugomin/Developer/Repository/chaosmin/SteamManager/src/SteamManager.Web/SteamManager.Web.csproj &
sleep 10 && curl -s -o /dev/null -w "%{http_code}" http://localhost:5066/games
pkill -f "SteamManager.Web" 2>/dev/null
```

Expected: `200` or `302`

- [ ] **Step 7: Run all tests**

```bash
/Users/hugomin/.dotnet/dotnet test /Users/hugomin/Developer/Repository/chaosmin/SteamManager/tests/SteamManager.Core.Tests/ 2>&1 | tail -5
```

Expected: `7 passed`

- [ ] **Step 8: Commit**

```bash
git add src/SteamManager.Web/Components/Pages/Games.razor
git commit -m "feat: Games page — button state machine, queue position badge, Force Refresh for Completed"
```

---

## Self-Review Checklist

After all tasks complete, verify against the spec and changelog:

- [ ] `GameStatus` enum: `Idle / Playing / Scheduled / Completed` — `Running` removed
- [ ] `Achievement.UnlockOffsetMinutes` fully removed — no references remain anywhere
- [ ] `game_queue` and `game_reference_player` tables exist (migration + inline guards)
- [ ] Max 2 concurrent: `GameIdleService.StartAsync` throws when `_running.Count >= 2`
- [ ] MCT reached → `GameStatus.Scheduled`: idle loop transitions and fires `MctReached`
- [ ] Auto-advance: `GameQueueService` subscribes to `MctReached` in constructor; `AdvanceQueueAsync` called
- [ ] `ScheduledUnlockAt` cleared on Stop: `GameIdleService.StopAsync` clears non-unlocked achievements
- [ ] Queue start re-anchors: `StartGameFromQueueAsync` recalculates schedules from actual start time
- [ ] Restart recovery: `StartupRecoveryService` re-anchors from server start, re-reads Steam playtime
- [ ] Burst detection: ≥5 same-timestamp throws unless `OverrideBurstCheck`
- [ ] Force Refresh: clears all achievements then runs full flow
- [ ] Global scanner: `UnlockSchedulerService` uses `PeriodicTimer(60s)` — no per-game timers
- [ ] Completed toast: `AchievementUnlockNotifier.GameCompleted` fires; Dashboard subscribes
- [ ] Deleted: `PlayQueueItem`, `AchievementDataService`, `PlayQueueService`, `IUnlockSchedulerService`, `IAchievementDataService`, `IPlayQueueService`
