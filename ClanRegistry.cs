using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YamlDotNet.Serialization;

namespace Clan;

internal static class ClanRegistry
{
    private const int SaveFormatVersion = 6;
    private const int MaximumSaveBytes = 64 * 1024 * 1024;
    private const float PositionUpdateInterval = 1f;
    private const float ClanChatInterval = 0.25f;
    private const float ClanPingInterval = 1f;
    private const float HudSelectionRefreshInterval = 2f;
    private const float HudFullResendInterval = 30f;

    private static Dictionary<string, ClanState> ClansById =
        new(StringComparer.Ordinal);
    private static Dictionary<string, ClanState> ClansByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ClanState> PrimaryClanByPlayerId =
        new(StringComparer.Ordinal);
    private static Dictionary<string, ClanState> GuestClanByPlayerId =
        new(StringComparer.Ordinal);
    private static Dictionary<string, ClanInvite> PendingInvitesByTarget =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> LastPositionUpdateByPlayer =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> AnnouncedOnlinePlayerIds =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HudViewerCache> HudCacheByViewerId =
        new(StringComparer.Ordinal);
    private static bool _directoryInvalidationPending;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .DisableAliases()
        .WithIndentedSequences()
        .Build();
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();

    private static string? _loadedSaveFile;

    public static void Init()
    {
        _loadedSaveFile = null;
        LastPositionUpdateByPlayer.Clear();
        AnnouncedOnlinePlayerIds.Clear();
        HudCacheByViewerId.Clear();
        _directoryInvalidationPending = false;
        SwapState(new RegistryData());
    }

    public static void ResetOnlinePresence()
    {
        AnnouncedOnlinePlayerIds.Clear();
        HudCacheByViewerId.Clear();
    }

    private static bool IsRegistryWriteRequest(ClanRequestType type)
    {
        ClanDataRules.RequireEnum(type, "clan request type");
        return type switch
        {
            ClanRequestType.RequestSnapshot or
                ClanRequestType.RequestDirectory or
                ClanRequestType.RequestHud or
                ClanRequestType.SendClanChat or
                ClanRequestType.SendClanPing or
                ClanRequestType.UpdatePosition => false,
            _ => true
        };
    }

    public static void HandleRequest(ZNetPeer? peer, ClanRequest request)
    {
        ClanPlayerRef actor = ClanIdentity.FromPeer(peer, out bool identityRetryable);
        if (!actor.IsValid)
        {
            string identityStatus = identityRetryable
                ? ClanRpc.IdentityNotReadyStatus
                : ClanRpc.IdentityRejectedStatus;
            ClanOperationResultCode identityResultCode = identityRetryable
                ? ClanOperationResultCode.IdentityUnavailable
                : ClanOperationResultCode.IdentityRejected;
            if (!identityRetryable)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Rejected clan request from peer {peer?.m_uid}: character ownership mismatch.");
            }

            SendIdentityFailure(peer, request, identityStatus, identityResultCode);
            return;
        }
        if (!ClanRpc.TryPinPeerIdentity(peer, actor))
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Rejected clan request from peer {peer?.m_uid}: character identity changed " +
                "during an active connection.");
            SendIdentityFailure(
                peer,
                request,
                ClanRpc.IdentityRejectedStatus,
                ClanOperationResultCode.IdentityRejected);
            return;
        }

        bool snapshotOnSuccess = true;
        string status;
        ClanOperationResultCode responseResultCode = ClanOperationResultCode.None;
        bool announcePresence = false;

        try
        {
            bool isRegistryWriteRequest = IsRegistryWriteRequest(request.Type);
            snapshotOnSuccess =
                request.Type == ClanRequestType.RequestSnapshot || isRegistryWriteRequest;
            EnsureLoaded();
            RefreshPlayer(actor);
            ClanRecentPlayers.RememberPlayer(actor);
            announcePresence =
                request.Type == ClanRequestType.RequestSnapshot &&
                AnnouncedOnlinePlayerIds.Add(actor.Id);

            if (request.Type == ClanRequestType.RequestDirectory)
            {
                ClanRpc.SendDirectorySnapshot(
                    peer,
                    BuildDirectoryFor(actor, request.RequestId));
                return;
            }
            if (request.Type == ClanRequestType.RequestHud)
            {
                ClanHudSnapshot? hudSnapshot = BuildHudSnapshotFor(
                    actor,
                    peer,
                    request.ClanId,
                    request.HudSelectionRevision,
                    request.HudStateRevision);
                if (hudSnapshot != null)
                {
                    ClanRpc.SendHudSnapshot(peer, hudSnapshot);
                }
                return;
            }

            if (isRegistryWriteRequest &&
                !ClanRpc.ConsumeMutationRequest(actor))
            {
                ClanRpc.SendSnapshot(peer, new ClanClientSnapshot
                {
                    Status = ClanRpc.MutationRateLimitedStatus,
                    ResponseRequestId = request.RequestId,
                    ResponseResultCode = ClanOperationResultCode.RateLimited
                });
                return;
            }
            if (request.Type == ClanRequestType.RenameClan)
            {
                OperationOutcome outcome = RenameClan(
                    actor,
                    request.ClanId,
                    request.ClanName);
                status = outcome.Status;
                responseResultCode = outcome.Code;
            }
            else
            {
                status = request.Type switch
                {
                    ClanRequestType.RequestSnapshot => "",
                    ClanRequestType.CreateClan => CreateClan(
                        actor,
                        request.ClanName,
                        request.Description,
                        request.EmblemKey),
                    ClanRequestType.Invite => Invite(actor, request.ClanId, request.TargetId),
                    ClanRequestType.Apply => Apply(actor, request.ClanId),
                    ClanRequestType.CancelApplication => CancelApplication(actor),
                    ClanRequestType.AcceptInvite => AcceptInvite(actor, request.InviteId),
                    ClanRequestType.DeclineInvite => DeclineInvite(actor, request.InviteId),
                    ClanRequestType.AcceptApplication => ResolveApplication(
                        actor, request.ClanId, request.TargetId, accept: true),
                    ClanRequestType.RejectApplication => ResolveApplication(
                        actor, request.ClanId, request.TargetId, accept: false),
                    ClanRequestType.KickPlayer => Kick(actor, request.ClanId, request.TargetId),
                    ClanRequestType.TransferLeadership => TransferLeadership(
                        actor, request.ClanId, request.TargetId),
                    ClanRequestType.SetRole => SetRole(
                        actor, request.ClanId, request.TargetId, request.Role),
                    ClanRequestType.UpdateClanProfile => UpdateClanProfile(
                        actor,
                        request.ClanId,
                        request.ClanName,
                        request.Description,
                        request.EmblemKey),
                    ClanRequestType.LeaveClan => Leave(actor, request.ClanId),
                    ClanRequestType.SendClanChat => SendClanChat(
                        actor,
                        request.ClanId,
                        request.Message),
                    ClanRequestType.SendClanPing => SendClanPing(
                        actor,
                        request.ClanId,
                        request.Position),
                    ClanRequestType.UpdatePosition => UpdatePosition(
                        actor,
                        peer,
                        request.ClanId,
                        request.Position),
                    _ => throw new InvalidDataException($"Unknown clan request type ({(int)request.Type}).")
                };
            }
        }
        catch (InvalidDataException ex)
        {
            ClanPlugin.ClanLogger.LogWarning($"Rejected clan request from {actor}: {ex.Message}");
            if (request.Type == ClanRequestType.RenameClan)
            {
                status = ClanLocalization.EncodeStatus("clan_status_rename_validation_failed");
                responseResultCode = ClanOperationResultCode.Failed;
            }
            else
            {
                status = ClanLocalization.EncodeStatus("clan_status_invalid_request_server");
                responseResultCode = ClanOperationResultCode.Failed;
            }
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning($"Clan request from {actor} failed: {ex}");
            status = ClanLocalization.EncodeStatus("clan_status_request_failed");
            responseResultCode = ClanOperationResultCode.Failed;
        }

        FlushDirectoryInvalidation();

        if (request.Type == ClanRequestType.RequestDirectory)
        {
            ClanRpc.SendDirectorySnapshot(peer, new ClanDirectorySnapshot
            {
                RequestId = request.RequestId,
                ResultCode = responseResultCode == ClanOperationResultCode.None
                    ? ClanOperationResultCode.Failed
                    : responseResultCode,
                Status = status
            });
            return;
        }

        if (!snapshotOnSuccess && string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        try
        {
            ClanRpc.SendSnapshot(
                peer,
                BuildSnapshotFor(
                    actor,
                    status,
                    responseRequestId: request.RequestId,
                    responseResultCode: responseResultCode));
            if (announcePresence)
            {
                BroadcastPresenceSnapshots(actor);
            }
        }
        catch (Exception ex)
        {
            if (announcePresence)
            {
                AnnouncedOnlinePlayerIds.Remove(actor.Id);
            }
            ClanPlugin.ClanLogger.LogWarning($"Failed to build clan snapshot for {actor}: {ex}");
            ClanRpc.SendSnapshot(peer, new ClanClientSnapshot
            {
                Status = ClanRpc.StateUnavailableStatus,
                ResponseRequestId = request.RequestId,
                ResponseResultCode = ClanOperationResultCode.Unavailable
            });
        }
    }

    public static ClanClientSnapshot BuildSnapshotFor(
        ClanPlayerRef player,
        string status = "",
        string forcedOfflinePlayerId = "",
        long responseRequestId = 0L,
        ClanOperationResultCode responseResultCode = ClanOperationResultCode.None)
    {
        ClanClientSnapshot snapshot = new()
        {
            Status = status,
            ResponseRequestId = responseRequestId,
            ResponseResultCode = responseResultCode
        };

        if (!player.IsValid)
        {
            return snapshot;
        }

        EnsureLoaded();
        ClanState? primaryClan = FindPrimaryClan(player);
        if (primaryClan != null &&
            primaryClan.Members.TryGetValue(player.Id, out ClanMember primaryMember))
        {
            snapshot.PrimaryClanId = primaryClan.ClanId;
            snapshot.PrimaryClanName = primaryClan.Name;
            snapshot.PrimaryRole = primaryMember.Role;
        }

        ClanState? guestClan = FindGuestClan(player);
        if (guestClan != null)
        {
            snapshot.GuestClanId = guestClan.ClanId;
            snapshot.GuestClanName = guestClan.Name;
        }

        ClanState? activeClan = FindActiveClan(player);
        if (activeClan != null)
        {
            snapshot.ClanId = activeClan.ClanId;
            snapshot.ClanName = activeClan.Name;
            snapshot.ClanDescription = activeClan.Description;
            snapshot.ClanEmblemKey = activeClan.EmblemKey;
            if (activeClan.Members.TryGetValue(player.Id, out ClanMember selfMember))
            {
                snapshot.SelfRole = selfMember.Role;
            }

            HashSet<string> onlinePlayerIds = new(
                ClanIdentity.GetOnlinePlayerRefs().Select(online => online.Id),
                StringComparer.Ordinal);
            if (forcedOfflinePlayerId.Length != 0)
            {
                onlinePlayerIds.Remove(forcedOfflinePlayerId);
            }
            snapshot.Roster.AddRange(activeClan.Members.Values
                .OrderByDescending(member => ClanDataRules.GetRolePower(member.Role))
                .ThenBy(member => member.Player.Name, StringComparer.OrdinalIgnoreCase)
                .Select(member => ToPlayerSummary(
                    member,
                    player,
                    onlinePlayerIds.Contains(member.Player.Id) &&
                    ReferenceEquals(FindActiveClan(member.Player), activeClan))));

            if (snapshot.CanModerate)
            {
                snapshot.Applications.AddRange(activeClan.Applications.Values
                    .OrderBy(applicant => applicant.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(ToApplicationSummary));
            }
        }

        if (PendingInvitesByTarget.TryGetValue(player.Id, out ClanInvite invite))
        {
            if (!ClansById.TryGetValue(invite.ClanId, out ClanState invitingClan))
            {
                throw new InvalidDataException(
                    $"Invite '{invite.InviteId}' references missing clan '{invite.ClanId}'.");
            }
            snapshot.Invite = new ClanInviteSummary
            {
                InviteId = invite.InviteId,
                ClanId = invitingClan.ClanId,
                ClanName = invitingClan.Name,
                FromName = invite.FromName
            };
        }

        ClanState? applicationClan = guestClan == null
            ? FindApplicationClan(player.Id)
            : null;
        if (applicationClan != null &&
            applicationClan.Applications.ContainsKey(player.Id))
        {
            snapshot.OwnApplicationClanId = applicationClan.ClanId;
            snapshot.OwnApplicationClanName = applicationClan.Name;
        }

        return snapshot;
    }

    public static ClanHudSnapshot? BuildHudSnapshotFor(
        ClanPlayerRef viewer,
        ZNetPeer? viewerPeer,
        string requestedClanId,
        long knownSelectionRevision,
        long knownStateRevision)
    {
        if (!viewer.IsValid)
        {
            return null;
        }

        EnsureLoaded();
        ClanState? clan = FindEffectiveClan(viewer, requestedClanId);
        if (clan == null)
        {
            return null;
        }

        float now = Time.realtimeSinceStartup;
        if (!HudCacheByViewerId.TryGetValue(viewer.Id, out HudViewerCache cache))
        {
            cache = new HudViewerCache();
            HudCacheByViewerId.Add(viewer.Id, cache);
        }

        bool contextChanged = !StringComparer.Ordinal.Equals(cache.ClanId, clan.ClanId);
        bool selectionRefreshDue = contextChanged ||
                                   float.IsNegativeInfinity(cache.LastSelectionRefreshTime) ||
                                   now < cache.LastSelectionRefreshTime ||
                                   now - cache.LastSelectionRefreshTime >=
                                   HudSelectionRefreshInterval;
        if (selectionRefreshDue)
        {
            RefreshHudSelection(cache, clan, viewer, viewerPeer, now);
        }
        else
        {
            PruneInvalidHudSelection(cache, clan);
        }

        bool forceFull = cache.SelectionRequiresFull;
        if (cache.PendingUpdate is PendingHudUpdate pending)
        {
            bool pendingMatchesContext =
                StringComparer.Ordinal.Equals(pending.Snapshot.ClanId, cache.ClanId) &&
                pending.Snapshot.SelectionRevision == cache.SelectionRevision;
            if (!pendingMatchesContext)
            {
                cache.PendingUpdate = null;
                forceFull = true;
            }
            else if (knownStateRevision == pending.Snapshot.StateRevision &&
                     knownSelectionRevision == pending.Snapshot.SelectionRevision)
            {
                CommitAcknowledgedHudUpdate(cache, pending);
                cache.PendingUpdate = null;
            }
            else if (knownStateRevision == 0L)
            {
                if (pending.Snapshot.ReplaceSelection)
                {
                    return pending.Snapshot;
                }

                cache.PendingUpdate = null;
                forceFull = true;
            }
            else if (knownStateRevision == cache.AcknowledgedStateRevision &&
                     (pending.Snapshot.ReplaceSelection ||
                      knownSelectionRevision == cache.SelectionRevision))
            {
                return pending.Snapshot;
            }
            else
            {
                cache.PendingUpdate = null;
                forceFull = true;
            }
        }

        if (knownSelectionRevision != cache.SelectionRevision ||
            knownStateRevision != cache.AcknowledgedStateRevision ||
            knownStateRevision == 0L)
        {
            forceFull = true;
        }
        if (float.IsNegativeInfinity(cache.LastFullResendTime) ||
            now < cache.LastFullResendTime ||
            now - cache.LastFullResendTime >= HudFullResendInterval ||
            cache.AcknowledgedStateRevision == long.MaxValue)
        {
            forceFull = true;
        }
        if (!forceFull && !HasCompleteHudHealthBaseline(cache))
        {
            forceFull = true;
        }

        Dictionary<string, HudHealthState> pendingHealth = new(StringComparer.Ordinal);
        ClanHudSnapshot snapshot = new()
        {
            ClanId = cache.ClanId,
            SelectionRevision = cache.SelectionRevision,
            StateRevision = NextHudRevision(cache.AcknowledgedStateRevision),
            ReplaceSelection = forceFull
        };
        foreach (HudSelectionEntry selected in cache.Selection)
        {
            HudHealthState health = ReadHudHealth(selected);
            if (!forceFull &&
                cache.AcknowledgedHealth.TryGetValue(
                    selected.PlayerId,
                    out HudHealthState acknowledged) &&
                acknowledged.Equals(health))
            {
                continue;
            }

            pendingHealth[selected.PlayerId] = health;
            snapshot.Players.Add(new ClanHudPlayerSummary
            {
                PlayerId = selected.PlayerId,
                HasHealth = health.HasHealth,
                CurrentHealth = health.CurrentHealth,
                MaxHealth = health.MaxHealth
            });
        }

        if (!forceFull && snapshot.Players.Count == 0)
        {
            return null;
        }

        cache.PendingUpdate = new PendingHudUpdate(snapshot, pendingHealth);
        if (forceFull)
        {
            cache.SelectionRequiresFull = false;
            cache.LastFullResendTime = now;
        }
        return snapshot;
    }

    private static void RefreshHudSelection(
        HudViewerCache cache,
        ClanState clan,
        ClanPlayerRef viewer,
        ZNetPeer? viewerPeer,
        float now)
    {
        Vector3 viewerPosition = viewerPeer != null
            ? viewerPeer.m_refPos
            : Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;
        Dictionary<string, HudCandidate> candidateByPlayerId = new(StringComparer.Ordinal);
        Player? localPlayer = viewerPeer == null ? Player.m_localPlayer : null;
        ClanPlayerRef localViewer = localPlayer != null
            ? ClanPlayerRef.Local()
            : default;
        bool canSeedViewer = viewerPeer != null ||
                             (localPlayer != null &&
                              localViewer.IsValid &&
                              localViewer == viewer);
        ZDOID viewerCharacterId = viewerPeer != null
            ? viewerPeer.m_characterID
            : localPlayer != null
                ? localPlayer.GetZDOID()
                : ZDOID.None;
        if (canSeedViewer &&
            clan.Members.TryGetValue(viewer.Id, out ClanMember localMember) &&
            ReferenceEquals(FindActiveClan(localMember.Player), clan))
        {
            candidateByPlayerId[viewer.Id] = new HudCandidate(
                localMember,
                viewerCharacterId,
                0f);
        }
        if (ZNet.instance != null)
        {
            foreach (ZNet.PlayerInfo playerInfo in ClanIdentity.GetOnlinePlayers())
            {
                ClanPlayerRef onlinePlayer = ClanIdentity.FromPlayerInfo(playerInfo);
                if (!onlinePlayer.IsValid ||
                    !clan.Members.TryGetValue(onlinePlayer.Id, out ClanMember member) ||
                    !ReferenceEquals(FindActiveClan(member.Player), clan))
                {
                    continue;
                }

                float distanceSquared = (playerInfo.m_position - viewerPosition).sqrMagnitude;
                HudCandidate candidate = new(
                    member,
                    playerInfo.m_characterID,
                    distanceSquared);
                if (!candidateByPlayerId.TryGetValue(
                        member.Player.Id,
                        out HudCandidate existing) ||
                    candidate.DistanceSquared < existing.DistanceSquared)
                {
                    candidateByPlayerId[member.Player.Id] = candidate;
                }
            }
        }

        List<HudSelectionEntry> selection = candidateByPlayerId.Values
            .OrderBy(item => item.DistanceSquared)
            .ThenBy(item => item.Member.Player.Id, StringComparer.Ordinal)
            .Take(ClanDataRules.MaxHudPlayers)
            .OrderByDescending(item => ClanDataRules.GetRolePower(item.Member.Role))
            .ThenBy(item => item.DistanceSquared)
            .ThenBy(item => item.Member.Player.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Member.Player.Id, StringComparer.Ordinal)
            .Select(item => new HudSelectionEntry(
                item.Member.Player,
                item.CharacterId))
            .ToList();
        ReplaceHudSelection(cache, clan.ClanId, selection);
        cache.LastSelectionRefreshTime = now;
    }

    private static void PruneInvalidHudSelection(HudViewerCache cache, ClanState clan)
    {
        List<HudSelectionEntry>? retained = null;
        for (int index = 0; index < cache.Selection.Count; index++)
        {
            HudSelectionEntry selected = cache.Selection[index];
            bool isStillEffective =
                clan.Members.ContainsKey(selected.PlayerId) &&
                ReferenceEquals(FindActiveClan(selected.Player), clan);
            if (isStillEffective)
            {
                retained?.Add(selected);
                continue;
            }

            if (retained == null)
            {
                retained = cache.Selection.Take(index).ToList();
            }
        }

        if (retained != null)
        {
            ReplaceHudSelection(cache, clan.ClanId, retained);
        }
    }

    private static void ReplaceHudSelection(
        HudViewerCache cache,
        string clanId,
        List<HudSelectionEntry> selection)
    {
        bool changed = !StringComparer.Ordinal.Equals(cache.ClanId, clanId) ||
                       cache.Selection.Count != selection.Count;
        if (!changed)
        {
            for (int index = 0; index < selection.Count; index++)
            {
                if (!StringComparer.Ordinal.Equals(
                        cache.Selection[index].PlayerId,
                        selection[index].PlayerId))
                {
                    changed = true;
                    break;
                }
            }
        }

        cache.ClanId = clanId;
        cache.Selection.Clear();
        cache.Selection.AddRange(selection);
        if (!changed)
        {
            return;
        }

        cache.SelectionRevision = NextHudRevision(cache.SelectionRevision);
        cache.SelectionRequiresFull = true;
        cache.PendingUpdate = null;
        cache.AcknowledgedHealth.Clear();
        cache.LastFullResendTime = float.NegativeInfinity;
    }

    private static bool HasCompleteHudHealthBaseline(HudViewerCache cache)
    {
        if (cache.AcknowledgedHealth.Count != cache.Selection.Count)
        {
            return false;
        }

        return cache.Selection.All(selected =>
            cache.AcknowledgedHealth.ContainsKey(selected.PlayerId));
    }

    private static HudHealthState ReadHudHealth(HudSelectionEntry selected)
    {
        bool hasHealth = TryReadHudHealth(
            selected.CharacterId,
            selected.Player,
            out float currentHealth,
            out float maxHealth);
        return new HudHealthState(
            hasHealth,
            hasHealth ? currentHealth : 0f,
            hasHealth ? maxHealth : 0f);
    }

    private static void CommitAcknowledgedHudUpdate(
        HudViewerCache cache,
        PendingHudUpdate pending)
    {
        if (pending.Snapshot.ReplaceSelection)
        {
            cache.AcknowledgedHealth.Clear();
        }
        foreach (KeyValuePair<string, HudHealthState> pair in pending.AbsoluteHealth)
        {
            cache.AcknowledgedHealth[pair.Key] = pair.Value;
        }
        cache.AcknowledgedStateRevision = pending.Snapshot.StateRevision;
    }

    private static long NextHudRevision(long revision)
    {
        return revision >= long.MaxValue ? 1L : revision + 1L;
    }

    private static void InvalidateHudCachesForDisconnectedPlayer(string playerId)
    {
        HudCacheByViewerId.Remove(playerId);
        foreach (HudViewerCache cache in HudCacheByViewerId.Values)
        {
            if (cache.Selection.All(selected =>
                    !StringComparer.Ordinal.Equals(selected.PlayerId, playerId)))
            {
                continue;
            }

            List<HudSelectionEntry> retained = cache.Selection
                .Where(selected => !StringComparer.Ordinal.Equals(
                    selected.PlayerId,
                    playerId))
                .ToList();
            ReplaceHudSelection(cache, cache.ClanId, retained);
        }
    }

    private static bool TryReadHudHealth(
        ZDOID characterId,
        ClanPlayerRef expectedPlayer,
        out float currentHealth,
        out float maxHealth)
    {
        currentHealth = 0f;
        maxHealth = 0f;
        if (characterId.IsNone() || ZDOMan.instance == null)
        {
            return TryReadLocalHudHealth(expectedPlayer, out currentHealth, out maxHealth);
        }

        ZDO zdo = ZDOMan.instance.GetZDO(characterId);
        if (zdo == null ||
            zdo.GetLong(ZDOVars.s_playerID, 0L) != expectedPlayer.CharacterPlayerId)
        {
            return TryReadLocalHudHealth(expectedPlayer, out currentHealth, out maxHealth);
        }

        maxHealth = zdo.GetFloat(ZDOVars.s_maxHealth, 0f);
        currentHealth = zdo.GetFloat(ZDOVars.s_health, maxHealth);
        return NormalizeHudHealth(ref currentHealth, ref maxHealth);
    }

    private static bool TryReadLocalHudHealth(
        ClanPlayerRef expectedPlayer,
        out float currentHealth,
        out float maxHealth)
    {
        currentHealth = 0f;
        maxHealth = 0f;
        if (Player.m_localPlayer == null || expectedPlayer != ClanPlayerRef.Local())
        {
            return false;
        }

        currentHealth = Player.m_localPlayer.GetHealth();
        maxHealth = Player.m_localPlayer.GetMaxHealth();
        return NormalizeHudHealth(ref currentHealth, ref maxHealth);
    }

    private static bool NormalizeHudHealth(ref float currentHealth, ref float maxHealth)
    {
        if (float.IsNaN(currentHealth) ||
            float.IsInfinity(currentHealth) ||
            float.IsNaN(maxHealth) ||
            float.IsInfinity(maxHealth) ||
            maxHealth <= 0f ||
            maxHealth > ClanDataRules.MaxHudHealth)
        {
            currentHealth = 0f;
            maxHealth = 0f;
            return false;
        }

        currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
        return true;
    }

    private static void SendIdentityFailure(
        ZNetPeer? peer,
        ClanRequest request,
        string status,
        ClanOperationResultCode resultCode)
    {
        if (request.Type == ClanRequestType.RequestDirectory)
        {
            ClanRpc.SendDirectorySnapshot(peer, new ClanDirectorySnapshot
            {
                RequestId = request.RequestId,
                ResultCode = resultCode,
                Status = status
            });
            return;
        }
        if (request.Type == ClanRequestType.RequestHud)
        {
            ClanRpc.SendHudSnapshot(peer, new ClanHudSnapshot());
            return;
        }

        ClanRpc.SendSnapshot(peer, new ClanClientSnapshot
        {
            Status = status,
            ResponseRequestId = request.RequestId,
            ResponseResultCode = resultCode
        });
    }

    public static void NotifyPlayerDisconnected(ClanPlayerRef player)
    {
        if (!player.IsValid ||
            ZNet.instance?.IsServer() != true)
        {
            return;
        }

        if (ClanIdentity.GetOnlinePlayerRefs().Any(online => online == player))
        {
            return;
        }

        ClanRecentPlayers.MarkPlayerOffline(player);
        AnnouncedOnlinePlayerIds.Remove(player.Id);
        InvalidateHudCachesForDisconnectedPlayer(player.Id);

        try
        {
            EnsureLoaded();
            ClanState? clan = FindActiveClan(player);
            if (clan == null)
            {
                return;
            }

            foreach (ClanMember member in clan.Members.Values)
            {
                if (member.Player != player &&
                    ReferenceEquals(FindActiveClan(member.Player), clan))
                {
                    SendSnapshotUpdate(
                        member.Player,
                        "",
                        forcedOfflinePlayerId: player.Id);
                }
            }
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Failed to publish clan presence change for {player}: {ex.Message}");
        }
    }

    public static ClanDirectorySnapshot BuildDirectoryFor(
        ClanPlayerRef viewer,
        long requestId,
        string status = "")
    {
        ClanDirectorySnapshot snapshot = new()
        {
            RequestId = ClanDataRules.RequireRequestId(requestId),
            ResultCode = ClanOperationResultCode.Success,
            Status = status
        };
        if (!viewer.IsValid)
        {
            return snapshot;
        }

        EnsureLoaded();
        ClanState? viewerPrimaryClan = FindPrimaryClan(viewer);
        ClanState? viewerGuestClan = FindGuestClan(viewer);
        ClanState? viewerApplicationClan = viewerGuestClan == null
            ? FindApplicationClan(viewer.Id)
            : null;
        PendingInvitesByTarget.TryGetValue(viewer.Id, out ClanInvite viewerInvite);
        snapshot.PublicClans.AddRange(ClansById.Values
            .OrderBy(clan => GetDirectoryClanRank(
                clan,
                viewerInvite,
                viewerGuestClan,
                viewerPrimaryClan,
                viewerApplicationClan))
            .ThenByDescending(clan => clan.CreationOrder)
            .ThenBy(clan => clan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(clan => clan.ClanId, StringComparer.Ordinal)
            .Select(clan => new ClanPublicSummary
            {
                ClanId = clan.ClanId,
                Name = clan.Name,
                Description = clan.Description,
                EmblemKey = clan.EmblemKey,
                LeaderName = GetLeader(clan).Player.Name,
                MemberCount = clan.Members.Count
            }));

        ClanState? viewerClan = FindActiveClan(viewer);
        bool viewerCanModerate = viewerClan != null &&
                                 viewerClan.Members.TryGetValue(
                                     viewer.Id,
                                     out ClanMember viewerMember) &&
                                 CanModerate(viewerMember);
        Dictionary<string, ClanState> applicationClanByPlayer = new(StringComparer.Ordinal);
        foreach (ClanState clan in ClansById.Values)
        {
            foreach (string applicantId in clan.Applications.Keys)
            {
                applicationClanByPlayer[applicantId] = clan;
            }
        }
        List<ClanRecentPlayerEntry> recentPlayers = ClanRecentPlayers
            .GetRecentPlayers()
            .ToList();
        if (recentPlayers.All(entry => entry.Player != viewer))
        {
            recentPlayers.Insert(0, new ClanRecentPlayerEntry(
                viewer,
                isOnline: true,
                lastSeenUtcTicks: DateTime.UtcNow.Ticks));
        }

        foreach (ClanRecentPlayerEntry recent in recentPlayers
                     .OrderByDescending(entry => entry.Player == viewer)
                     .ThenByDescending(entry =>
                         viewerCanModerate &&
                         applicationClanByPlayer.TryGetValue(
                             entry.Player.Id,
                             out ClanState applicationClan) &&
                         ReferenceEquals(applicationClan, viewerClan))
                     .ThenByDescending(entry => entry.IsOnline)
                     .ThenByDescending(entry => entry.LastSeenUtcTicks)
                     .ThenBy(entry => entry.Player.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(ClanDataRules.MaxDirectoryPlayers))
        {
            ClanPlayerRef player = recent.Player;
            ClanState? primaryClan = FindPrimaryClan(player);
            ClanState? guestClan = FindGuestClan(player);
            ClanState? applicationClan = guestClan == null &&
                                         applicationClanByPlayer.TryGetValue(
                                             player.Id,
                                             out ClanState appliedClan)
                ? appliedClan
                : null;
            bool applicationForViewer = viewerCanModerate &&
                                        ReferenceEquals(applicationClan, viewerClan);
            bool isSelf = player == viewer;
            bool hasPendingInvite = PendingInvitesByTarget.TryGetValue(
                player.Id,
                out ClanInvite visibleInvite);
            bool inviteVisibleToViewer = hasPendingInvite &&
                                         (isSelf ||
                                          (viewerCanModerate &&
                                           ClansById.TryGetValue(
                                               visibleInvite.ClanId,
                                               out ClanState visibleInviteClan) &&
                                           ReferenceEquals(viewerClan, visibleInviteClan)));
            ClanDirectoryPlayerState playerState;
            string clanName;

            if (primaryClan != null || guestClan != null)
            {
                playerState = ClanDirectoryPlayerState.Clan;
                clanName = (guestClan ?? primaryClan)!.Name;
            }
            else if ((isSelf && applicationClan != null) || applicationForViewer)
            {
                playerState = ClanDirectoryPlayerState.Pending;
                clanName = applicationClan!.Name;
            }
            else if (inviteVisibleToViewer &&
                     ClansById.TryGetValue(visibleInvite.ClanId, out ClanState invitingClan))
            {
                playerState = ClanDirectoryPlayerState.Invited;
                clanName = invitingClan.Name;
            }
            else
            {
                playerState = ClanDirectoryPlayerState.None;
                clanName = "";
            }

            snapshot.Players.Add(new ClanDirectoryPlayerSummary
            {
                PlayerId = player.Id,
                PlayerName = player.Name,
                State = playerState,
                ClanName = clanName,
                IsOnline = recent.IsOnline,
                IsSelf = isSelf,
                CanInvite = viewerCanModerate &&
                            !isSelf &&
                            guestClan == null &&
                            viewerClan != null &&
                            !viewerClan.Members.ContainsKey(player.Id) &&
                            applicationClan == null &&
                            !PendingInvitesByTarget.ContainsKey(player.Id),
                CanResolveApplication = applicationForViewer,
                LastSeenUtcTicks = recent.LastSeenUtcTicks
            });
        }

        return snapshot;
    }

    private static int GetDirectoryClanRank(
        ClanState clan,
        ClanInvite? viewerInvite,
        ClanState? viewerGuestClan,
        ClanState? viewerPrimaryClan,
        ClanState? viewerApplicationClan)
    {
        if (viewerInvite != null &&
            StringComparer.Ordinal.Equals(clan.ClanId, viewerInvite.ClanId))
        {
            return 0;
        }
        if (ReferenceEquals(clan, viewerGuestClan))
        {
            return 1;
        }
        if (ReferenceEquals(clan, viewerPrimaryClan))
        {
            return 2;
        }
        return ReferenceEquals(clan, viewerApplicationClan) ? 3 : 4;
    }

    private static ClanState? FindPrimaryClan(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return null;
        }

        return PrimaryClanByPlayerId.TryGetValue(player.Id, out ClanState clan) ? clan : null;
    }

    internal static ClanWardAuthorizationResolution ResolveWardAuthorization(
        string platformId,
        long characterPlayerId,
        out string clanId,
        out string clanName)
    {
        clanId = "";
        clanName = "";
        if (ZNet.instance?.IsServer() != true)
        {
            return ClanWardAuthorizationResolution.Unavailable;
        }

        try
        {
            EnsureLoaded();
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Could not load the Clan registry for ward authorization: {exception.Message}");
            return ClanWardAuthorizationResolution.Unavailable;
        }

        ClanPlayerRef player = CreateIntegrationPlayerRef(platformId, characterPlayerId);
        if (!player.IsValid)
        {
            return ClanWardAuthorizationResolution.ResolvedNoAuthorization;
        }

        if (!PrimaryClanByPlayerId.TryGetValue(player.Id, out ClanState clan))
        {
            return ClanWardAuthorizationResolution.ResolvedNoAuthorization;
        }
        if (!clan.Members.TryGetValue(player.Id, out ClanMember member))
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Primary Clan membership index for '{player.Id}' is inconsistent.");
            return ClanWardAuthorizationResolution.Unavailable;
        }
        if (member.Role == ClanRole.Guest)
        {
            return ClanWardAuthorizationResolution.ResolvedNoAuthorization;
        }
        if (member.Role is not ClanRole.Leader and not ClanRole.Officer and not ClanRole.Member)
        {
            return ClanWardAuthorizationResolution.Unavailable;
        }

        clanId = clan.ClanId;
        clanName = clan.Name;
        return ClanWardAuthorizationResolution.Authorized;
    }

    internal static ClanMembershipResolution ResolveMemberships(
        string platformId,
        long characterPlayerId,
        out string primaryClanId,
        out string primaryClanName,
        out string guestClanId,
        out string guestClanName)
    {
        primaryClanId = "";
        primaryClanName = "";
        guestClanId = "";
        guestClanName = "";
        if (ZNet.instance?.IsServer() != true)
        {
            return ClanMembershipResolution.Unavailable;
        }

        try
        {
            EnsureLoaded();
            ClanPlayerRef player = CreateIntegrationPlayerRef(platformId, characterPlayerId);
            if (!player.IsValid)
            {
                return ClanMembershipResolution.Unavailable;
            }

            if (!TryResolveIndexedMembership(
                    player,
                    PrimaryClanByPlayerId,
                    expectGuest: false,
                    out string resolvedPrimaryClanId,
                    out string resolvedPrimaryClanName) ||
                !TryResolveIndexedMembership(
                    player,
                    GuestClanByPlayerId,
                    expectGuest: true,
                    out string resolvedGuestClanId,
                    out string resolvedGuestClanName))
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan membership indexes for '{player.Id}' are inconsistent.");
                return ClanMembershipResolution.Unavailable;
            }

            primaryClanId = resolvedPrimaryClanId;
            primaryClanName = resolvedPrimaryClanName;
            guestClanId = resolvedGuestClanId;
            guestClanName = resolvedGuestClanName;
            return ClanMembershipResolution.Resolved;
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Could not resolve authoritative Clan memberships: {exception.Message}");
            return ClanMembershipResolution.Unavailable;
        }
    }

    private static ClanPlayerRef CreateIntegrationPlayerRef(
        string platformId,
        long characterPlayerId)
    {
        string canonicalPlatformId = ClanDataRules.NormalizePlatformId(platformId);
        if (canonicalPlatformId.Length > 0 &&
            ulong.TryParse(
                canonicalPlatformId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            // Valheim exposes the same Steam identity as both a bare Steam64 account id in
            // some integrations and as Splatform's canonical `Steam_<id>` representation.
            canonicalPlatformId = "Steam_" + canonicalPlatformId;
        }

        return new ClanPlayerRef(canonicalPlatformId, characterPlayerId, "");
    }

    private static bool TryResolveIndexedMembership(
        ClanPlayerRef player,
        Dictionary<string, ClanState> membershipIndex,
        bool expectGuest,
        out string clanId,
        out string clanName)
    {
        clanId = "";
        clanName = "";
        if (!membershipIndex.TryGetValue(player.Id, out ClanState clan))
        {
            return true;
        }

        if (clan == null ||
            !ClansById.TryGetValue(clan.ClanId, out ClanState indexedClan) ||
            !ReferenceEquals(indexedClan, clan) ||
            !clan.Members.TryGetValue(player.Id, out ClanMember member) ||
            member == null ||
            !member.Player.IsValid ||
            !StringComparer.Ordinal.Equals(member.Player.Id, player.Id))
        {
            return false;
        }

        bool validRole = expectGuest
            ? member.Role == ClanRole.Guest
            : member.Role is ClanRole.Leader or ClanRole.Officer or ClanRole.Member;
        if (!validRole)
        {
            return false;
        }

        clanId = clan.ClanId;
        clanName = clan.Name;
        return true;
    }

    private static ClanState? FindGuestClan(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return null;
        }

        return GuestClanByPlayerId.TryGetValue(player.Id, out ClanState clan) ? clan : null;
    }

    private static ClanState? FindEffectiveClan(
        ClanPlayerRef player,
        string requestedClanId)
    {
        string clanId = ClanDataRules.RequireClanId(requestedClanId);
        ClanState? effectiveClan = FindActiveClan(player);
        return effectiveClan != null &&
               StringComparer.Ordinal.Equals(effectiveClan.ClanId, clanId)
            ? effectiveClan
            : null;
    }

    private static ClanState? FindActiveClan(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return null;
        }

        return FindGuestClan(player) ?? FindPrimaryClan(player);
    }

    private static string CreateClan(
        ClanPlayerRef actor,
        string requestedName,
        string requestedDescription,
        string requestedEmblemKey)
    {
        string clanName = ClanDataRules.RequireClanName(requestedName);
        string description = ClanDataRules.RequireClanDescription(requestedDescription);
        string emblemKey = ClanDataRules.RequireClanEmblemKey(requestedEmblemKey);
        if (!ClanEmoji.IsAvailableEmblemKey(emblemKey))
        {
            return ClanLocalization.EncodeStatus("clan_status_emblem_unavailable");
        }
        if (FindPrimaryClan(actor) != null || FindGuestClan(actor) != null)
        {
            return ClanLocalization.EncodeStatus("clan_status_leave_affiliations_before_create");
        }

        if (ClansByName.ContainsKey(clanName))
        {
            return ClanLocalization.EncodeStatus("clan_status_name_exists", clanName);
        }
        if (ClansById.Count >= ClanDataRules.MaxClans)
        {
            return ClanLocalization.EncodeStatus("clan_status_server_clan_limit");
        }

        string clanId;
        do
        {
            clanId = Guid.NewGuid().ToString("N");
        } while (ClansById.ContainsKey(clanId));

        ClanState clan = new(clanId)
        {
            CreationOrder = GetNextClanCreationOrder(),
            Name = clanName,
            Description = description,
            EmblemKey = emblemKey
        };
        clan.Members.Add(actor.Id, new ClanMember
        {
            Player = actor,
            Role = ClanRole.Leader
        });
        ClansById.Add(clan.ClanId, clan);
        ClansByName.Add(clan.Name, clan);
        PrimaryClanByPlayerId.Add(actor.Id, clan);
        Save(wardAuthorizationChanged: true);
        return ClanLocalization.EncodeStatus("clan_status_created", clanName);
    }

    private static long GetNextClanCreationOrder()
    {
        long latest = 0L;
        foreach (ClanState clan in ClansById.Values)
        {
            latest = Math.Max(
                latest,
                RequireClanCreationOrder(clan.CreationOrder));
        }
        if (latest == long.MaxValue)
        {
            throw new InvalidDataException("Clan creation order is exhausted.");
        }
        return latest + 1L;
    }

    private static long RequireClanCreationOrder(
        long value,
        string fieldName = "clan creation order")
    {
        if (value <= 0L)
        {
            throw new InvalidDataException($"{fieldName} must be positive.");
        }
        return value;
    }

    private static string Invite(
        ClanPlayerRef actor,
        string requestedClanId,
        string targetId)
    {
        ClanState? clan = RequirePermission(actor, requestedClanId, CanModerate);
        if (clan == null)
        {
            return ClanLocalization.EncodeStatus("clan_status_only_moderators_invite");
        }

        if (!TryFindKnownPlayerById(targetId, out ClanPlayerRef target))
        {
            return ClanLocalization.EncodeStatus("clan_status_player_not_recent");
        }

        if (clan.Members.ContainsKey(target.Id))
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_player_already_connected",
                target.Name,
                clan.Name);
        }
        if (FindGuestClan(target) != null)
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_player_leave_guest_before_join",
                target.Name);
        }

        ClanInvite invite = new()
        {
            ClanId = clan.ClanId,
            FromName = actor.Name,
            Target = target
        };
        if (!PendingInvitesByTarget.ContainsKey(target.Id) &&
            PendingInvitesByTarget.Count >= ClanDataRules.MaxInvites)
        {
            return ClanLocalization.EncodeStatus("clan_status_server_invite_limit");
        }

        if (PendingInvitesByTarget.TryGetValue(target.Id, out ClanInvite existingInvite) &&
            StringComparer.Ordinal.Equals(existingInvite.ClanId, clan.ClanId))
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_invite_already_pending",
                target.Name,
                clan.Name);
        }

        PendingInvitesByTarget[target.Id] = invite;
        Save();
        SendSnapshotUpdate(
            target,
            ClanLocalization.EncodeStatus(
                "clan_status_invited_you",
                actor.Name,
                clan.Name));
        return ClanLocalization.EncodeStatus("clan_status_invite_sent", target.Name);
    }

    private static string Apply(
        ClanPlayerRef actor,
        string requestedClanId)
    {
        string clanId = ClanDataRules.RequireClanId(requestedClanId);
        if (!ClansById.TryGetValue(clanId, out ClanState clan))
        {
            return ClanLocalization.EncodeStatus("clan_status_selected_clan_missing");
        }

        if (clan.Members.ContainsKey(actor.Id))
        {
            return ClanLocalization.EncodeStatus("clan_status_already_connected", clan.Name);
        }
        if (FindGuestClan(actor) != null)
        {
            return ClanLocalization.EncodeStatus("clan_status_leave_guest_before_apply");
        }

        ClanState? previousApplicationClan = FindApplicationClan(actor.Id);
        if (!ReferenceEquals(previousApplicationClan, clan) &&
            clan.Applications.Count >= ClanDataRules.MaxApplicationsPerClan)
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_application_limit",
                clan.Name);
        }

        if (ReferenceEquals(previousApplicationClan, clan))
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_application_already_pending",
                clan.Name);
        }

        RemoveApplication(actor.Id);
        clan.Applications[actor.Id] = actor;
        Save();

        if (previousApplicationClan != null &&
            !ReferenceEquals(previousApplicationClan, clan))
        {
            SendModeratorSnapshots(
                previousApplicationClan,
                ClanLocalization.EncodeStatus(
                    "clan_status_application_withdrawn_notice",
                    actor.Name));
        }
        SendModeratorSnapshots(
            clan,
            ClanLocalization.EncodeStatus("clan_status_applied_notice", actor.Name));
        return ClanLocalization.EncodeStatus("clan_status_applied", clan.Name);
    }

    private static string CancelApplication(ClanPlayerRef actor)
    {
        ClanState? clan = RemoveApplication(actor.Id);
        if (clan == null)
        {
            return ClanLocalization.EncodeStatus("clan_status_no_pending_application");
        }

        Save();
        SendModeratorSnapshots(
            clan,
            ClanLocalization.EncodeStatus(
                "clan_status_application_withdrawn_notice",
                actor.Name));
        return ClanLocalization.EncodeStatus("clan_status_application_cancelled", clan.Name);
    }

    private static string AcceptInvite(ClanPlayerRef actor, string inviteId)
    {
        if (!PendingInvitesByTarget.TryGetValue(actor.Id, out ClanInvite invite) ||
            !StringComparer.Ordinal.Equals(invite.InviteId, inviteId))
        {
            return ClanLocalization.EncodeStatus("clan_status_invite_missing");
        }

        if (!ClansById.TryGetValue(invite.ClanId, out ClanState clan))
        {
            PendingInvitesByTarget.Remove(actor.Id);
            Save();
            return ClanLocalization.EncodeStatus("clan_status_inviting_clan_missing");
        }

        if (clan.Members.ContainsKey(actor.Id))
        {
            return ClanLocalization.EncodeStatus("clan_status_already_connected", clan.Name);
        }
        if (FindGuestClan(actor) != null)
        {
            return ClanLocalization.EncodeStatus("clan_status_leave_guest_before_accept");
        }
        if (clan.Members.Count >= ClanDataRules.MaxMembersPerClan)
        {
            return ClanLocalization.EncodeStatus("clan_status_member_limit", clan.Name);
        }

        ClanState? previousEffectiveClan = FindActiveClan(actor);
        AddGuestToClan(clan, actor);
        PendingInvitesByTarget.Remove(actor.Id);
        ClanState? previousApplicationClan = RemoveApplication(actor.Id);
        Save(wardAuthorizationChanged: true);
        BroadcastSnapshots(
            clan,
            ClanLocalization.EncodeStatus("clan_status_joined_guest_notice", actor.Name),
            actor.Id);
        if (previousEffectiveClan != null &&
            !ReferenceEquals(previousEffectiveClan, clan))
        {
            BroadcastSnapshots(previousEffectiveClan, "", actor.Id);
        }
        if (previousApplicationClan != null &&
            !ReferenceEquals(previousApplicationClan, clan))
        {
            SendModeratorSnapshots(
                previousApplicationClan,
                ClanLocalization.EncodeStatus(
                    "clan_status_application_withdrawn_notice",
                    actor.Name));
        }
        return ClanLocalization.EncodeStatus("clan_status_joined_guest", clan.Name);
    }

    private static string DeclineInvite(ClanPlayerRef actor, string inviteId)
    {
        if (!PendingInvitesByTarget.TryGetValue(actor.Id, out ClanInvite invite) ||
            !StringComparer.Ordinal.Equals(invite.InviteId, inviteId))
        {
            return ClanLocalization.EncodeStatus("clan_status_invite_missing");
        }

        PendingInvitesByTarget.Remove(actor.Id);
        Save();
        string clanName = ClansById.TryGetValue(invite.ClanId, out ClanState clan)
            ? clan.Name
            : invite.ClanId;
        return ClanLocalization.EncodeStatus("clan_status_invite_declined", clanName);
    }

    private static string ResolveApplication(
        ClanPlayerRef actor,
        string requestedClanId,
        string applicantId,
        bool accept)
    {
        ClanState? clan = RequirePermission(actor, requestedClanId, CanModerate);
        if (clan == null)
        {
            return ClanLocalization.EncodeStatus("clan_status_only_moderators_resolve");
        }

        if (!clan.Applications.TryGetValue(applicantId, out ClanPlayerRef applicant))
        {
            return ClanLocalization.EncodeStatus("clan_status_application_missing");
        }

        if (accept && clan.Members.ContainsKey(applicant.Id))
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_player_already_connected",
                applicant.Name,
                clan.Name);
        }
        if (accept && FindGuestClan(applicant) != null)
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_applicant_leave_guest",
                applicant.Name);
        }
        if (accept && clan.Members.Count >= ClanDataRules.MaxMembersPerClan)
        {
            return ClanLocalization.EncodeStatus("clan_status_member_limit", clan.Name);
        }

        if (accept)
        {
            ClanState? previousEffectiveClan = FindActiveClan(applicant);
            AddGuestToClan(clan, applicant);
            clan.Applications.Remove(applicantId);
            PendingInvitesByTarget.Remove(applicantId);
            Save(wardAuthorizationChanged: true);
            BroadcastSnapshots(
                clan,
                ClanLocalization.EncodeStatus(
                    "clan_status_joined_guest_notice",
                    applicant.Name),
                actor.Id);
            if (previousEffectiveClan != null &&
                !ReferenceEquals(previousEffectiveClan, clan))
            {
                BroadcastSnapshots(previousEffectiveClan, "", applicant.Id);
            }
            return ClanLocalization.EncodeStatus(
                "clan_status_application_accepted_guest",
                applicant.Name);
        }

        clan.Applications.Remove(applicantId);
        Save();
        SendSnapshotUpdate(
            applicant,
            ClanLocalization.EncodeStatus(
                "clan_status_application_rejected_you",
                clan.Name));
        SendModeratorSnapshots(
            clan,
            ClanLocalization.EncodeStatus(
                "clan_status_application_rejected_notice",
                applicant.Name),
            actor.Id);
        return ClanLocalization.EncodeStatus(
            "clan_status_application_rejected",
            applicant.Name);
    }

    private static string Kick(
        ClanPlayerRef actor,
        string requestedClanId,
        string targetId)
    {
        ClanState? clan = RequirePermission(actor, requestedClanId, CanModerate);
        if (clan == null)
        {
            return ClanLocalization.EncodeStatus("clan_status_remove_unauthorized");
        }

        if (!clan.Members.TryGetValue(targetId, out ClanMember target))
        {
            return ClanLocalization.EncodeStatus("clan_status_player_not_connected");
        }

        if (target.Role == ClanRole.Leader)
        {
            return ClanLocalization.EncodeStatus("clan_status_transfer_before_remove_leader");
        }

        if (!CanAffectMember(clan, actor, target))
        {
            return ClanLocalization.EncodeStatus("clan_status_cannot_remove_equal_role");
        }

        ClanState? previousEffectiveClan = FindActiveClan(target.Player);
        clan.Members.Remove(target.Player.Id);
        RemoveMembershipIndex(clan, target);
        if (FindPrimaryClan(target.Player) == null && FindGuestClan(target.Player) == null)
        {
            LastPositionUpdateByPlayer.Remove(target.Player.Id);
        }
        Save(wardAuthorizationChanged: true);
        SendSnapshotUpdate(
            target.Player,
            ClanLocalization.EncodeStatus("clan_status_removed_you", clan.Name));
        BroadcastSnapshots(
            clan,
            ClanLocalization.EncodeStatus(
                "clan_status_removed_notice",
                target.Player.Name),
            actor.Id);
        ClanState? currentEffectiveClan = FindActiveClan(target.Player);
        if (!ReferenceEquals(previousEffectiveClan, currentEffectiveClan) &&
            currentEffectiveClan != null &&
            !ReferenceEquals(currentEffectiveClan, clan))
        {
            BroadcastSnapshots(currentEffectiveClan, "", target.Player.Id);
        }
        return ClanLocalization.EncodeStatus("clan_status_removed", target.Player.Name);
    }

    private static string SetRole(
        ClanPlayerRef actor,
        string requestedClanId,
        string targetId,
        ClanRole requestedRole)
    {
        requestedRole = ClanDataRules.RequireAssignableRole(requestedRole, "clan role");
        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan == null ||
            !clan.Members.TryGetValue(actor.Id, out ClanMember actorMember) ||
            !CanModerate(actorMember))
        {
            return ClanLocalization.EncodeStatus("clan_status_only_moderators_roles");
        }

        if (!clan.Members.TryGetValue(targetId, out ClanMember target))
        {
            return ClanLocalization.EncodeStatus("clan_status_player_not_connected");
        }

        if (target.Player == actor)
        {
            return ClanLocalization.EncodeStatus("clan_status_cannot_change_own_role");
        }

        if (target.Role == ClanRole.Leader || requestedRole == ClanRole.Leader)
        {
            return ClanLocalization.EncodeStatus("clan_status_use_leadership_transfer");
        }

        if (!CanAffectMember(clan, actor, target))
        {
            return ClanLocalization.EncodeStatus("clan_status_cannot_change_equal_role");
        }

        if (ClanDataRules.GetRolePower(requestedRole) >=
            ClanDataRules.GetRolePower(actorMember.Role))
        {
            return ClanLocalization.EncodeStatus("clan_status_cannot_assign_equal_role");
        }

        if (target.Role == requestedRole)
        {
            return EncodeRoleStatus(
                "clan_status_player_already_role_",
                requestedRole,
                target.Player.Name);
        }

        bool wasGuest = target.Role == ClanRole.Guest;
        bool willBeGuest = requestedRole == ClanRole.Guest;
        if (wasGuest && !willBeGuest)
        {
            if (PrimaryClanByPlayerId.TryGetValue(target.Player.Id, out ClanState primaryClan))
            {
                if (!ReferenceEquals(primaryClan, clan))
                {
                    return ClanLocalization.EncodeStatus(
                        "clan_status_player_has_primary_clan",
                        target.Player.Name);
                }
                throw new InvalidDataException(
                    $"Player '{target.Player.Id}' is indexed as both Guest and primary in the same clan.");
            }
            if (!GuestClanByPlayerId.TryGetValue(target.Player.Id, out ClanState indexedGuestClan) ||
                !ReferenceEquals(indexedGuestClan, clan))
            {
                throw new InvalidDataException(
                    $"Guest membership index for '{target.Player.Id}' is inconsistent.");
            }

            GuestClanByPlayerId.Remove(target.Player.Id);
            PrimaryClanByPlayerId.Add(target.Player.Id, clan);
        }
        else if (!wasGuest && willBeGuest)
        {
            if (GuestClanByPlayerId.TryGetValue(target.Player.Id, out ClanState guestClan))
            {
                if (!ReferenceEquals(guestClan, clan))
                {
                    return ClanLocalization.EncodeStatus(
                        "clan_status_player_has_guest_clan",
                        target.Player.Name);
                }
                throw new InvalidDataException(
                    $"Player '{target.Player.Id}' is indexed as both primary and Guest in the same clan.");
            }
            if (FindApplicationClan(target.Player.Id) != null ||
                PendingInvitesByTarget.ContainsKey(target.Player.Id))
            {
                return ClanLocalization.EncodeStatus(
                    "clan_status_player_resolve_pending_guest",
                    target.Player.Name);
            }
            if (!PrimaryClanByPlayerId.TryGetValue(target.Player.Id, out ClanState indexedPrimaryClan) ||
                !ReferenceEquals(indexedPrimaryClan, clan))
            {
                throw new InvalidDataException(
                    $"Primary membership index for '{target.Player.Id}' is inconsistent.");
            }

            PrimaryClanByPlayerId.Remove(target.Player.Id);
            GuestClanByPlayerId.Add(target.Player.Id, clan);
        }

        target.Role = requestedRole;
        Save(wardAuthorizationChanged: true);
        if (!ReferenceEquals(FindActiveClan(target.Player), clan))
        {
            SendSnapshotUpdate(
                target.Player,
                EncodeRoleStatus(
                    "clan_status_your_role_changed_",
                    requestedRole,
                    clan.Name));
        }
        BroadcastSnapshots(
            clan,
            EncodeRoleStatus(
                "clan_status_role_changed_notice_",
                requestedRole,
                target.Player.Name),
            actor.Id);
        return EncodeRoleStatus(
            "clan_status_role_set_",
            requestedRole,
            target.Player.Name);
    }

    private static string TransferLeadership(
        ClanPlayerRef actor,
        string requestedClanId,
        string targetId)
    {
        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan != null && !clan.IsLeader(actor))
        {
            clan = null;
        }
        if (clan == null)
        {
            return ClanLocalization.EncodeStatus("clan_status_only_leader_transfer");
        }

        if (!clan.Members.TryGetValue(targetId, out ClanMember target) ||
            target.Role is not ClanRole.Officer and not ClanRole.Member)
        {
            return ClanLocalization.EncodeStatus("clan_status_transfer_target_role");
        }

        if (target.Player == actor)
        {
            return ClanLocalization.EncodeStatus("clan_status_already_leader");
        }
        if (!ReferenceEquals(FindActiveClan(target.Player), clan))
        {
            return ClanLocalization.EncodeStatus(
                "clan_status_leave_guest_before_leadership",
                target.Player.Name);
        }

        clan.Members[actor.Id].Role = ClanRole.Member;
        target.Role = ClanRole.Leader;
        Save(wardAuthorizationChanged: true);
        BroadcastSnapshots(
            clan,
            ClanLocalization.EncodeStatus(
                "clan_status_now_leader_notice",
                target.Player.Name),
            actor.Id);
        return ClanLocalization.EncodeStatus(
            "clan_status_leadership_transferred",
            target.Player.Name);
    }

    private static string UpdateClanProfile(
        ClanPlayerRef actor,
        string requestedClanId,
        string requestedName,
        string requestedDescription,
        string requestedEmblemKey)
    {
        string clanId = ClanDataRules.RequireClanId(requestedClanId);
        string clanName = ClanDataRules.RequireClanName(requestedName);
        string description = ClanDataRules.RequireClanDescription(requestedDescription);
        string emblemKey = ClanDataRules.RequireClanEmblemKey(requestedEmblemKey);
        if (!ClanEmoji.IsAvailableEmblemKey(emblemKey))
        {
            return ClanLocalization.EncodeStatus("clan_status_emblem_unavailable");
        }
        ClanState? clan = FindActiveClan(actor);
        if (clan == null ||
            !clan.IsLeader(actor) ||
            !StringComparer.Ordinal.Equals(clan.ClanId, clanId))
        {
            return ClanLocalization.EncodeStatus("clan_status_only_leader_update_profile");
        }

        return ApplyClanProfileChange(
            clan,
            clanName,
            description,
            emblemKey,
            actor.Id).Status;
    }

    private static OperationOutcome RenameClan(
        ClanPlayerRef actor,
        string requestedClanId,
        string requestedName)
    {
        string clanId = ClanDataRules.RequireClanId(requestedClanId);
        string clanName = ClanDataRules.RequireClanName(requestedName);
        ClanState? clan = FindActiveClan(actor);
        if (clan == null || !StringComparer.Ordinal.Equals(clan.ClanId, clanId))
        {
            return new OperationOutcome(
                ClanOperationResultCode.ClanChanged,
                ClanLocalization.EncodeStatus("clan_status_clan_changed_before_rename"));
        }
        if (!clan.IsLeader(actor))
        {
            return new OperationOutcome(
                ClanOperationResultCode.Unauthorized,
                ClanLocalization.EncodeStatus("clan_status_only_leader_rename"));
        }
        OperationOutcome outcome = ApplyClanProfileChange(
            clan,
            clanName,
            clan.Description,
            clan.EmblemKey,
            actor.Id);
        return outcome.Code switch
        {
            ClanOperationResultCode.Success => new OperationOutcome(
                ClanOperationResultCode.Success,
                ClanLocalization.EncodeStatus("clan_status_renamed", clanName)),
            ClanOperationResultCode.Unchanged => new OperationOutcome(
                ClanOperationResultCode.Unchanged,
                ClanLocalization.EncodeStatus("clan_status_name_unchanged")),
            _ => outcome
        };
    }

    private static OperationOutcome ApplyClanProfileChange(
        ClanState clan,
        string clanName,
        string description,
        string emblemKey,
        string actorId)
    {
        if (ClansByName.TryGetValue(clanName, out ClanState existingClan) &&
            !ReferenceEquals(existingClan, clan))
        {
            return new OperationOutcome(
                ClanOperationResultCode.NameTaken,
                ClanLocalization.EncodeStatus("clan_status_name_exists", clanName));
        }

        bool profileChanged =
            !StringComparer.Ordinal.Equals(clan.Name, clanName) ||
            !StringComparer.Ordinal.Equals(clan.Description, description) ||
            !StringComparer.Ordinal.Equals(clan.EmblemKey, emblemKey);
        if (!profileChanged)
        {
            return new OperationOutcome(
                ClanOperationResultCode.Unchanged,
                ClanLocalization.EncodeStatus("clan_status_profile_unchanged"));
        }

        ClanState committedClan = CommitClanProfile(
            clan,
            clanName,
            description,
            emblemKey);
        NotifyProfileCommitted(committedClan, actorId);
        return new OperationOutcome(
            ClanOperationResultCode.Success,
            ClanLocalization.EncodeStatus("clan_status_profile_updated"));
    }

    private static ClanState CommitClanProfile(
        ClanState clan,
        string clanName,
        string description,
        string emblemKey)
    {
        if (!ClansById.TryGetValue(clan.ClanId, out ClanState indexedClan) ||
            !ReferenceEquals(indexedClan, clan) ||
            !ClansByName.TryGetValue(clan.Name, out ClanState namedClan) ||
            !ReferenceEquals(namedClan, clan))
        {
            throw new InvalidOperationException(
                $"Clan '{clan.ClanId}' is inconsistent with the registry indexes.");
        }

        string saveFile = ResolveSaveFile();
        if (!string.Equals(_loadedSaveFile, saveFile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The clan registry storage changed while clan state was being updated.");
        }

        ValidateClanIndexes();
        YamlRegistryDocument document = CreateSaveDocument();
        YamlClanDto? target = document.Clans?
            .SingleOrDefault(candidate =>
                candidate != null &&
                StringComparer.Ordinal.Equals(candidate.ClanId, clan.ClanId));
        if (target == null)
        {
            throw new InvalidOperationException(
                $"Clan '{clan.ClanId}' is missing from the save candidate.");
        }

        target.Name = ClanDataRules.RequireClanName(clanName);
        target.Description = ClanDataRules.RequireClanDescription(description);
        target.EmblemKey = ClanDataRules.RequireClanEmblemKey(emblemKey);

        byte[] bytes = SerializeSave(document);
        if (bytes.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
        }
        RegistryData candidate = ParseSave(bytes);
        PreserveRuntimeMemberState(candidate);

        try
        {
            WriteAtomically(saveFile, bytes);
        }
        catch
        {
            RecoverPersistedStateAfterSaveFailure();
            throw;
        }

        SwapState(candidate);
        _directoryInvalidationPending = true;
        ClanApi.NotifyRegistryChanged();
        return ClansById[clan.ClanId];
    }

    private static string Leave(ClanPlayerRef actor, string requestedClanId)
    {
        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan == null || !clan.Members.TryGetValue(actor.Id, out ClanMember member))
        {
            return ClanLocalization.EncodeStatus("clan_status_not_connected_to_selected_clan");
        }

        if (clan.IsLeader(actor) &&
            clan.Members.Values.Count(member => member.Role != ClanRole.Guest) > 1)
        {
            return ClanLocalization.EncodeStatus("clan_status_transfer_before_leaving");
        }

        clan.Members.Remove(actor.Id);
        RemoveMembershipIndex(clan, member);
        if (FindPrimaryClan(actor) == null && FindGuestClan(actor) == null)
        {
            LastPositionUpdateByPlayer.Remove(actor.Id);
        }
        if (!clan.Members.Values.Any(member => member.Role != ClanRole.Guest))
        {
            Dictionary<string, ClanPlayerRef> affectedPlayers = new(StringComparer.Ordinal);
            HashSet<ClanState> fallbackClans = new();
            foreach (ClanMember remaining in clan.Members.Values)
            {
                affectedPlayers[remaining.Player.Id] = remaining.Player;
                ClanState? fallbackClan = FindPrimaryClan(remaining.Player);
                if (fallbackClan != null && !ReferenceEquals(fallbackClan, clan))
                {
                    fallbackClans.Add(fallbackClan);
                }
            }
            foreach (ClanPlayerRef applicant in clan.Applications.Values)
            {
                affectedPlayers[applicant.Id] = applicant;
            }
            foreach (ClanInvite invite in PendingInvitesByTarget.Values)
            {
                if (string.Equals(
                        invite.ClanId,
                        clan.ClanId,
                        StringComparison.Ordinal))
                {
                    affectedPlayers[invite.Target.Id] = invite.Target;
                }
            }

            RemoveClan(clan);
            Save(wardAuthorizationChanged: true);
            foreach (ClanPlayerRef affected in affectedPlayers.Values)
            {
                if (affected != actor)
                {
                    SendSnapshotUpdate(
                        affected,
                        ClanLocalization.EncodeStatus(
                            "clan_status_disbanded_notice",
                            clan.Name));
                }
            }
            foreach (ClanState fallbackClan in fallbackClans)
            {
                BroadcastSnapshots(fallbackClan, "");
            }

            return ClanLocalization.EncodeStatus("clan_status_disbanded", clan.Name);
        }

        Save(wardAuthorizationChanged: true);
        BroadcastSnapshots(
            clan,
            ClanLocalization.EncodeStatus(
                "clan_status_left_notice",
                actor.Name,
                clan.Name));
        ClanState? currentEffectiveClan = FindActiveClan(actor);
        if (currentEffectiveClan != null && !ReferenceEquals(currentEffectiveClan, clan))
        {
            BroadcastSnapshots(currentEffectiveClan, "", actor.Id);
        }
        return member.Role == ClanRole.Guest
            ? ClanLocalization.EncodeStatus("clan_status_left_guest", clan.Name)
            : ClanLocalization.EncodeStatus("clan_status_left", clan.Name);
    }

    private static string SendClanChat(
        ClanPlayerRef actor,
        string requestedClanId,
        string requestedMessage)
    {
        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan == null ||
            !clan.Members.TryGetValue(actor.Id, out ClanMember member))
        {
            return ClanLocalization.EncodeStatus("clan_status_not_connected_to_selected_clan");
        }

        string message = ClanDataRules.RequireText(
            requestedMessage,
            ClanDataRules.MaxChatMessageLength,
            "clan chat message",
            allowEmpty: false,
            allowLineBreaks: false);
        float now = Time.realtimeSinceStartup;
        if (now >= member.LastClanChatTime &&
            now - member.LastClanChatTime < ClanChatInterval)
        {
            return "";
        }

        member.LastClanChatTime = now;
        string acceptedClanId = clan.ClanId;
        ClanApi.NotifyServerChatAccepted(actor, acceptedClanId, clan.Name, message);
        // A subscriber can synchronously replace the registry. Deliver to the current
        // members of the accepted clan, even if the sender changed clans in the callback.
        if (ClansById.TryGetValue(acceptedClanId, out ClanState currentClan))
        {
            ClanRpc.BroadcastChat(currentClan, actor.Name, message);
        }
        return "";
    }

    private static string SendClanPing(
        ClanPlayerRef actor,
        string requestedClanId,
        Vector3 position)
    {
        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan == null ||
            !clan.Members.TryGetValue(actor.Id, out ClanMember member))
        {
            return ClanLocalization.EncodeStatus("clan_status_not_connected_to_selected_clan");
        }

        ClanDataRules.RequireFiniteVector(position, "ping position");
        float now = Time.realtimeSinceStartup;
        if (now >= member.LastClanPingTime &&
            now - member.LastClanPingTime < ClanPingInterval)
        {
            return "";
        }

        member.LastClanPingTime = now;
        ClanRpc.BroadcastPing(clan, actor, position);
        return "";
    }

    private static string UpdatePosition(
        ClanPlayerRef actor,
        ZNetPeer? peer,
        string requestedClanId,
        Vector3 requestedPosition)
    {
        if (!ClanPlugin.ShareClanPositions.Value.IsOn())
        {
            return "";
        }

        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan == null)
        {
            return "";
        }

        Vector3 position = peer != null ? peer.m_refPos : requestedPosition;
        ClanDataRules.RequireFiniteVector(position, "player position");
        float now = Time.realtimeSinceStartup;
        if (LastPositionUpdateByPlayer.TryGetValue(actor.Id, out float lastUpdate) &&
            now >= lastUpdate &&
            now - lastUpdate < PositionUpdateInterval)
        {
            return "";
        }

        LastPositionUpdateByPlayer[actor.Id] = now;
        ClanRpc.BroadcastPosition(clan, actor, position);
        return "";
    }

    internal static bool IsEffectiveMember(ClanState clan, ClanMember member)
    {
        return ReferenceEquals(FindActiveClan(member.Player), clan);
    }

    private static ClanState? RequirePermission(
        ClanPlayerRef actor,
        string requestedClanId,
        Func<ClanMember, bool> canUse)
    {
        ClanState? clan = FindEffectiveClan(actor, requestedClanId);
        if (clan == null || !clan.Members.TryGetValue(actor.Id, out ClanMember member))
        {
            return null;
        }

        return canUse(member) ? clan : null;
    }

    private static bool CanModerate(ClanMember member)
    {
        return member.Role is ClanRole.Leader or ClanRole.Officer;
    }

    private static string EncodeRoleStatus(
        string keyPrefix,
        ClanRole role,
        params object[] arguments)
    {
        string suffix = role switch
        {
            ClanRole.Leader => "leader",
            ClanRole.Officer => "officer",
            ClanRole.Member => "member",
            ClanRole.Guest => "guest",
            _ => throw new InvalidDataException("Clan role is unsupported.")
        };
        return ClanLocalization.EncodeStatus(keyPrefix + suffix, arguments);
    }

    private static bool CanAffectMember(ClanState clan, ClanPlayerRef actor, ClanMember target)
    {
        return clan.Members.TryGetValue(actor.Id, out ClanMember actorMember) &&
               ClanDataRules.GetRolePower(actorMember.Role) >
               ClanDataRules.GetRolePower(target.Role);
    }

    private static void AddGuestToClan(ClanState clan, ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            throw new InvalidDataException("Cannot add an unidentified player to a clan.");
        }

        if (clan.Members.ContainsKey(player.Id))
        {
            throw new InvalidOperationException($"Player is already connected to '{clan.Name}'.");
        }
        if (GuestClanByPlayerId.ContainsKey(player.Id))
        {
            throw new InvalidOperationException(
                "Player must leave their current guest clan before joining another as Guest.");
        }
        if (clan.Members.Count >= ClanDataRules.MaxMembersPerClan)
        {
            throw new InvalidOperationException($"Clan '{clan.Name}' has reached the member limit.");
        }

        ClanMember member = new()
        {
            Player = player,
            Role = ClanRole.Guest
        };
        clan.Members.Add(player.Id, member);
        GuestClanByPlayerId.Add(player.Id, clan);
    }

    private static void RemoveMembershipIndex(ClanState clan, ClanMember member)
    {
        Dictionary<string, ClanState> index = member.Role == ClanRole.Guest
            ? GuestClanByPlayerId
            : PrimaryClanByPlayerId;
        if (!index.TryGetValue(member.Player.Id, out ClanState indexedClan) ||
            !ReferenceEquals(indexedClan, clan))
        {
            throw new InvalidDataException(
                $"Membership index for '{member.Player.Id}' in clan '{clan.ClanId}' is inconsistent.");
        }
        index.Remove(member.Player.Id);
    }

    private static ClanState? RemoveApplication(string playerId)
    {
        ClanState? existingClan = FindApplicationClan(playerId);
        if (existingClan == null)
        {
            return null;
        }

        existingClan.Applications.Remove(playerId);
        return existingClan;
    }

    private static ClanState? FindApplicationClan(string playerId)
    {
        return ClansById.Values.FirstOrDefault(clan => clan.Applications.ContainsKey(playerId));
    }

    private static void RemoveClan(ClanState clan)
    {
        ClansById.Remove(clan.ClanId);
        ClansByName.Remove(clan.Name);
        foreach (ClanMember member in clan.Members.Values)
        {
            RemoveMembershipIndex(clan, member);
            if (FindPrimaryClan(member.Player) == null && FindGuestClan(member.Player) == null)
            {
                LastPositionUpdateByPlayer.Remove(member.Player.Id);
            }
        }

        string[] inviteTargets = PendingInvitesByTarget
            .Where(pair => StringComparer.Ordinal.Equals(pair.Value.ClanId, clan.ClanId))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string targetId in inviteTargets)
        {
            PendingInvitesByTarget.Remove(targetId);
        }
    }

    private static ClanMember GetLeader(ClanState clan)
    {
        ClanMember? leader = clan.Members.Values.FirstOrDefault(member =>
            member.Role == ClanRole.Leader);
        return leader ?? throw new InvalidDataException($"Clan '{clan.Name}' has no leader.");
    }

    private static void BroadcastSnapshots(
        ClanState clan,
        string status,
        string excludedPlayerId = "")
    {
        foreach (ClanMember member in clan.Members.Values)
        {
            if (StringComparer.Ordinal.Equals(member.Player.Id, excludedPlayerId) ||
                !ReferenceEquals(FindActiveClan(member.Player), clan))
            {
                continue;
            }

            SendSnapshotUpdate(member.Player, status);
        }
    }

    private static void BroadcastPresenceSnapshots(ClanPlayerRef connectedPlayer)
    {
        ClanState? clan = FindActiveClan(connectedPlayer);
        if (clan != null)
        {
            BroadcastSnapshots(clan, "", connectedPlayer.Id);
        }
    }

    private static void BroadcastProfileSnapshots(ClanState clan, string excludedPlayerId)
    {
        HashSet<string> notifiedPlayerIds = new(StringComparer.Ordinal)
        {
            excludedPlayerId
        };
        foreach (ClanMember member in clan.Members.Values)
        {
            if (notifiedPlayerIds.Add(member.Player.Id))
            {
                SendSnapshotUpdate(
                    member.Player,
                    ClanLocalization.EncodeStatus("clan_status_profile_updated"));
            }
        }

        foreach (ClanPlayerRef applicant in clan.Applications.Values)
        {
            if (notifiedPlayerIds.Add(applicant.Id))
            {
                SendSnapshotUpdate(
                    applicant,
                    ClanLocalization.EncodeStatus("clan_status_profile_updated"));
            }
        }

        foreach (ClanInvite invite in PendingInvitesByTarget.Values)
        {
            if (StringComparer.Ordinal.Equals(invite.ClanId, clan.ClanId) &&
                notifiedPlayerIds.Add(invite.Target.Id))
            {
                SendSnapshotUpdate(
                    invite.Target,
                    ClanLocalization.EncodeStatus("clan_status_profile_updated"));
            }
        }
    }

    private static void NotifyProfileCommitted(ClanState clan, string excludedPlayerId)
    {
        try
        {
            BroadcastProfileSnapshots(clan, excludedPlayerId);
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan profile '{clan.ClanId}' was committed, but notifying clients failed: " +
                exception);
        }
    }

    private static void FlushDirectoryInvalidation()
    {
        if (!_directoryInvalidationPending)
        {
            return;
        }

        _directoryInvalidationPending = false;
        try
        {
            ClanRpc.BroadcastDirectoryInvalidation();
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan data was saved, but invalidating client directories failed: {exception}");
        }
    }

    private static void SendModeratorSnapshots(
        ClanState clan,
        string status,
        string excludedPlayerId = "")
    {
        foreach (ClanMember member in clan.Members.Values)
        {
            if (CanModerate(member) &&
                ReferenceEquals(FindActiveClan(member.Player), clan) &&
                !StringComparer.Ordinal.Equals(
                    member.Player.Id,
                    excludedPlayerId))
            {
                SendSnapshotUpdate(member.Player, status);
            }
        }
    }

    private static void SendSnapshotUpdate(
        ClanPlayerRef player,
        string status,
        string forcedOfflinePlayerId = "")
    {
        try
        {
            ZNetPeer? peer = ClanIdentity.FindPeer(player);
            bool isLocalPlayer =
                Player.m_localPlayer != null &&
                player == ClanPlayerRef.Local();
            if (peer == null && !isLocalPlayer)
            {
                return;
            }

            ClanRpc.SendSnapshot(
                peer,
                BuildSnapshotFor(player, status, forcedOfflinePlayerId));
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Failed to update clan snapshot for {player}: {ex.Message}");
        }
    }

    private static void RefreshPlayer(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return;
        }

        if (PrimaryClanByPlayerId.TryGetValue(player.Id, out ClanState primaryClan) &&
            primaryClan.Members.TryGetValue(player.Id, out ClanMember primaryMember))
        {
            primaryMember.Player = player;
        }
        if (GuestClanByPlayerId.TryGetValue(player.Id, out ClanState guestClan) &&
            guestClan.Members.TryGetValue(player.Id, out ClanMember guestMember))
        {
            guestMember.Player = player;
        }

        if (PendingInvitesByTarget.TryGetValue(player.Id, out ClanInvite invite))
        {
            invite.Target = player;
        }

        ClanState? applicationClan = GuestClanByPlayerId.ContainsKey(player.Id)
            ? null
            : FindApplicationClan(player.Id);
        if (applicationClan != null &&
            applicationClan.Applications.ContainsKey(player.Id))
        {
            applicationClan.Applications[player.Id] = player;
        }
    }

    private static bool TryFindKnownPlayerById(string targetId, out ClanPlayerRef player)
    {
        string id = ClanDataRules.RequirePlayerKey(targetId, "target player key");
        foreach (ClanPlayerRef online in ClanIdentity.GetOnlinePlayerRefs())
        {
            if (StringComparer.Ordinal.Equals(online.Id, id))
            {
                player = online;
                return true;
            }
        }

        return ClanRecentPlayers.TryGetPlayer(id, out player);
    }

    private static ClanPlayerSummary ToPlayerSummary(
        ClanMember member,
        ClanPlayerRef viewer,
        bool isOnline)
    {
        return new ClanPlayerSummary
        {
            Player = member.Player,
            Role = member.Role,
            IsSelf = member.Player == viewer,
            IsOnline = isOnline
        };
    }

    private static ClanApplicationSummary ToApplicationSummary(ClanPlayerRef applicant)
    {
        return new ClanApplicationSummary
        {
            PlayerId = applicant.Id,
            PlayerName = applicant.Name
        };
    }

    private static void EnsureLoaded()
    {
        string saveFile = ResolveSaveFile();
        if (string.Equals(_loadedSaveFile, saveFile, StringComparison.Ordinal))
        {
            return;
        }

        RegistryData loaded = Load(saveFile);
        SwapState(loaded);
        LastPositionUpdateByPlayer.Clear();
        AnnouncedOnlinePlayerIds.Clear();
        HudCacheByViewerId.Clear();
        _loadedSaveFile = saveFile;
    }

    private static string ResolveSaveFile()
    {
        return Path.Combine(ClanPlugin.DataDirectory, "clans.yml");
    }

    private static RegistryData Load(string saveFile)
    {
        string backupFile = saveFile + ".bak";
        if (TryLoadSave(
                saveFile,
                out _,
                out RegistryData? primary,
                out Exception? primaryError))
        {
            ClanPlugin.ClanLogger.LogInfo(
                $"Loaded {primary!.ClansById.Count} clans from {saveFile}.");
            return primary!;
        }

        if (primaryError == null)
        {
            if (!TryLoadSave(
                    backupFile,
                    out byte[]? backupBytes,
                    out RegistryData? recovered,
                    out Exception? backupError))
            {
                if (backupError != null)
                {
                    string quarantinedBackup = QuarantineUnsupportedSave(backupFile);
                    ClanPlugin.ClanLogger.LogWarning(
                        $"Clan save was missing and its backup was unsupported or invalid. " +
                        $"The backup was moved to {quarantinedBackup}; the clan registry will start empty: " +
                        backupError.Message);
                }

                return new RegistryData();
            }

            WriteAtomically(saveFile, backupBytes!);
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was missing. Restored {recovered!.ClansById.Count} clans from " +
                $"the validated backup {backupFile} to {saveFile}.");
            return recovered!;
        }

        byte[]? recoveryBytes = null;
        RegistryData? recovery = null;
        Exception? recoveryError = null;
        if (TryLoadSave(
                backupFile,
                out recoveryBytes,
                out recovery,
                out recoveryError))
        {
            string quarantinedPrimary = QuarantineUnsupportedSave(saveFile);
            WriteAtomically(saveFile, recoveryBytes!);
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was unsupported or invalid and was moved to {quarantinedPrimary}: " +
                $"{primaryError.Message} Restored {recovery!.ClansById.Count} clans from " +
                $"the validated same-version backup {backupFile}.");
            return recovery!;
        }

        string quarantinedPrimaryWithoutRecovery = QuarantineUnsupportedSave(saveFile);
        if (recoveryError != null)
        {
            string quarantinedBackup = QuarantineUnsupportedSave(backupFile);
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was unsupported or invalid and was moved to {quarantinedPrimaryWithoutRecovery}: " +
                $"{primaryError.Message} Its backup was also unsupported or invalid and was moved " +
                $"to {quarantinedBackup}: {recoveryError.Message} The clan registry will start empty.");
        }
        else
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan save was unsupported or invalid and was moved to {quarantinedPrimaryWithoutRecovery}: " +
                $"{primaryError.Message} No backup was available; the clan registry will start empty.");
        }

        return new RegistryData();
    }

    private static bool TryLoadSave(
        string saveFile,
        out byte[]? bytes,
        out RegistryData? data,
        out Exception? invalidContent)
    {
        bytes = null;
        data = null;
        invalidContent = null;
        try
        {
            bytes = ReadSaveBytes(saveFile);
            data = ParseSave(bytes);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (IsInvalidSaveContent(error))
        {
            invalidContent = error;
            return false;
        }
    }

    private static bool IsInvalidSaveContent(Exception error)
    {
        return error is InvalidDataException or
            DecoderFallbackException or
            YamlDotNet.Core.YamlException;
    }

    private static RegistryData ParseSave(byte[] bytes)
    {
        string yaml = DecodeStrictUtf8(bytes);
        YamlRegistryDocument? document = YamlDeserializer.Deserialize<YamlRegistryDocument>(yaml);
        if (document == null)
        {
            throw new InvalidDataException("Clan save is empty.");
        }
        if (document.FormatVersion != SaveFormatVersion)
        {
            throw new InvalidDataException(
                $"Save format version {document.FormatVersion} is unsupported; " +
                $"only version {SaveFormatVersion} is accepted.");
        }
        if (document.Clans == null)
        {
            throw new InvalidDataException("Clan save is missing the clans sequence.");
        }
        if (document.PendingInvites == null)
        {
            throw new InvalidDataException("Clan save is missing the pending_invites sequence.");
        }

        RegistryData data = new();
        HashSet<long> creationOrders = new();
        HashSet<string> applicantIds = new(StringComparer.Ordinal);
        Dictionary<string, ClanState> applicationClanByPlayer = new(StringComparer.Ordinal);
        ClanDataRules.RequireCount(document.Clans.Count, ClanDataRules.MaxClans, "clan");
        foreach (YamlClanDto? serializedClan in document.Clans)
        {
            ClanState clan = ReadClan(serializedClan);
            if (!creationOrders.Add(clan.CreationOrder))
            {
                throw new InvalidDataException(
                    $"Duplicate clan creation order '{clan.CreationOrder}'.");
            }
            if (data.ClansById.ContainsKey(clan.ClanId))
            {
                throw new InvalidDataException($"Duplicate clan id '{clan.ClanId}'.");
            }
            if (data.ClansByName.ContainsKey(clan.Name))
            {
                throw new InvalidDataException($"Duplicate clan name '{clan.Name}'.");
            }
            data.ClansById.Add(clan.ClanId, clan);
            data.ClansByName.Add(clan.Name, clan);

            foreach (KeyValuePair<string, ClanMember> memberPair in clan.Members)
            {
                Dictionary<string, ClanState> index = memberPair.Value.Role == ClanRole.Guest
                    ? data.GuestClanByPlayerId
                    : data.PrimaryClanByPlayerId;
                if (index.ContainsKey(memberPair.Key))
                {
                    string slotName = memberPair.Value.Role == ClanRole.Guest
                        ? "Guest"
                        : "primary";
                    throw new InvalidDataException(
                        $"Player '{memberPair.Key}' belongs to more than one {slotName} clan.");
                }
                index.Add(memberPair.Key, clan);
            }

            foreach (string applicantId in clan.Applications.Keys)
            {
                if (!applicantIds.Add(applicantId))
                {
                    throw new InvalidDataException($"Player '{applicantId}' has more than one application.");
                }
                applicationClanByPlayer.Add(applicantId, clan);
            }
        }

        foreach (string applicantId in applicantIds)
        {
            ClanState applicationClan = applicationClanByPlayer[applicantId];
            if (data.GuestClanByPlayerId.ContainsKey(applicantId))
            {
                throw new InvalidDataException(
                    $"Player '{applicantId}' cannot have a Guest clan and a pending application.");
            }
            if (applicationClan.Members.ContainsKey(applicantId))
            {
                throw new InvalidDataException(
                    $"Player '{applicantId}' cannot apply to a clan they already belong to.");
            }
        }

        ClanDataRules.RequireCount(
            document.PendingInvites.Count,
            ClanDataRules.MaxInvites,
            "invite");
        HashSet<string> inviteIds = new(StringComparer.Ordinal);
        foreach (YamlInviteDto? serializedInvite in document.PendingInvites)
        {
            ClanInvite invite = ReadInvite(serializedInvite);
            if (!inviteIds.Add(invite.InviteId))
            {
                throw new InvalidDataException($"Duplicate invite id '{invite.InviteId}'.");
            }

            if (!data.ClansById.TryGetValue(invite.ClanId, out ClanState inviteClan))
            {
                throw new InvalidDataException($"Invite references missing clan '{invite.ClanId}'.");
            }

            if (data.GuestClanByPlayerId.ContainsKey(invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Invite target '{invite.Target.Id}' already has a Guest clan.");
            }
            if (inviteClan.Members.ContainsKey(invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Invite target '{invite.Target.Id}' already belongs to the inviting clan.");
            }

            if (data.PendingInvitesByTarget.ContainsKey(invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Player '{invite.Target.Id}' has more than one pending invite.");
            }
            data.PendingInvitesByTarget.Add(invite.Target.Id, invite);
        }

        return data;
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        int offset = bytes.Length >= 3 &&
                     bytes[0] == 0xEF &&
                     bytes[1] == 0xBB &&
                     bytes[2] == 0xBF
            ? 3
            : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static ClanState ReadClan(YamlClanDto? source)
    {
        if (source == null)
        {
            throw new InvalidDataException("Clan save contains a null clan entry.");
        }

        ClanState clan = new(ClanDataRules.RequireClanId(source.ClanId))
        {
            CreationOrder = RequireClanCreationOrder(source.CreationOrder),
            Name = ClanDataRules.RequireClanName(source.Name),
            Description = ClanDataRules.RequireClanDescription(source.Description),
            EmblemKey = ClanDataRules.RequireClanEmblemKey(source.EmblemKey)
        };

        if (source.Members == null)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' is missing the members sequence.");
        }
        ClanDataRules.RequireCount(
            source.Members.Count,
            ClanDataRules.MaxMembersPerClan,
            "clan member");
        if (source.Members.Count == 0)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' has no members.");
        }

        int leaderCount = 0;
        foreach (YamlMemberDto? serializedMember in source.Members)
        {
            if (serializedMember == null)
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains a null member entry.");
            }

            ClanPlayerRef player = ReadPlayer(serializedMember.Player, "clan member");
            ClanRole role = ParseRole(serializedMember.Role, "clan role");
            if (role == ClanRole.Leader)
            {
                leaderCount++;
            }

            if (clan.Members.ContainsKey(player.Id))
            {
                throw new InvalidDataException($"Duplicate member '{player.Id}' in clan '{clan.Name}'.");
            }
            clan.Members.Add(player.Id, new ClanMember
            {
                Player = player,
                Role = role
            });
        }

        if (leaderCount != 1)
        {
            throw new InvalidDataException(
                $"Clan '{clan.Name}' must have exactly one leader; found {leaderCount}.");
        }

        if (source.Applications == null)
        {
            throw new InvalidDataException(
                $"Clan '{clan.Name}' is missing the applications sequence.");
        }
        ClanDataRules.RequireCount(
            source.Applications.Count,
            ClanDataRules.MaxApplicationsPerClan,
            "application");
        foreach (YamlApplicationDto? serializedApplication in source.Applications)
        {
            if (serializedApplication == null)
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains a null application entry.");
            }

            ClanPlayerRef applicant = ReadPlayer(
                serializedApplication.Player,
                "application player");
            if (clan.Applications.ContainsKey(applicant.Id))
            {
                throw new InvalidDataException(
                    $"Duplicate application from '{applicant.Id}' in clan '{clan.Name}'.");
            }
            clan.Applications.Add(applicant.Id, applicant);
        }

        return clan;
    }

    private static ClanInvite ReadInvite(YamlInviteDto? source)
    {
        if (source == null)
        {
            throw new InvalidDataException("Clan save contains a null invite entry.");
        }

        return new ClanInvite
        {
            InviteId = ClanDataRules.RequireText(
                source.InviteId,
                ClanDataRules.MaxInviteIdLength,
                "invite id",
                allowEmpty: false),
            ClanId = ClanDataRules.RequireClanId(source.ClanId, "invite clan id"),
            FromName = ClanDataRules.RequireText(
                source.FromName,
                ClanDataRules.MaxPlayerNameLength,
                "invite sender name"),
            Target = ReadPlayer(source.Target, "invite target")
        };
    }

    private static ClanPlayerRef ReadPlayer(YamlPlayerDto? source, string fieldName)
    {
        if (source == null)
        {
            throw new InvalidDataException($"{fieldName} is missing.");
        }

        string platformId = ClanDataRules.RequirePlatformId(
            source.PlatformId,
            fieldName + " platform id");
        long playerId = ClanDataRules.RequireCharacterPlayerId(
            source.CharacterPlayerId,
            fieldName + " character player id");
        string name = ClanDataRules.RequireText(
            source.Name,
            ClanDataRules.MaxPlayerNameLength,
            fieldName + " name");
        ClanPlayerRef player = new(platformId, playerId, name);
        if (!player.IsValid)
        {
            throw new InvalidDataException($"{fieldName} identity is invalid.");
        }
        return player;
    }

    private static ClanRole ParseRole(string? value, string fieldName)
    {
        string name = ClanDataRules.RequireText(
            value,
            16,
            fieldName,
            allowEmpty: false);
        return name switch
        {
            "leader" => ClanRole.Leader,
            "officer" => ClanRole.Officer,
            "member" => ClanRole.Member,
            "guest" => ClanRole.Guest,
            _ => throw new InvalidDataException($"{fieldName} has unknown value '{name}'.")
        };
    }

    private static void Save(bool wardAuthorizationChanged = false)
    {
        try
        {
            string saveFile = ResolveSaveFile();
            if (!string.Equals(_loadedSaveFile, saveFile, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The clan registry storage changed while clan state was being updated.");
            }

            ValidateClanIndexes();
            ClanDataRules.RequireCount(ClansById.Count, ClanDataRules.MaxClans, "clan");
            ClanDataRules.RequireCount(
                PendingInvitesByTarget.Count,
                ClanDataRules.MaxInvites,
                "invite");

            YamlRegistryDocument document = CreateSaveDocument();
            byte[] bytes = SerializeSave(document);
            if (bytes.Length > MaximumSaveBytes)
            {
                throw new InvalidDataException(
                    $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
            }

            // Never replace a known-good save with output that this exact reader cannot consume.
            _ = ParseSave(bytes);
            WriteAtomically(saveFile, bytes);
            _directoryInvalidationPending = true;
        }
        catch
        {
            RecoverPersistedStateAfterSaveFailure();
            throw;
        }

        if (wardAuthorizationChanged)
        {
            ClanApi.NotifyRegistryChanged();
        }
    }

    private static void RecoverPersistedStateAfterSaveFailure()
    {
        if (RestorePersistedState())
        {
            return;
        }

        _loadedSaveFile = null;
        LastPositionUpdateByPlayer.Clear();
        SwapState(new RegistryData());
        ClanPlugin.ClanLogger.LogError(
            "Clan registry was invalidated after both saving and restoring the persisted " +
            "state failed. The next registry access must reload clans.yml.");
    }

    private static void PreserveRuntimeMemberState(RegistryData candidate)
    {
        foreach (KeyValuePair<string, ClanState> clanPair in ClansById)
        {
            if (!candidate.ClansById.TryGetValue(clanPair.Key, out ClanState candidateClan))
            {
                continue;
            }

            foreach (KeyValuePair<string, ClanMember> memberPair in clanPair.Value.Members)
            {
                if (!candidateClan.Members.TryGetValue(
                        memberPair.Key,
                        out ClanMember candidateMember))
                {
                    continue;
                }

                candidateMember.LastClanChatTime = memberPair.Value.LastClanChatTime;
                candidateMember.LastClanPingTime = memberPair.Value.LastClanPingTime;
            }
        }
    }

    private static void ValidateClanIndexes()
    {
        if (ClansById.Count != ClansByName.Count)
        {
            throw new InvalidDataException("Clan id and name indexes contain different counts.");
        }

        foreach (KeyValuePair<string, ClanState> namePair in ClansByName)
        {
            string canonicalName = ClanDataRules.RequireClanName(namePair.Key);
            if (!StringComparer.Ordinal.Equals(namePair.Key, canonicalName) ||
                !StringComparer.Ordinal.Equals(namePair.Value.Name, namePair.Key) ||
                !ClansById.TryGetValue(namePair.Value.ClanId, out ClanState idClan) ||
                !ReferenceEquals(idClan, namePair.Value))
            {
                throw new InvalidDataException(
                    $"Clan name index '{namePair.Key}' is not canonical or points to the wrong clan.");
            }
        }

        int primaryMemberCount = 0;
        int guestMemberCount = 0;
        HashSet<long> creationOrders = new();
        HashSet<string> primaryMemberIds = new(StringComparer.Ordinal);
        HashSet<string> guestMemberIds = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ClanState> pair in ClansById)
        {
            ClanState clan = pair.Value;
            long creationOrder = RequireClanCreationOrder(clan.CreationOrder);
            if (!creationOrders.Add(creationOrder))
            {
                throw new InvalidDataException(
                    $"Clan '{pair.Key}' has duplicate creation order '{creationOrder}'.");
            }
            if (!StringComparer.Ordinal.Equals(pair.Key, clan.ClanId) ||
                !ClansByName.TryGetValue(clan.Name, out ClanState namedClan) ||
                !ReferenceEquals(namedClan, clan))
            {
                throw new InvalidDataException(
                    $"Clan '{pair.Key}' is inconsistent with the registry indexes.");
            }

            foreach (KeyValuePair<string, ClanMember> memberPair in clan.Members)
            {
                ClanMember member = memberPair.Value;
                if (member == null ||
                    !member.Player.IsValid ||
                    !StringComparer.Ordinal.Equals(memberPair.Key, member.Player.Id))
                {
                    throw new InvalidDataException(
                        $"Clan '{pair.Key}' has an inconsistent member index for '{memberPair.Key}'.");
                }

                ClanRole role = ClanDataRules.RequireEnum(member.Role, "clan role");
                if (role == ClanRole.Guest)
                {
                    if (!guestMemberIds.Add(memberPair.Key) ||
                        !GuestClanByPlayerId.TryGetValue(
                            memberPair.Key,
                            out ClanState indexedGuestClan) ||
                        !ReferenceEquals(indexedGuestClan, clan))
                    {
                        throw new InvalidDataException(
                            $"Clan '{pair.Key}' has an inconsistent Guest index for '{memberPair.Key}'.");
                    }
                    guestMemberCount++;
                }
                else
                {
                    if (!primaryMemberIds.Add(memberPair.Key) ||
                        !PrimaryClanByPlayerId.TryGetValue(
                            memberPair.Key,
                            out ClanState indexedPrimaryClan) ||
                        !ReferenceEquals(indexedPrimaryClan, clan))
                    {
                        throw new InvalidDataException(
                            $"Clan '{pair.Key}' has an inconsistent primary index for '{memberPair.Key}'.");
                    }
                    primaryMemberCount++;
                }
            }
        }

        if (PrimaryClanByPlayerId.Count != primaryMemberCount ||
            GuestClanByPlayerId.Count != guestMemberCount)
        {
            throw new InvalidDataException(
                "The role-aware player-to-clan indexes contain a different number of members than the clans.");
        }
    }

    private static bool RestorePersistedState()
    {
        string? loadedSaveFile = _loadedSaveFile;
        if (loadedSaveFile == null || string.IsNullOrWhiteSpace(loadedSaveFile))
        {
            return false;
        }

        try
        {
            RegistryData persisted = Load(loadedSaveFile);
            SwapState(persisted);
            LastPositionUpdateByPlayer.Clear();
            return true;
        }
        catch (Exception restoreError)
        {
            ClanPlugin.ClanLogger.LogError(
                $"Failed to restore clan state after a save error: {restoreError}");
            return false;
        }
    }

    private static YamlRegistryDocument CreateSaveDocument()
    {
        List<YamlClanDto?> clans = new();
        List<YamlInviteDto?> pendingInvites = new();
        YamlRegistryDocument document = new()
        {
            FormatVersion = SaveFormatVersion,
            Clans = clans,
            PendingInvites = pendingInvites
        };

        foreach (ClanState clan in ClansById.Values.OrderBy(
                     clan => clan.ClanId,
                     StringComparer.Ordinal))
        {
            clans.Add(CreateClanDto(clan));
        }

        foreach (KeyValuePair<string, ClanInvite> pair in PendingInvitesByTarget.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            ClanInvite invite = pair.Value;
            if (invite == null ||
                !invite.Target.IsValid ||
                !StringComparer.Ordinal.Equals(pair.Key, invite.Target.Id))
            {
                throw new InvalidDataException(
                    $"Pending invite target index '{pair.Key}' is inconsistent.");
            }
            pendingInvites.Add(CreateInviteDto(invite));
        }

        return document;
    }

    private static byte[] SerializeSave(YamlRegistryDocument document)
    {
        string yaml = YamlSerializer.Serialize(document)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        if (!yaml.EndsWith("\n", StringComparison.Ordinal))
        {
            yaml += "\n";
        }
        return StrictUtf8.GetBytes(yaml);
    }

    private static YamlClanDto CreateClanDto(ClanState clan)
    {
        ClanDataRules.RequireCount(
            clan.Members.Count,
            ClanDataRules.MaxMembersPerClan,
            "clan member");
        ClanDataRules.RequireCount(
            clan.Applications.Count,
            ClanDataRules.MaxApplicationsPerClan,
            "application");
        if (clan.Members.Count == 0)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' has no members.");
        }

        List<YamlMemberDto?> members = new();
        List<YamlApplicationDto?> applications = new();
        YamlClanDto serialized = new()
        {
            ClanId = ClanDataRules.RequireClanId(clan.ClanId),
            CreationOrder = RequireClanCreationOrder(clan.CreationOrder),
            Name = ClanDataRules.RequireClanName(clan.Name),
            Description = ClanDataRules.RequireClanDescription(clan.Description),
            EmblemKey = ClanDataRules.RequireClanEmblemKey(clan.EmblemKey),
            Members = members,
            Applications = applications
        };

        int leaderCount = 0;
        foreach (KeyValuePair<string, ClanMember> pair in clan.Members.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            ClanMember member = pair.Value;
            if (member == null ||
                !member.Player.IsValid ||
                !StringComparer.Ordinal.Equals(pair.Key, member.Player.Id))
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains an inconsistent member '{pair.Key}'.");
            }

            ClanRole role = ClanDataRules.RequireEnum(member.Role, "clan role");
            if (role == ClanRole.Leader)
            {
                leaderCount++;
            }

            members.Add(new YamlMemberDto
            {
                Player = CreatePlayerDto(member.Player, "clan member"),
                Role = FormatRole(role)
            });
        }
        if (leaderCount != 1)
        {
            throw new InvalidDataException($"Clan '{clan.Name}' must have exactly one leader.");
        }

        foreach (KeyValuePair<string, ClanPlayerRef> pair in clan.Applications.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            ClanPlayerRef applicant = pair.Value;
            if (!applicant.IsValid ||
                !StringComparer.Ordinal.Equals(pair.Key, applicant.Id))
            {
                throw new InvalidDataException(
                    $"Clan '{clan.Name}' contains an inconsistent application '{pair.Key}'.");
            }

            applications.Add(new YamlApplicationDto
            {
                Player = CreatePlayerDto(applicant, "application player")
            });
        }

        return serialized;
    }

    private static YamlInviteDto CreateInviteDto(ClanInvite invite)
    {
        return new YamlInviteDto
        {
            InviteId = ClanDataRules.RequireText(
                invite.InviteId,
                ClanDataRules.MaxInviteIdLength,
                "invite id",
                allowEmpty: false,
                allowLineBreaks: false),
            ClanId = ClanDataRules.RequireClanId(invite.ClanId, "invite clan id"),
            FromName = ClanDataRules.RequireText(
                invite.FromName,
                ClanDataRules.MaxPlayerNameLength,
                "invite sender name"),
            Target = CreatePlayerDto(invite.Target, "invite target")
        };
    }

    private static YamlPlayerDto CreatePlayerDto(ClanPlayerRef player, string fieldName)
    {
        if (!player.IsValid)
        {
            throw new InvalidDataException($"{fieldName} identity is invalid.");
        }

        return new YamlPlayerDto
        {
            PlatformId = ClanDataRules.RequirePlatformId(
                player.PlatformId,
                fieldName + " platform id"),
            CharacterPlayerId = ClanDataRules.RequireCharacterPlayerId(
                player.CharacterPlayerId,
                fieldName + " character player id"),
            Name = ClanDataRules.RequireText(
                player.Name,
                ClanDataRules.MaxPlayerNameLength,
                fieldName + " name")
        };
    }

    private static string FormatRole(ClanRole role)
    {
        return ClanDataRules.RequireEnum(role, "clan role") switch
        {
            ClanRole.Leader => "leader",
            ClanRole.Officer => "officer",
            ClanRole.Member => "member",
            ClanRole.Guest => "guest",
            _ => throw new InvalidDataException("Clan role is unsupported.")
        };
    }

    private static void WriteAtomically(string saveFile, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(saveFile);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Clan save directory is invalid.");
        }

        Directory.CreateDirectory(directory);
        string tempFile = saveFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(
                       tempFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(saveFile))
            {
                string backupFile = saveFile + ".bak";
                File.Replace(tempFile, saveFile, backupFile, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFile, saveFile);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception cleanupError)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Failed to clean temporary clan save '{tempFile}': {cleanupError.Message}");
            }

            throw;
        }
    }

    private static byte[] ReadSaveBytes(string saveFile)
    {
        FileInfo info = new(saveFile);
        if (info.Length < 0L || info.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
        }

        byte[] bytes = File.ReadAllBytes(saveFile);
        if (bytes.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Clan save exceeds the {MaximumSaveBytes}-byte limit.");
        }

        return bytes;
    }

    private static string QuarantineUnsupportedSave(string saveFile)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string quarantineFile = saveFile +
                                    ".unsupported-or-corrupt-" +
                                    timestamp +
                                    "-" +
                                    Guid.NewGuid().ToString("N");
            if (File.Exists(quarantineFile))
            {
                continue;
            }

            try
            {
                File.Move(saveFile, quarantineFile);
                return quarantineFile;
            }
            catch (IOException) when (File.Exists(saveFile) && File.Exists(quarantineFile))
            {
                // Another writer claimed this generated destination. Retry with a new unique name.
            }
        }

        throw new IOException($"Could not allocate a unique quarantine file for '{saveFile}'.");
    }

    private static void SwapState(RegistryData data)
    {
        ClansById = data.ClansById;
        ClansByName = data.ClansByName;
        PrimaryClanByPlayerId = data.PrimaryClanByPlayerId;
        GuestClanByPlayerId = data.GuestClanByPlayerId;
        PendingInvitesByTarget = data.PendingInvitesByTarget;
    }

    private readonly struct OperationOutcome
    {
        public readonly ClanOperationResultCode Code;
        public readonly string Status;

        public OperationOutcome(ClanOperationResultCode code, string status)
        {
            Code = code;
            Status = status;
        }
    }

    private sealed class HudCandidate
    {
        public ClanMember Member { get; }
        public ZDOID CharacterId { get; }
        public float DistanceSquared { get; }

        public HudCandidate(
            ClanMember member,
            ZDOID characterId,
            float distanceSquared)
        {
            Member = member;
            CharacterId = characterId;
            DistanceSquared = distanceSquared;
        }
    }

    private sealed class HudSelectionEntry
    {
        public string PlayerId => Player.Id;
        public ClanPlayerRef Player { get; }
        public ZDOID CharacterId { get; }

        public HudSelectionEntry(ClanPlayerRef player, ZDOID characterId)
        {
            Player = player;
            CharacterId = characterId;
        }
    }

    private readonly struct HudHealthState : IEquatable<HudHealthState>
    {
        public readonly bool HasHealth;
        public readonly float CurrentHealth;
        public readonly float MaxHealth;

        public HudHealthState(bool hasHealth, float currentHealth, float maxHealth)
        {
            HasHealth = hasHealth;
            CurrentHealth = currentHealth;
            MaxHealth = maxHealth;
        }

        public bool Equals(HudHealthState other)
        {
            return HasHealth == other.HasHealth &&
                   CurrentHealth.Equals(other.CurrentHealth) &&
                   MaxHealth.Equals(other.MaxHealth);
        }

        public override bool Equals(object? obj)
        {
            return obj is HudHealthState other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = HasHealth.GetHashCode();
                hashCode = (hashCode * 397) ^ CurrentHealth.GetHashCode();
                hashCode = (hashCode * 397) ^ MaxHealth.GetHashCode();
                return hashCode;
            }
        }
    }

    private sealed class PendingHudUpdate
    {
        public ClanHudSnapshot Snapshot { get; }
        public IReadOnlyDictionary<string, HudHealthState> AbsoluteHealth { get; }

        public PendingHudUpdate(
            ClanHudSnapshot snapshot,
            IReadOnlyDictionary<string, HudHealthState> absoluteHealth)
        {
            Snapshot = snapshot;
            AbsoluteHealth = absoluteHealth;
        }
    }

    private sealed class HudViewerCache
    {
        public string ClanId = "";
        public long SelectionRevision;
        public long AcknowledgedStateRevision;
        public float LastSelectionRefreshTime = float.NegativeInfinity;
        public float LastFullResendTime = float.NegativeInfinity;
        public bool SelectionRequiresFull = true;
        public readonly List<HudSelectionEntry> Selection = new();
        public readonly Dictionary<string, HudHealthState> AcknowledgedHealth =
            new(StringComparer.Ordinal);
        public PendingHudUpdate? PendingUpdate;
    }

    private sealed class YamlRegistryDocument
    {
        public YamlRegistryDocument()
        {
        }

        [YamlMember(Alias = "format_version", ApplyNamingConventions = false, Order = 1)]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "clans", ApplyNamingConventions = false, Order = 2)]
        public List<YamlClanDto?>? Clans { get; set; }

        [YamlMember(Alias = "pending_invites", ApplyNamingConventions = false, Order = 3)]
        public List<YamlInviteDto?>? PendingInvites { get; set; }
    }

    private sealed class YamlClanDto
    {
        public YamlClanDto()
        {
        }

        [YamlMember(Alias = "clan_id", ApplyNamingConventions = false, Order = 1)]
        public string? ClanId { get; set; }

        [YamlMember(Alias = "creation_order", ApplyNamingConventions = false, Order = 2)]
        public long CreationOrder { get; set; }

        [YamlMember(Alias = "name", ApplyNamingConventions = false, Order = 3)]
        public string? Name { get; set; }

        [YamlMember(Alias = "description", ApplyNamingConventions = false, Order = 4)]
        public string? Description { get; set; }

        [YamlMember(Alias = "emblem_key", ApplyNamingConventions = false, Order = 5)]
        public string? EmblemKey { get; set; }

        [YamlMember(Alias = "members", ApplyNamingConventions = false, Order = 6)]
        public List<YamlMemberDto?>? Members { get; set; }

        [YamlMember(Alias = "applications", ApplyNamingConventions = false, Order = 7)]
        public List<YamlApplicationDto?>? Applications { get; set; }
    }

    private sealed class YamlMemberDto
    {
        public YamlMemberDto()
        {
        }

        [YamlMember(Alias = "player", ApplyNamingConventions = false, Order = 1)]
        public YamlPlayerDto? Player { get; set; }

        [YamlMember(Alias = "role", ApplyNamingConventions = false, Order = 2)]
        public string? Role { get; set; }
    }

    private sealed class YamlApplicationDto
    {
        public YamlApplicationDto()
        {
        }

        [YamlMember(Alias = "player", ApplyNamingConventions = false, Order = 1)]
        public YamlPlayerDto? Player { get; set; }
    }

    private sealed class YamlInviteDto
    {
        public YamlInviteDto()
        {
        }

        [YamlMember(Alias = "invite_id", ApplyNamingConventions = false, Order = 1)]
        public string? InviteId { get; set; }

        [YamlMember(Alias = "clan_id", ApplyNamingConventions = false, Order = 2)]
        public string? ClanId { get; set; }

        [YamlMember(Alias = "from_name", ApplyNamingConventions = false, Order = 3)]
        public string? FromName { get; set; }

        [YamlMember(Alias = "target", ApplyNamingConventions = false, Order = 4)]
        public YamlPlayerDto? Target { get; set; }
    }

    private sealed class YamlPlayerDto
    {
        public YamlPlayerDto()
        {
        }

        [YamlMember(Alias = "platform_id", ApplyNamingConventions = false, Order = 1)]
        public string? PlatformId { get; set; }

        [YamlMember(Alias = "player_id", ApplyNamingConventions = false, Order = 2)]
        public long CharacterPlayerId { get; set; }

        [YamlMember(Alias = "name", ApplyNamingConventions = false, Order = 3)]
        public string? Name { get; set; }
    }

    private sealed class RegistryData
    {
        public readonly Dictionary<string, ClanState> ClansById =
            new(StringComparer.Ordinal);
        public readonly Dictionary<string, ClanState> ClansByName =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ClanState> PrimaryClanByPlayerId =
            new(StringComparer.Ordinal);
        public readonly Dictionary<string, ClanState> GuestClanByPlayerId =
            new(StringComparer.Ordinal);
        public readonly Dictionary<string, ClanInvite> PendingInvitesByTarget =
            new(StringComparer.Ordinal);
    }
}
