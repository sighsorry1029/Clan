using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Clan;

/// <summary>
/// Result of a server-authoritative ward authorization lookup.
/// </summary>
public enum ClanWardAuthorizationResolution
{
    /// <summary>The authoritative Clan registry cannot currently answer the query.</summary>
    Unavailable = 0,
    /// <summary>The identity resolved but has no authorized primary Clan membership.</summary>
    ResolvedNoAuthorization = 1,
    /// <summary>The identity is a Leader, Officer, or Member of the returned primary Clan.</summary>
    Authorized = 2
}

/// <summary>
/// Result of a server-authoritative primary and Guest Clan membership lookup.
/// </summary>
public enum ClanMembershipResolution
{
    /// <summary>The authoritative Clan registry cannot currently answer the query.</summary>
    Unavailable = 0,
    /// <summary>
    /// The identity was resolved. Empty membership values mean that the identity has no
    /// membership of that kind.
    /// </summary>
    Resolved = 1
}

public sealed class ClanInfo
{
    public string ClanId { get; }
    public string Name { get; }
    public string Description { get; }
    public string EmblemKey { get; }
    public string LeaderName { get; }
    public int MemberCount { get; }

    internal ClanInfo(
        string clanId,
        string name,
        string description,
        string emblemKey,
        string leaderName,
        int memberCount)
    {
        ClanId = clanId;
        Name = name;
        Description = description;
        EmblemKey = emblemKey;
        LeaderName = leaderName;
        MemberCount = memberCount;
    }
}

public sealed class ClanMemberInfo
{
    public string PlayerKey { get; }
    public string PlatformId { get; }
    public long PlayerId { get; }
    public string Name { get; }
    public ClanRole Role { get; }
    public bool IsSelf { get; }
    public bool IsOnline { get; }

    internal ClanMemberInfo(ClanPlayerSummary source)
    {
        PlayerKey = source.Player.Id;
        PlatformId = source.Player.PlatformId;
        PlayerId = source.Player.CharacterPlayerId;
        Name = source.Player.Name;
        Role = source.Role;
        IsSelf = source.IsSelf;
        IsOnline = source.IsOnline;
    }
}

public sealed class ClanLocalSnapshot
{
    private static readonly IReadOnlyList<ClanMemberInfo> NoMembers =
        new ReadOnlyCollection<ClanMemberInfo>(Array.Empty<ClanMemberInfo>());

    public bool IsReady { get; }
    public bool HasClan => Clan != null;
    public bool HasPrimaryClan => !string.IsNullOrWhiteSpace(PrimaryClanId);
    public bool HasGuestClan => !string.IsNullOrWhiteSpace(GuestClanId);
    public bool HasAnyClan => HasPrimaryClan || HasGuestClan;
    /// <summary>
    /// The clan currently used by gameplay systems. Guest takes precedence over primary.
    /// </summary>
    public string EffectiveClanId => Clan?.ClanId ?? "";
    public string PrimaryClanId { get; }
    public string PrimaryClanName { get; }
    public ClanRole? PrimaryRole { get; }
    public string GuestClanId { get; }
    public string GuestClanName { get; }
    /// <summary>The effective clan projection (Guest first, otherwise primary).</summary>
    public ClanInfo? Clan { get; }
    /// <summary>The local character's role in the effective clan.</summary>
    public ClanRole? SelfRole { get; }
    /// <summary>The effective clan roster.</summary>
    public IReadOnlyList<ClanMemberInfo> Members { get; }

    internal static ClanLocalSnapshot Empty { get; } = new(
        false,
        "",
        "",
        null,
        "",
        "",
        null,
        null,
        NoMembers);

    internal ClanLocalSnapshot(
        bool isReady,
        string primaryClanId,
        string primaryClanName,
        ClanRole? primaryRole,
        string guestClanId,
        string guestClanName,
        ClanInfo? clan,
        ClanRole? selfRole,
        IReadOnlyList<ClanMemberInfo> members)
    {
        IsReady = isReady;
        PrimaryClanId = primaryClanId ?? "";
        PrimaryClanName = primaryClanName ?? "";
        PrimaryRole = primaryRole;
        GuestClanId = guestClanId ?? "";
        GuestClanName = guestClanName ?? "";
        Clan = clan;
        SelfRole = selfRole;
        Members = members;
    }
}

public enum ClanRenameResultCode
{
    Unknown = 0,
    Success = 1,
    Unchanged = 2,
    InvalidName = 3,
    NotConnected = 4,
    IdentityUnavailable = 5,
    NotLeader = 6,
    ClanChanged = 7,
    NameAlreadyExists = 8,
    RateLimited = 9,
    TimedOut = 10,
    SessionEnded = 11,
    Failed = 12
}

public sealed class ClanRenameResult
{
    public long RequestId { get; }
    public ClanRenameResultCode Code { get; }
    public bool Succeeded =>
        Code is ClanRenameResultCode.Success or ClanRenameResultCode.Unchanged;
    public string RequestedName { get; }
    public string Message { get; }
    public ClanLocalSnapshot State { get; }

    internal ClanRenameResult(
        long requestId,
        ClanRenameResultCode code,
        string requestedName,
        string message,
        ClanLocalSnapshot state)
    {
        RequestId = requestId;
        Code = code;
        RequestedName = requestedName;
        Message = message;
        State = state;
    }
}

public sealed class ClanRenameRequestHandle
{
    private readonly object _gate = new();
    private Action<ClanRenameResult>? _completed;
    private ClanRenameResult? _result;

    public long RequestId { get; }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _result != null;
            }
        }
    }

    public ClanRenameResult? Result
    {
        get
        {
            lock (_gate)
            {
                return _result;
            }
        }
    }

    /// <summary>
    /// Invokes a newly-added subscriber immediately when this request already completed.
    /// </summary>
    public event Action<ClanRenameResult>? Completed
    {
        add
        {
            if (value == null)
            {
                return;
            }

            ClanRenameResult? completedResult;
            lock (_gate)
            {
                completedResult = _result;
                if (completedResult == null)
                {
                    _completed += value;
                    return;
                }
            }
            InvokeSubscriber(value, completedResult);
        }
        remove
        {
            lock (_gate)
            {
                _completed -= value;
            }
        }
    }

    internal ClanRenameRequestHandle(long requestId)
    {
        RequestId = requestId;
    }

    public bool TryGetResult(out ClanRenameResult? result)
    {
        lock (_gate)
        {
            result = _result;
            return result != null;
        }
    }

    internal bool Complete(ClanRenameResult result)
    {
        Action<ClanRenameResult>? subscribers;
        lock (_gate)
        {
            if (_result != null)
            {
                return false;
            }
            _result = result;
            subscribers = _completed;
            _completed = null;
        }

        if (subscribers != null)
        {
            foreach (Action<ClanRenameResult> subscriber in subscribers.GetInvocationList())
            {
                InvokeSubscriber(subscriber, result);
            }
        }
        return true;
    }

    private static void InvokeSubscriber(
        Action<ClanRenameResult> subscriber,
        ClanRenameResult result)
    {
        try
        {
            subscriber(result);
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan API rename completion subscriber failed: {exception}");
        }
    }
}

public static class ClanApi
{
    public const int ApiVersion = 4;

    private const float RenameTimeoutSeconds = 15f;
    private static readonly object PendingLock = new();
    private static readonly Dictionary<long, PendingRename> PendingRenames = new();
    private static readonly IReadOnlyList<ClanInfo> NoClans =
        new ReadOnlyCollection<ClanInfo>(Array.Empty<ClanInfo>());

    private static bool _initialized;
    private static long _registryRevision;

    public static ClanLocalSnapshot Current { get; private set; } = ClanLocalSnapshot.Empty;
    public static IReadOnlyList<ClanInfo> KnownClans { get; private set; } = NoClans;
    /// <summary>
    /// Monotonically increases after a membership or profile change is committed by the server.
    /// Consumers should treat this as an invalidation token, not as persisted Clan data.
    /// </summary>
    public static long RegistryRevision => Interlocked.Read(ref _registryRevision);

    public static event Action<ClanLocalSnapshot>? StateChanged;
    public static event Action<IReadOnlyList<ClanInfo>>? DirectoryChanged;
    public static event Action<ClanRenameResult>? RenameCompleted;
    /// <summary>
    /// Raised after a server-authoritative membership or profile change is durably committed.
    /// Subscriber exceptions are isolated from Clan persistence and from other subscribers.
    /// </summary>
    public static event Action? WardAuthorizationChanged;
    /// <summary>
    /// Raised after a server-authoritative membership or profile change is durably committed.
    /// Subscriber exceptions are isolated from Clan persistence and from other subscribers.
    /// </summary>
    public static event Action? RegistryChanged;

    /// <summary>
    /// Resolves the canonical platform-and-character identity against the authoritative server
    /// registry. Only a primary Leader, Officer, or Member is authorized; Guest connections are
    /// deliberately excluded. Empty output values accompany every non-authorized result.
    /// </summary>
    public static ClanWardAuthorizationResolution ResolveWardAuthorization(
        string platformId,
        long characterPlayerId,
        out string clanId,
        out string clanName)
    {
        return ClanRegistry.ResolveWardAuthorization(
            platformId,
            characterPlayerId,
            out clanId,
            out clanName);
    }

    /// <summary>
    /// Resolves the canonical platform-and-character identity against the authoritative server
    /// registry and returns its independent primary and Guest memberships. A successfully
    /// resolved identity with no membership returns <see cref="ClanMembershipResolution.Resolved"/>
    /// with empty values.
    /// </summary>
    public static ClanMembershipResolution ResolveMemberships(
        string platformId,
        long characterPlayerId,
        out string primaryClanId,
        out string primaryClanName,
        out string guestClanId,
        out string guestClanName)
    {
        return ClanRegistry.ResolveMemberships(
            platformId,
            characterPlayerId,
            out primaryClanId,
            out primaryClanName,
            out guestClanId,
            out guestClanName);
    }

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ClanRpc.SnapshotChanged += OnSnapshotChanged;
        ClanRpc.DirectoryChanged += OnDirectoryChanged;
        Current = CreateLocalSnapshot(ClanRpc.CurrentSnapshot);
        KnownClans = CreateDirectory(ClanRpc.CurrentDirectory);
    }

    internal static void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        ClanRpc.SnapshotChanged -= OnSnapshotChanged;
        ClanRpc.DirectoryChanged -= OnDirectoryChanged;
        CompleteAll(ClanRenameResultCode.SessionEnded, "Clan API was shut down.");
        _initialized = false;
        Current = ClanLocalSnapshot.Empty;
        KnownClans = NoClans;
        StateChanged = null;
        DirectoryChanged = null;
        RenameCompleted = null;
        WardAuthorizationChanged = null;
        RegistryChanged = null;
    }

    internal static void NotifyRegistryChanged()
    {
        Interlocked.Increment(ref _registryRevision);
        InvokeInvalidationSubscribers(
            WardAuthorizationChanged,
            "ward authorization");
        InvokeInvalidationSubscribers(RegistryChanged, "registry");
    }

    private static void InvokeInvalidationSubscribers(
        Action? subscribers,
        string eventName)
    {
        if (subscribers == null)
        {
            return;
        }

        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan API {eventName} subscriber failed: {exception}");
            }
        }
    }

    internal static void ResetSession()
    {
        ClanLocalSnapshot nextState = CreateLocalSnapshot(ClanRpc.CurrentSnapshot);
        bool stateChanged = !HasSameState(Current, nextState);
        Current = nextState;
        if (stateChanged)
        {
            InvokeSubscribers(StateChanged, Current, "state");
        }

        bool directoryChanged = KnownClans.Count != 0;
        KnownClans = NoClans;
        if (directoryChanged)
        {
            InvokeSubscribers(DirectoryChanged, KnownClans, "directory");
        }
        CompleteAll(ClanRenameResultCode.SessionEnded, "The clan session ended.");
    }

    internal static void Tick()
    {
        List<PendingRename>? timedOut = null;
        float now = Time.realtimeSinceStartup;
        lock (PendingLock)
        {
            foreach (PendingRename pending in PendingRenames.Values)
            {
                if (now >= pending.Deadline)
                {
                    timedOut ??= new List<PendingRename>();
                    timedOut.Add(pending);
                }
            }
            if (timedOut != null)
            {
                foreach (PendingRename pending in timedOut)
                {
                    PendingRenames.Remove(pending.Handle.RequestId);
                }
            }
        }

        if (timedOut == null)
        {
            return;
        }
        foreach (PendingRename pending in timedOut)
        {
            Complete(
                pending,
                ClanRenameResultCode.TimedOut,
                "The clan rename request timed out.");
        }
    }

    public static ClanInfo? GetClan(string clanId)
    {
        if (Current.Clan != null &&
            StringComparer.Ordinal.Equals(Current.Clan.ClanId, clanId))
        {
            return Current.Clan;
        }
        return KnownClans.FirstOrDefault(clan =>
            StringComparer.Ordinal.Equals(clan.ClanId, clanId));
    }

    public static IReadOnlyList<ClanMemberInfo>? GetMembers(string clanId)
    {
        return Current.Clan != null &&
               StringComparer.Ordinal.Equals(Current.Clan.ClanId, clanId)
            ? Current.Members
            : null;
    }

    public static long RequestDirectoryRefresh()
    {
        return ClanRpc.RequestDirectory();
    }

    public static ClanRenameRequestHandle RequestRenameClan(string newName)
    {
        long requestId = ClanRpc.NextRequestId();
        ClanRenameRequestHandle handle = new(requestId);
        string normalizedName;
        try
        {
            normalizedName = ClanDataRules.RequireClanName(newName);
        }
        catch (InvalidDataException exception)
        {
            CompleteLocal(
                handle,
                ClanRenameResultCode.InvalidName,
                newName ?? "",
                exception.Message);
            return handle;
        }

        if (!Current.IsReady)
        {
            CompleteLocal(
                handle,
                ClanRenameResultCode.IdentityUnavailable,
                normalizedName,
                "Clan identity is not ready.");
            return handle;
        }
        if (Current.Clan == null)
        {
            CompleteLocal(
                handle,
                ClanRenameResultCode.ClanChanged,
                normalizedName,
                "The local character does not belong to a clan.");
            return handle;
        }

        PendingRename pending = new(
            handle,
            normalizedName,
            Time.realtimeSinceStartup + RenameTimeoutSeconds);
        lock (PendingLock)
        {
            PendingRenames.Add(requestId, pending);
        }

        bool sent = ClanRpc.Send(new ClanRequest
        {
            Type = ClanRequestType.RenameClan,
            RequestId = requestId,
            ClanId = Current.Clan.ClanId,
            ClanName = normalizedName
        });
        if (!sent)
        {
            bool removed;
            lock (PendingLock)
            {
                removed = PendingRenames.Remove(requestId);
            }
            if (removed)
            {
                Complete(
                    pending,
                    ClanRenameResultCode.NotConnected,
                    "Clan server is not connected.");
            }
        }
        return handle;
    }

    private static void OnSnapshotChanged(ClanClientSnapshot snapshot)
    {
        ClanLocalSnapshot next = CreateLocalSnapshot(snapshot);
        bool changed = !HasSameState(Current, next);
        Current = next;
        if (changed)
        {
            InvokeSubscribers(StateChanged, Current, "state");
        }

        if (snapshot.ResponseRequestId <= 0L)
        {
            return;
        }

        PendingRename? pending;
        lock (PendingLock)
        {
            if (!PendingRenames.TryGetValue(snapshot.ResponseRequestId, out pending))
            {
                return;
            }
            PendingRenames.Remove(snapshot.ResponseRequestId);
        }
        Complete(pending, MapResult(snapshot), snapshot.Status);
    }

    private static void OnDirectoryChanged(ClanDirectorySnapshot directory)
    {
        IReadOnlyList<ClanInfo> next = CreateDirectory(directory);
        bool changed = !HasSameDirectory(KnownClans, next);
        KnownClans = next;
        if (changed)
        {
            InvokeSubscribers(DirectoryChanged, KnownClans, "directory");
        }
    }

    private static ClanLocalSnapshot CreateLocalSnapshot(ClanClientSnapshot source)
    {
        List<ClanMemberInfo> members = source.Roster
            .Select(member => new ClanMemberInfo(member))
            .ToList();
        IReadOnlyList<ClanMemberInfo> readOnlyMembers =
            new ReadOnlyCollection<ClanMemberInfo>(members);
        ClanInfo? clan = null;
        if (source.HasClan)
        {
            string leaderName = source.Roster
                .FirstOrDefault(member => member.Role == ClanRole.Leader)
                ?.Name ?? "";
            clan = new ClanInfo(
                source.ClanId,
                source.ClanName,
                source.ClanDescription,
                source.ClanEmblemKey,
                leaderName,
                source.Roster.Count);
        }
        return new ClanLocalSnapshot(
            ClanRpc.IsIdentityReady,
            source.PrimaryClanId,
            source.PrimaryClanName,
            source.HasPrimaryClan ? source.PrimaryRole : null,
            source.GuestClanId,
            source.GuestClanName,
            clan,
            source.HasClan ? source.SelfRole : null,
            readOnlyMembers);
    }

    private static IReadOnlyList<ClanInfo> CreateDirectory(ClanDirectorySnapshot source)
    {
        List<ClanInfo> clans = source.PublicClans
            .Select(clan => new ClanInfo(
                clan.ClanId,
                clan.Name,
                clan.Description,
                clan.EmblemKey,
                clan.LeaderName,
                clan.MemberCount))
            .ToList();
        return new ReadOnlyCollection<ClanInfo>(clans);
    }

    private static ClanRenameResultCode MapResult(ClanClientSnapshot snapshot)
    {
        return snapshot.ResponseResultCode switch
        {
            ClanOperationResultCode.Success => ClanRenameResultCode.Success,
            ClanOperationResultCode.Unchanged => ClanRenameResultCode.Unchanged,
            ClanOperationResultCode.InvalidName => ClanRenameResultCode.InvalidName,
            ClanOperationResultCode.IdentityUnavailable => ClanRenameResultCode.IdentityUnavailable,
            ClanOperationResultCode.IdentityRejected => ClanRenameResultCode.IdentityUnavailable,
            ClanOperationResultCode.Unauthorized => ClanRenameResultCode.NotLeader,
            ClanOperationResultCode.ClanChanged => ClanRenameResultCode.ClanChanged,
            ClanOperationResultCode.NameTaken => ClanRenameResultCode.NameAlreadyExists,
            ClanOperationResultCode.RateLimited => ClanRenameResultCode.RateLimited,
            ClanOperationResultCode.Unavailable => ClanRenameResultCode.Failed,
            _ => ClanRenameResultCode.Failed
        };
    }

    private static void CompleteLocal(
        ClanRenameRequestHandle handle,
        ClanRenameResultCode code,
        string requestedName,
        string message)
    {
        ClanRenameResult result = new(
            handle.RequestId,
            code,
            requestedName,
            message,
            Current);
        if (handle.Complete(result))
        {
            InvokeSubscribers(RenameCompleted, result, "rename completion");
        }
    }

    private static void Complete(
        PendingRename pending,
        ClanRenameResultCode code,
        string message)
    {
        CompleteLocal(
            pending.Handle,
            code,
            pending.RequestedName,
            message);
    }

    private static void CompleteAll(ClanRenameResultCode code, string message)
    {
        PendingRename[] pending;
        lock (PendingLock)
        {
            pending = PendingRenames.Values.ToArray();
            PendingRenames.Clear();
        }
        foreach (PendingRename request in pending)
        {
            Complete(request, code, message);
        }
    }

    private static bool HasSameState(ClanLocalSnapshot left, ClanLocalSnapshot right)
    {
        if (left.IsReady != right.IsReady ||
            !StringComparer.Ordinal.Equals(left.PrimaryClanId, right.PrimaryClanId) ||
            !StringComparer.Ordinal.Equals(left.PrimaryClanName, right.PrimaryClanName) ||
            left.PrimaryRole != right.PrimaryRole ||
            !StringComparer.Ordinal.Equals(left.GuestClanId, right.GuestClanId) ||
            !StringComparer.Ordinal.Equals(left.GuestClanName, right.GuestClanName) ||
            left.SelfRole != right.SelfRole ||
            !HasSameClan(left.Clan, right.Clan) ||
            left.Members.Count != right.Members.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Members.Count; index++)
        {
            ClanMemberInfo a = left.Members[index];
            ClanMemberInfo b = right.Members[index];
            if (!StringComparer.Ordinal.Equals(a.PlayerKey, b.PlayerKey) ||
                !StringComparer.Ordinal.Equals(a.Name, b.Name) ||
                a.Role != b.Role ||
                a.IsSelf != b.IsSelf ||
                a.IsOnline != b.IsOnline)
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasSameDirectory(
        IReadOnlyList<ClanInfo> left,
        IReadOnlyList<ClanInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (!HasSameClan(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasSameClan(ClanInfo? left, ClanInfo? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        return left != null && right != null &&
               StringComparer.Ordinal.Equals(left.ClanId, right.ClanId) &&
               StringComparer.Ordinal.Equals(left.Name, right.Name) &&
               StringComparer.Ordinal.Equals(left.Description, right.Description) &&
               StringComparer.Ordinal.Equals(left.EmblemKey, right.EmblemKey) &&
               StringComparer.Ordinal.Equals(left.LeaderName, right.LeaderName) &&
               left.MemberCount == right.MemberCount;
    }

    private static void InvokeSubscribers<T>(
        Action<T>? subscribers,
        T value,
        string eventName)
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
                    $"Clan API {eventName} subscriber failed: {exception}");
            }
        }
    }

    private sealed class PendingRename
    {
        public ClanRenameRequestHandle Handle { get; }
        public string RequestedName { get; }
        public float Deadline { get; }

        public PendingRename(
            ClanRenameRequestHandle handle,
            string requestedName,
            float deadline)
        {
            Handle = handle;
            RequestedName = requestedName;
            Deadline = deadline;
        }
    }
}
