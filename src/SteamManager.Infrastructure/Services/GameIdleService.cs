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
    public event Action<int, int>? ProgressUpdated;

    public GameIdleService(SteamClientWrapper steam, IServiceScopeFactory scopeFactory, ILogger<GameIdleService> logger)
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

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId, ct)
            ?? throw new InvalidOperationException($"Game {appId} not found");

        var wasCompleted = game.Status == GameStatus.Completed;
        game.Status = GameStatus.Running;
        game.LastSessionStart = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // If manually restarted after completing, idle indefinitely (user chose to keep going)
        var targetMinutes = wasCompleted
            ? int.MaxValue
            : Math.Max((int)(game.TargetHours * 60), game.ReferencePlayMinutes ?? 0);

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

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var game = await db.Games.FirstOrDefaultAsync(g => g.AppId == appId);
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

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var game = await db.Games.FirstAsync(g => g.AppId == appId, ct);
                game.TotalPlayMinutes++;
                await db.SaveChangesAsync(ct);
                ProgressUpdated?.Invoke(appId, game.TotalPlayMinutes);

                if (game.TotalPlayMinutes >= targetMinutes)
                {
                    var allUnlocked = !await db.Achievements
                        .AnyAsync(a => a.GameId == game.Id && !a.IsUnlocked, ct);
                    if (allUnlocked)
                    {
                        game.Status = GameStatus.Completed;
                        await db.SaveChangesAsync(ct);
                        SendGamesPlayed(0);
                        _running.Remove(appId);
                        _logger.LogInformation("Game {AppId}: all achievements unlocked, idle complete", appId);
                        return;
                    }
                }
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
