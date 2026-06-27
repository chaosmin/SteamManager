using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SteamManager.Infrastructure.Persistence;

/// <summary>Design-time factory used by dotnet-ef migrations tooling.</summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Try to load from .env file (project root)
        var envFile = Path.Combine(FindProjectRoot(), ".env");
        if (File.Exists(envFile))
        {
            foreach (var line in File.ReadAllLines(envFile))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var idx = trimmed.IndexOf('=');
                if (idx < 0) continue;
                var key = trimmed[..idx].Trim();
                var val = trimmed[(idx + 1)..].Trim();
                Environment.SetEnvironmentVariable(key, val);
            }
        }

        var connStr = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
            ?? BuildConnStr();

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connStr, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;

        return new AppDbContext(opts);
    }

    private static string BuildConnStr()
    {
        var host = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("DB_PORT") ?? "3306";
        var name = Environment.GetEnvironmentVariable("DB_NAME") ?? "steam_manager";
        var user = Environment.GetEnvironmentVariable("DB_USER") ?? "root";
        var pass = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";
        return $"server={host};port={port};database={name};user={user};password={pass}";
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".env"))) return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
