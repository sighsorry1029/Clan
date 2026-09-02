using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Clan;

internal static class ClanRpc
{
    internal static readonly string IdentityNotReadyStatus =
        ClanLocalization.EncodeStatus("clan_status_identity_not_ready");
    internal static readonly string IdentityRejectedStatus =
        ClanLocalization.EncodeStatus("clan_status_identity_rejected");
    internal static readonly string SnapshotRateLimitedStatus =
        ClanLocalization.EncodeStatus("clan_status_snapshot_rate_limited");
    internal static readonly string MutationRateLimitedStatus =
        ClanLocalization.EncodeStatus("clan_status_mutation_rate_limited");
    internal static readonly string DirectoryRateLimitedStatus =
        ClanLocalization.EncodeStatus("clan_status_directory_rate_limited");
    internal static readonly string StateUnavailableStatus =
        ClanLocalization.EncodeStatus("clan_status_state_unavailable");

    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumDirectoryResponseBytes = 512 * 1024;
    private const int MaximumDirectoryRequestsPerWindow = 1;
    private const float DirectoryRequestWindowSeconds = 2f;
    private const float DirectoryRequestTimeoutSeconds = 30f;
    private const int MaximumSnapshotRequestsPerWindow = 5;
    private const float SnapshotRequestWindowSeconds = 2f;
    private const int MaximumHudRequestsPerWindow = 5;
    private const float HudRequestWindowSeconds = 2f;
    private const int MaximumMutationRequestsPerWindow = 6;
    private const float MutationRequestWindowSeconds = 5f;
    private const int MutationBudgetPruneThreshold = 256;
    private const int InitialSnapshotRetryCount = 20;
    private const float InitialSnapshotRetryIntervalSeconds = 0.5f;
    private const string ProtocolVersion = "v13";
    private static readonly string DirectoryTruncatedStatus =
        ClanLocalization.EncodeStatus("clan_status_directory_truncated");

    private static readonly string RequestRpc =
        $"{ClanPlugin.ModGUID}.rpc.request.{ProtocolVersion}";
    private static readonly string ResponseRpc =
        $"{ClanPlugin.ModGUID}.rpc.response.{ProtocolVersion}";
    private static readonly Dictionary<ZRpc, RequestBudget> DirectoryRequestBudgets = new();
    private static readonly Dictionary<ZRpc, RequestBudget> SnapshotRequestBudgets = new();
    private static readonly Dictionary<ZRpc, RequestBudget> HudRequestBudgets = new();
    private static readonly Dictionary<string, RequestBudget> MutationRequestBudgets =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<ZRpc, ClanPlayerRef> PeerIdentities = new();
    private static readonly object RequestIdLock = new();
    private static long _nextRequestId;
    private static long _pendingDirectoryRequestId;
    private static float _pendingDirectoryRequestStartedAt;
    private static int _initialSnapshotRetriesRemaining;
    private static float _nextInitialSnapshotRetryAt;
    private static bool _initialSnapshotBootstrapStarted;
    private static bool _initialSnapshotBootstrapComplete;
    private static bool _identityReady;
    private static bool _retryDirectoryWhenIdentityReady;
    private static float _directoryIdentityRetryAt;
    private static bool _hudRecoveryRequestInProgress;

    public static ClanClientSnapshot CurrentSnapshot { get; private set; } = new();
    public static ClanHudSnapshot CurrentHudSnapshot { get; private set; } = new();
    public static ClanDirectorySnapshot CurrentDirectory { get; private set; } = new();
    public static bool IsDirectoryRequestPending => _pendingDirectoryRequestId > 0L;
    public static bool IsDirectoryRefreshScheduled => _retryDirectoryWhenIdentityReady;
    internal static bool IsIdentityReady => _identityReady;

    public static event Action<ClanClientSnapshot>? SnapshotChanged;
    public static event Action<ClanHudSnapshot>? HudSnapshotChanged;
    public static event Action<ClanDirectorySnapshot>? DirectoryChanged;
    public static event Action<string>? StatusReceived;
    public static event Action<string, string>? ChatReceived;

    public static bool Send(ClanRequest request)
    {
        ZPackage package = new();
        try
        {
            request.Write(package);
        }
        catch (InvalidDataException ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Rejected invalid local clan request: {ex.Message}");
            NotifyStatus(ClanLocalization.Text("clan_status_invalid_request_server"));
            return false;
        }

        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        if (serverRpc != null)
        {
            serverRpc.Invoke(RequestRpc, package);
            return true;
        }

        if (ZNet.instance?.IsServer() == true)
        {
            package.SetPos(0);
            ClanRegistry.HandleRequest(null, ClanRequest.Read(package));
            return true;
        }

        NotifyStatus(ClanLocalization.Text("clan_status_server_not_connected"));
        return false;
    }

    public static void RequestSnapshot()
    {
        Send(ClanRequest.Simple(ClanRequestType.RequestSnapshot));
    }

    public static void RequestHudSnapshot(string clanId)
    {
        if (string.IsNullOrWhiteSpace(clanId))
        {
            return;
        }

        long selectionRevision = 0L;
        long stateRevision = 0L;
        if (StringComparer.Ordinal.Equals(CurrentHudSnapshot.ClanId, clanId))
        {
            selectionRevision = CurrentHudSnapshot.SelectionRevision;
            stateRevision = CurrentHudSnapshot.StateRevision;
        }

        Send(new ClanRequest
        {
            Type = ClanRequestType.RequestHud,
            ClanId = clanId,
            HudSelectionRevision = selectionRevision,
            HudStateRevision = stateRevision
        });
    }

    public static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (_pendingDirectoryRequestId > 0L &&
            (now < _pendingDirectoryRequestStartedAt ||
             now - _pendingDirectoryRequestStartedAt >= DirectoryRequestTimeoutSeconds))
        {
            ClanPlugin.ClanLogger.LogWarning(
                "Clan directory request timed out; scheduling one retry.");
            ClearPendingDirectoryRequest();
            ScheduleDirectoryRefresh(0f);
        }

        EnsureInitialSnapshotBootstrap();
        if (!_initialSnapshotBootstrapComplete &&
            _initialSnapshotRetriesRemaining > 0 &&
            now >= _nextInitialSnapshotRetryAt &&
            CanRunInitialSnapshotBootstrap())
        {
            _initialSnapshotRetriesRemaining--;
            _nextInitialSnapshotRetryAt = now + InitialSnapshotRetryIntervalSeconds;
            RequestSnapshot();
        }

        if (_identityReady &&
            _retryDirectoryWhenIdentityReady &&
            now >= _directoryIdentityRetryAt &&
            _pendingDirectoryRequestId <= 0L)
        {
            RequestDirectory();
        }
    }

    private static void EnsureInitialSnapshotBootstrap()
    {
        if (_initialSnapshotBootstrapStarted ||
            _initialSnapshotBootstrapComplete ||
            !CanRunInitialSnapshotBootstrap())
        {
            return;
        }

        _initialSnapshotBootstrapStarted = true;
        _identityReady = false;
        _initialSnapshotRetriesRemaining = InitialSnapshotRetryCount;
        _nextInitialSnapshotRetryAt = Time.realtimeSinceStartup;
        if (CanRunInitialSnapshotBootstrap())
        {
            _nextInitialSnapshotRetryAt += InitialSnapshotRetryIntervalSeconds;
            RequestSnapshot();
        }
    }

    private static bool CanRunInitialSnapshotBootstrap()
    {
        ZNet? network = ZNet.instance;
        return Game.instance != null &&
               Player.m_localPlayer != null &&
               network != null &&
               (network.GetServerRPC() != null || network.IsServer());
    }

    public static long RequestDirectory()
    {
        if (_pendingDirectoryRequestId > 0L)
        {
            return _pendingDirectoryRequestId;
        }

        bool refreshWasScheduled = _retryDirectoryWhenIdentityReady;
        ZNet? network = ZNet.instance;
        if (network == null ||
            (network.GetServerRPC() == null && !network.IsServer()))
        {
            ClearPendingDirectoryRequest();
            if (refreshWasScheduled)
            {
                ScheduleDirectoryRefresh(
                    DirectoryRequestWindowSeconds + 0.1f,
                    postpone: true);
            }
            NotifyStatus(ClanLocalization.Text("clan_status_server_not_connected"));
            return 0L;
        }

        _retryDirectoryWhenIdentityReady = false;
        long requestId = NextRequestId();
        _pendingDirectoryRequestId = requestId;
        _pendingDirectoryRequestStartedAt = Time.realtimeSinceStartup;
        if (!Send(new ClanRequest
        {
            Type = ClanRequestType.RequestDirectory,
            RequestId = requestId
        }))
        {
            ClearPendingDirectoryRequest();
            if (refreshWasScheduled)
            {
                ScheduleDirectoryRefresh(
                    DirectoryRequestWindowSeconds + 0.1f,
                    postpone: true);
            }
            return 0L;
        }
        return requestId;
    }

    internal static long NextRequestId()
    {
        lock (RequestIdLock)
        {
            if (_nextRequestId == long.MaxValue)
            {
                _nextRequestId = 0L;
            }
            return ++_nextRequestId;
        }
    }

    public static void InvalidateDirectory()
    {
        ClearPendingDirectoryRequest();
        ScheduleDirectoryRefresh(DirectoryRequestWindowSeconds + 0.1f);
        CurrentDirectory = new ClanDirectorySnapshot();
        Publish(DirectoryChanged, CurrentDirectory, "directory");
    }

    private static void ScheduleDirectoryRefresh(float delaySeconds, bool postpone = false)
    {
        float refreshAt = Time.realtimeSinceStartup + Mathf.Max(0f, delaySeconds);
        if (!_retryDirectoryWhenIdentityReady ||
            (postpone
                ? refreshAt > _directoryIdentityRetryAt
                : refreshAt < _directoryIdentityRetryAt))
        {
            _directoryIdentityRetryAt = refreshAt;
        }
        _retryDirectoryWhenIdentityReady = true;
    }

    private static void ClearPendingDirectoryRequest()
    {
        _pendingDirectoryRequestId = 0L;
        _pendingDirectoryRequestStartedAt = 0f;
    }

    public static void NotifyStatus(string message)
    {
        Publish(StatusReceived, ClanLocalization.ResolveStatus(message), "status");
    }

    public static bool TryPinPeerIdentity(ZNetPeer? peer, ClanPlayerRef player)
    {
        if (peer?.m_rpc == null)
        {
            return true;
        }

        if (!player.IsValid)
        {
            return false;
        }
        if (PeerIdentities.TryGetValue(peer.m_rpc, out ClanPlayerRef pinnedIdentity))
        {
            return pinnedIdentity == player;
        }

        PeerIdentities.Add(peer.m_rpc, player);
        return true;
    }

    public static bool TryGetPinnedPeerIdentity(
        ZNetPeer? peer,
        out ClanPlayerRef player)
    {
        player = default;
        return peer?.m_rpc != null && PeerIdentities.TryGetValue(peer.m_rpc, out player);
    }

    public static void SendSnapshot(ZNetPeer? peer, ClanClientSnapshot snapshot)
    {
        ZPackage response = new();
        response.Write((int)ClanResponseType.Snapshot);
        snapshot.Write(response);
        SendResponse(peer, response);
    }

    public static void SendHudSnapshot(ZNetPeer? peer, ClanHudSnapshot snapshot)
    {
        ZPackage response = new();
        response.Write((int)ClanResponseType.Hud);
        snapshot.Write(response);
        SendResponse(peer, response);
    }

    public static void SendDirectorySnapshot(
        ZNetPeer? peer,
        ClanDirectorySnapshot snapshot)
    {
        int clanCount = snapshot.PublicClans.Count;
        int playerCount = snapshot.Players.Count;
        ZPackage response = CreateDirectoryResponse(snapshot, clanCount, playerCount);
        if (response.Size() <= MaximumDirectoryResponseBytes)
        {
            SendResponse(peer, response);
            return;
        }

        snapshot.IsTruncated = true;
        if (string.IsNullOrWhiteSpace(snapshot.Status))
        {
            snapshot.Status = DirectoryTruncatedStatus;
        }
        playerCount = FindLargestPlayerPrefix(snapshot, clanCount, playerCount);
        response = CreateDirectoryResponse(snapshot, clanCount, playerCount);
        if (response.Size() > MaximumDirectoryResponseBytes)
        {
            playerCount = 0;
            clanCount = FindLargestClanPrefix(snapshot, clanCount);
            response = CreateDirectoryResponse(snapshot, clanCount, playerCount);
        }
        if (response.Size() > MaximumDirectoryResponseBytes)
        {
            snapshot.Status = ClanLocalization.EncodeStatus(
                "clan_status_directory_response_too_large");
            snapshot.IsTruncated = true;
            response = CreateDirectoryResponse(snapshot, 0, 0);
        }

        SendResponse(peer, response);
    }

    private static int FindLargestPlayerPrefix(
        ClanDirectorySnapshot snapshot,
        int clanCount,
        int maximumPlayerCount)
    {
        int low = 0;
        int high = maximumPlayerCount;
        int best = 0;
        while (low <= high)
        {
            int candidate = low + (high - low) / 2;
            if (CreateDirectoryResponse(snapshot, clanCount, candidate).Size() <=
                MaximumDirectoryResponseBytes)
            {
                best = candidate;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        return best;
    }

    private static int FindLargestClanPrefix(
        ClanDirectorySnapshot snapshot,
        int maximumClanCount)
    {
        int low = 0;
        int high = maximumClanCount;
        int best = 0;
        while (low <= high)
        {
            int candidate = low + (high - low) / 2;
            if (CreateDirectoryResponse(snapshot, candidate, 0).Size() <=
                MaximumDirectoryResponseBytes)
            {
                best = candidate;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        return best;
    }

    private static ZPackage CreateDirectoryResponse(
        ClanDirectorySnapshot snapshot,
        int clanCount,
        int playerCount)
    {
        ZPackage response = new();
        response.Write((int)ClanResponseType.Directory);
        snapshot.Write(response, clanCount, playerCount);
        return response;
    }

    public static void BroadcastChat(ClanState clan, string senderName, string message)
    {
        Dictionary<string, ZNetPeer> peers = BuildPeerLookup();
        ZPackage response = new();
        response.Write((int)ClanResponseType.Chat);
        ClanDataRules.WriteClanId(response, clan.ClanId);
        ClanDataRules.WritePlayerName(response, senderName, "chat sender");
        ClanDataRules.WriteText(
            response,
            message,
            ClanDataRules.MaxChatMessageLength,
            "chat message",
            allowEmpty: false);
        foreach (ClanMember member in clan.Members.Values)
        {
            if (!ClanRegistry.IsEffectiveMember(clan, member))
            {
                continue;
            }
            SendResponse(FindPeer(member.Player, peers), response, member.Player);
        }
    }

    public static void BroadcastPing(ClanState clan, ClanPlayerRef sender, Vector3 position)
    {
        Dictionary<string, ZNetPeer> peers = BuildPeerLookup();
        ZPackage response = new();
        response.Write((int)ClanResponseType.MapPing);
        ClanDataRules.WriteClanId(response, clan.ClanId);
        sender.Write(response);
        response.Write(position);
        foreach (ClanMember member in clan.Members.Values)
        {
            if (!ClanRegistry.IsEffectiveMember(clan, member))
            {
                continue;
            }
            SendResponse(FindPeer(member.Player, peers), response, member.Player);
        }
    }

    public static void BroadcastPosition(ClanState clan, ClanPlayerRef sender, Vector3 position)
    {
        Dictionary<string, ZNetPeer> peers = BuildPeerLookup();
        ZPackage response = new();
        response.Write((int)ClanResponseType.PositionUpdate);
        ClanDataRules.WriteClanId(response, clan.ClanId);
        sender.Write(response);
        response.Write(position);
        foreach (ClanMember member in clan.Members.Values)
        {
            if (member.Player == sender ||
                !ClanRegistry.IsEffectiveMember(clan, member))
            {
                continue;
            }

            SendResponse(FindPeer(member.Player, peers), response, member.Player);
        }
    }

    public static void BroadcastDirectoryInvalidation()
    {
        ZPackage response = new();
        response.Write((int)ClanResponseType.DirectoryInvalidated);
        if (ZNet.instance != null)
        {
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
            {
                SendResponse(peer, response);
            }
        }

        if (Player.m_localPlayer != null)
        {
            TryHandleResponsePackage(response);
        }
    }

    private static void SendResponse(ZNetPeer? peer, ZPackage package, ClanPlayerRef target = default)
    {
        try
        {
            if (peer != null)
            {
                peer.m_rpc.Invoke(ResponseRpc, package);
                return;
            }

            if (!target.IsValid ||
                (Player.m_localPlayer != null && target == ClanPlayerRef.Local()))
            {
                TryHandleResponsePackage(package);
            }
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Failed to send clan response to {target}: {ex.Message}");
        }
    }

    private static ZNetPeer? FindPeer(ClanPlayerRef player, IReadOnlyDictionary<string, ZNetPeer> peers)
    {
        return player.IsValid && peers.TryGetValue(player.Id, out ZNetPeer peer) ? peer : null;
    }

    private static Dictionary<string, ZNetPeer> BuildPeerLookup()
    {
        Dictionary<string, ZNetPeer> peers = new(StringComparer.Ordinal);
        if (ZNet.instance == null)
        {
            return peers;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
        {
            ClanPlayerRef player = TryGetPinnedPeerIdentity(peer, out ClanPlayerRef pinnedPlayer)
                ? pinnedPlayer
                : ClanIdentity.FromPeer(peer);
            if (player.IsValid)
            {
                peers[player.Id] = peer;
            }
        }

        return peers;
    }

    private static void ClientHandleResponse(ZRpc rpc, ZPackage package)
    {
        TryHandleResponsePackage(package);
    }

    private static void TryHandleResponsePackage(ZPackage package)
    {
        ClanResponseType type = default;
        bool packageValidated = false;
        try
        {
            package.SetPos(0);
            type = (ClanResponseType)package.ReadInt();
            switch (type)
            {
                case ClanResponseType.Snapshot:
                    ClanClientSnapshot snapshot = ClanClientSnapshot.Read(package);
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    snapshot.Status = ClanLocalization.ResolveStatus(snapshot.Status);
                    bool transientSnapshotFailure = snapshot.ResponseResultCode is
                        ClanOperationResultCode.IdentityUnavailable or
                        ClanOperationResultCode.SnapshotRateLimited or
                        ClanOperationResultCode.RateLimited or
                        ClanOperationResultCode.Unavailable;
                    if (transientSnapshotFailure)
                    {
                        if (snapshot.ResponseRequestId > 0L)
                        {
                            CopySnapshotPresentation(CurrentSnapshot, snapshot);
                            Publish(SnapshotChanged, snapshot, "snapshot");
                        }
                        if (!_initialSnapshotBootstrapComplete &&
                            _initialSnapshotRetriesRemaining > 0 &&
                            snapshot.ResponseResultCode == ClanOperationResultCode.Unavailable)
                        {
                            _nextInitialSnapshotRetryAt =
                                Time.realtimeSinceStartup + InitialSnapshotRetryIntervalSeconds;
                        }
                        else if (!_initialSnapshotBootstrapComplete &&
                                 _initialSnapshotRetriesRemaining > 0 &&
                                 snapshot.ResponseResultCode ==
                                 ClanOperationResultCode.SnapshotRateLimited)
                        {
                            _nextInitialSnapshotRetryAt =
                                Time.realtimeSinceStartup + SnapshotRequestWindowSeconds + 0.1f;
                        }
                        Publish(StatusReceived, snapshot.Status, "status");
                        break;
                    }
                    bool bootstrapRetryableFailure =
                        !_initialSnapshotBootstrapComplete &&
                        snapshot.ResponseResultCode == ClanOperationResultCode.Failed;
                    if (!bootstrapRetryableFailure)
                    {
                        _initialSnapshotRetriesRemaining = 0;
                        _initialSnapshotBootstrapStarted = true;
                        _initialSnapshotBootstrapComplete = true;
                    }
                    bool identityRejected = snapshot.ResponseResultCode ==
                                            ClanOperationResultCode.IdentityRejected;
                    _identityReady = !identityRejected;
                    if (identityRejected)
                    {
                        _retryDirectoryWhenIdentityReady = false;
                    }
                    bool effectiveClanChanged = !StringComparer.Ordinal.Equals(
                        CurrentSnapshot.ClanId,
                        snapshot.ClanId);
                    CurrentSnapshot = snapshot;
                    if (effectiveClanChanged)
                    {
                        CurrentHudSnapshot = new ClanHudSnapshot();
                        Publish(HudSnapshotChanged, CurrentHudSnapshot, "HUD snapshot");
                    }
                    ClanMap.OnSnapshotChanged(CurrentSnapshot);
                    Publish(SnapshotChanged, CurrentSnapshot, "snapshot");
                    if (!string.IsNullOrWhiteSpace(CurrentSnapshot.Status))
                    {
                        Publish(StatusReceived, CurrentSnapshot.Status, "status");
                    }
                    break;
                case ClanResponseType.Chat:
                    string chatClanId = ClanDataRules.ReadClanId(package, "chat clan id");
                    string senderName = ClanDataRules.ReadText(
                        package,
                        ClanDataRules.MaxPlayerNameLength,
                        "chat sender");
                    string message = ClanDataRules.ReadText(
                        package,
                        ClanDataRules.MaxChatMessageLength,
                        "chat message",
                        allowEmpty: false);
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    if (StringComparer.Ordinal.Equals(
                            chatClanId,
                            CurrentSnapshot.ClanId))
                    {
                        Publish(ChatReceived, senderName, message, "chat");
                    }
                    break;
                case ClanResponseType.MapPing:
                    string pingClanId = ClanDataRules.ReadClanId(package, "ping clan id");
                    ClanPlayerRef pingSender = ClanPlayerRef.Read(package);
                    Vector3 pingPosition = ClanDataRules.ReadFiniteVector(package, "map ping position");
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    if (StringComparer.Ordinal.Equals(
                            pingClanId,
                            CurrentSnapshot.ClanId))
                    {
                        ClanMap.OnMapPing(pingSender, pingPosition);
                    }
                    break;
                case ClanResponseType.PositionUpdate:
                    string positionClanId = ClanDataRules.ReadClanId(
                        package,
                        "position clan id");
                    ClanPlayerRef player = ClanPlayerRef.Read(package);
                    Vector3 position = ClanDataRules.ReadFiniteVector(package, "player position");
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    if (StringComparer.Ordinal.Equals(
                            positionClanId,
                            CurrentSnapshot.ClanId))
                    {
                        ClanMap.OnPositionUpdate(player, position);
                    }
                    break;
                case ClanResponseType.Hud:
                    ClanHudSnapshot hudSnapshot = ClanHudSnapshot.Read(package);
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    HandleHudSnapshot(hudSnapshot);
                    break;
                case ClanResponseType.Directory:
                    ClanDirectorySnapshot directory = ClanDirectorySnapshot.Read(package);
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    directory.Status = ClanLocalization.ResolveStatus(directory.Status);
                    if (_pendingDirectoryRequestId <= 0L ||
                        directory.RequestId != _pendingDirectoryRequestId)
                    {
                        return;
                    }
                    ClearPendingDirectoryRequest();
                    if (directory.ResultCode == ClanOperationResultCode.IdentityUnavailable)
                    {
                        ScheduleDirectoryRefresh(
                            DirectoryRequestWindowSeconds + 0.1f,
                            postpone: true);
                        Publish(StatusReceived, directory.Status, "status");
                        break;
                    }
                    if (directory.ResultCode == ClanOperationResultCode.RateLimited)
                    {
                        ScheduleDirectoryRefresh(
                            DirectoryRequestWindowSeconds + 0.1f,
                            postpone: true);
                    }
                    if (!directory.IsTruncated &&
                        directory.ResultCode is not ClanOperationResultCode.None and
                            not ClanOperationResultCode.Success &&
                        directory.PublicClans.Count == 0 &&
                        directory.Players.Count == 0)
                    {
                        directory.PublicClans.AddRange(CurrentDirectory.PublicClans);
                        directory.Players.AddRange(CurrentDirectory.Players);
                        directory.IsTruncated = CurrentDirectory.IsTruncated;
                    }
                    CurrentDirectory = directory;
                    Publish(DirectoryChanged, CurrentDirectory, "directory");
                    if (directory.ResultCode != ClanOperationResultCode.RateLimited &&
                        !string.IsNullOrWhiteSpace(CurrentDirectory.Status))
                    {
                        Publish(StatusReceived, CurrentDirectory.Status, "status");
                    }
                    break;
                case ClanResponseType.DirectoryInvalidated:
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    ClearPendingDirectoryRequest();
                    ScheduleDirectoryRefresh(DirectoryRequestWindowSeconds + 0.1f);
                    CurrentDirectory = new ClanDirectorySnapshot();
                    Publish(DirectoryChanged, CurrentDirectory, "directory");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown clan response type {(int)type}.");
            }
        }
        catch (Exception ex) when (!packageValidated)
        {
            ClanPlugin.ClanLogger.LogWarning($"Rejected malformed clan response: {ex.Message}");
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogError(
                $"Failed to apply decoded clan {type} response: {ex}");
        }
    }

    private static void CopySnapshotPresentation(
        ClanClientSnapshot source,
        ClanClientSnapshot target)
    {
        target.PrimaryClanId = source.PrimaryClanId;
        target.PrimaryClanName = source.PrimaryClanName;
        target.PrimaryRole = source.PrimaryRole;
        target.GuestClanId = source.GuestClanId;
        target.GuestClanName = source.GuestClanName;
        target.ClanId = source.ClanId;
        target.ClanName = source.ClanName;
        target.ClanDescription = source.ClanDescription;
        target.ClanEmblemKey = source.ClanEmblemKey;
        target.SelfRole = source.SelfRole;
        target.Roster.Clear();
        target.Roster.AddRange(source.Roster);
        target.Applications.Clear();
        target.Applications.AddRange(source.Applications);
        target.Invite = source.Invite;
        target.OwnApplicationClanId = source.OwnApplicationClanId;
        target.OwnApplicationClanName = source.OwnApplicationClanName;
    }

    private static void RequirePackageConsumed(ZPackage package)
    {
        if (package.GetPos() != package.Size())
        {
            throw new InvalidOperationException("Clan response contains unexpected trailing data.");
        }
    }

    private static void HandleHudSnapshot(ClanHudSnapshot incoming)
    {
        if (!StringComparer.Ordinal.Equals(incoming.ClanId, CurrentSnapshot.ClanId))
        {
            // Snapshot changes already clear the HUD. Ignore late responses from
            // the previous clan and empty identity-failure responses instead of
            // turning them into an immediate retry loop.
            return;
        }

        if (incoming.ReplaceSelection)
        {
            CurrentHudSnapshot = incoming;
            Publish(HudSnapshotChanged, CurrentHudSnapshot, "HUD snapshot");
            return;
        }

        if (!StringComparer.Ordinal.Equals(
                incoming.ClanId,
                CurrentHudSnapshot.ClanId) ||
            incoming.SelectionRevision != CurrentHudSnapshot.SelectionRevision)
        {
            ResetHudSnapshotAndRequestFull();
            return;
        }

        if (incoming.StateRevision == CurrentHudSnapshot.StateRevision)
        {
            return;
        }
        if (CurrentHudSnapshot.StateRevision == long.MaxValue ||
            incoming.StateRevision != CurrentHudSnapshot.StateRevision + 1L)
        {
            ResetHudSnapshotAndRequestFull();
            return;
        }

        int[] targetIndexes = new int[incoming.Players.Count];
        for (int incomingIndex = 0; incomingIndex < incoming.Players.Count; incomingIndex++)
        {
            string playerId = incoming.Players[incomingIndex].PlayerId;
            int targetIndex = FindHudPlayerIndex(CurrentHudSnapshot, playerId);
            if (targetIndex < 0)
            {
                ResetHudSnapshotAndRequestFull();
                return;
            }
            targetIndexes[incomingIndex] = targetIndex;
        }

        for (int incomingIndex = 0; incomingIndex < incoming.Players.Count; incomingIndex++)
        {
            ClanHudPlayerSummary source = incoming.Players[incomingIndex];
            ClanHudPlayerSummary target =
                CurrentHudSnapshot.Players[targetIndexes[incomingIndex]];
            target.HasHealth = source.HasHealth;
            target.CurrentHealth = source.CurrentHealth;
            target.MaxHealth = source.MaxHealth;
        }

        CurrentHudSnapshot.StateRevision = incoming.StateRevision;
        CurrentHudSnapshot.ReplaceSelection = true;
        Publish(HudSnapshotChanged, CurrentHudSnapshot, "HUD snapshot");
    }

    private static int FindHudPlayerIndex(ClanHudSnapshot snapshot, string playerId)
    {
        for (int index = 0; index < snapshot.Players.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(snapshot.Players[index].PlayerId, playerId))
            {
                return index;
            }
        }
        return -1;
    }

    private static void ResetHudSnapshotAndRequestFull()
    {
        CurrentHudSnapshot = new ClanHudSnapshot();
        Publish(HudSnapshotChanged, CurrentHudSnapshot, "HUD snapshot");

        string activeClanId = CurrentSnapshot.ClanId;
        if (string.IsNullOrWhiteSpace(activeClanId) || _hudRecoveryRequestInProgress)
        {
            return;
        }

        try
        {
            _hudRecoveryRequestInProgress = true;
            RequestHudSnapshot(activeClanId);
        }
        finally
        {
            _hudRecoveryRequestInProgress = false;
        }
    }

    private static void ServerHandleRequest(ZNetPeer peer, ZRpc rpc, ZPackage package)
    {
        try
        {
            if (package.Size() > MaximumRequestBytes)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Rejected oversized clan request ({package.Size()} bytes) from peer {peer.m_uid}.");
                return;
            }

            package.SetPos(0);
            ClanRequest request = ClanRequest.Read(package);
            if (request.Type == ClanRequestType.RequestDirectory &&
                !ConsumeRequest(
                    rpc,
                    DirectoryRequestBudgets,
                    MaximumDirectoryRequestsPerWindow,
                    DirectoryRequestWindowSeconds))
            {
                SendDirectorySnapshot(peer, new ClanDirectorySnapshot
                {
                    RequestId = request.RequestId,
                    ResultCode = ClanOperationResultCode.RateLimited,
                    Status = DirectoryRateLimitedStatus
                });
                return;
            }
            if (request.Type == ClanRequestType.RequestSnapshot &&
                !ConsumeRequest(
                    rpc,
                    SnapshotRequestBudgets,
                    MaximumSnapshotRequestsPerWindow,
                    SnapshotRequestWindowSeconds))
            {
                ClanRpc.SendSnapshot(peer, new ClanClientSnapshot
                {
                    Status = SnapshotRateLimitedStatus,
                    ResponseResultCode = ClanOperationResultCode.SnapshotRateLimited
                });
                return;
            }
            if (request.Type == ClanRequestType.RequestHud &&
                !ConsumeRequest(
                    rpc,
                    HudRequestBudgets,
                    MaximumHudRequestsPerWindow,
                    HudRequestWindowSeconds))
            {
                return;
            }
            ClanRegistry.HandleRequest(peer, request);
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning($"Rejected malformed clan request: {ex.Message}");
        }
    }

    private static void ResetSession()
    {
        CurrentSnapshot = new ClanClientSnapshot();
        CurrentHudSnapshot = new ClanHudSnapshot();
        CurrentDirectory = new ClanDirectorySnapshot();
        ClearPendingDirectoryRequest();
        _initialSnapshotRetriesRemaining = 0;
        _nextInitialSnapshotRetryAt = 0f;
        _initialSnapshotBootstrapStarted = false;
        _initialSnapshotBootstrapComplete = false;
        _identityReady = false;
        _retryDirectoryWhenIdentityReady = false;
        _directoryIdentityRetryAt = 0f;
        _hudRecoveryRequestInProgress = false;
        DirectoryRequestBudgets.Clear();
        SnapshotRequestBudgets.Clear();
        HudRequestBudgets.Clear();
        MutationRequestBudgets.Clear();
        PeerIdentities.Clear();
        ClanApi.ResetSession();
        ClanRegistry.ResetOnlinePresence();
        ClanMap.ResetSession();
        ClanPanelController.ResetSearchState();
        Publish(SnapshotChanged, CurrentSnapshot, "snapshot");
        Publish(HudSnapshotChanged, CurrentHudSnapshot, "HUD snapshot");
        Publish(DirectoryChanged, CurrentDirectory, "directory");
    }

    private static void Publish<T>(Action<T>? subscribers, T value, string eventName)
    {
        if (subscribers == null)
        {
            return;
        }

        foreach (Action<T> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(value);
            }
            catch (Exception exception)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan RPC {eventName} subscriber failed: {exception}");
            }
        }
    }

    private static void Publish<TFirst, TSecond>(
        Action<TFirst, TSecond>? subscribers,
        TFirst first,
        TSecond second,
        string eventName)
    {
        if (subscribers == null)
        {
            return;
        }

        foreach (Action<TFirst, TSecond> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(first, second);
            }
            catch (Exception exception)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan RPC {eventName} subscriber failed: {exception}");
            }
        }
    }

    internal static bool ConsumeMutationRequest(ClanPlayerRef actor)
    {
        if (!actor.IsValid)
        {
            return false;
        }

        float now = Time.realtimeSinceStartup;
        if (MutationRequestBudgets.Count >= MutationBudgetPruneThreshold &&
            !MutationRequestBudgets.ContainsKey(actor.Id))
        {
            PruneExpiredMutationBudgets(now);
        }
        if (!MutationRequestBudgets.TryGetValue(actor.Id, out RequestBudget budget))
        {
            budget = new RequestBudget(now);
            MutationRequestBudgets.Add(actor.Id, budget);
        }
        return ConsumeBudget(
            budget,
            MaximumMutationRequestsPerWindow,
            MutationRequestWindowSeconds,
            now);
    }

    private static void PruneExpiredMutationBudgets(float now)
    {
        List<string>? expired = null;
        foreach (KeyValuePair<string, RequestBudget> pair in MutationRequestBudgets)
        {
            float elapsed = now - pair.Value.WindowStartedAt;
            if (elapsed >= MutationRequestWindowSeconds || elapsed < 0f)
            {
                expired ??= new List<string>();
                expired.Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }
        foreach (string playerId in expired)
        {
            MutationRequestBudgets.Remove(playerId);
        }
    }

    private static bool ConsumeRequest(
        ZRpc rpc,
        IDictionary<ZRpc, RequestBudget> budgets,
        int maximumRequests,
        float windowSeconds)
    {
        float now = Time.realtimeSinceStartup;
        if (!budgets.TryGetValue(rpc, out RequestBudget budget))
        {
            budget = new RequestBudget(now);
            budgets.Add(rpc, budget);
        }
        return ConsumeBudget(budget, maximumRequests, windowSeconds, now);
    }

    private static bool ConsumeBudget(
        RequestBudget budget,
        int maximumRequests,
        float windowSeconds,
        float? nowOverride = null)
    {
        float now = nowOverride ?? Time.realtimeSinceStartup;
        if (now - budget.WindowStartedAt >= windowSeconds || now < budget.WindowStartedAt)
        {
            budget.WindowStartedAt = now;
            budget.Requests = 0;
        }

        if (budget.Requests >= maximumRequests)
        {
            return false;
        }

        budget.Requests++;
        return true;
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
    private static class RegisterClanRpc
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            ClanEmoji.RegisterEmojiFileRpc(__instance, peer);

            if (__instance.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(
                    RequestRpc,
                    (Action<ZRpc, ZPackage>)((rpc, package) => ServerHandleRequest(peer, rpc, package)));
            }
            else
            {
                peer.m_rpc.Register<ZPackage>(ResponseRpc, (Action<ZRpc, ZPackage>)ClientHandleResponse);
            }
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    private static class ResetClanSessionOnGameStart
    {
        private static void Postfix()
        {
            ResetSession();
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    private static class ClearClanSession
    {
        private static void Prefix()
        {
            ResetSession();
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect), typeof(ZNetPeer))]
    private static class ClearEmojiPeerOnDisconnect
    {
        private static void Prefix(ZNetPeer peer, out ClanPlayerRef __state)
        {
            if (peer?.m_rpc != null &&
                PeerIdentities.TryGetValue(peer.m_rpc, out ClanPlayerRef pinnedIdentity))
            {
                __state = pinnedIdentity;
            }
            else
            {
                __state = ZNet.instance?.IsServer() == true
                    ? ClanIdentity.FromPeer(peer)
                    : default;
            }
            ClanEmoji.ForgetEmojiPeer(peer?.m_rpc);
            if (peer?.m_rpc != null)
            {
                DirectoryRequestBudgets.Remove(peer.m_rpc);
                SnapshotRequestBudgets.Remove(peer.m_rpc);
                HudRequestBudgets.Remove(peer.m_rpc);
                PeerIdentities.Remove(peer.m_rpc);
            }
        }

        private static void Postfix(ClanPlayerRef __state)
        {
            ClanRegistry.NotifyPlayerDisconnected(__state);
        }
    }

    private sealed class RequestBudget
    {
        public float WindowStartedAt;
        public int Requests;

        public RequestBudget(float windowStartedAt)
        {
            WindowStartedAt = windowStartedAt;
        }
    }
}
