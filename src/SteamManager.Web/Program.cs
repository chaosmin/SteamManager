using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Http;
using SteamManager.Infrastructure.Services;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Infrastructure.Steam;
using SteamManager.Web.Components;
using SteamManager.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Startup guard: SESSION_ENCRYPTION_KEY must be set
var encKey = builder.Configuration["SESSION_ENCRYPTION_KEY"]
    ?? Environment.GetEnvironmentVariable("SESSION_ENCRYPTION_KEY");
if (string.IsNullOrWhiteSpace(encKey))
    throw new InvalidOperationException(
        "SESSION_ENCRYPTION_KEY is required. Set it as an environment variable (min 32 chars).");

// Build connection string from env vars
var dbHost = builder.Configuration["DB_HOST"] ?? Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
var dbPort = builder.Configuration["DB_PORT"] ?? Environment.GetEnvironmentVariable("DB_PORT") ?? "3306";
var dbName = builder.Configuration["DB_NAME"] ?? Environment.GetEnvironmentVariable("DB_NAME") ?? "steam_manager";
var dbUser = builder.Configuration["DB_USER"] ?? Environment.GetEnvironmentVariable("DB_USER") ?? "steam_mgr";
var dbPass = builder.Configuration["DB_PASSWORD"] ?? Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";
var connStr = $"Server={dbHost};Port={dbPort};Database={dbName};User={dbUser};Password={dbPass};Convert Zero Datetime=True;AllowPublicKeyRetrieval=True;ConnectionTimeout=30;";

// Serilog
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// Database
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseMySql(connStr, new MySqlServerVersion(new Version(8, 0, 0)),
        mysql => mysql.EnableRetryOnFailure(3)));

// Audit service (singleton — uses IServiceScopeFactory internally for DB access)
builder.Services.AddSingleton<ISteamAuditService, SteamAuditService>();

// Steam + Core services
builder.Services.AddSingleton<SteamClientWrapper>();
builder.Services.AddSingleton<AchievementHandler>();
builder.Services.AddSingleton<AchievementUnlockNotifier>();
builder.Services.AddSingleton<ISteamSessionService, SteamSessionService>();
builder.Services.AddSingleton<IGameIdleService, GameIdleService>();
builder.Services.AddHostedService<UnlockSchedulerService>();
builder.Services.AddSingleton<IGameQueueService, GameQueueService>();
builder.Services.AddScoped<IGameRefreshService, GameRefreshService>();
builder.Services.AddScoped<StartupRecoveryService>();
builder.Services.AddSingleton<SyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());
builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<SyncBackgroundService>());

// HTTP clients
builder.Services.AddSingleton<PlaywrightBrowserService>();
builder.Services.AddHttpClient<SteamWebApiClient>();
builder.Services.AddHttpClient<SteamHuntersClient>(client =>
{
    // Browser-like headers required to bypass SteamHunters bot detection on HTML pages
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
    client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
    client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
    client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
});
builder.Services.AddHttpClient<SteamCommunityClient>();

// MudBlazor
builder.Services.AddMudServices();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Auto-migrate + force UTC session
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("SET time_zone = '+00:00'");

    // Ensure tables exist — guard against migration history/table state mismatch
    await db.Database.ExecuteSqlRawAsync(@"
        CREATE TABLE IF NOT EXISTS steam_audit_log (
            Id BIGINT NOT NULL AUTO_INCREMENT,
            Source VARCHAR(50) NOT NULL,
            Operation VARCHAR(100) NOT NULL,
            AppId INT NULL,
            RequestSummary VARCHAR(500) NULL,
            Success TINYINT(1) NOT NULL,
            ResponseSummary VARCHAR(1000) NULL,
            DurationMs INT NOT NULL,
            CreatedAt DATETIME(6) NOT NULL,
            PRIMARY KEY (Id),
            KEY IX_steam_audit_log_CreatedAt (CreatedAt),
            KEY IX_steam_audit_log_Source_Operation (Source, Operation)
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

    // Add new game columns (ignore if already exists)
    foreach (var ddl in new[]
    {
        "ALTER TABLE game ADD COLUMN steam_playtime_at_refresh INT NOT NULL DEFAULT 0",
        "ALTER TABLE game ADD COLUMN target_minutes INT NULL",
        "ALTER TABLE game ADD COLUMN session_started_at DATETIME(6) NULL",
        "ALTER TABLE achievement ADD COLUMN scheduled_unlock_at DATETIME(6) NULL",
    })
    {
        try { await db.Database.ExecuteSqlRawAsync(ddl); }
        catch (Exception) { /* column already exists — ignore */ }
    }
}

// Startup recovery — run in background so Kestrel starts immediately
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var recovery = scope.ServiceProvider.GetRequiredService<StartupRecoveryService>();
    await recovery.RecoverAsync();
});

app.UseMiddleware<UiAuthMiddleware>();
app.UseStaticFiles();
app.UseAntiforgery();

// Minimal API login endpoint — sets auth cookie and redirects
// (HttpContext is unavailable in Blazor 8 Interactive Server rendering)
app.MapPost("/api/login", (HttpContext ctx, IConfiguration cfg) =>
{
    var password = cfg["UI_ACCESS_PASSWORD"]
        ?? Environment.GetEnvironmentVariable("UI_ACCESS_PASSWORD") ?? "";
    var input = ctx.Request.Form["password"].ToString();
    var expected = SteamManager.Web.Middleware.UiAuthMiddleware.Hash(password);
    var inputHash = SteamManager.Web.Middleware.UiAuthMiddleware.Hash(input);
    if (inputHash == expected)
    {
        ctx.Response.Cookies.Append("ui_auth", expected,
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddDays(30), HttpOnly = true, SameSite = SameSiteMode.Lax });
        return Results.Redirect("/");
    }
    return Results.Redirect("/login?error=1");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
