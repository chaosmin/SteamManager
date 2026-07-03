using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamManager.Core.Services;

namespace SteamManager.Infrastructure.Steam;

public class SteamClientWrapper : IDisposable
{
    public SteamClient Client { get; } = new();
    public SteamUser SteamUser { get; }
    public SteamFriends SteamFriends { get; }
    public CallbackManager CallbackManager { get; }

    private readonly UserStatsHandler _userStatsHandler = new();

    public bool IsConnected => Client.IsConnected;
    public bool IsLoggedOn { get; private set; }

    public event Action? OnConnected;
    public event Action<bool>? OnDisconnected;
    public event Action? OnLoggedOn;
    public event Action<string>? OnLoggedOff;

    private readonly ILogger<SteamClientWrapper> _logger;
    private readonly ISteamAuditService _audit;
    private CancellationTokenSource _cts = new();
    private Task? _callbackLoop;

    public SteamClientWrapper(ILogger<SteamClientWrapper> logger, ISteamAuditService audit)
    {
        _logger = logger;
        _audit = audit;
        SteamUser = Client.GetHandler<SteamUser>()!;
        SteamFriends = Client.GetHandler<SteamFriends>()!;
        CallbackManager = new CallbackManager(Client);
        Client.AddHandler(_userStatsHandler);
        _userStatsHandler.StoreResponseReceived += (gameId, resp) =>
            _logger.LogInformation("StoreUserStats response: game={GameId} eresult={EResult}",
                gameId, (EResult)resp.eresult);

        CallbackManager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _logger.LogInformation("Steam: connected");
            OnConnected?.Invoke();
        });

        CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            IsLoggedOn = false;
            _logger.LogWarning("Steam: disconnected (user-initiated={U})", cb.UserInitiated);
            OnDisconnected?.Invoke(cb.UserInitiated);
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
        _cts.Cancel();
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

    public async Task<CMsgClientGetUserStatsResponse> GetUserStatsAsync(uint appId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats);
        request.SourceJobID = Client.GetNextJobID();
        request.Body.game_id = appId;
        request.Body.steam_id_for_user = Client.SteamID;

        var task = _userStatsHandler.ExpectGetResponse(request.SourceJobID);
        Client.Send(request);
        var response = await task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        sw.Stop();

        _ = _audit.LogAsync("SteamKit2", "GetUserStats", (int)appId,
            $"appId={appId}",
            response.eresult == 1,
            $"eresult={response.eresult} stats={response.stats.Count}",
            (int)sw.ElapsedMilliseconds);

        return response;
    }

    /// <summary>
    /// Sends a StoreUserStats2 request and awaits Steam's acknowledgement response.
    /// Returns true if Steam accepted the store (eresult == OK).
    /// The waiter must be registered before Client.Send() — this method handles ordering internally.
    /// </summary>
    public async Task<(bool Ok, int EResult)> StoreUserStatsAsync(
        ClientMsgProtobuf<CMsgClientStoreUserStats2> request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Register BEFORE send to avoid missing the response callback.
        var task = _userStatsHandler.ExpectStoreResponse(request.Body.game_id);
        Client.Send(request);
        var response = await task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        sw.Stop();
        return (response.eresult == (int)EResult.OK, response.eresult);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _callbackLoop?.Wait(2000);
        Client.Disconnect();
    }
}
