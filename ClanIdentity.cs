using System;
using System.Collections.Generic;
using System.Linq;
using Splatform;

namespace Clan;

internal static class ClanIdentity
{
    private static readonly Platform SteamPlatform = new("Steam");

    public static ClanPlayerRef FromPeer(ZNetPeer? peer)
    {
        return FromPeer(peer, out _);
    }

    public static ClanPlayerRef FromPeer(ZNetPeer? peer, out bool retryable)
    {
        retryable = true;
        if (peer == null)
        {
            ClanPlayerRef localPlayer = ClanPlayerRef.Local();
            retryable = !localPlayer.IsValid;
            return localPlayer;
        }

        if (!peer.IsReady() ||
            peer.m_characterID.IsNone() ||
            ZNet.instance == null)
        {
            return default;
        }
        if (peer.m_characterID.UserID != peer.m_uid)
        {
            retryable = false;
            return default;
        }

        string platformId = ResolvePeerPlatformId(peer);
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return default;
        }

        long characterPlayerId = ReadCharacterPlayerId(peer.m_characterID);
        ClanPlayerRef player = new(platformId, characterPlayerId, peer.m_playerName);
        retryable = !player.IsValid;
        return player;
    }

    public static ClanPlayerRef FromPlayer(Player player)
    {
        return player == null ? default : FromCharacterId(player.GetZDOID());
    }

    public static ClanPlayerRef FromPlayerInfo(ZNet.PlayerInfo info)
    {
        if (info.m_characterID.IsNone())
        {
            return default;
        }

        return new ClanPlayerRef(
            info.m_userInfo.m_id.ToString(),
            ReadCharacterPlayerId(info.m_characterID),
            info.m_name);
    }

    public static bool TryMatchRosterPlayer(
        ZNet.PlayerInfo info,
        IReadOnlyList<ClanPlayerSummary> roster,
        out ClanPlayerRef player)
    {
        player = default;
        ClanPlayerRef exact = FromPlayerInfo(info);
        if (exact.IsValid && roster.Any(member => member.Id == exact.Id))
        {
            player = exact;
            return true;
        }

        string platformId = ClanDataRules.NormalizePlatformId(info.m_userInfo.m_id.ToString());
        if (platformId.Length == 0)
        {
            return false;
        }

        ClanPlayerRef soleOnlineMatch = default;
        int onlineMatches = 0;
        foreach (ClanPlayerSummary member in roster)
        {
            if (!member.Player.IsValid ||
                !StringComparer.Ordinal.Equals(member.Player.PlatformId, platformId))
            {
                continue;
            }

            if (member.IsOnline)
            {
                onlineMatches++;
                soleOnlineMatch = member.Player;
            }
        }

        if (onlineMatches == 1)
        {
            player = soleOnlineMatch;
            return true;
        }
        return false;
    }

    public static ZNetPeer? FindPeer(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return null;
        }

        foreach (ZNetPeer peer in GetConnectedPeers())
        {
            ClanPlayerRef connected =
                ClanRpc.TryGetPinnedPeerIdentity(peer, out ClanPlayerRef pinnedPlayer)
                    ? pinnedPlayer
                    : FromPeer(peer);
            if (connected.IsValid && StringComparer.Ordinal.Equals(connected.Id, player.Id))
            {
                return peer;
            }
        }

        return null;
    }

    public static IEnumerable<ClanPlayerRef> GetOnlinePlayerRefs()
    {
        HashSet<string> yieldedPlayerIds = new(StringComparer.Ordinal);
        ClanPlayerRef localPlayer = Player.m_localPlayer != null
            ? ClanPlayerRef.Local()
            : default;
        if (localPlayer.IsValid && yieldedPlayerIds.Add(localPlayer.Id))
        {
            yield return localPlayer;
        }

        foreach (ZNetPeer peer in GetConnectedPeers())
        {
            ClanPlayerRef player =
                ClanRpc.TryGetPinnedPeerIdentity(peer, out ClanPlayerRef pinnedPlayer)
                    ? pinnedPlayer
                    : FromPeer(peer);
            if (player.IsValid && yieldedPlayerIds.Add(player.Id))
            {
                yield return player;
            }
        }
    }

    private static ClanPlayerRef FromCharacterId(ZDOID characterId)
    {
        if (characterId == ZDOID.None)
        {
            return default;
        }

        foreach (ZNet.PlayerInfo info in GetOnlinePlayers())
        {
            if (info.m_characterID == characterId)
            {
                return FromPlayerInfo(info);
            }
        }

        return default;
    }

    private static long ReadCharacterPlayerId(ZDOID characterId)
    {
        if (characterId.IsNone() || ZDOMan.instance == null)
        {
            return 0L;
        }

        ZDO zdo = ZDOMan.instance.GetZDO(characterId);
        return zdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
    }

    private static string ResolvePeerPlatformId(ZNetPeer peer)
    {
        foreach (ZNet.PlayerInfo info in GetOnlinePlayers())
        {
            if (info.m_characterID == peer.m_characterID)
            {
                string playerInfoId = info.m_userInfo.m_id.ToString();
                if (!string.IsNullOrWhiteSpace(playerInfoId))
                {
                    return playerInfoId;
                }
            }
        }

        string host = peer.m_socket?.GetHostName() ?? "";
        if (string.IsNullOrWhiteSpace(host) || ZNet.instance == null)
        {
            return "";
        }

        PlatformUserID platformId = ZNet.m_onlineBackend == OnlineBackendType.Steamworks
            ? new PlatformUserID(SteamPlatform, host)
            : new PlatformUserID(host);
        return platformId.ToString();
    }

    private static IEnumerable<ZNetPeer> GetConnectedPeers()
    {
        return ZNet.instance?.GetConnectedPeers() ?? Enumerable.Empty<ZNetPeer>();
    }

    internal static IEnumerable<ZNet.PlayerInfo> GetOnlinePlayers()
    {
        return ZNet.instance?.GetPlayerList() ?? Enumerable.Empty<ZNet.PlayerInfo>();
    }
}
