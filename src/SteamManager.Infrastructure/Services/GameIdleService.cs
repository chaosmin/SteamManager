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

public class GameIdleService(
    SteamClientWrapper steam,
    IServiceScopeFactory scopeFactory,
    ILogger<GameIdleService> logger) : IGameIdleService
{
    private readonly Dictionary<int, CancellationTokenSource> _running = [];
    public event Action<int, int>? ProgressUpdated;

    public bool IsRunning(int appId) => _running.ContainsKey(appId);

    public async Task StartAsync(int appId, CancellationToken ct = default)
    {
        if (_running.ContainsKey(appId)) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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

        var targetMinutes = (int)(game.TargetHours * 60);

        SendGamesPlayed(appId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[appId] = cts;
        _ = RunIdleLoopAsync(appId, targetMinutes, cts.Token);
    }

    public async Task StopAsync(int appId)
    {
        if (!_running.TryGetValue(appId, out var cts)) return;
        await cts.CancelAsync();
        _running.Remove(appId);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var game = await db.GameConfigs.Include(g => g.Progress)
            .FirstOrDefaultAsync(g => g.AppId == appId);
        if (game != null)
        {
            game.Status = GameStatus.Idle;
            await db.SaveChangesAsync();
        }
        SendGamesPlayed(0);
    }

    private async Task RunIdleLoopAsync(int appId, int targetMinutes, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
                    SendGamesPlayed(0);
                    _running.Remove(appId);
                    logger.LogInformation("Game {AppId}: target reached, idle complete", appId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Idle loop error for {AppId}", appId); }
    }

    private void SendGamesPlayed(int appId)
    {
        var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        if (appId != 0)
            msg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = (ulong)appId });
        steam.Client.Send(msg);
    }
}
