namespace SteamManager.Core.Services;

public interface ISyncService
{
    bool IsSyncing { get; }
    int SyncProgress { get; }           // 0–100
    string? SyncStatusMessage { get; }
    DateTime? NextRunUtc { get; }
    event Action? SyncStateChanged;
    Task TriggerAsync(CancellationToken ct = default);
}
