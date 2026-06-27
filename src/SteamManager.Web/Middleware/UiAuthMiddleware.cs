using System.Security.Cryptography;
using System.Text;

namespace SteamManager.Web.Middleware;

public class UiAuthMiddleware(RequestDelegate next, IConfiguration config)
{
    private const string CookieName = "ui_auth";

    public async Task InvokeAsync(HttpContext ctx)
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
        if (path.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/login", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            await next(ctx);
            return;
        }

        var expected = Hash(password);
        var cookie = ctx.Request.Cookies[CookieName];

        if (cookie == expected)
        {
            await next(ctx);
            return;
        }

        ctx.Response.Redirect("/login");
    }

    public static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLower();
}
