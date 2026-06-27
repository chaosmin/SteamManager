using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamManager.Core.Models;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Persistence;

namespace SteamManager.Infrastructure.Services;

public class StartupRecoveryService(
    ISteamSessionService session,
    IGameIdleService idle,
    IUnlockSchedulerService scheduler,
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

        var runningGames = await db.Games
            .Where(g => g.Status == GameStatus.Running)
            .ToListAsync(ct);

        foreach (var game in runningGames)
        {
            logger.LogInformation("Resuming game {AppId} ({Name})", game.AppId, game.Name);
            await idle.StartAsync(game.AppId, ct);
            await scheduler.StartAsync(game.AppId, ct);
        }

    }
}
