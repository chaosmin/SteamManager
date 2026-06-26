using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamManager.Infrastructure.Crypto;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Infrastructure.Services;

public class SteamSessionService(
    SteamClientWrapper steam,
    AppDbContext db,
    IConfiguration config,
    ILogger<SteamSessionService> logger) : ISteamSessionService
{
    public LoginState State { get; private set; } = LoginState.NotLoggedIn;
    public string? DisplayName { get; private set; }
    public event Action? StateChanged;

    private string EncKey => config["SESSION_ENCRYPTION_KEY"]
        ?? Environment.GetEnvironmentVariable("SESSION_ENCRYPTION_KEY")!;
    private string? _pendingUsername;

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct);
        if (cfg?.SessionToken == null) return false;
        try
        {
            var token = AesEncryption.Decrypt(cfg.SessionToken, EncKey);
            await steam.ConnectWithReconnectAsync(ct);

            var tcs = new TaskCompletionSource<bool>();
            steam.OnLoggedOn += () => tcs.TrySetResult(true);
            steam.OnLoggedOff += _ => tcs.TrySetResult(false);

            steam.SteamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = cfg.Username,
                AccessToken = token,
                ShouldRememberPassword = true,
            });

            if (await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct))
            {
                DisplayName = steam.SteamFriends.GetPersonaName();
                SetState(LoginState.LoggedIn);
                return true;
            }
            SetState(LoginState.SessionExpired);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session restore failed");
            return false;
        }
    }

    public async Task<LoginState> BeginLoginAsync(string username, string password, CancellationToken ct = default)
    {
        _pendingUsername = username;
        await steam.ConnectWithReconnectAsync(ct);

        var tcs = new TaskCompletionSource<LoginState>();
        steam.CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            if (cb.Result is EResult.AccountLoginDeniedNeedTwoFactor or EResult.TwoFactorCodeMismatch)
                tcs.TrySetResult(LoginState.AwaitingTwoFactor);
            else if (cb.Result == EResult.OK)
                tcs.TrySetResult(LoginState.LoggedIn);
            else
                tcs.TrySetResult(LoginState.NotLoggedIn);
        });

        steam.SteamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = username,
            Password = password,
            ShouldRememberPassword = true,
        });

        var state = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (state == LoginState.LoggedIn) await PersistSessionAsync(ct);
        SetState(state);
        return state;
    }

    public async Task<bool> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        steam.OnLoggedOn += () => tcs.TrySetResult(true);
        steam.OnLoggedOff += _ => tcs.TrySetResult(false);

        steam.SteamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = _pendingUsername!,
            TwoFactorCode = code,
            ShouldRememberPassword = true,
        });

        var ok = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (ok) await PersistSessionAsync(ct);
        return ok;
    }

    private async Task PersistSessionAsync(CancellationToken ct)
    {
        // SteamKit2 3.x: capture session token via SessionTokenCallback
        string? sessionToken = null;
        var keySub = steam.CallbackManager.Subscribe<SteamUser.SessionTokenCallback>(cb =>
        {
            sessionToken = cb.SessionToken.ToString();
        });
        // Give SteamKit2 up to 5s to deliver the session token
        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        keySub.Dispose();

        if (sessionToken == null)
        {
            logger.LogWarning("No session token received; session will not be persisted");
            return;
        }

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? new Core.Models.SteamConfig();

        cfg.Username = _pendingUsername!;
        cfg.PasswordEnc = null;
        cfg.SessionToken = AesEncryption.Encrypt(sessionToken, EncKey);
        cfg.SessionUpdatedAt = DateTime.UtcNow;

        if (cfg.Id == 0) db.SteamConfigs.Add(cfg);
        await db.SaveChangesAsync(ct);

        DisplayName = steam.SteamFriends.GetPersonaName();
        SetState(LoginState.LoggedIn);
    }

    public async Task LogoutAsync()
    {
        steam.SteamUser.LogOff();
        SetState(LoginState.NotLoggedIn);
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync();
        if (cfg != null) { cfg.SessionToken = null; await db.SaveChangesAsync(); }
    }

    private void SetState(LoginState s) { State = s; StateChanged?.Invoke(); }
}
