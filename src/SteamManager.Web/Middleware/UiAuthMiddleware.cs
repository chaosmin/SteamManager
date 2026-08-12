using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Web.Middleware;

public class UiAuthMiddleware(RequestDelegate next, IConfiguration config)
{
    public const string CookieName = "ui_auth";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        var password = config["UI_ACCESS_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("UI_ACCESS_PASSWORD");

        // No password configured — allow all (dev mode)
        if (string.IsNullOrWhiteSpace(password))
        {
            await next(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? "";
        if (IsPubliclyAccessible(path))
        {
            await next(ctx);
            return;
        }

        var cookie = ctx.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(cookie))
        {
            var cfg = await db.SteamConfigs.AsNoTracking().FirstOrDefaultAsync();
            if (cfg?.UiSessionTokenHash != null && cfg.UiSessionIssuedAt.HasValue
                && DateTime.UtcNow - cfg.UiSessionIssuedAt.Value < SessionLifetime
                && FixedTimeEquals(HashToken(cookie), cfg.UiSessionTokenHash))
            {
                await next(ctx);
                return;
            }
        }

        ctx.Response.Redirect("/login");
    }

    // Explicit allowlist of unauthenticated routes/assets, rather than "any path with a dot" —
    // an extension-based heuristic would also bypass auth for any future non-static route
    // that happens to contain a period (e.g. a future /export/data.json endpoint).
    private static readonly string[] PublicPrefixes =
    [
        "/login", "/_blazor", "/_framework/", "/_content/", "/api/login",
        "/app.css", "/favicon.png", "/SteamManager.Web.styles.css", "/bootstrap/",
    ];

    private static bool IsPubliclyAccessible(string path) =>
        PublicPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Generates a new random session token. The raw value goes in the cookie; only its hash is persisted.</summary>
    public static string GenerateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLower();

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
