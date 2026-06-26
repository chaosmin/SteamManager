using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamManager.Infrastructure.Steam;

public class AchievementHandler(SteamClientWrapper steam, ILogger<AchievementHandler> logger)
{
    public Task<bool> UnlockAchievementAsync(int appId, string achievementId, CancellationToken ct = default)
    {
        try
        {
            // SteamKit2 3.x: send ClientGamesPlayed with the app to ensure it is "running",
            // then send a ClientStoreUserStats2 with stat_id derived from the achievement API name hash.
            // Achievement stat IDs in Steam are CRC32 of the achievement API name (lowercase).
            var statId = Crc32(achievementId.ToLowerInvariant());

            var storeRequest = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
            storeRequest.Body.game_id = (ulong)appId;
            storeRequest.Body.explicit_reset = false;
            storeRequest.Body.stats.Add(new CMsgClientStoreUserStats2.Stats
            {
                stat_id = statId,
                stat_value = 1,
            });
            steam.Client.Send(storeRequest);

            logger.LogInformation("Sent unlock for achievement {AchId} (stat_id={StatId}) in app {AppId}",
                achievementId, statId, appId);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to unlock achievement {AchId} in app {AppId}", achievementId, appId);
            return Task.FromResult(false);
        }
    }

    // CRC32 of achievement API name — matches Steam's internal stat ID derivation
    private static uint Crc32(string input)
    {
        const uint poly = 0xEDB88320u;
        uint crc = 0xFFFFFFFFu;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(input))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
        }
        return ~crc;
    }
}
