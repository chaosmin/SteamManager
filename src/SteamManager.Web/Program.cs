using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using SteamManager.Core.Models;
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
// Simple in-memory per-IP throttle: 5 failed attempts locks that IP out for 5 minutes.
var loginAttempts = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime LockedUntil)>();
app.MapPost("/api/login", async (HttpContext ctx, IConfiguration cfg, AppDbContext db) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (loginAttempts.TryGetValue(ip, out var attempt) && attempt.LockedUntil > DateTime.UtcNow)
        return Results.Redirect("/login?error=locked");

    var password = cfg["UI_ACCESS_PASSWORD"]
        ?? Environment.GetEnvironmentVariable("UI_ACCESS_PASSWORD") ?? "";
    var input = ctx.Request.Form["password"].ToString();

    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(input), Encoding.UTF8.GetBytes(password)))
    {
        var count = attempt.Count + 1;
        var lockedUntil = count >= 5 ? DateTime.UtcNow.AddMinutes(5) : DateTime.MinValue;
        loginAttempts[ip] = (count, lockedUntil);
        return Results.Redirect("/login?error=1");
    }

    loginAttempts.TryRemove(ip, out _);

    // Issue a fresh random session token — invalidates any previously issued cookie immediately.
    var token = UiAuthMiddleware.GenerateToken();
    var sessionCfg = await db.SteamConfigs.FirstOrDefaultAsync() ?? new SteamConfig();
    sessionCfg.UiSessionTokenHash = UiAuthMiddleware.HashToken(token);
    sessionCfg.UiSessionIssuedAt = DateTime.UtcNow;
    if (sessionCfg.Id == 0) db.SteamConfigs.Add(sessionCfg);
    await db.SaveChangesAsync();

    ctx.Response.Cookies.Append(UiAuthMiddleware.CookieName, token,
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddDays(30), HttpOnly = true, SameSite = SameSiteMode.Lax });
    return Results.Redirect("/");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
