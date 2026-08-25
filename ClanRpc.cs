using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Clan;

internal static class ClanRpc
{
    internal const string IdentityNotReadyStatus =
        "Unable to identify clan character. Character data may still be loading.";
    internal const string IdentityRejectedStatus =
        "Unable to verify clan character ownership.";
    internal const string SnapshotRateLimitedStatus =
        "Clan snapshot refresh is temporarily rate limited.";
    internal const string MutationRateLimitedStatus =
        "Clan changes are temporarily rate limited. Please wait a few seconds.";
    internal const string DirectoryRateLimitedStatus =
        "Clan directory refresh is temporarily rate limited.";
    internal const string StateUnavailableStatus =
        "Clan state is temporarily unavailable.";

    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumDirectoryResponseBytes = 512 * 1024;
    private const int MaximumDirectoryRequestsPerWindow = 1;
    private const float DirectoryRequestWindowSeconds = 2f;
    private const int MaximumSnapshotRequestsPerWindow = 5;
    private const float SnapshotRequestWindowSeconds = 2f;
    private const int MaximumHudRequestsPerWindow = 5;
    private const float HudRequestWindowSeconds = 2f;
    private const int MaximumMutationRequestsPerWindow = 6;
    private const float MutationRequestWindowSeconds = 5f;
    private const int InitialSnapshotRetryCount = 20;
    private const float InitialSnapshotRetryIntervalSeconds = 0.5f;
    private const string DirectoryTruncatedStatus =
        "Clan directory was truncated to fit the network response limit.";

    private static readonly string RequestRpc = $"{ClanPlugin.ModGUID}.rpc.request.v12";
    private static readonly string ResponseRpc = $"{ClanPlugin.ModGUID}.rpc.response.v12";
    private static readonly Dictionary<ZRpc, RequestBudget> DirectoryRequestBudgets = new();
    private static readonly Dictionary<ZRpc, RequestBudget> SnapshotRequestBudgets = new();
    private static readonly Dictionary<ZRpc, RequestBudget> HudRequestBudgets = new();
    private static readonly Dictionary<string, RequestBudget> MutationRequestBudgets =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<ZRpc, ClanPlayerRef> PeerIdentities = new();
    private static readonly object RequestIdLock = new();
    private static long _nextRequestId;
    private static long _pendingDirectoryRequestId;
    private static int _initialSnapshotRetriesRemaining;
    private static float _nextInitialSnapshotRetryAt;
    private static bool _initialSnapshotBootstrapStarted;
    private static bool _initialSnapshotBootstrapComplete;
    private static bool _identityReady;
    private static bool _retryDirectoryWhenIdentityReady;
    private static float _directoryIdentityRetryAt;
    private static bool _hudRecoveryRequestInProgress;
    private static int _diagnosticSessionOrdinal;

    public static ClanClientSnapshot CurrentSnapshot { get; private set; } = new();
    public static ClanHudSnapshot CurrentHudSnapshot { get; private set; } = new();
    public static ClanDirectorySnapshot CurrentDirectory { get; private set; } = new();
    public static bool IsDirectoryRequestPending => _pendingDirectoryRequestId > 0L;
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
            NotifyStatus($"Invalid clan request: {ex.Message}");
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

        NotifyStatus("Clan server is not connected.");
        return false;
    }

    public static void RequestSnapshot(string reason = "manual")
    {
        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Snapshot request session={_diagnosticSessionOrdinal}; " +
            $"frame={Time.frameCount}; reason={reason}; " +
            $"bootstrapStarted={_initialSnapshotBootstrapStarted}; " +
            $"bootstrapComplete={_initialSnapshotBootstrapComplete}; " +
            $"retriesRemaining={_initialSnapshotRetriesRemaining}; " +
            $"transportReady={CanRunInitialSnapshotBootstrap()}; " +
            $"currentHasClan={CurrentSnapshot.HasClan}.");
        if (!Send(ClanRequest.Simple(ClanRequestType.RequestSnapshot)))
        {
            ClanPlugin.ClanLogger.LogInfo(
                $"[Clan.Diag] Snapshot request send failed " +
                $"session={_diagnosticSessionOrdinal}; frame={Time.frameCount}; " +
                $"reason={reason}.");
        }
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
        EnsureInitialSnapshotBootstrap();
        if (!_initialSnapshotBootstrapComplete &&
            _initialSnapshotRetriesRemaining > 0 &&
            now >= _nextInitialSnapshotRetryAt &&
            CanRunInitialSnapshotBootstrap())
        {
            _initialSnapshotRetriesRemaining--;
            _nextInitialSnapshotRetryAt = now + InitialSnapshotRetryIntervalSeconds;
            RequestSnapshot("bootstrap-retry");
        }

        if (_identityReady &&
            _retryDirectoryWhenIdentityReady &&
            now >= _directoryIdentityRetryAt)
        {
            _retryDirectoryWhenIdentityReady = false;
            RequestDirectory();
        }
    }

    private static void RequestInitialSnapshot(string trigger)
    {
        if (_initialSnapshotBootstrapStarted || _initialSnapshotBootstrapComplete)
        {
            return;
        }

        _initialSnapshotBootstrapStarted = true;
        _identityReady = false;
        _initialSnapshotRetriesRemaining = InitialSnapshotRetryCount;
        _nextInitialSnapshotRetryAt = Time.realtimeSinceStartup;
        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Snapshot bootstrap started " +
            $"session={_diagnosticSessionOrdinal}; frame={Time.frameCount}; " +
            $"trigger={trigger}; " +
            $"transportReady={CanRunInitialSnapshotBootstrap()}; " +
            $"retries={InitialSnapshotRetryCount}.");
        if (CanRunInitialSnapshotBootstrap())
        {
            _nextInitialSnapshotRetryAt += InitialSnapshotRetryIntervalSeconds;
            RequestSnapshot($"bootstrap-{trigger}");
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

        RequestInitialSnapshot("tick-fallback");
    }

    private static bool CanRunInitialSnapshotBootstrap()
    {
        ZNet? network = ZNet.instance;
        return Game.instance != null &&
               Player.m_localPlayer != null &&
               network != null &&
               (network.GetServerRPC() != null || network.IsServer());
    }

    private static void LogSnapshotResponse(
        ClanClientSnapshot snapshot,
        bool transientFailure)
    {
        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Snapshot response session={_diagnosticSessionOrdinal}; " +
            $"frame={Time.frameCount}; result={snapshot.ResponseResultCode}; " +
            $"trackedRequest={snapshot.ResponseRequestId > 0L}; " +
            $"updatesCurrent={!transientFailure}; " +
            $"previousHasClan={CurrentSnapshot.HasClan}; " +
            $"incomingHasClan={snapshot.HasClan}; " +
            $"effectiveClanChanged={!StringComparer.Ordinal.Equals(CurrentSnapshot.ClanId, snapshot.ClanId)}; " +
            $"hasPrimary={snapshot.HasPrimaryClan}; hasGuest={snapshot.HasGuestClan}; " +
            $"roster={snapshot.Roster.Count}; applications={snapshot.Applications.Count}; " +
            $"invite={snapshot.Invite != null}; ownApplication={snapshot.HasOwnApplication}; " +
            $"bootstrapStarted={_initialSnapshotBootstrapStarted}; " +
            $"bootstrapComplete={_initialSnapshotBootstrapComplete}; " +
            $"retriesRemaining={_initialSnapshotRetriesRemaining}.");
    }

    public static long RequestDirectory()
    {
        _retryDirectoryWhenIdentityReady = false;
        ZNet? network = ZNet.instance;
        if (network == null ||
            (network.GetServerRPC() == null && !network.IsServer()))
        {
            _pendingDirectoryRequestId = 0L;
            NotifyStatus("Clan server is not connected.");
            return 0L;
        }

        long requestId = NextRequestId();
        _pendingDirectoryRequestId = requestId;
        if (!Send(new ClanRequest
        {
            Type = ClanRequestType.RequestDirectory,
            RequestId = requestId
        }))
        {
            _pendingDirectoryRequestId = 0L;
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
        _pendingDirectoryRequestId = 0L;
        _retryDirectoryWhenIdentityReady = true;
        _directoryIdentityRetryAt = Time.realtimeSinceStartup + DirectoryRequestWindowSeconds + 0.1f;
        CurrentDirectory = new ClanDirectorySnapshot();
        DirectoryChanged?.Invoke(CurrentDirectory);
    }

    public static void NotifyStatus(string message)
    {
        StatusReceived?.Invoke(message);
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

        MarkDirectoryTruncated(snapshot);
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
            snapshot.Status = "Clan directory exceeds the network response limit.";
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

    private static void MarkDirectoryTruncated(ClanDirectorySnapshot snapshot)
    {
        snapshot.IsTruncated = true;
        if (string.IsNullOrWhiteSpace(snapshot.Status))
        {
            snapshot.Status = DirectoryTruncatedStatus;
            return;
        }

        string combined = snapshot.Status + "\n" + DirectoryTruncatedStatus;
        if (combined.Length <= ClanDataRules.MaxStatusLength)
        {
            snapshot.Status = combined;
        }
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
        response.Write(senderName);
        response.Write(message);
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
                    bool transientSnapshotFailure = snapshot.ResponseResultCode is
                        ClanOperationResultCode.IdentityUnavailable or
                        ClanOperationResultCode.SnapshotRateLimited or
                        ClanOperationResultCode.RateLimited or
                        ClanOperationResultCode.Unavailable;
                    LogSnapshotResponse(snapshot, transientSnapshotFailure);
                    if (transientSnapshotFailure)
                    {
                        if (snapshot.ResponseRequestId > 0L)
                        {
                            CopySnapshotPresentation(CurrentSnapshot, snapshot);
                            SnapshotChanged?.Invoke(snapshot);
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
                        StatusReceived?.Invoke(snapshot.Status);
                        break;
                    }
                    bool bootstrapWasComplete = _initialSnapshotBootstrapComplete;
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
                        HudSnapshotChanged?.Invoke(CurrentHudSnapshot);
                    }
                    ClanMap.OnSnapshotChanged(CurrentSnapshot);
                    SnapshotChanged?.Invoke(CurrentSnapshot);
                    if (!string.IsNullOrWhiteSpace(CurrentSnapshot.Status))
                    {
                        StatusReceived?.Invoke(CurrentSnapshot.Status);
                    }
                    if (!bootstrapWasComplete && _initialSnapshotBootstrapComplete)
                    {
                        ClanPlugin.ClanLogger.LogInfo(
                            $"[Clan.Diag] Snapshot bootstrap completed " +
                            $"session={_diagnosticSessionOrdinal}; frame={Time.frameCount}; " +
                            $"result={snapshot.ResponseResultCode}; " +
                            $"identityReady={_identityReady}; " +
                            $"hasClan={CurrentSnapshot.HasClan}.");
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
                        ChatReceived?.Invoke(senderName, message);
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
                    if (_pendingDirectoryRequestId <= 0L ||
                        directory.RequestId != _pendingDirectoryRequestId)
                    {
                        return;
                    }
                    _pendingDirectoryRequestId = 0L;
                    if (directory.ResultCode == ClanOperationResultCode.IdentityUnavailable)
                    {
                        _retryDirectoryWhenIdentityReady = true;
                        _directoryIdentityRetryAt =
                            Time.realtimeSinceStartup + DirectoryRequestWindowSeconds + 0.1f;
                        StatusReceived?.Invoke(directory.Status);
                        break;
                    }
                    if (directory.ResultCode == ClanOperationResultCode.RateLimited)
                    {
                        _retryDirectoryWhenIdentityReady = true;
                        _directoryIdentityRetryAt =
                            Time.realtimeSinceStartup + DirectoryRequestWindowSeconds + 0.1f;
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
                    DirectoryChanged?.Invoke(CurrentDirectory);
                    if (!string.IsNullOrWhiteSpace(CurrentDirectory.Status))
                    {
                        StatusReceived?.Invoke(CurrentDirectory.Status);
                    }
                    break;
                case ClanResponseType.DirectoryInvalidated:
                    RequirePackageConsumed(package);
                    packageValidated = true;
                    _pendingDirectoryRequestId = 0L;
                    _retryDirectoryWhenIdentityReady = true;
                    _directoryIdentityRetryAt = Time.realtimeSinceStartup + 0.1f;
                    CurrentDirectory = new ClanDirectorySnapshot();
                    DirectoryChanged?.Invoke(CurrentDirectory);
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
            HudSnapshotChanged?.Invoke(CurrentHudSnapshot);
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
        HudSnapshotChanged?.Invoke(CurrentHudSnapshot);
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
        HudSnapshotChanged?.Invoke(CurrentHudSnapshot);

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
                !ConsumeDirectoryRequest(rpc))
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
                !ConsumeSnapshotRequest(rpc))
            {
                ClanRpc.SendSnapshot(peer, new ClanClientSnapshot
                {
                    Status = SnapshotRateLimitedStatus,
                    ResponseResultCode = ClanOperationResultCode.SnapshotRateLimited
                });
                return;
            }
            if (request.Type == ClanRequestType.RequestHud &&
                !ConsumeHudRequest(rpc))
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

    private static void ResetSession(string reason)
    {
        _diagnosticSessionOrdinal++;
        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Session reset session={_diagnosticSessionOrdinal}; " +
            $"frame={Time.frameCount}; reason={reason}; " +
            $"previousHasClan={CurrentSnapshot.HasClan}; " +
            $"bootstrapStarted={_initialSnapshotBootstrapStarted}; " +
            $"bootstrapComplete={_initialSnapshotBootstrapComplete}; " +
            $"identityReady={_identityReady}.");
        CurrentSnapshot = new ClanClientSnapshot();
        CurrentHudSnapshot = new ClanHudSnapshot();
        CurrentDirectory = new ClanDirectorySnapshot();
        _pendingDirectoryRequestId = 0L;
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
        SnapshotChanged?.Invoke(CurrentSnapshot);
        HudSnapshotChanged?.Invoke(CurrentHudSnapshot);
        DirectoryChanged?.Invoke(CurrentDirectory);
    }

    private static bool ConsumeDirectoryRequest(ZRpc rpc)
    {
        return ConsumeRequest(
            rpc,
            DirectoryRequestBudgets,
            MaximumDirectoryRequestsPerWindow,
            DirectoryRequestWindowSeconds);
    }

    private static bool ConsumeSnapshotRequest(ZRpc rpc)
    {
        return ConsumeRequest(
            rpc,
            SnapshotRequestBudgets,
            MaximumSnapshotRequestsPerWindow,
            SnapshotRequestWindowSeconds);
    }

    private static bool ConsumeHudRequest(ZRpc rpc)
    {
        return ConsumeRequest(
            rpc,
            HudRequestBudgets,
            MaximumHudRequestsPerWindow,
            HudRequestWindowSeconds);
    }

    internal static bool ConsumeMutationRequest(ClanPlayerRef actor)
    {
        if (!actor.IsValid)
        {
            return false;
        }

        float now = Time.realtimeSinceStartup;
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
            ResetSession("game-start");
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
    private static class RequestSnapshotWhenPlayerIsReady
    {
        private static void Postfix(Player __result)
        {
            if (__result != null && __result == Player.m_localPlayer)
            {
                RequestInitialSnapshot("player-spawn");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    private static class ClearClanSession
    {
        private static void Prefix()
        {
            ResetSession("network-shutdown");
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
