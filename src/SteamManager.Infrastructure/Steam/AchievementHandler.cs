using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Internal;
using SteamManager.Core.Services;

namespace SteamManager.Infrastructure.Steam;

public class AchievementHandler
{
    private readonly SteamClientWrapper _steam;
    private readonly ILogger<AchievementHandler> _logger;
    private readonly ISteamAuditService _audit;

    public AchievementHandler(SteamClientWrapper steam, ILogger<AchievementHandler> logger, ISteamAuditService audit)
    {
        _steam = steam;
        _logger = logger;
        _audit = audit;
    }

    public async Task<bool> UnlockAchievementAsync(int appId, string achievementId, CancellationToken ct = default)
    {
        try
        {
            // Step 1: Fetch current stats + schema. Steam validates crc_stats on the store request.
            var statsResponse = await _steam.GetUserStatsAsync((uint)appId, ct);
            if (statsResponse.eresult != 1)
            {
                _logger.LogWarning("GetUserStats failed for app {AppId}: eresult={EResult}", appId, statsResponse.eresult);
                return false;
            }

            // Step 2: Parse the binary KeyValue schema to find which stat block and bit position
            // correspond to this achievement. Achievements are bit-fields inside type=4 stats.
            if (!FindAchievementBit(achievementId, statsResponse, out var statId, out var bitNum, out var currentValue))
            {
                _logger.LogWarning("Achievement {AchId} not found in schema for app {AppId} (schema bytes={Bytes})",
                    achievementId, appId, statsResponse.schema?.Length ?? 0);
                return false;
            }

            // Step 3: If bit already set, clear it first so Steam records a new unlock timestamp.
            // Needed when re-unlocking an achievement that was locked with SAM or similar tools.
            if ((currentValue & ((uint)1 << bitNum)) != 0)
            {
                _logger.LogInformation(
                    "Achievement {AchId} app {AppId}: bit already set — clearing first to refresh timestamp",
                    achievementId, appId);

                var clearValue = currentValue & ~((uint)1 << bitNum);
                var clearReq = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
                clearReq.Body.game_id         = (ulong)appId;
                clearReq.Body.settor_steam_id = _steam.Client.SteamID;
                clearReq.Body.settee_steam_id = _steam.Client.SteamID;
                clearReq.Body.explicit_reset  = false;
                clearReq.Body.crc_stats       = statsResponse.crc_stats;
                clearReq.Body.stats.Add(new CMsgClientStoreUserStats2.Stats
                    { stat_id = statId, stat_value = clearValue });

                var (clearOk, clearEresult) = await _steam.StoreUserStatsAsync(clearReq, ct);
                if (!clearOk)
                {
                    _logger.LogWarning("Clear failed for {AchId} app {AppId}: eresult={E}",
                        achievementId, appId, clearEresult);
                    return false;
                }

                // Re-fetch to get updated crc_stats after the clear.
                await Task.Delay(1000, ct);
                statsResponse = await _steam.GetUserStatsAsync((uint)appId, ct);
                if (statsResponse.eresult != 1)
                {
                    _logger.LogWarning("Re-fetch GetUserStats failed after clear for app {AppId}: eresult={E}",
                        appId, statsResponse.eresult);
                    return false;
                }
                if (!FindAchievementBit(achievementId, statsResponse, out statId, out bitNum, out currentValue))
                {
                    _logger.LogWarning("Achievement {AchId} not found in schema after clear for app {AppId}",
                        achievementId, appId);
                    return false;
                }
            }

            // Step 4: Set the bit (preserve other bits in the same stat block).
            var newValue = currentValue | ((uint)1 << bitNum);

            var storeRequest = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
            storeRequest.Body.game_id = (ulong)appId;
            storeRequest.Body.settor_steam_id = _steam.Client.SteamID;
            storeRequest.Body.settee_steam_id = _steam.Client.SteamID;
            storeRequest.Body.explicit_reset = false;
            storeRequest.Body.crc_stats = statsResponse.crc_stats;
            storeRequest.Body.stats.Add(new CMsgClientStoreUserStats2.Stats
            {
                stat_id    = statId,
                stat_value = newValue,
            });

            // Step 5: Send and await Steam's acknowledgement.
            var sw = Stopwatch.StartNew();
            var (storeOk, eresult) = await _steam.StoreUserStatsAsync(storeRequest, ct);
            sw.Stop();

            _logger.LogInformation(
                "StoreUserStats for {AchId} (stat_id={StatId} bit={Bit} old={Old} new={New} crc={Crc}) app {AppId}: eresult={EResult}",
                achievementId, statId, bitNum, currentValue, newValue, statsResponse.crc_stats, appId, eresult);

            _ = _audit.LogAsync("SteamKit2", "StoreUserStats", appId,
                $"achievement={achievementId} stat_id={statId} bit={bitNum} old={currentValue} new={newValue}",
                storeOk,
                $"eresult={eresult}",
                (int)sw.ElapsedMilliseconds);

            return storeOk;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unlock achievement {AchId} in app {AppId}", achievementId, appId);
            return false;
        }
    }

    public async Task<bool> LockAchievementAsync(int appId, string achievementId, CancellationToken ct = default)
    {
        try
        {
            var statsResponse = await _steam.GetUserStatsAsync((uint)appId, ct);
            if (statsResponse.eresult != 1)
            {
                _logger.LogWarning("GetUserStats failed for app {AppId}: eresult={E}", appId, statsResponse.eresult);
                return false;
            }

            if (!FindAchievementBit(achievementId, statsResponse, out var statId, out var bitNum, out var currentValue))
                return false;

            // Already locked — nothing to do
            if ((currentValue & ((uint)1 << bitNum)) == 0) return true;

            var newValue = currentValue & ~((uint)1 << bitNum);
            var request = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
            request.Body.game_id         = (ulong)appId;
            request.Body.settor_steam_id = _steam.Client.SteamID;
            request.Body.settee_steam_id = _steam.Client.SteamID;
            request.Body.explicit_reset  = false;
            request.Body.crc_stats       = statsResponse.crc_stats;
            request.Body.stats.Add(new CMsgClientStoreUserStats2.Stats { stat_id = statId, stat_value = newValue });

            var sw = Stopwatch.StartNew();
            var (ok, eresult) = await _steam.StoreUserStatsAsync(request, ct);
            sw.Stop();

            _ = _audit.LogAsync("SteamKit2", "LockAchievement", appId,
                $"achievement={achievementId} stat_id={statId} bit={bitNum} old={currentValue} new={newValue}",
                ok, $"eresult={eresult}", (int)sw.ElapsedMilliseconds);

            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lock achievement {AchId} in app {AppId}", achievementId, appId);
            return false;
        }
    }

    /// <summary>
    /// Parses the binary KeyValue schema returned by GetUserStats to find the parent stat block
    /// (type=4 / ACHIEVEMENTS) and bit position for the given achievement API name.
    /// </summary>
    private bool FindAchievementBit(
        string achievementId,
        CMsgClientGetUserStatsResponse statsResponse,
        out uint statId, out int bitNum, out uint currentValue)
    {
        statId = 0; bitNum = 0; currentValue = 0;

        var schemaBytes = statsResponse.schema;
        if (schemaBytes == null || schemaBytes.Length == 0)
        {
            _logger.LogWarning("Empty schema returned by GetUserStats");
            return false;
        }

        var schema = new KeyValue();
        using var ms = new MemoryStream(schemaBytes);
        if (!schema.TryReadAsBinary(ms))
        {
            _logger.LogWarning("Failed to parse schema binary KeyValue");
            return false;
        }

        // Schema root → "stats" node → numbered children (each is a stat block).
        var statsNode = schema["stats"];
        var statChildren = statsNode == KeyValue.Invalid ? schema.Children : statsNode.Children;

        foreach (var stat in statChildren)
        {
            if (!uint.TryParse(stat.Name, out var statNum)) continue;

            var typeVal = stat["type"].Value ?? string.Empty;
            if (typeVal != "4" && !typeVal.Equals("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase)) continue;

            var bitsNode = stat["bits"];
            if (bitsNode == KeyValue.Invalid) continue;

            foreach (var bit in bitsNode.Children)
            {
                if (!int.TryParse(bit.Name, out var bn)) continue;
                var name = bit["name"].Value ?? string.Empty;
                if (!string.Equals(name, achievementId, StringComparison.OrdinalIgnoreCase)) continue;

                statId = statNum;
                bitNum = bn;
                var found = statsResponse.stats.Find(s => s.stat_id == statNum);
                currentValue = found?.stat_value ?? 0;
                return true;
            }
        }
        return false;
    }
}
