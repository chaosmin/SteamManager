using SteamKit2;
using SteamKit2.Internal;

namespace SteamManager.Infrastructure.Steam;

/// <summary>
/// Custom ClientMsgHandler that intercepts GetUserStats and StoreUserStats responses.
/// Allows async waiting for responses keyed by SteamKit2 job ID.
/// </summary>
internal sealed class UserStatsHandler : ClientMsgHandler
{
    private readonly Dictionary<JobID, TaskCompletionSource<CMsgClientGetUserStatsResponse>> _pending = [];
    public event Action<ulong, CMsgClientStoreUserStatsResponse>? StoreResponseReceived;

    internal Task<CMsgClientGetUserStatsResponse> ExpectGetResponse(JobID jobId)
    {
        var tcs = new TaskCompletionSource<CMsgClientGetUserStatsResponse>();
        _pending[jobId] = tcs;
        return tcs.Task;
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType == EMsg.ClientGetUserStatsResponse)
        {
            var msg = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);
            if (_pending.TryGetValue(msg.TargetJobID, out var tcs))
            {
                _pending.Remove(msg.TargetJobID);
                tcs.TrySetResult(msg.Body);
            }
        }
        else if (packetMsg.MsgType == EMsg.ClientStoreUserStatsResponse)
        {
            var msg = new ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>(packetMsg);
            StoreResponseReceived?.Invoke(msg.Body.game_id, msg.Body);
        }
    }
}
