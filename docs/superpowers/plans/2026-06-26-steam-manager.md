# SteamManager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Docker-deployable ASP.NET Core + Blazor Server service that auto-idles Steam games and unlocks achievements based on real 100%-completion player timing data.

**Architecture:** Three-project .NET 8 solution (Web / Core / Infrastructure). SteamKit2 communicates directly with Steam's CM servers — no Steam client required. All persistent state lives in an external MySQL 8 instance. Blazor Server provides real-time UI via SignalR.

**Tech Stack:** .NET 8, ASP.NET Core, Blazor Server, SteamKit2, EF Core 8 + Pomelo MySQL driver, Serilog, xUnit, Docker.

---

## File Map

```
SteamManager/
├── src/
│   ├── SteamManager.Web/
│   │   ├── SteamManager.Web.csproj
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── Middleware/UiAuthMiddleware.cs
│   │   ├── Pages/Login.razor, Dashboard.razor, Games.razor, Settings.razor
│   │   └── Components/GameCard.razor, AchievementList.razor
│   ├── SteamManager.Core/
│   │   ├── Models/SteamConfig, GameConfig, GameProgress, AchievementScheduleItem, AchievementCache
│   │   ├── Services/ISteamSessionService+Impl, IGameIdleService+Impl,
│   │   │          IAchievementDataService+Impl, IUnlockSchedulerService+Impl,
│   │   │          AchievementIntervalCalculator, StartupRecoveryService
│   │   └── Dto/AchievementIntervalDto, SteamAchievementDto
│   └── SteamManager.Infrastructure/
│       ├── Persistence/AppDbContext, Migrations/
│       ├── Crypto/AesEncryption
│       ├── Steam/SteamClientWrapper, AchievementHandler
│       └── Http/SteamWebApiClient, SteamHuntersClient
├── tests/
│   ├── SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests, UnlockSchedulerResumeTests
│   └── SteamManager.Infrastructure.Tests/Crypto/AesEncryptionTests
├── Dockerfile
└── docker-compose.yml
```

---

### Task 1: Solution & Project Scaffold

**Files:**
- Create: `SteamManager.sln`
- Create: `src/SteamManager.Web/SteamManager.Web.csproj`
- Create: `src/SteamManager.Core/SteamManager.Core.csproj`
- Create: `src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj`
- Create: `tests/SteamManager.Core.Tests/SteamManager.Core.Tests.csproj`
- Create: `tests/SteamManager.Infrastructure.Tests/SteamManager.Infrastructure.Tests.csproj`

- [ ] **Step 1: Create solution and projects**

```bash
cd /Users/hugomin/Developer/Repository/chaosmin/SteamManager
dotnet new sln -n SteamManager
dotnet new blazorserver -n SteamManager.Web -o src/SteamManager.Web --no-restore
dotnet new classlib -n SteamManager.Core -o src/SteamManager.Core --no-restore
dotnet new classlib -n SteamManager.Infrastructure -o src/SteamManager.Infrastructure --no-restore
dotnet new xunit -n SteamManager.Core.Tests -o tests/SteamManager.Core.Tests --no-restore
dotnet new xunit -n SteamManager.Infrastructure.Tests -o tests/SteamManager.Infrastructure.Tests --no-restore

dotnet sln add src/SteamManager.Web/SteamManager.Web.csproj
dotnet sln add src/SteamManager.Core/SteamManager.Core.csproj
dotnet sln add src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj
dotnet sln add tests/SteamManager.Core.Tests/SteamManager.Core.Tests.csproj
dotnet sln add tests/SteamManager.Infrastructure.Tests/SteamManager.Infrastructure.Tests.csproj
```

- [ ] **Step 2: Add project references**

```bash
dotnet add src/SteamManager.Web reference src/SteamManager.Core
dotnet add src/SteamManager.Web reference src/SteamManager.Infrastructure
dotnet add src/SteamManager.Infrastructure reference src/SteamManager.Core
dotnet add tests/SteamManager.Core.Tests reference src/SteamManager.Core
dotnet add tests/SteamManager.Infrastructure.Tests reference src/SteamManager.Infrastructure
```

- [ ] **Step 3: Add NuGet packages**

```bash
# Infrastructure
dotnet add src/SteamManager.Infrastructure package SteamKit2
dotnet add src/SteamManager.Infrastructure package Pomelo.EntityFrameworkCore.MySql --version 8.0.2
dotnet add src/SteamManager.Infrastructure package Microsoft.EntityFrameworkCore.Design --version 8.0.6
dotnet add src/SteamManager.Infrastructure package Microsoft.Extensions.Http

# Web
dotnet add src/SteamManager.Web package Serilog.AspNetCore
dotnet add src/SteamManager.Web package Serilog.Sinks.File

# Tests
dotnet add tests/SteamManager.Core.Tests package Moq
dotnet add tests/SteamManager.Infrastructure.Tests package Moq
```

- [ ] **Step 4: Verify build**

```bash
dotnet build SteamManager.sln
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git init
git add SteamManager.sln src/ tests/ .gitignore
git commit -m "feat: scaffold solution with Web/Core/Infrastructure projects"
```

---

### Task 2: EF Core Entities + DbContext

**Files:**
- Create: `src/SteamManager.Core/Models/SteamConfig.cs`
- Create: `src/SteamManager.Core/Models/GameConfig.cs`
- Create: `src/SteamManager.Core/Models/GameProgress.cs`
- Create: `src/SteamManager.Core/Models/AchievementScheduleItem.cs`
- Create: `src/SteamManager.Core/Models/AchievementCache.cs`
- Create: `src/SteamManager.Infrastructure/Persistence/AppDbContext.cs`

- [ ] **Step 1: Create SteamConfig entity**

`src/SteamManager.Core/Models/SteamConfig.cs`:
```csharp
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Create GameConfig entity**

`src/SteamManager.Core/Models/GameConfig.cs`:
```csharp
namespace SteamManager.Core.Models;

public enum GameStatus { Idle, Running, Completed }

public class GameConfig
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal TargetHours { get; set; }
    public bool EnableAchievements { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Idle;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public GameProgress? Progress { get; set; }
    public ICollection<AchievementScheduleItem> AchievementSchedule { get; set; } = [];
}
```

- [ ] **Step 3: Create remaining entities**

`src/SteamManager.Core/Models/GameProgress.cs`:
```csharp
namespace SteamManager.Core.Models;

public class GameProgress
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public int AccumulatedMinutes { get; set; }
    public DateTime? LastSessionStart { get; set; } // UTC
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public GameConfig Game { get; set; } = null!;
}
```

`src/SteamManager.Core/Models/AchievementScheduleItem.cs`:
```csharp
namespace SteamManager.Core.Models;

public class AchievementScheduleItem
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string AchievementId { get; set; } = string.Empty;
    public int OffsetMinutes { get; set; }   // relative to AccumulatedMinutes
    public bool Done { get; set; }
    public DateTime? UnlockedAt { get; set; } // UTC
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public GameConfig Game { get; set; } = null!;
}
```

`src/SteamManager.Core/Models/AchievementCache.cs`:
```csharp
namespace SteamManager.Core.Models;

public class AchievementCache
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public string Data { get; set; } = "[]";  // JSON: AchievementIntervalDto[]
    public DateTime FetchedAt { get; set; }   // UTC
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Create AppDbContext**

`src/SteamManager.Infrastructure/Persistence/AppDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using SteamManager.Core.Models;

namespace SteamManager.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SteamConfig> SteamConfigs => Set<SteamConfig>();
    public DbSet<GameConfig> GameConfigs => Set<GameConfig>();
    public DbSet<GameProgress> GameProgresses => Set<GameProgress>();
    public DbSet<AchievementScheduleItem> AchievementSchedules => Set<AchievementScheduleItem>();
    public DbSet<AchievementCache> AchievementCaches => Set<AchievementCache>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SteamConfig>(e =>
        {
            e.ToTable("steam_config");
            e.Property(x => x.DisplayTimezone).HasDefaultValue("UTC");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<GameConfig>(e =>
        {
            e.ToTable("game_config");
            e.HasIndex(x => x.AppId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<GameProgress>(e =>
        {
            e.ToTable("game_progress");
            e.HasIndex(x => x.AppId).IsUnique();
            e.HasOne(x => x.Game).WithOne(g => g.Progress)
             .HasForeignKey<GameProgress>(x => x.AppId)
             .HasPrincipalKey<GameConfig>(g => g.AppId);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<AchievementScheduleItem>(e =>
        {
            e.ToTable("achievement_schedule");
            e.HasIndex(x => new { x.AppId, x.AchievementId }).IsUnique();
            e.HasIndex(x => new { x.AppId, x.Done, x.OffsetMinutes });
            e.HasOne(x => x.Game).WithMany(g => g.AchievementSchedule)
             .HasForeignKey(x => x.AppId)
             .HasPrincipalKey<GameConfig>(g => g.AppId);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<AchievementCache>(e =>
        {
            e.ToTable("achievement_cache");
            e.HasIndex(x => x.AppId).IsUnique();
            e.Property(x => x.Data).HasColumnType("json");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });
    }
}
```

- [ ] **Step 5: Build**

```bash
dotnet build SteamManager.sln
```
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
git add src/SteamManager.Core/Models/ src/SteamManager.Infrastructure/Persistence/AppDbContext.cs
git commit -m "feat: EF Core entities and AppDbContext"
```

---

### Task 3: AES Encryption Utility (TDD)

**Files:**
- Create: `src/SteamManager.Infrastructure/Crypto/AesEncryption.cs`
- Create: `tests/SteamManager.Infrastructure.Tests/Crypto/AesEncryptionTests.cs`

- [ ] **Step 1: Write failing tests**

`tests/SteamManager.Infrastructure.Tests/Crypto/AesEncryptionTests.cs`:
```csharp
using SteamManager.Infrastructure.Crypto;
using Xunit;

namespace SteamManager.Infrastructure.Tests.Crypto;

public class AesEncryptionTests
{
    private const string Key = "this-is-a-32-char-test-key-12345";

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginal()
    {
        var plaintext = "super-secret-token-value";
        var encrypted = AesEncryption.Encrypt(plaintext, Key);
        var decrypted = AesEncryption.Decrypt(encrypted, Key);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var a = AesEncryption.Encrypt("same-input", Key);
        var b = AesEncryption.Encrypt("same-input", Key);
        Assert.NotEqual(a, b); // random IV
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsException()
    {
        var encrypted = AesEncryption.Encrypt("data", Key);
        Assert.ThrowsAny<Exception>(() =>
            AesEncryption.Decrypt(encrypted, "wrong-key-00000000000000000000000"));
    }

    [Fact]
    public void Encrypt_EmptyInputs_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => AesEncryption.Encrypt("", Key));
        Assert.Throws<ArgumentException>(() => AesEncryption.Encrypt("data", ""));
    }
}
```

- [ ] **Step 2: Run — verify FAIL**

```bash
dotnet test tests/SteamManager.Infrastructure.Tests/ --filter "AesEncryptionTests"
```
Expected: FAIL — type not found.

- [ ] **Step 3: Implement**

`src/SteamManager.Infrastructure/Crypto/AesEncryption.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;

namespace SteamManager.Infrastructure.Crypto;

public static class AesEncryption
{
    public static string Encrypt(string plaintext, string key)
    {
        if (string.IsNullOrEmpty(plaintext)) throw new ArgumentException("plaintext");
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");

        using var aes = Aes.Create();
        aes.Key = DeriveKey(key);
        aes.GenerateIV();

        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);
        using var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        cs.Write(bytes, 0, bytes.Length);
        cs.FlushFinalBlock();
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string ciphertext, string key)
    {
        if (string.IsNullOrEmpty(ciphertext)) throw new ArgumentException("ciphertext");
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");

        var data = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = DeriveKey(key);
        aes.IV = data[..16];

        using var ms = new MemoryStream(data, 16, data.Length - 16);
        using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var reader = new StreamReader(cs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] DeriveKey(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
```

- [ ] **Step 4: Run — verify PASS**

```bash
dotnet test tests/SteamManager.Infrastructure.Tests/ --filter "AesEncryptionTests" -v
```
Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SteamManager.Infrastructure/Crypto/ tests/SteamManager.Infrastructure.Tests/Crypto/
git commit -m "feat: AES-256 encryption utility with TDD"
```

---

### Task 4: DB Migration + Startup Validation

**Files:**
- Create: `src/SteamManager.Web/appsettings.json`
- Modify: `src/SteamManager.Web/Program.cs`

- [ ] **Step 1: Create appsettings.json**

`src/SteamManager.Web/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Port=3306;Database=steam_manager;User=steam_mgr;Password=REPLACE_VIA_ENV;Convert Zero Datetime=True;AllowPublicKeyRetrieval=True;ConnectionTimeout=30;"
  },
  "AchievementData": {
    "CacheExpiryDays": 7,
    "MaxReferencePlayers": 20,
    "IntervalJitterPercent": 10,
    "FallbackIntervalPerPercentDiff": 2
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/steam-manager-.log", "rollingInterval": "Day" } }
    ]
  }
}
```

- [ ] **Step 2: Write Program.cs**

`src/SteamManager.Web/Program.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Serilog;
using SteamManager.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Startup guard
var encKey = builder.Configuration["SESSION_ENCRYPTION_KEY"]
    ?? Environment.GetEnvironmentVariable("SESSION_ENCRYPTION_KEY");
if (string.IsNullOrWhiteSpace(encKey))
    throw new InvalidOperationException(
        "SESSION_ENCRYPTION_KEY is required. Set it as an environment variable (min 32 chars).");

// Serilog
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// Database
var connStr = builder.Configuration.GetConnectionString("Default")!;
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseMySql(connStr, ServerVersion.AutoDetect(connStr),
        mysql => mysql.EnableRetryOnFailure(3)));

// Blazor
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Auto-migrate + force UTC session
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("SET time_zone = '+00:00'");
}

app.UseMiddleware<SteamManager.Web.Middleware.UiAuthMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await app.RunAsync();
```

- [ ] **Step 3: Create initial migration**

```bash
cd /Users/hugomin/Developer/Repository/chaosmin/SteamManager
dotnet tool install --global dotnet-ef 2>/dev/null || true

export $(grep -v '^#' .env | grep -v '^$' | xargs)
export ConnectionStrings__Default="Server=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};User=${DB_USER};Password=${DB_PASSWORD};Convert Zero Datetime=True;AllowPublicKeyRetrieval=True;"
export SESSION_ENCRYPTION_KEY="migration-placeholder-key-32chars"

dotnet ef migrations add InitialCreate \
  --project src/SteamManager.Infrastructure \
  --startup-project src/SteamManager.Web \
  --output-dir Persistence/Migrations
```
Expected: Migration files in `src/SteamManager.Infrastructure/Persistence/Migrations/`.

- [ ] **Step 4: Apply migration to verify DB**

```bash
dotnet ef database update \
  --project src/SteamManager.Infrastructure \
  --startup-project src/SteamManager.Web

export PATH="/opt/homebrew/opt/mysql-client/bin:$PATH"
mysql -h 192.168.71.39 -u steam_mgr -pN3auZdk5nHe7ERsVaHQlogN steam_manager -e "SHOW TABLES;"
```
Expected: `achievement_cache`, `achievement_schedule`, `game_config`, `game_progress`, `steam_config`, `__EFMigrationsHistory`.

- [ ] **Step 5: Commit**

```bash
git add src/SteamManager.Web/ src/SteamManager.Infrastructure/Persistence/Migrations/
git commit -m "feat: EF Core migration, UTC enforcement, SESSION_ENCRYPTION_KEY startup guard"
```

---

### Task 5: SteamClientWrapper — Connection & Reconnect

**Files:**
- Create: `src/SteamManager.Infrastructure/Steam/SteamClientWrapper.cs`

- [ ] **Step 1: Implement**

`src/SteamManager.Infrastructure/Steam/SteamClientWrapper.cs`:
```csharp
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace SteamManager.Infrastructure.Steam;

public class SteamClientWrapper : IDisposable
{
    public SteamClient Client { get; } = new();
    public SteamUser SteamUser { get; }
    public SteamFriends SteamFriends { get; }
    public CallbackManager CallbackManager { get; }

    public bool IsConnected => Client.IsConnected;
    public bool IsLoggedOn { get; private set; }

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnLoggedOn;
    public event Action<string>? OnLoggedOff;

    private readonly ILogger<SteamClientWrapper> _logger;
    private CancellationTokenSource _cts = new();
    private Task? _callbackLoop;

    public SteamClientWrapper(ILogger<SteamClientWrapper> logger)
    {
        _logger = logger;
        SteamUser = Client.GetHandler<SteamUser>()!;
        SteamFriends = Client.GetHandler<SteamFriends>()!;
        CallbackManager = new CallbackManager(Client);

        CallbackManager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _logger.LogInformation("Steam: connected");
            OnConnected?.Invoke();
        });

        CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            IsLoggedOn = false;
            _logger.LogWarning("Steam: disconnected (user-initiated={U})", cb.UserInitiated);
            OnDisconnected?.Invoke();
        });

        CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            if (cb.Result == EResult.OK)
            {
                IsLoggedOn = true;
                _logger.LogInformation("Steam: logged on as {Id}", Client.SteamID);
                OnLoggedOn?.Invoke();
            }
            else
            {
                _logger.LogWarning("Steam: logon failed — {Result}", cb.Result);
                OnLoggedOff?.Invoke(cb.Result.ToString());
            }
        });

        CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(cb =>
        {
            IsLoggedOn = false;
            _logger.LogWarning("Steam: logged off — {Result}", cb.Result);
            OnLoggedOff?.Invoke(cb.Result.ToString());
        });
    }

    public void Connect()
    {
        _cts = new CancellationTokenSource();
        Client.Connect();
        _callbackLoop = Task.Run(() => RunCallbackLoop(_cts.Token));
    }

    public async Task ConnectWithReconnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            Connect();
            var connected = new TaskCompletionSource();
            void OnConn() => connected.TrySetResult();
            OnConnected += OnConn;
            await Task.WhenAny(connected.Task, Task.Delay(30_000, ct));
            OnConnected -= OnConn;
            if (IsConnected) return;
            _logger.LogWarning("Steam: retrying in {S}s", delay.TotalSeconds);
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }

    private void RunCallbackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            CallbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _callbackLoop?.Wait(2000);
        Client.Disconnect();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/SteamManager.Infrastructure/SteamManager.Infrastructure.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/SteamManager.Infrastructure/Steam/SteamClientWrapper.cs
git commit -m "feat: SteamClientWrapper with exponential backoff reconnection"
```

---

### Task 6: SteamSessionService

**Files:**
- Create: `src/SteamManager.Core/Services/ISteamSessionService.cs`
- Create: `src/SteamManager.Core/Services/SteamSessionService.cs`

- [ ] **Step 1: Create interface**

`src/SteamManager.Core/Services/ISteamSessionService.cs`:
```csharp
namespace SteamManager.Core.Services;

public enum LoginState { NotLoggedIn, AwaitingTwoFactor, LoggedIn, SessionExpired }

public interface ISteamSessionService
{
    LoginState State { get; }
    string? DisplayName { get; }
    event Action? StateChanged;

    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);
    Task<LoginState> BeginLoginAsync(string username, string password, CancellationToken ct = default);
    Task<bool> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default);
    Task LogoutAsync();
}
```

- [ ] **Step 2: Implement SteamSessionService**

`src/SteamManager.Core/Services/SteamSessionService.cs`:
```csharp
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamManager.Infrastructure.Crypto;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Core.Services;

public class SteamSessionService(
    SteamClientWrapper steam,
    AppDbContext db,
    IConfiguration config,
    ILogger<SteamSessionService> logger) : ISteamSessionService
{
    public LoginState State { get; private set; } = LoginState.NotLoggedIn;
    public string? DisplayName { get; private set; }
    public event Action? StateChanged;

    private string EncKey => config["SESSION_ENCRYPTION_KEY"]!;
    private string? _pendingUsername;

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        if (cfg?.SessionToken == null) return false;
        try
        {
            var token = AesEncryption.Decrypt(cfg.SessionToken, EncKey);
            await steam.ConnectWithReconnectAsync(ct);

            var tcs = new TaskCompletionSource<bool>();
            steam.OnLoggedOn += () => tcs.TrySetResult(true);
            steam.OnLoggedOff += _ => tcs.TrySetResult(false);

            steam.SteamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = cfg.Username,
                AccessToken = token,
                ShouldRememberPassword = true,
            });

            if (await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct))
            {
                DisplayName = steam.SteamFriends.GetPersonaName();
                SetState(LoginState.LoggedIn);
                return true;
            }
            SetState(LoginState.SessionExpired);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session restore failed");
            return false;
        }
    }

    public async Task<LoginState> BeginLoginAsync(string username, string password, CancellationToken ct = default)
    {
        _pendingUsername = username;
        await steam.ConnectWithReconnectAsync(ct);

        var tcs = new TaskCompletionSource<LoginState>();
        steam.CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            if (cb.Result is EResult.AccountLoginDeniedNeedTwoFactor or EResult.TwoFactorCodeMismatch)
                tcs.TrySetResult(LoginState.AwaitingTwoFactor);
            else if (cb.Result == EResult.OK)
                tcs.TrySetResult(LoginState.LoggedIn);
            else
                tcs.TrySetResult(LoginState.NotLoggedIn);
        });

        steam.SteamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = username,
            Password = password,
            ShouldRememberPassword = true,
        });

        var state = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (state == LoginState.LoggedIn) await PersistSessionAsync(ct);
        SetState(state);
        return state;
    }

    public async Task<bool> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        steam.OnLoggedOn += () => tcs.TrySetResult(true);
        steam.OnLoggedOff += _ => tcs.TrySetResult(false);

        steam.SteamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = _pendingUsername!,
            TwoFactorCode = code,
            ShouldRememberPassword = true,
        });

        var ok = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (ok) await PersistSessionAsync(ct);
        return ok;
    }

    private async Task PersistSessionAsync(CancellationToken ct)
    {
        var token = steam.SteamUser.RefreshToken;
        if (token == null) return;

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? new Core.Models.SteamConfig();

        cfg.Username = _pendingUsername!;
        cfg.PasswordEnc = null;  // clear password immediately after login
        cfg.SessionToken = AesEncryption.Encrypt(token, EncKey);
        cfg.SessionUpdatedAt = DateTime.UtcNow;

        if (cfg.Id == 0) db.SteamConfigs.Add(cfg);
        await db.SaveChangesAsync(ct);

        DisplayName = steam.SteamFriends.GetPersonaName();
        SetState(LoginState.LoggedIn);
    }

    public async Task LogoutAsync()
    {
        steam.SteamUser.LogOff();
        SetState(LoginState.NotLoggedIn);
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync();
        if (cfg != null) { cfg.SessionToken = null; await db.SaveChangesAsync(); }
    }

    private void SetState(LoginState s) { State = s; StateChanged?.Invoke(); }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build SteamManager.sln
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/SteamManager.Core/Services/ISteamSessionService.cs \
        src/SteamManager.Core/Services/SteamSessionService.cs
git commit -m "feat: SteamSessionService — 2FA login, token encrypt/persist, password clearing"
```

---

### Task 7: GameIdleService

**Files:**
- Create: `src/SteamManager.Core/Services/IGameIdleService.cs`
- Create: `src/SteamManager.Core/Services/GameIdleService.cs`

- [ ] **Step 1: Create interface**

`src/SteamManager.Core/Services/IGameIdleService.cs`:
```csharp
namespace SteamManager.Core.Services;

public interface IGameIdleService
{
    bool IsRunning(int appId);
    Task StartAsync(int appId, CancellationToken ct = default);
    Task StopAsync(int appId);
    event Action<int, int>? ProgressUpdated; // (appId, accumulatedMinutes)
}
```

- [ ] **Step 2: Implement**

`src/SteamManager.Core/Services/GameIdleService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Core.Services;

public class GameIdleService(
    SteamClientWrapper steam,
    AppDbContext db,
    ILogger<GameIdleService> logger) : IGameIdleService
{
    private readonly Dictionary<int, CancellationTokenSource> _running = [];
    public event Action<int, int>? ProgressUpdated;

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public async Task StartAsync(int appId, CancellationToken ct = default)
    {
        if (_running.ContainsKey(appId)) return;

        var game = await db.GameConfigs.Include(g => g.Progress)
            .FirstOrDefaultAsync(g => g.AppId == appId, ct)
            ?? throw new InvalidOperationException($"Game {appId} not found");

        if (game.Progress == null)
        {
            game.Progress = new GameProgress { AppId = appId };
            db.GameProgresses.Add(game.Progress);
        }

        game.Status = GameStatus.Running;
        game.Progress.LastSessionStart = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        steam.SteamFriends.SetPlayedGame(appId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        _ = RunIdleLoopAsync(appId, (int)(game.TargetHours * 60), cts.Token);
    }

    public async Task StopAsync(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return;
        await cts.CancelAsync();
        _running.Remove(appId);

        var game = await db.GameConfigs.Include(g => g.Progress)
            .FirstOrDefaultAsync(g => g.AppId == appId);
        if (game != null)
        {
            game.Status = GameStatus.Idle;
            await db.SaveChangesAsync();
        }
        steam.SteamFriends.SetPlayedGame(0);
    }

    private async Task RunIdleLoopAsync(int appId, int targetMinutes, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                var progress = await db.GameProgresses.FirstAsync(p => p.AppId == appId, ct);
                progress.AccumulatedMinutes++;
                progress.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                ProgressUpdated?.Invoke(appId, progress.AccumulatedMinutes);

                if (progress.AccumulatedMinutes >= targetMinutes)
                {
                    var game = await db.GameConfigs.FirstAsync(g => g.AppId == appId, ct);
                    game.Status = GameStatus.Completed;
                    await db.SaveChangesAsync(ct);
                    steam.SteamFriends.SetPlayedGame(0);
                    _running.Remove(appId);
                    logger.LogInformation("Game {AppId}: target reached, idle complete", appId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Idle loop error for {AppId}", appId); }
    }
}
```

- [ ] **Step 3: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Core/Services/IGameIdleService.cs \
        src/SteamManager.Core/Services/GameIdleService.cs
git commit -m "feat: GameIdleService with per-minute DB progress and auto-stop"
```

---

### Task 8: DTOs + Achievement Interval Calculator (TDD)

**Files:**
- Create: `src/SteamManager.Core/Dto/SteamAchievementDto.cs`
- Create: `src/SteamManager.Core/Dto/AchievementIntervalDto.cs`
- Create: `src/SteamManager.Core/Services/AchievementIntervalCalculator.cs`
- Create: `tests/SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests.cs`

- [ ] **Step 1: Create DTOs**

`src/SteamManager.Core/Dto/SteamAchievementDto.cs`:
```csharp
namespace SteamManager.Core.Dto;

public record SteamAchievementDto(
    string ApiName,
    string DisplayName,
    double GlobalPercent,
    long? UnlockTime    // Unix timestamp; null if locked
);
```

`src/SteamManager.Core/Dto/AchievementIntervalDto.cs`:
```csharp
namespace SteamManager.Core.Dto;

public record AchievementIntervalDto(
    string AchievementId,
    int OffsetMinutes   // median minutes from start of playthrough
);
```

- [ ] **Step 2: Write failing tests**

`tests/SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests.cs`:
```csharp
using SteamManager.Core.Dto;
using SteamManager.Core.Services;
using Xunit;

namespace SteamManager.Core.Tests.Services;

public class AchievementIntervalCalculatorTests
{
    [Fact]
    public void Calculate_TwoPlayers_ReturnsMedianIntervals()
    {
        // Player 1: A@t=0, B@t=60min, C@t=120min
        // Player 2: A@t=0, B@t=40min, C@t=100min
        // Expected median offsets: A=0, B=50, C=110
        var players = new List<List<SteamAchievementDto>>
        {
            [
                new("ACH_A", "A", 80.0, 1_000_000_000),
                new("ACH_B", "B", 50.0, 1_000_003_600),
                new("ACH_C", "C", 20.0, 1_000_007_200),
            ],
            [
                new("ACH_A", "A", 80.0, 1_000_000_000),
                new("ACH_B", "B", 50.0, 1_000_002_400),
                new("ACH_C", "C", 20.0, 1_000_006_000),
            ],
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
        Assert.Equal(80, result[1].OffsetMinutes);   // (80-40)*2 = 80
        Assert.Equal("ACH_HARD", result[2].AchievementId);
        Assert.Equal(150, result[2].OffsetMinutes);  // 80 + (40-5)*2 = 150
    }
}
```

- [ ] **Step 3: Run — verify FAIL**

```bash
dotnet test tests/SteamManager.Core.Tests/ --filter "AchievementIntervalCalculatorTests"
```
Expected: FAIL — type not found.

- [ ] **Step 4: Implement calculator**

`src/SteamManager.Core/Services/AchievementIntervalCalculator.cs`:
```csharp
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
```

- [ ] **Step 5: Run — verify PASS**

```bash
dotnet test tests/SteamManager.Core.Tests/ --filter "AchievementIntervalCalculatorTests" -v
```
Expected: 3 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SteamManager.Core/Dto/ \
        src/SteamManager.Core/Services/AchievementIntervalCalculator.cs \
        tests/SteamManager.Core.Tests/Services/AchievementIntervalCalculatorTests.cs
git commit -m "feat: achievement interval calculator (median + fallback) with TDD"
```

---

### Task 9: AchievementDataService + HTTP Clients

**Files:**
- Create: `src/SteamManager.Infrastructure/Http/SteamWebApiClient.cs`
- Create: `src/SteamManager.Infrastructure/Http/SteamHuntersClient.cs`
- Create: `src/SteamManager.Core/Services/IAchievementDataService.cs`
- Create: `src/SteamManager.Core/Services/AchievementDataService.cs`

- [ ] **Step 1: SteamWebApiClient**

`src/SteamManager.Infrastructure/Http/SteamWebApiClient.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Dto;

namespace SteamManager.Infrastructure.Http;

public class SteamWebApiClient(HttpClient http, ILogger<SteamWebApiClient> logger)
{
    private static readonly TimeSpan CallDelay = TimeSpan.FromSeconds(1);

    public async Task<List<SteamAchievementDto>> GetSchemaAchievementsAsync(
        int appId, string apiKey, CancellationToken ct = default)
    {
        var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l=english";
        var json = await FetchWithRetryAsync(url, ct);
        var achievements = JsonDocument.Parse(json).RootElement
            .GetProperty("game").GetProperty("availableGameStats").GetProperty("achievements")
            .EnumerateArray()
            .Select(a => new SteamAchievementDto(
                a.GetProperty("name").GetString()!,
                a.GetProperty("displayName").GetString()!,
                0.0, null))
            .ToList();

        await Task.Delay(CallDelay, ct);
        var pctJson = await FetchWithRetryAsync(
            $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={appId}", ct);
        var pctMap = JsonDocument.Parse(pctJson).RootElement
            .GetProperty("achievementpercentages").GetProperty("achievements")
            .EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("percent").GetDouble());

        return achievements.Select(a => a with { GlobalPercent = pctMap.GetValueOrDefault(a.ApiName) }).ToList();
    }

    public async Task<List<SteamAchievementDto>> GetPlayerAchievementsAsync(
        long steamId, int appId, string apiKey, CancellationToken ct = default)
    {
        await Task.Delay(CallDelay, ct); // 1s rate limiting between player calls
        try
        {
            var url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={apiKey}&steamid={steamId}&appid={appId}";
            var json = await FetchWithRetryAsync(url, ct);
            return JsonDocument.Parse(json).RootElement
                .GetProperty("playerstats").GetProperty("achievements")
                .EnumerateArray()
                .Select(a => new SteamAchievementDto(
                    a.GetProperty("apiname").GetString()!,
                    a.GetProperty("apiname").GetString()!,
                    0.0,
                    a.GetProperty("achieved").GetInt32() == 1
                        ? (long?)a.GetProperty("unlocktime").GetInt64()
                        : null))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get achievements for player {SteamId}", steamId);
            return [];
        }
    }

    private async Task<string> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt >= 3) throw new HttpRequestException("Steam API 429 after 3 retries");
                logger.LogWarning("Steam API 429, waiting 60s (attempt {A}/3)", attempt);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}
```

- [ ] **Step 2: SteamHuntersClient**

`src/SteamManager.Infrastructure/Http/SteamHuntersClient.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamManager.Infrastructure.Http;

public class SteamHuntersClient(HttpClient http, ILogger<SteamHuntersClient> logger)
{
    public async Task<List<long>> GetPerfectPlayersAsync(int appId, int maxCount, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://steamhunters.com/api/apps/{appId}/players?perfectOnly=true&limit={maxCount}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("SteamHunters {Status} for appId {AppId}", resp.StatusCode, appId);
                return [];
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement
                .EnumerateArray()
                .Select(e => e.GetProperty("steamId").GetInt64())
                .Take(maxCount)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SteamHunters unavailable for {AppId}, will use fallback", appId);
            return [];
        }
    }
}
```

- [ ] **Step 3: Interface + Service**

`src/SteamManager.Core/Services/IAchievementDataService.cs`:
```csharp
using SteamManager.Core.Dto;

namespace SteamManager.Core.Services;

public interface IAchievementDataService
{
    Task<List<AchievementIntervalDto>> GetIntervalsAsync(int appId, CancellationToken ct = default);
    Task RefreshCacheAsync(int appId, CancellationToken ct = default);
}
```

`src/SteamManager.Core/Services/AchievementDataService.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Dto;
using SteamManager.Core.Models;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Core.Services;

public class AchievementDataService(
    AppDbContext db,
    SteamWebApiClient steamApi,
    SteamHuntersClient hunters,
    IConfiguration config,
    ILogger<AchievementDataService> logger) : IAchievementDataService
{
    private int CacheExpiryDays => config.GetValue("AchievementData:CacheExpiryDays", 7);
    private int MaxPlayers => config.GetValue("AchievementData:MaxReferencePlayers", 20);
    private int FallbackInterval => config.GetValue("AchievementData:FallbackIntervalPerPercentDiff", 2);

    public async Task<List<AchievementIntervalDto>> GetIntervalsAsync(int appId, CancellationToken ct = default)
    {
        var cache = await db.AchievementCaches.FirstOrDefaultAsync(c => c.AppId == appId, ct);
        if (cache != null && cache.FetchedAt > DateTime.UtcNow.AddDays(-CacheExpiryDays))
        {
            logger.LogDebug("Cache hit for appId {AppId}", appId);
            return JsonSerializer.Deserialize<List<AchievementIntervalDto>>(cache.Data)!;
        }

        await RefreshCacheAsync(appId, ct);
        cache = await db.AchievementCaches.FirstAsync(c => c.AppId == appId, ct);
        return JsonSerializer.Deserialize<List<AchievementIntervalDto>>(cache.Data)!;
    }

    public async Task RefreshCacheAsync(int appId, CancellationToken ct = default)
    {
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Steam config not found");
        var apiKey = cfg.WebApiKey
            ?? throw new InvalidOperationException("Steam Web API key not configured");

        logger.LogInformation("Fetching achievement intervals for appId {AppId}", appId);
        var schema = await steamApi.GetSchemaAchievementsAsync(appId, apiKey, ct);
        var playerIds = await hunters.GetPerfectPlayersAsync(appId, MaxPlayers, ct);

        List<AchievementIntervalDto> intervals;
        if (playerIds.Count > 0)
        {
            var playerData = new List<List<SteamAchievementDto>>();
            foreach (var id in playerIds)
            {
                var data = await steamApi.GetPlayerAchievementsAsync(id, appId, apiKey, ct);
                if (data.Any(a => a.UnlockTime.HasValue)) playerData.Add(data);
            }
            intervals = playerData.Count > 0
                ? AchievementIntervalCalculator.Calculate(playerData)
                : AchievementIntervalCalculator.CalculateFallback(schema, FallbackInterval);
        }
        else
        {
            logger.LogWarning("No perfect players for {AppId}, using fallback", appId);
            intervals = AchievementIntervalCalculator.CalculateFallback(schema, FallbackInterval);
        }

        var json = JsonSerializer.Serialize(intervals);
        var cache = await db.AchievementCaches.FirstOrDefaultAsync(c => c.AppId == appId, ct);
        if (cache == null) { cache = new AchievementCache { AppId = appId }; db.AchievementCaches.Add(cache); }
        cache.Data = json;
        cache.FetchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Cached {Count} intervals for appId {AppId}", intervals.Count, appId);
    }
}
```

- [ ] **Step 4: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Infrastructure/Http/ \
        src/SteamManager.Core/Services/IAchievementDataService.cs \
        src/SteamManager.Core/Services/AchievementDataService.cs
git commit -m "feat: AchievementDataService — Steam API + SteamHunters + fallback + rate limiting"
```

---

### Task 10: AchievementHandler + UnlockSchedulerService (TDD)

**Files:**
- Create: `src/SteamManager.Infrastructure/Steam/AchievementHandler.cs`
- Create: `src/SteamManager.Core/Services/IUnlockSchedulerService.cs`
- Create: `src/SteamManager.Core/Services/UnlockSchedulerService.cs`
- Create: `tests/SteamManager.Core.Tests/Services/UnlockSchedulerResumeTests.cs`

- [ ] **Step 1: Write failing tests for scheduler math**

`tests/SteamManager.Core.Tests/Services/UnlockSchedulerResumeTests.cs`:
```csharp
using SteamManager.Core.Services;
using Xunit;

namespace SteamManager.Core.Tests.Services;

public class UnlockSchedulerResumeTests
{
    [Fact]
    public void GetWaitMinutes_ReturnsCorrectDelay()
    {
        Assert.Equal(5, UnlockSchedulerService.GetWaitMinutes(55, 60));
    }

    [Fact]
    public void GetWaitMinutes_WhenPast_ReturnsZero()
    {
        Assert.Equal(0, UnlockSchedulerService.GetWaitMinutes(65, 60));
    }

    [Fact]
    public void ApplyJitter_StaysWithinTenPercent()
    {
        for (int i = 0; i < 200; i++)
        {
            var jittered = UnlockSchedulerService.ApplyJitter(100, jitterPercent: 10);
            Assert.InRange(jittered, 90, 110);
        }
    }
}
```

- [ ] **Step 2: Run — verify FAIL**

```bash
dotnet test tests/SteamManager.Core.Tests/ --filter "UnlockSchedulerResumeTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement AchievementHandler**

`src/SteamManager.Infrastructure/Steam/AchievementHandler.cs`:
```csharp
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamManager.Infrastructure.Steam;

public class AchievementHandler(SteamClientWrapper steam, ILogger<AchievementHandler> logger)
{
    public async Task<bool> UnlockAchievementAsync(
        int appId, string achievementId, CancellationToken ct = default)
    {
        // 1. Request stats schema
        var statsReq = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats);
        statsReq.Body.game_id = (ulong)appId;
        statsReq.Body.steam_id_for_user = steam.Client.SteamID!.ConvertToUInt64();

        var statsTcs = new TaskCompletionSource<CMsgClientGetUserStatsResponse>();
        using var sub = steam.CallbackManager.Subscribe<SteamUnifiedMessages.ServiceMethodResponse>(cb =>
        {
            if (cb.MethodName?.Contains("GetUserStats") == true)
                statsTcs.TrySetResult(cb.GetDeserializedResponse<CMsgClientGetUserStatsResponse>());
        });
        steam.Client.Send(statsReq);

        CMsgClientGetUserStatsResponse statsResponse;
        try { statsResponse = await statsTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch (TimeoutException) { logger.LogWarning("Timeout getting stats for {AppId}", appId); return false; }

        // 2. Parse schema — find achievement's stat_id and bit position
        var schema = KeyValue.LoadFromString(statsResponse.schema ?? "");
        if (schema == null) { logger.LogWarning("Empty schema for {AppId}", appId); return false; }

        uint? statId = null;
        int? bitNum = null;
        foreach (var stat in schema["stats"].Children)
        {
            foreach (var bit in stat["bits"].Children)
            {
                if (string.Equals(bit["name"].Value, achievementId, StringComparison.OrdinalIgnoreCase))
                {
                    statId = uint.Parse(stat["id"].Value!);
                    bitNum = int.Parse(bit["bit"].Value!);
                    break;
                }
            }
            if (statId.HasValue) break;
        }

        if (!statId.HasValue)
        {
            logger.LogWarning("Achievement {Id} not found in schema for {AppId}", achievementId, appId);
            return false;
        }

        // 3. Set bit in current stat value
        var currentValue = statsResponse.stats.FirstOrDefault(s => s.stat_id == statId.Value)?.stat_value ?? 0;
        var newValue = currentValue | (1u << bitNum!.Value);
        if (newValue == currentValue)
        {
            logger.LogInformation("Achievement {Id} already unlocked for {AppId}", achievementId, appId);
            return true;
        }

        // 4. Send updated stats
        var storeReq = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
        storeReq.Body.game_id = (ulong)appId;
        storeReq.Body.explicit_reset = false;
        storeReq.Body.stats.Add(new CMsgClientStoreUserStats2.Stats { stat_id = statId.Value, stat_value = newValue });
        steam.Client.Send(storeReq);

        await Task.Delay(5000, ct); // allow Steam to process
        logger.LogInformation("Achievement {Id} unlocked for {AppId}", achievementId, appId);
        return true;
    }
}
```

- [ ] **Step 4: Implement interface + scheduler**

`src/SteamManager.Core/Services/IUnlockSchedulerService.cs`:
```csharp
namespace SteamManager.Core.Services;

public interface IUnlockSchedulerService
{
    bool IsRunning(int appId);
    Task StartAsync(int appId, CancellationToken ct = default);
    Task StopAsync(int appId);
    event Action<int, string>? AchievementUnlocked; // (appId, achievementId)
}
```

`src/SteamManager.Core/Services/UnlockSchedulerService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Core.Services;

public class UnlockSchedulerService(
    AppDbContext db,
    AchievementHandler handler,
    IAchievementDataService dataService,
    ILogger<UnlockSchedulerService> logger) : IUnlockSchedulerService
{
    private readonly Dictionary<int, CancellationTokenSource> _running = [];
    public event Action<int, string>? AchievementUnlocked;

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public async Task StartAsync(int appId, CancellationToken ct = default)
    {
        if (_running.ContainsKey(appId)) return;

        var hasSchedule = await db.AchievementSchedules.AnyAsync(s => s.AppId == appId, ct);
        if (!hasSchedule)
        {
            var intervals = await dataService.GetIntervalsAsync(appId, ct);
            foreach (var i in intervals)
                db.AchievementSchedules.Add(new Core.Models.AchievementScheduleItem
                {
                    AppId = appId, AchievementId = i.AchievementId, OffsetMinutes = i.OffsetMinutes, Done = false
                });
            await db.SaveChangesAsync(ct);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        _ = RunLoopAsync(appId, cts.Token);
    }

    public async Task StopAsync(int appId)
    {
        if (_running.TryGetValue(appId, out var cts)) { await cts.CancelAsync(); _running.Remove(appId); }
    }

    private async Task RunLoopAsync(int appId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var progress = await db.GameProgresses.FirstOrDefaultAsync(p => p.AppId == appId, ct);
                if (progress == null) { await Task.Delay(5000, ct); continue; }

                var next = await db.AchievementSchedules
                    .Where(s => s.AppId == appId && !s.Done)
                    .OrderBy(s => s.OffsetMinutes)
                    .FirstOrDefaultAsync(ct);

                if (next == null) { _running.Remove(appId); return; }

                var wait = ApplyJitter(GetWaitMinutes(progress.AccumulatedMinutes, next.OffsetMinutes), 10);
                if (wait > 0)
                {
                    logger.LogDebug("{AppId}: next '{Id}' in {W}min", appId, next.AchievementId, wait);
                    await Task.Delay(TimeSpan.FromMinutes(wait), ct);
                }

                next.Done = true;
                next.UnlockedAt = DateTime.UtcNow;
                var ok = await handler.UnlockAchievementAsync(appId, next.AchievementId, ct);
                await db.SaveChangesAsync(ct);

                if (ok) { AchievementUnlocked?.Invoke(appId, next.AchievementId); }
                else logger.LogWarning("Skipping '{Id}' for {AppId} — unlock failed", next.AchievementId, appId);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Schedule loop error for {AppId}", appId); }
    }

    public static int GetWaitMinutes(int accumulatedMinutes, int nextOffsetMinutes) =>
        Math.Max(0, nextOffsetMinutes - accumulatedMinutes);

    public static int ApplyJitter(int baseMinutes, int jitterPercent)
    {
        if (baseMinutes <= 0) return 0;
        var range = (int)Math.Ceiling(baseMinutes * jitterPercent / 100.0);
        return baseMinutes + Random.Shared.Next(-range, range + 1);
    }
}
```

- [ ] **Step 5: Run tests — verify PASS**

```bash
dotnet test tests/SteamManager.Core.Tests/ --filter "UnlockSchedulerResumeTests" -v
```
Expected: 3 tests PASS.

- [ ] **Step 6: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Infrastructure/Steam/AchievementHandler.cs \
        src/SteamManager.Core/Services/IUnlockSchedulerService.cs \
        src/SteamManager.Core/Services/UnlockSchedulerService.cs \
        tests/SteamManager.Core.Tests/Services/UnlockSchedulerResumeTests.cs
git commit -m "feat: AchievementHandler (EMsg.ClientStoreUserStats2) + UnlockScheduler with TDD"
```

---

### Task 11: Startup Recovery + DI Registration

**Files:**
- Create: `src/SteamManager.Core/Services/StartupRecoveryService.cs`
- Modify: `src/SteamManager.Web/Program.cs`

- [ ] **Step 1: Implement StartupRecoveryService**

`src/SteamManager.Core/Services/StartupRecoveryService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Core.Services;

public class StartupRecoveryService(
    ISteamSessionService session,
    IGameIdleService idle,
    IUnlockSchedulerService scheduler,
    AppDbContext db,
    ILogger<StartupRecoveryService> logger)
{
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var restored = await session.TryRestoreSessionAsync(ct);
        if (!restored)
        {
            logger.LogInformation("No valid Steam session — awaiting user login");
            return;
        }

        var running = await db.GameConfigs
            .Where(g => g.Status == GameStatus.Running)
            .ToListAsync(ct);

        foreach (var game in running)
        {
            logger.LogInformation("Recovering appId {AppId}", game.AppId);
            await idle.StartAsync(game.AppId, ct);
            if (game.EnableAchievements)
                await scheduler.StartAsync(game.AppId, ct);
        }

        logger.LogInformation("Recovery complete — {N} game(s) resumed", running.Count);
    }
}
```

- [ ] **Step 2: Register all services in Program.cs**

Add the following service registrations before `var app = builder.Build()`:

```csharp
// Infrastructure singletons
builder.Services.AddSingleton<SteamManager.Infrastructure.Steam.SteamClientWrapper>();
builder.Services.AddSingleton<SteamManager.Infrastructure.Steam.AchievementHandler>();

// HTTP clients
builder.Services.AddHttpClient<SteamManager.Infrastructure.Http.SteamWebApiClient>();
builder.Services.AddHttpClient<SteamManager.Infrastructure.Http.SteamHuntersClient>();

// Core services (Singleton so background loops persist across Blazor circuits)
builder.Services.AddSingleton<ISteamSessionService, SteamSessionService>();
builder.Services.AddSingleton<IGameIdleService, GameIdleService>();
builder.Services.AddSingleton<IAchievementDataService, AchievementDataService>();
builder.Services.AddSingleton<IUnlockSchedulerService, UnlockSchedulerService>();
builder.Services.AddScoped<StartupRecoveryService>();
```

Add after the MigrateAsync block:

```csharp
using (var scope = app.Services.CreateScope())
{
    var recovery = scope.ServiceProvider.GetRequiredService<StartupRecoveryService>();
    await recovery.RecoverAsync();
}
```

- [ ] **Step 3: Build**

```bash
dotnet build SteamManager.sln
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/SteamManager.Core/Services/StartupRecoveryService.cs \
        src/SteamManager.Web/Program.cs
git commit -m "feat: startup recovery, full DI registration"
```

---

### Task 12: UI Auth Middleware + Login Page

**Files:**
- Create: `src/SteamManager.Web/Middleware/UiAuthMiddleware.cs`
- Create: `src/SteamManager.Web/Pages/Login.razor`

- [ ] **Step 1: Implement middleware**

`src/SteamManager.Web/Middleware/UiAuthMiddleware.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;

namespace SteamManager.Web.Middleware;

public class UiAuthMiddleware(RequestDelegate next, IConfiguration config)
{
    private const string CookieName = "sm_auth";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var password = config["UI_ACCESS_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("UI_ACCESS_PASSWORD");

        if (string.IsNullOrEmpty(password)) { await next(ctx); return; }

        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/login") || path.StartsWith("/_blazor") || path.StartsWith("/_framework"))
        {
            await next(ctx);
            return;
        }

        if (ctx.Request.Cookies.TryGetValue(CookieName, out var token) && token == Hash(password))
        {
            await next(ctx);
            return;
        }

        ctx.Response.Redirect("/login");
    }

    public static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    public static void SetCookie(HttpContext ctx, string password) =>
        ctx.Response.Cookies.Append(CookieName, Hash(password),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddDays(30), HttpOnly = true });
}
```

- [ ] **Step 2: Create Login page**

`src/SteamManager.Web/Pages/Login.razor`:
```razor
@page "/login"
@inject IConfiguration Config
@inject NavigationManager Nav
@inject IHttpContextAccessor HttpCtx

<h3>SteamManager Login</h3>
@if (_error) { <p style="color:red">Wrong password.</p> }
<input type="password" @bind="_input" placeholder="Access password" />
<button @onclick="Submit">Login</button>

@code {
    string _input = "";
    bool _error;

    void Submit()
    {
        var expected = Config["UI_ACCESS_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("UI_ACCESS_PASSWORD") ?? "";
        if (_input == expected)
        {
            SteamManager.Web.Middleware.UiAuthMiddleware.SetCookie(HttpCtx.HttpContext!, expected);
            Nav.NavigateTo("/", forceLoad: true);
        }
        else { _error = true; }
    }
}
```

- [ ] **Step 3: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Web/Middleware/UiAuthMiddleware.cs \
        src/SteamManager.Web/Pages/Login.razor
git commit -m "feat: UI password auth middleware + login page with 30-day cookie"
```

---

### Task 13: Settings Page

**Files:**
- Create: `src/SteamManager.Web/Pages/Settings.razor`

- [ ] **Step 1: Implement**

`src/SteamManager.Web/Pages/Settings.razor`:
```razor
@page "/settings"
@using Microsoft.EntityFrameworkCore
@using SteamManager.Core.Services
@using SteamManager.Infrastructure.Persistence
@inject ISteamSessionService Session
@inject AppDbContext Db

<h2>Settings</h2>

<h3>Steam Account</h3>
@if (Session.State == LoginState.LoggedIn)
{
    <p>Logged in as <strong>@Session.DisplayName</strong></p>
    <button @onclick="Logout">Logout</button>
}
else if (Session.State == LoginState.AwaitingTwoFactor)
{
    <p>Enter Steam Guard code:</p>
    <input @bind="_code" maxlength="6" style="width:80px" />
    <button @onclick="Submit2FA">Submit</button>
    @if (_err != null) { <p style="color:red">@_err</p> }
}
else
{
    <input placeholder="Username" @bind="_user" />
    <input type="password" placeholder="Password" @bind="_pass" />
    <button @onclick="BeginLogin">Login</button>
    @if (_err != null) { <p style="color:red">@_err</p> }
}

<h3>Steam Web API Key</h3>
<input @bind="_apiKey" style="width:320px" placeholder="Steam Web API Key" />
<button @onclick="SaveApiKey">Save</button>
@if (_apiKeySaved) { <span style="color:green"> Saved</span> }

<h3>Display Timezone</h3>
<select @bind="_tz" style="width:300px">
    @foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
    {
        <option value="@zone.Id">@zone.DisplayName</option>
    }
</select>
<button @onclick="SaveTz">Save</button>
@if (_tzSaved) { <span style="color:green"> Saved</span> }

@code {
    string _user = "", _pass = "", _code = "";
    string? _err;
    string _apiKey = "";
    bool _apiKeySaved;
    string _tz = "UTC";
    bool _tzSaved;

    protected override async Task OnInitializedAsync()
    {
        var cfg = await Db.SteamConfigs.FirstOrDefaultAsync();
        if (cfg != null) { _apiKey = cfg.WebApiKey ?? ""; _tz = cfg.DisplayTimezone; }
        else _tz = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
    }

    async Task BeginLogin()
    {
        _err = null;
        var state = await Session.BeginLoginAsync(_user, _pass);
        if (state == LoginState.LoggedIn) await SetDefaultTimezone();
        else if (state != LoginState.AwaitingTwoFactor) _err = "Login failed";
        StateHasChanged();
    }

    async Task Submit2FA()
    {
        var ok = await Session.SubmitTwoFactorCodeAsync(_code);
        if (ok) await SetDefaultTimezone();
        else _err = "Invalid code";
        StateHasChanged();
    }

    async Task Logout() => await Session.LogoutAsync();

    async Task SaveApiKey()
    {
        var cfg = await Db.SteamConfigs.FirstOrDefaultAsync() ?? new Core.Models.SteamConfig();
        cfg.WebApiKey = _apiKey;
        if (cfg.Id == 0) Db.SteamConfigs.Add(cfg);
        await Db.SaveChangesAsync();
        _apiKeySaved = true;
    }

    async Task SaveTz()
    {
        var cfg = await Db.SteamConfigs.FirstOrDefaultAsync();
        if (cfg != null) { cfg.DisplayTimezone = _tz; await Db.SaveChangesAsync(); }
        _tzSaved = true;
    }

    async Task SetDefaultTimezone()
    {
        var cfg = await Db.SteamConfigs.FirstOrDefaultAsync();
        if (cfg != null && cfg.DisplayTimezone == "UTC")
        {
            cfg.DisplayTimezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            await Db.SaveChangesAsync();
        }
    }
}
```

- [ ] **Step 2: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Web/Pages/Settings.razor
git commit -m "feat: Settings page — Steam login/2FA, API key, IANA timezone selector"
```

---

### Task 14: Games Page + GameCard Component

**Files:**
- Create: `src/SteamManager.Web/Components/GameCard.razor`
- Create: `src/SteamManager.Web/Pages/Games.razor`

- [ ] **Step 1: GameCard component**

`src/SteamManager.Web/Components/GameCard.razor`:
```razor
@using SteamManager.Core.Models

<div style="border:1px solid #ccc;padding:12px;margin:8px;display:inline-block;width:240px;vertical-align:top">
    <img src="https://cdn.cloudflare.steamstatic.com/steam/apps/@Game.AppId/header.jpg"
         style="width:100%" alt="@Game.Name" />
    <h4>@Game.Name</h4>
    <p>@(AccumulatedMinutes/60.0).ToString("F1") / @Game.TargetHours h</p>
    <progress value="@AccumulatedMinutes" max="@((int)(Game.TargetHours*60))" style="width:100%"></progress>
    <p>Status: @Game.Status</p>
    @if (Game.Status == GameStatus.Running)
    {
        <button @onclick="() => OnStop.InvokeAsync(Game.AppId)">Stop</button>
    }
    else if (Game.Status == GameStatus.Idle)
    {
        <button @onclick="() => OnStart.InvokeAsync(Game.AppId)">Start</button>
    }
    else { <span style="color:green">✔ Completed</span> }
</div>

@code {
    [Parameter] public GameConfig Game { get; set; } = null!;
    [Parameter] public int AccumulatedMinutes { get; set; }
    [Parameter] public EventCallback<int> OnStart { get; set; }
    [Parameter] public EventCallback<int> OnStop { get; set; }
}
```

- [ ] **Step 2: Games page**

`src/SteamManager.Web/Pages/Games.razor`:
```razor
@page "/games"
@using Microsoft.EntityFrameworkCore
@using SteamManager.Core.Models
@using SteamManager.Core.Services
@using SteamManager.Infrastructure.Persistence
@using SteamManager.Web.Components
@inject AppDbContext Db
@inject IGameIdleService Idle
@inject IUnlockSchedulerService Scheduler
@inject ISteamSessionService Session
@implements IDisposable

<h2>Games</h2>

@if (Session.State != LoginState.LoggedIn)
{
    <p>Please login in <a href="/settings">Settings</a> first.</p>
}
else
{
    <h3>Add Game</h3>
    <input @bind="_appId" placeholder="AppID" style="width:100px" />
    <input @bind="_hours" type="number" placeholder="Target hours" style="width:80px" min="1" />
    <label><input type="checkbox" @bind="_ach" /> Achievements</label>
    <button @onclick="Add">Add</button>
    @if (_err != null) { <p style="color:red">@_err</p> }

    <h3>My Games</h3>
    @foreach (var g in _games)
    {
        <GameCard Game="g"
                  AccumulatedMinutes="_progress.GetValueOrDefault(g.AppId)"
                  OnStart="Start" OnStop="Stop" />
    }
}

@code {
    List<GameConfig> _games = [];
    Dictionary<int, int> _progress = [];
    string _appId = ""; decimal _hours = 10; bool _ach = true;
    string? _err;

    protected override async Task OnInitializedAsync()
    {
        Idle.ProgressUpdated += OnProgress;
        await Load();
    }

    async Task Load()
    {
        _games = await Db.GameConfigs.Include(g => g.Progress).ToListAsync();
        _progress = _games.Where(g => g.Progress != null)
            .ToDictionary(g => g.AppId, g => g.Progress!.AccumulatedMinutes);
    }

    async Task Add()
    {
        _err = null;
        if (!int.TryParse(_appId, out var id) || id <= 0) { _err = "Invalid AppID"; return; }
        if (await Db.GameConfigs.AnyAsync(g => g.AppId == id)) { _err = "Already added"; return; }

        string name = $"App {id}";
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=basic_info");
            var doc = System.Text.Json.JsonDocument.Parse(json);
            name = doc.RootElement.GetProperty(id.ToString()).GetProperty("data").GetProperty("name").GetString() ?? name;
        }
        catch { }

        Db.GameConfigs.Add(new GameConfig { AppId = id, Name = name, TargetHours = _hours, EnableAchievements = _ach });
        await Db.SaveChangesAsync();
        await Load(); _appId = "";
    }

    async Task Start(int appId)
    {
        await Idle.StartAsync(appId);
        if ((await Db.GameConfigs.FirstAsync(g => g.AppId == appId)).EnableAchievements)
            await Scheduler.StartAsync(appId);
        await Load();
    }

    async Task Stop(int appId)
    {
        await Idle.StopAsync(appId);
        await Scheduler.StopAsync(appId);
        await Load();
    }

    void OnProgress(int appId, int min) { _progress[appId] = min; InvokeAsync(StateHasChanged); }
    public void Dispose() => Idle.ProgressUpdated -= OnProgress;
}
```

- [ ] **Step 3: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Web/Components/GameCard.razor \
        src/SteamManager.Web/Pages/Games.razor
git commit -m "feat: Games page with add/start/stop and real-time progress"
```

---

### Task 15: Dashboard Page + AchievementList Component

**Files:**
- Create: `src/SteamManager.Web/Components/AchievementList.razor`
- Create: `src/SteamManager.Web/Pages/Dashboard.razor`

- [ ] **Step 1: AchievementList component**

`src/SteamManager.Web/Components/AchievementList.razor`:
```razor
@using SteamManager.Core.Models

<table style="width:100%;font-size:0.85em">
    <thead><tr><th>Achievement</th><th>Status</th><th>Unlocked At</th></tr></thead>
    <tbody>
    @foreach (var item in Items)
    {
        <tr>
            <td>@item.AchievementId</td>
            <td>@(item.Done ? "✅" : "⏳")</td>
            <td>@(item.UnlockedAt.HasValue ? item.UnlockedAt.Value.ToString("g") : "-")</td>
        </tr>
    }
    </tbody>
</table>

@code {
    [Parameter] public IEnumerable<AchievementScheduleItem> Items { get; set; } = [];
}
```

- [ ] **Step 2: Dashboard page**

`src/SteamManager.Web/Pages/Dashboard.razor`:
```razor
@page "/"
@using Microsoft.EntityFrameworkCore
@using SteamManager.Core.Models
@using SteamManager.Core.Services
@using SteamManager.Infrastructure.Persistence
@using SteamManager.Web.Components
@inject ISteamSessionService Session
@inject AppDbContext Db
@inject IGameIdleService Idle
@inject IUnlockSchedulerService Scheduler
@implements IDisposable

@if (Session.State != LoginState.LoggedIn)
{
    <div style="background:#fff3cd;padding:12px;border-radius:4px;margin-bottom:12px">
        ⚠️ Not logged in. Go to <a href="/settings">Settings</a>.
    </div>
}
else
{
    <p>Steam: <strong>@Session.DisplayName</strong></p>
}

@foreach (var game in _games)
{
    var prog = _progress.GetValueOrDefault(game.AppId);
    var next = _nextAch.GetValueOrDefault(game.AppId);
    var waitMin = next != null ? UnlockSchedulerService.GetWaitMinutes(prog?.AccumulatedMinutes ?? 0, next.OffsetMinutes) : (int?)null;
    var schedule = _schedules.GetValueOrDefault(game.AppId, []);
    var done = schedule.Count(s => s.Done);

    <div style="border:1px solid #ddd;padding:12px;margin:8px;border-radius:6px">
        <img src="https://cdn.cloudflare.steamstatic.com/steam/apps/@game.AppId/header.jpg" style="height:50px;vertical-align:middle" />
        <strong style="margin-left:8px">@game.Name</strong>
        <span style="margin-left:12px;color:gray">@game.Status</span>

        @if (prog != null)
        {
            <div>
                @((prog.AccumulatedMinutes / 60.0).ToString("F1")) / @game.TargetHours h
                <progress value="@prog.AccumulatedMinutes" max="@((int)(game.TargetHours*60))" style="width:180px;margin-left:8px"></progress>
            </div>
        }

        @if (schedule.Count > 0)
        {
            <div>Achievements: @done/@schedule.Count
                @if (next != null && waitMin.HasValue)
                {
                    <span> — next in ~@waitMin min (@next.AchievementId)</span>
                }
            </div>
            <details><summary>Show list</summary><AchievementList Items="schedule" /></details>
        }
    </div>
}

@code {
    List<GameConfig> _games = [];
    Dictionary<int, GameProgress> _progress = [];
    Dictionary<int, AchievementScheduleItem?> _nextAch = [];
    Dictionary<int, List<AchievementScheduleItem>> _schedules = [];

    protected override async Task OnInitializedAsync()
    {
        Session.StateChanged += Refresh;
        Idle.ProgressUpdated += OnProgress;
        Scheduler.AchievementUnlocked += OnUnlocked;
        await Load();
    }

    async Task Load()
    {
        _games = await Db.GameConfigs.Include(g => g.Progress).ToListAsync();
        _progress = _games.Where(g => g.Progress != null)
            .ToDictionary(g => g.AppId, g => g.Progress!);
        foreach (var game in _games)
        {
            var s = await Db.AchievementSchedules.Where(x => x.AppId == game.AppId)
                .OrderBy(x => x.OffsetMinutes).ToListAsync();
            _schedules[game.AppId] = s;
            _nextAch[game.AppId] = s.FirstOrDefault(x => !x.Done);
        }
    }

    void OnProgress(int appId, int min)
    {
        if (_progress.TryGetValue(appId, out var p)) p.AccumulatedMinutes = min;
        InvokeAsync(StateHasChanged);
    }

    void OnUnlocked(int appId, string _) => InvokeAsync(async () => { await Load(); StateHasChanged(); });
    void Refresh() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Session.StateChanged -= Refresh;
        Idle.ProgressUpdated -= OnProgress;
        Scheduler.AchievementUnlocked -= OnUnlocked;
    }
}
```

- [ ] **Step 3: Build + Commit**

```bash
dotnet build SteamManager.sln
git add src/SteamManager.Web/Components/AchievementList.razor \
        src/SteamManager.Web/Pages/Dashboard.razor
git commit -m "feat: Dashboard with real-time progress, achievement countdown, achievement list"
```

---

### Task 16: Dockerfile + docker-compose

**Files:**
- Create: `Dockerfile`
- Create: `docker-compose.yml`

- [ ] **Step 1: Dockerfile**

`Dockerfile`:
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SteamManager.Web/SteamManager.Web.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/logs
VOLUME ["/app/logs"]
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "SteamManager.Web.dll"]
```

- [ ] **Step 2: docker-compose.yml**

`docker-compose.yml`:
```yaml
services:
  steam-manager:
    image: steam-manager:latest
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "5000:5000"
    restart: unless-stopped
    env_file:
      - .env
    environment:
      - TZ=Asia/Shanghai
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5000
      - ConnectionStrings__Default=Server=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};User=${DB_USER};Password=${DB_PASSWORD};Convert Zero Datetime=True;AllowPublicKeyRetrieval=True;ConnectionTimeout=30;
    volumes:
      - ./logs:/app/logs
```

- [ ] **Step 3: Fill in .env values**

Before building, ensure `.env` has `SESSION_ENCRYPTION_KEY` and `UI_ACCESS_PASSWORD` filled in:

```bash
# Edit .env and set:
# SESSION_ENCRYPTION_KEY=<at least 32 random chars>
# UI_ACCESS_PASSWORD=<your chosen password>
```

- [ ] **Step 4: Build Docker image**

```bash
docker build -t steam-manager:latest .
```
Expected: `Successfully built` and `Successfully tagged steam-manager:latest`.

- [ ] **Step 5: Test run**

```bash
docker run --rm --env-file .env \
  -e TZ=Asia/Shanghai \
  -e ASPNETCORE_URLS=http://+:5000 \
  -p 5000:5000 \
  steam-manager:latest
```
Expected: App starts, migrations applied, `http://localhost:5000` shows login page.

- [ ] **Step 6: Commit**

```bash
git add Dockerfile docker-compose.yml
git commit -m "feat: Dockerfile multi-stage build + docker-compose for NAS deployment"
```

---

## Self-Review

### Spec Coverage

| Requirement | Task |
|---|---|
| 自动挂机 | 7 |
| 智能成就解锁 (interval calc) | 8 |
| 智能成就解锁 (SteamKit2 unlock) | 10 |
| 断点续传 | 10 (GetWaitMinutes), 11 (RecoverAsync) |
| Web UI — Dashboard | 15 |
| Web UI — Games | 14 |
| Web UI — Settings + 时区 | 13 |
| UI 认证 | 12 |
| MySQL + EF Core | 2, 4 |
| AES-256 加密 | 3 |
| SteamKit2 断线重连 | 5 |
| 启动恢复流程 | 11 |
| Steam API 限速 (1s delay + 429) | 9 |
| SteamHunters 降级 | 9 (CalculateFallback) |
| SESSION_ENCRYPTION_KEY 必填 | 4 |
| 密码登录后清空 | 6 (PersistSessionAsync) |
| Docker NAS 部署 | 16 |
| Serilog | 4 (appsettings.json) |
| UTC 存储 + SET time_zone | 2, 4 |
| .env + .gitignore | Pre-plan ✅ |

### Type Consistency

- `AchievementIntervalDto(string AchievementId, int OffsetMinutes)` — Tasks 8, 9, 10, 15 ✅
- `IGameIdleService.ProgressUpdated: Action<int, int>` — Tasks 7, 14, 15 ✅
- `IUnlockSchedulerService.AchievementUnlocked: Action<int, string>` — Tasks 10, 15 ✅
- `UnlockSchedulerService.GetWaitMinutes(int, int)` static — Tasks 10, 15 ✅
- `LoginState` enum — Tasks 6, 13, 14, 15 ✅
