using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Clan;

// Only optional group credit uses these connection-bound messages. Clan storage and
// the external mods' native RPCs remain unchanged.
internal static class ClanGroupSharing
{
    private const string RequestRpc = "Clan_GroupCreditRequest_v1";
    private const string ResponseRpc = "Clan_GroupCreditResponse_v1";
    private const byte EpicExperience = 1;
    private const byte QuestKill = 2;
    private const int MaximumPackageBytes = 512;
    private static readonly Dictionary<ZRpc, PeerState> Peers = new();
    private static readonly PeerState Host = new();
    private static readonly DeathCredits SharedDeaths = new();
    private static readonly DeathCredits ReceivedDeaths = new();
    private static long _outgoingSequence;
    private static long _deliverySequence;
    private static long _lastDeliverySequence;

    internal static void RegisterPeer(ZNet net, ZNetPeer peer)
    {
        if (net.IsServer())
        {
            Peers[peer.m_rpc] = new PeerState();
            peer.m_rpc.Register<ZPackage>(RequestRpc,
                (Action<ZRpc, ZPackage>)((rpc, package) => ReceiveRequest(peer, rpc, package)));
        }
        else
        {
            peer.m_rpc.Register<ZPackage>(ResponseRpc, (Action<ZRpc, ZPackage>)ReceiveResponse);
        }
    }

    internal static void ForgetPeer(ZRpc? rpc)
    {
        if (rpc != null) Peers.Remove(rpc);
    }

    internal static void ResetSession()
    {
        Peers.Clear();
        Host.Reset();
        SharedDeaths.Clear();
        ReceivedDeaths.Clear();
        _outgoingSequence = _deliverySequence = _lastDeliverySequence = 0;
    }

    internal static void ShareEpicExperience(int experience, Vector3 position, int monsterLevel)
    {
        if (!EpicMmoCompat.IsReady || !ClanPlugin.ShareEpicMmoExperience.Value.IsOn() ||
            Player.m_localPlayer == null || experience <= 0 || !IsFinite(position)) return;

        ZPackage package = new();
        package.Write(EpicExperience);
        package.Write(++_outgoingSequence);
        package.Write(experience);
        package.Write(position);
        package.Write(monsterLevel);
        SendRequest(package);
    }

    private static void SendRequest(ZPackage package)
    {
        if (ZNet.instance == null) return;
        if (ZNet.instance.IsServer())
        {
            package.SetPos(0);
            ReceiveRequest(null, null, package);
        }
        else
        {
            ZNet.instance.GetServerPeer()?.m_rpc.Invoke(RequestRpc, package);
        }
    }

    internal static void ShareQuestKill(ZDOID victim, ZDOID killer, string prefab, Vector3 position)
    {
        if (!QuestForgeCompat.IsReady || !ClanPlugin.ShareQuestForgeKills.Value.IsOn() ||
            victim.IsNone() || killer.IsNone() || prefab.Length == 0 || prefab.Length > 128 || !IsFinite(position)) return;
        ZPackage package = new();
        package.Write(QuestKill);
        package.Write(++_outgoingSequence);
        package.Write(victim);
        package.Write(killer);
        package.Write(prefab);
        package.Write(position);
        SendRequest(package);
    }

    private static void ReceiveRequest(ZNetPeer? peer, ZRpc? rpc, ZPackage package)
    {
        if (ZNet.instance?.IsServer() != true || package.Size() > MaximumPackageBytes) return;
        PeerState state;
        if (peer == null)
        {
            if (rpc != null) return;
            state = Host;
        }
        else
        {
            if (rpc != peer.m_rpc || !peer.IsReady() ||
                !ZNet.instance.GetConnectedPeers().Contains(peer)) return;
            if (!Peers.TryGetValue(rpc, out var existing)) Peers[rpc] = state = new PeerState();
            else state = existing;
        }

        try
        {
            byte kind = package.ReadByte();
            long sequence = package.ReadLong();
            if (!state.Accept(sequence, Time.realtimeSinceStartup)) return;
            if (kind == EpicExperience)
            {
                int experience = package.ReadInt();
                Vector3 position = package.ReadVector3();
                int level = package.ReadInt();
                RequireEnd(package);
                if (!ClanPlugin.ShareEpicMmoExperience.Value.IsOn() || !EpicMmoCompat.IsReady ||
                    experience <= 0 || !IsFinite(position)) return;
                ClanPlayerRef killer = Identity(peer);
                string clan = EffectiveClan(killer);
                if (clan.Length == 0) return;
                long killerPeer = peer?.m_uid ?? ZNet.GetUID();
                DeliverToClan(killer, clan, (target, characterId) =>
                {
                    ZPackage response = CreateResponse(EpicExperience, characterId);
                    response.Write(killerPeer);
                    response.Write(experience);
                    response.Write(position);
                    response.Write(level);
                    SendResponse(target, response);
                });
            }
            else if (kind == QuestKill)
            {
                ZDOID victim = package.ReadZDOID();
                ZDOID killerId = package.ReadZDOID();
                string prefab = package.ReadString();
                Vector3 position = package.ReadVector3();
                RequireEnd(package);
                if (!QuestForgeCompat.IsReady || !ClanPlugin.ShareQuestForgeKills.Value.IsOn() ||
                    victim.IsNone() || prefab.Length == 0 || prefab.Length > 128 || !IsFinite(position)) return;
                ZDO? zdo = ZDOMan.instance?.GetZDO(victim);
                if (zdo == null || zdo.GetOwner() != (peer?.m_uid ?? ZNet.GetUID()) ||
                    zdo.GetPrefab() != prefab.GetStableHashCode() ||
                    (position - zdo.GetPosition()).sqrMagnitude > 32f * 32f) return;
                GameObject? monster = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (monster == null || monster.GetComponent<Character>() == null ||
                    monster.GetComponent<Player>() != null) return;
                if (!TryFindKiller(killerId, out ClanPlayerRef killer)) return;
                string clan = EffectiveClan(killer);
                if (clan.Length == 0 || !SharedDeaths.Accept(victim, Time.realtimeSinceStartup)) return;
                DeliverToClan(killer, clan, (target, characterId) =>
                {
                    ZDO? character = ZDOMan.instance?.GetZDO(characterId);
                    if (character == null || !WithinQuestRange(character.GetPosition(), position)) return;
                    ZPackage response = CreateResponse(QuestKill, characterId);
                    response.Write(victim);
                    response.Write(prefab);
                    response.Write(position);
                    SendResponse(target, response);
                });
            }
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning($"Rejected group credit request: {exception.Message}");
        }
    }

    private static bool TryFindKiller(ZDOID characterId, out ClanPlayerRef killer)
    {
        killer = default;
        if (characterId.IsNone()) return false;
        if (Player.m_localPlayer != null && Player.m_localPlayer.GetZDOID() == characterId)
            killer = Identity(null);
        else
        {
            foreach (ZNetPeer candidate in ZNet.instance.GetConnectedPeers())
                if (candidate.IsReady() && candidate.m_characterID == characterId)
                {
                    killer = Identity(candidate);
                    break;
                }
        }
        return killer.IsValid;
    }

    private static void DeliverToClan(ClanPlayerRef killer, string clan, Action<ZNetPeer?, ZDOID> deliver)
    {
        void Consider(ZNetPeer? peer, ZDOID characterId)
        {
            ClanPlayerRef recipient = Identity(peer);
            if (!characterId.IsNone() && recipient.IsValid && recipient != killer &&
                StringComparer.Ordinal.Equals(EffectiveClan(recipient), clan))
                deliver(peer, characterId);
        }

        if (Player.m_localPlayer != null) Consider(null, Player.m_localPlayer.GetZDOID());
        foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
        {
            if (peer.IsReady()) Consider(peer, peer.m_characterID);
        }
    }

    private static ClanPlayerRef Identity(ZNetPeer? peer)
    {
        // A pinned account/character must still match this connection's live character.
        ClanPlayerRef current = ClanIdentity.FromPeer(peer);
        return ClanRpc.TryGetPinnedPeerIdentity(peer, out ClanPlayerRef pinned) && pinned != current
            ? default : current;
    }

    private static string EffectiveClan(ClanPlayerRef player)
    {
        if (!player.IsValid || ClanApi.ResolveMemberships(player.PlatformId, player.CharacterPlayerId,
                out string primary, out _, out string guest, out _) != ClanMembershipResolution.Resolved)
            return "";
        return guest.Length > 0 ? guest : primary;
    }

    private static ZPackage CreateResponse(byte kind, ZDOID characterId)
    {
        ZPackage package = new();
        package.Write(kind);
        package.Write(++_deliverySequence);
        package.Write(characterId);
        return package;
    }

    private static void SendResponse(ZNetPeer? peer, ZPackage package)
    {
        if (peer != null) peer.m_rpc.Invoke(ResponseRpc, package);
        else
        {
            package.SetPos(0);
            ReceiveResponse(null, package);
        }
    }

    private static void ReceiveResponse(ZRpc? rpc, ZPackage package)
    {
        ZNet? net = ZNet.instance;
        if (net == null || package.Size() > MaximumPackageBytes ||
            (net.IsServer() ? rpc != null : rpc == null || rpc != net.GetServerPeer()?.m_rpc)) return;
        try
        {
            byte kind = package.ReadByte();
            long sequence = package.ReadLong();
            ZDOID characterId = package.ReadZDOID();
            if (sequence <= _lastDeliverySequence) return;
            _lastDeliverySequence = sequence;
            if (Player.m_localPlayer == null || Player.m_localPlayer.GetZDOID() != characterId) return;
            if (kind == EpicExperience)
            {
                long killer = package.ReadLong();
                int experience = package.ReadInt();
                Vector3 position = package.ReadVector3();
                int level = package.ReadInt();
                RequireEnd(package);
                if (ClanPlugin.ShareEpicMmoExperience.Value.IsOn() && experience > 0 && IsFinite(position))
                    EpicMmoCompat.ReceiveExperience(killer, experience, position, level);
            }
            else if (kind == QuestKill)
            {
                ZDOID victim = package.ReadZDOID();
                string prefab = package.ReadString();
                Vector3 position = package.ReadVector3();
                RequireEnd(package);
                if (QuestForgeCompat.IsReady && ClanPlugin.ShareQuestForgeKills.Value.IsOn() &&
                    !victim.IsNone() && prefab.Length > 0 && prefab.Length <= 128 &&
                    !Player.m_localPlayer.IsDead() && WithinQuestRange(Player.m_localPlayer.transform.position, position) &&
                    ReceivedDeaths.Accept(victim, Time.realtimeSinceStartup))
                    QuestForgeCompat.ReceiveKill(prefab);
            }
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning($"Could not apply group credit: {exception.Message}");
        }
    }

    private static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
        !float.IsNaN(v.z) && !float.IsInfinity(v.z);

    private static bool WithinQuestRange(Vector3 player, Vector3 death)
    {
        float range = ClanPlugin.QuestForgeShareRange.Value;
        return IsFinite(player) && IsFinite(death) && range > 0f && !float.IsInfinity(range) &&
               (player - death).sqrMagnitude <= range * range;
    }

    private static void RequireEnd(ZPackage package)
    {
        if (package.GetPos() != package.Size()) throw new InvalidDataException("Unexpected credit payload.");
    }

    private sealed class PeerState
    {
        private long _lastSequence;
        private float _windowStart;
        private int _requests;

        internal bool Accept(long sequence, float now)
        {
            if (sequence <= _lastSequence) return false;
            _lastSequence = sequence;
            if (now < _windowStart || now - _windowStart >= 5f)
            {
                _windowStart = now;
                _requests = 0;
            }
            return ++_requests <= 256;
        }

        internal void Reset()
        {
            _lastSequence = 0;
            _windowStart = 0;
            _requests = 0;
        }
    }

    // Retain across an individual peer disconnect; clear only with the game session.
    // Never evict a fresh ID to admit a replay during an unusually large burst.
    private sealed class DeathCredits
    {
        private readonly HashSet<ZDOID> _seen = new();
        private readonly Queue<KeyValuePair<ZDOID, float>> _expiry = new();

        internal bool Accept(ZDOID id, float now)
        {
            while (_expiry.Count > 0 && (now < _expiry.Peek().Value || now - _expiry.Peek().Value >= 120f))
                _seen.Remove(_expiry.Dequeue().Key);
            if (_seen.Count >= 4096 || !_seen.Add(id)) return false;
            _expiry.Enqueue(new KeyValuePair<ZDOID, float>(id, now));
            return true;
        }

        internal void Clear()
        {
            _seen.Clear();
            _expiry.Clear();
        }
    }
}
