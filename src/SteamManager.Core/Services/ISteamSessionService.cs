namespace SteamManager.Core.Services;

public enum LoginState { NotLoggedIn, AwaitingTwoFactor, LoggedIn, SessionExpired }

public interface ISteamSessionService
{
    LoginState State { get; }
    string? DisplayName { get; }
    ulong? SteamId64 { get; }
    event Action? StateChanged;

    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);
    Task<LoginState> BeginLoginAsync(string username, string password, CancellationToken ct = default);
    Task<bool> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default);
    Task LogoutAsync();
}
