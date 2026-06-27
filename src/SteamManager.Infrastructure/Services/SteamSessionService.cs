using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamManager.Infrastructure.Crypto;
using SteamManager.Infrastructure.Persistence;
using SteamManager.Core.Services;
using SteamManager.Infrastructure.Steam;

namespace SteamManager.Infrastructure.Services;

public class SteamSessionService(
    SteamClientWrapper steam,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SteamSessionService> logger) : ISteamSessionService
{
    public LoginState State { get; private set; } = LoginState.NotLoggedIn;
    public string? DisplayName { get; private set; }
    public ulong? SteamId64 { get; private set; }
    public event Action? StateChanged;

    private string EncKey => config["SESSION_ENCRYPTION_KEY"]
        ?? Environment.GetEnvironmentVariable("SESSION_ENCRYPTION_KEY")!;

    private string? _pendingUsername;
    private PendingAuthenticator? _authenticator;
    private Task<AuthPollResult>? _pollTask;

    // ── IAuthenticator that pauses when a 2FA code is required ──────────────

    private sealed class PendingAuthenticator : IAuthenticator
    {
        public readonly TaskCompletionSource CodeNeeded = new();
        private TaskCompletionSource<string> _codeTcs = new();

        public void SubmitCode(string code) => _codeTcs.TrySetResult(code);

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            if (previousCodeWasIncorrect) _codeTcs = new();
            CodeNeeded.TrySetResult();
            return _codeTcs.Task;
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            if (previousCodeWasIncorrect) _codeTcs = new();
            CodeNeeded.TrySetResult();
            return _codeTcs.Task;
        }

        // Device-confirmation (phone pop-up without code) — not supported
        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);
    }

    // ── Public interface ─────────────────────────────────────────────────────

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
                SteamId64 = steam.Client.SteamID;
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

        _authenticator = new PendingAuthenticator();

        CredentialsAuthSession session;
        try
        {
            session = await steam.Client.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = _authenticator,
                });
        }
        catch (AuthenticationException ex)
        {
            logger.LogWarning("Steam auth failed: {Result}", ex.Result);
            SetState(LoginState.NotLoggedIn);
            return LoginState.NotLoggedIn;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Steam auth error");
            SetState(LoginState.NotLoggedIn);
            return LoginState.NotLoggedIn;
        }

        // Background polling — will call IAuthenticator if a code is needed
        _pollTask = session.PollingWaitForResultAsync(ct);

        // Race: poll completes immediately (no 2FA) vs authenticator needs a code
        var first = await Task.WhenAny(_pollTask, _authenticator.CodeNeeded.Task);

        if (first == _pollTask)
        {
            if (_pollTask.IsCompletedSuccessfully)
                return await FinishLoginAsync(_pollTask.Result, ct);

            var ex = _pollTask.Exception?.InnerException;
            logger.LogWarning(ex, "Auth polling failed");
            SetState(LoginState.NotLoggedIn);
            return LoginState.NotLoggedIn;
        }

        // Authenticator.CodeNeeded fired — waiting for user to supply the code
        SetState(LoginState.AwaitingTwoFactor);
        return LoginState.AwaitingTwoFactor;
    }

    public async Task<bool> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default)
    {
        if (_authenticator == null || _pollTask == null) return false;
        try
        {
            _authenticator.SubmitCode(code);
            var result = await _pollTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var state = await FinishLoginAsync(result, ct);
            return state == LoginState.LoggedIn;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "2FA submission failed");
            SetState(LoginState.NotLoggedIn);
            return false;
        }
    }

    private async Task<LoginState> FinishLoginAsync(AuthPollResult pollResult, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<LoginState>();
        var sub = steam.CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
            tcs.TrySetResult(cb.Result == EResult.OK ? LoginState.LoggedIn : LoginState.NotLoggedIn));

        steam.SteamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = _pendingUsername!,
            AccessToken = pollResult.RefreshToken,
            ShouldRememberPassword = true,
        });

        var state = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        sub.Dispose();

        if (state == LoginState.LoggedIn)
            await PersistSessionAsync(pollResult.RefreshToken, ct);

        SetState(state);
        return state;
    }

    private async Task PersistSessionAsync(string refreshToken, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cfg = await db.SteamConfigs.FirstOrDefaultAsync(ct)
            ?? new Core.Models.SteamConfig();

        cfg.Username = _pendingUsername!;
        cfg.PasswordEnc = null;
        cfg.SessionToken = AesEncryption.Encrypt(refreshToken, EncKey);
        cfg.SessionUpdatedAt = DateTime.UtcNow;

        if (cfg.Id == 0) db.SteamConfigs.Add(cfg);
        await db.SaveChangesAsync(ct);

        DisplayName = steam.SteamFriends.GetPersonaName();
        SteamId64 = steam.Client.SteamID;
    }

    public async Task LogoutAsync()
    {
        steam.SteamUser.LogOff();
        SetState(LoginState.NotLoggedIn);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cfg = await db.SteamConfigs.FirstOrDefaultAsync();
        if (cfg != null) { cfg.SessionToken = null; await db.SaveChangesAsync(); }
    }

    private void SetState(LoginState s) { State = s; StateChanged?.Invoke(); }
}
