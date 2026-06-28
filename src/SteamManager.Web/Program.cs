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

// Steam + Core services
builder.Services.AddSingleton<SteamClientWrapper>();
builder.Services.AddSingleton<AchievementHandler>();
builder.Services.AddSingleton<AchievementUnlockNotifier>();
builder.Services.AddSingleton<ISteamSessionService, SteamSessionService>();
builder.Services.AddSingleton<IGameIdleService, GameIdleService>();
builder.Services.AddSingleton<IUnlockSchedulerService, UnlockSchedulerService>();
builder.Services.AddScoped<IAchievementDataService, AchievementDataService>();
builder.Services.AddScoped<StartupRecoveryService>();
builder.Services.AddSingleton<SyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());
builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<SyncBackgroundService>());

// HTTP clients
builder.Services.AddHttpClient<SteamWebApiClient>();
builder.Services.AddHttpClient<SteamHuntersClient>();
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
