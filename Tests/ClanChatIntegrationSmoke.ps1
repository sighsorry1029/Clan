#requires -Version 7.0
<#
Source-linked smoke tests for Clan API v5. Production notifier, acceptance, text
validation, broadcast recipient filtering, registry index replacement, and local-host
send methods are compiled unchanged with minimal game dependency stubs. Full rename
persistence and identity/dispatch/disposal boundaries are checked only in source.
This does not replace an in-game dedicated-server/client integration test.
Run: pwsh -NoProfile -File Tests/ClanChatIntegrationSmoke.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$clanRoot = Split-Path -Parent $PSScriptRoot
$apiSource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanApi.cs') -Raw
$registrySource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanRegistry.cs') -Raw
$rpcSource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanRpc.cs') -Raw
$modelSource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanModels.cs') -Raw

function Get-SourceBlock([string] $Source, [string] $Declaration) {
    $start = $Source.IndexOf($Declaration, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production declaration: $Declaration" }
    $open = $Source.IndexOf('{', $start)
    if ($open -lt 0) { throw "Missing production body: $Declaration" }
    $depth = 0
    for ($offset = $open; $offset -lt $Source.Length; $offset++) {
        if ($Source[$offset] -eq '{') { $depth++ }
        if ($Source[$offset] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $Source.Substring($start, $offset - $start + 1) }
        }
    }
    throw "Unterminated production body: $Declaration"
}

function Assert-SourceOrder([string] $Source, [string[]] $Tokens, [string] $Label) {
    $previous = -1
    foreach ($token in $Tokens) {
        $next = $Source.IndexOf($token, $previous + 1, [StringComparison]::Ordinal)
        if ($next -lt 0) { throw "Source contract failed ($Label): $token" }
        $previous = $next
    }
}

$eventDeclaration = [regex]::Match(
    $apiSource,
    'public static event Action<string, long, string, string, string, string>\? ServerChatAccepted;').Value
if (!$eventDeclaration) { throw 'ServerChatAccepted public event signature changed.' }
$notify = Get-SourceBlock $apiSource 'internal static void NotifyServerChatAccepted('
$nonfatal = Get-SourceBlock $apiSource 'private static bool IsNonFatalChatSubscriberException('
$sendChat = Get-SourceBlock $registrySource 'private static string SendClanChat('
$sendRequest = Get-SourceBlock $rpcSource 'public static bool Send(ClanRequest request)'
$broadcastChat = Get-SourceBlock $rpcSource 'public static void BroadcastChat('
$isEffectiveMember = Get-SourceBlock $registrySource 'internal static bool IsEffectiveMember('
$findEffectiveClan = Get-SourceBlock $registrySource 'private static ClanState? FindEffectiveClan('
$findActiveClan = Get-SourceBlock $registrySource 'private static ClanState? FindActiveClan('
$findPrimaryClan = Get-SourceBlock $registrySource 'private static ClanState? FindPrimaryClan('
$findGuestClan = Get-SourceBlock $registrySource 'private static ClanState? FindGuestClan('
$swapState = Get-SourceBlock $registrySource 'private static void SwapState('
$registryData = Get-SourceBlock $registrySource 'private sealed class RegistryData'
$clanRole = Get-SourceBlock $modelSource 'public enum ClanRole'
$clanMember = Get-SourceBlock $modelSource 'internal sealed class ClanMember'
$requireText = Get-SourceBlock $modelSource 'public static string RequireText('
$requireClanId = Get-SourceBlock $modelSource 'public static string RequireClanId('
$validationFailure = Get-SourceBlock $modelSource 'private static InvalidDataException ValidationFailure('
$validationField = Get-SourceBlock $modelSource 'internal enum ClanValidationField'
$validationError = Get-SourceBlock $modelSource 'internal enum ClanValidationError'
$chatInterval = [regex]::Match($registrySource, 'private const float ClanChatInterval = [^;]+;').Value
$maxChatLength = [regex]::Match($modelSource, 'public const int MaxChatMessageLength = [^;]+;').Value

Assert-SourceOrder (Get-SourceBlock $registrySource 'public static void HandleRequest(') @(
    'ClanIdentity.FromPeer(peer, out bool identityRetryable)',
    'if (!actor.IsValid)',
    'return;',
    'if (!ClanRpc.TryPinPeerIdentity(peer, actor))',
    'return;',
    'ClanRequestType.SendClanChat => SendClanChat(') 'authoritative identity before chat dispatch'
Assert-SourceOrder $sendChat @(
    'FindEffectiveClan(actor, requestedClanId)',
    '!clan.Members.TryGetValue(actor.Id, out ClanMember member)',
    'ClanDataRules.RequireText(',
    'now - member.LastClanChatTime < ClanChatInterval',
    'return "";',
    'member.LastClanChatTime = now;',
    'ClanApi.NotifyServerChatAccepted(',
    'ClanRpc.BroadcastChat(') 'validate, accept, notify, deliver'
Assert-SourceOrder (Get-SourceBlock $apiSource 'internal static void Dispose()') @(
    'ServerChatAccepted = null;', 'if (!_initialized)') 'unconditional subscription cleanup'
if ([regex]::Matches($registrySource, 'ClanApi\.NotifyServerChatAccepted\(').Count -ne 1) {
    throw 'Accepted chat must have exactly one registry event emission site.'
}
Assert-SourceOrder (Get-SourceBlock $registrySource 'private static ClanState CommitClanProfile(') @(
    'RegistryData candidate = ParseSave(bytes);',
    'PreserveRuntimeMemberState(candidate);',
    'WriteAtomically(saveFile, bytes);',
    'SwapState(candidate);',
    'ClanApi.NotifyRegistryChanged();') 'profile commit replaces indexes before subscriber notifications'
[xml] $clanProject = Get-Content -LiteralPath (Join-Path $clanRoot 'Clan.csproj') -Raw
foreach ($compileItem in $clanProject.Project.ItemGroup.Compile) {
    if (!$compileItem.Include -or $compileItem.Include -in @('ClanApi.cs', 'ClanRegistry.cs', 'ClanRegistry.Persistence.cs')) { continue }
    $activeSource = Get-Content -LiteralPath (Join-Path $clanRoot $compileItem.Include) -Raw
    if ($activeSource.Contains('NotifyServerChatAccepted(')) {
        throw "Unexpected event emission outside server acceptance: $($compileItem.Include)"
    }
}

$testNamespace = 'ClanChatSmoke_' + [Guid]::NewGuid().ToString('N')
$harnessSource = @"
#nullable enable
#pragma warning disable CS8600 // Modern BCL TryGetValue annotations differ from the game's .NET Framework references.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
namespace $testNamespace
{
    $validationField
    $validationError
    internal static class ClanDataRules
    {
        $maxChatLength
        public const int ClanIdLength = 32;
        private static readonly object ValidationErrorDataKey = new();
        $requireText
        $requireClanId
        $validationFailure
        public static void WriteClanId(ZPackage package, string value) => package.Write(RequireClanId(value));
        public static void WritePlayerName(ZPackage package, string value, string fieldName) => package.Write(value);
        public static void WriteText(ZPackage package, string value, int maximumLength, string fieldName, bool allowEmpty) =>
            package.Write(RequireText(value, maximumLength, fieldName, allowEmpty));
    }
    internal readonly struct ClanPlayerRef
    {
        public readonly string Id, PlatformId, Name;
        public readonly long CharacterPlayerId;
        public bool IsValid => !string.IsNullOrEmpty(Id) && CharacterPlayerId != 0L;
        public ClanPlayerRef(string platformId, long characterPlayerId, string name)
        { PlatformId = platformId; CharacterPlayerId = characterPlayerId; Name = name; Id = platformId + "/" + characterPlayerId; }
    }
    $clanRole
    $clanMember
    internal sealed class ClanInvite { }
    internal sealed class ClanState
    {
        public string ClanId = "11111111111111111111111111111111", Name = "Accepted clan";
        public readonly Dictionary<string, ClanMember> Members = new();
    }
    internal static class Time { public static float realtimeSinceStartup; }
    internal sealed class ZNet
    {
        public static ZNet? instance;
        public bool Server;
        public bool IsServer() => Server;
        public ZRpc? GetServerRPC() => null;
    }
    internal sealed class ZRpc { public void Invoke(string name, ZPackage package) { } }
    internal sealed class ZNetPeer { }
    internal enum ClanResponseType { Chat = 1 }
    internal sealed class ZPackage
    {
        public ClanRequest? Request;
        public readonly List<object> Values = new();
        public void Write(int value) => Values.Add(value);
        public void Write(string value) => Values.Add(value);
        public void SetPos(int value) { }
    }
    internal sealed class ClanRequest
    {
        public string ClanId = "", Message = "";
        public void Write(ZPackage package) => package.Request = this;
        public static ClanRequest Read(ZPackage package) => package.Request!;
    }
    internal static class ClanLocalization
    {
        public static string EncodeStatus(string key) => key;
        public static string Text(string key) => key;
    }
    internal sealed class SmokeLogger
    {
        public bool ThrowOnWarning;
        public readonly List<string> Warnings = new();
        public void LogWarning(string warning)
        {
            Warnings.Add(warning);
            if (ThrowOnWarning) throw new InvalidOperationException("broken log sink");
        }
    }
    internal static class ClanPlugin { public static readonly SmokeLogger ClanLogger = new(); }
    public static class ClanApi
    {
        $eventDeclaration
        $notify
        $nonfatal
        internal static void ResetSmokeSubscribers() => ServerChatAccepted = null;
    }
    internal static class ClanRegistry
    {
        $chatInterval
        private static Dictionary<string, ClanState> ClansById = new(StringComparer.Ordinal);
        private static Dictionary<string, ClanState> ClansByName = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, ClanState> PrimaryClanByPlayerId = new(StringComparer.Ordinal);
        private static Dictionary<string, ClanState> GuestClanByPlayerId = new(StringComparer.Ordinal);
        private static Dictionary<string, ClanInvite> PendingInvitesByTarget = new(StringComparer.Ordinal);
        internal static ClanPlayerRef Actor;
        internal static int LocalDispatchCount;
        $findEffectiveClan
        $findActiveClan
        $findPrimaryClan
        $findGuestClan
        $isEffectiveMember
        $swapState
        $registryData
        // Build a committed candidate without simulating YAML/filesystem I/O; use the real index swap.
        internal static void ReplaceState(params ClanState[] clans)
        {
            RegistryData candidate = new();
            foreach (ClanState clan in clans)
            {
                candidate.ClansById.Add(clan.ClanId, clan);
                candidate.ClansByName.Add(clan.Name, clan);
                foreach (ClanMember member in clan.Members.Values)
                {
                    var index = member.Role == ClanRole.Guest ? candidate.GuestClanByPlayerId : candidate.PrimaryClanByPlayerId;
                    index.Add(member.Player.Id, clan);
                }
            }
            SwapState(candidate);
        }
        $sendChat
        internal static string Submit(ClanPlayerRef actor, string clanId, string message) =>
            SendClanChat(actor, clanId, message);
        // Stub the large registry dispatcher; its real identity/order boundary is checked above.
        internal static void HandleRequest(object? peer, ClanRequest request)
        {
            if (peer != null) throw new Exception("Expected local-host dispatch");
            LocalDispatchCount++;
            SendClanChat(Actor, request.ClanId, request.Message);
        }
    }
    internal static class ClanRpc
    {
        private const string RequestRpc = "clan-request";
        internal static int BroadcastCount;
        internal static Action? BeforeBroadcast;
        internal static bool ThrowOnBroadcast;
        internal static readonly List<string> Recipients = new();
        internal static readonly List<ZPackage> Deliveries = new();
        $broadcastChat
        private static Dictionary<string, ZNetPeer> BuildPeerLookup()
        {
            BeforeBroadcast?.Invoke();
            BroadcastCount++;
            if (ThrowOnBroadcast) throw new InvalidOperationException("delivery failed");
            return new Dictionary<string, ZNetPeer>();
        }
        private static ZNetPeer? FindPeer(ClanPlayerRef player, IReadOnlyDictionary<string, ZNetPeer> peers) => null;
        // Observe recipients after the production membership filter, without requiring a game socket.
        private static void SendResponse(ZNetPeer? peer, ZPackage package, ClanPlayerRef target)
        { Recipients.Add(target.Id); Deliveries.Add(package); }
        private static void NotifyStatus(string message) { }
        $sendRequest
    }
    public static class Harness
    {
        private static int _checks;
        private static void Check(bool condition, string label)
        { if (!condition) throw new Exception(label); _checks++; }
        private static ClanState Reset()
        {
            ClanApi.ResetSmokeSubscribers();
            ZNet.instance = new ZNet { Server = true };
            Time.realtimeSinceStartup = 10f;
            ClanPlugin.ClanLogger.ThrowOnWarning = false;
            ClanPlugin.ClanLogger.Warnings.Clear();
            ClanRpc.BroadcastCount = 0;
            ClanRpc.BeforeBroadcast = null;
            ClanRpc.ThrowOnBroadcast = false;
            ClanRpc.Recipients.Clear();
            ClanRpc.Deliveries.Clear();
            ClanRegistry.LocalDispatchCount = 0;
            ClanRegistry.Actor = new ClanPlayerRef("Steam_76561198000000000", 123L, "Accepted player");
            ClanState clan = new();
            clan.Members.Add(ClanRegistry.Actor.Id, new ClanMember { Player = ClanRegistry.Actor, Role = ClanRole.Leader });
            ClanRegistry.ReplaceState(clan);
            return clan;
        }
        private static ClanState CopyClan(ClanState source)
        {
            ClanState copy = new() { ClanId = source.ClanId, Name = source.Name };
            foreach (ClanMember member in source.Members.Values)
            {
                copy.Members.Add(member.Player.Id, new ClanMember
                {
                    Player = member.Player,
                    Role = member.Role,
                    LastClanChatTime = member.LastClanChatTime,
                    LastClanPingTime = member.LastClanPingTime
                });
            }
            return copy;
        }
        public static string Run()
        {
            _checks = 0;
            ClanState clan = Reset();
            int calls = 0;
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) => calls++;
            ZNet.instance = null;
            ClanApi.NotifyServerChatAccepted(ClanRegistry.Actor, clan.ClanId, clan.Name, "private");
            ZNet.instance = new ZNet { Server = false };
            ClanApi.NotifyServerChatAccepted(ClanRegistry.Actor, clan.ClanId, clan.Name, "private");
            Check(calls == 0 && ClanPlugin.ClanLogger.Warnings.Count == 0, "Null/client network must not publish or log content");

            clan = Reset();
            calls = 0;
            bool payloadValid = true, rateOrderValid = true;
            const string acceptedMessage = "private-clan-message";
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) => throw new InvalidOperationException(m);
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) =>
            {
                calls++;
                payloadValid &= p == ClanRegistry.Actor.PlatformId && id == 123L && n == ClanRegistry.Actor.Name &&
                    c == clan.ClanId && cn == clan.Name && m == acceptedMessage;
                rateOrderValid &= clan.Members[ClanRegistry.Actor.Id].LastClanChatTime == Time.realtimeSinceStartup;
            };
            ClanRpc.BeforeBroadcast = () => Check(calls == ClanRpc.BroadcastCount + 1, "Exactly one accepted event before each delivery");
            ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, "  " + acceptedMessage + "  ");
            Check(calls == 1 && ClanRpc.BroadcastCount == 1, "Dedicated acceptance and subscriber isolation");
            Check(payloadValid && rateOrderValid, "Six authoritative payload fields and acceptance-before-event ordering");
            Check(ClanPlugin.ClanLogger.Warnings.Count == 1 && !ClanPlugin.ClanLogger.Warnings[0].Contains(acceptedMessage),
                  "Subscriber diagnostic must exclude message/exception contents");
            Time.realtimeSinceStartup += 0.1f;
            ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, acceptedMessage);
            Check(calls == 1 && ClanRpc.BroadcastCount == 1, "Rate-limited request must not publish or deliver");
            Time.realtimeSinceStartup += 0.25f;
            ClanPlugin.ClanLogger.ThrowOnWarning = true;
            ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, acceptedMessage);
            Check(calls == 2 && ClanRpc.BroadcastCount == 2 && payloadValid && rateOrderValid,
                  "Broken logger must not block later subscribers/delivery");

            foreach (string invalid in new[] { "", " \t ", new string('x', 401), "bad\ntext", "<b>text</b>" })
            {
                clan = Reset();
                calls = 0;
                ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) => calls++;
                bool rejected = false;
                try { ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, invalid); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && calls == 0 && ClanRpc.BroadcastCount == 0 &&
                      float.IsNegativeInfinity(clan.Members[ClanRegistry.Actor.Id].LastClanChatTime),
                      "Invalid text must not accept, notify, consume rate slot, or deliver");
            }

            clan = Reset();
            calls = 0;
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) => calls++;
            string wrongClan = ClanRegistry.Submit(ClanRegistry.Actor, "22222222222222222222222222222222", "private");
            clan.Members.Clear();
            string nonmember = ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, "private");
            Check(wrongClan.Length != 0 && nonmember.Length != 0 && calls == 0 && ClanRpc.BroadcastCount == 0,
                  "Invalid clan/membership must not publish or deliver");

            clan = Reset();
            calls = 0;
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) => calls++;
            bool sent = ClanRpc.Send(new ClanRequest { ClanId = clan.ClanId, Message = "local host" });
            Check(sent && ClanRegistry.LocalDispatchCount == 1 && calls == 1 && ClanRpc.BroadcastCount == 1,
                  "Real local-host Send fallback must reach accepted chat once");

            clan = Reset();
            calls = 0;
            ClanPlayerRef other = new("Steam_76561198000000001", 456L, "Other member");
            clan.Members.Add(other.Id, new ClanMember { Player = other });
            ClanRegistry.ReplaceState(clan);
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) =>
            {
                calls++;
                ClanState committed = CopyClan(clan);
                committed.Name = "Renamed clan";
                ClanRegistry.ReplaceState(committed);
            };
            sent = ClanRpc.Send(new ClanRequest { ClanId = clan.ClanId, Message = "rename during acceptance" });
            Check(sent && calls == 1 && ClanRpc.BroadcastCount == 1 && ClanRpc.Recipients.Count == 2 &&
                  ClanRpc.Recipients.Contains(ClanRegistry.Actor.Id) && ClanRpc.Recipients.Contains(other.Id),
                  "Reentrant registry replacement must deliver once to every current effective member");
            Check(ClanRpc.Deliveries.All(package => (string)package.Values[1] == clan.ClanId &&
                  (string)package.Values[3] == "rename during acceptance"),
                  "Reentrant rename must preserve accepted clan ID and message payload");

            clan = Reset();
            calls = 0;
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) =>
            {
                calls++;
                ClanRegistry.ReplaceState();
            };
            ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, "removed during acceptance");
            Check(calls == 1 && ClanRpc.BroadcastCount == 0 && ClanRpc.Recipients.Count == 0,
                  "Removed accepted clan must not broadcast or retry the accepted event");

            clan = Reset();
            calls = 0;
            clan.Members.Add(other.Id, new ClanMember { Player = other });
            ClanRegistry.ReplaceState(clan);
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) =>
            {
                calls++;
                ClanState committed = CopyClan(clan);
                committed.Members.Remove(ClanRegistry.Actor.Id);
                committed.Members[other.Id].Role = ClanRole.Leader;
                ClanState actorDestination = new()
                {
                    ClanId = "22222222222222222222222222222222", Name = "Actor destination"
                };
                actorDestination.Members.Add(ClanRegistry.Actor.Id,
                    new ClanMember { Player = ClanRegistry.Actor, Role = ClanRole.Leader });
                ClanRegistry.ReplaceState(committed, actorDestination);
            };
            ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, "actor left during acceptance");
            Check(calls == 1 && ClanRpc.BroadcastCount == 1 && ClanRpc.Recipients.SequenceEqual(new[] { other.Id }),
                  "Accepted chat must reach remaining members of its original clan without following the actor");

            clan = Reset();
            calls = 0;
            clan.Members.Add(other.Id, new ClanMember { Player = other });
            ClanRegistry.ReplaceState(clan);
            ClanPlayerRef newcomer = new("Steam_76561198000000002", 789L, "New member");
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) =>
            {
                calls++;
                ClanState committed = CopyClan(clan);
                committed.Members.Add(newcomer.Id, new ClanMember { Player = newcomer });
                ClanState guestDestination = new()
                {
                    ClanId = "33333333333333333333333333333333", Name = "Guest destination"
                };
                ClanPlayerRef guestLeader = new("Steam_76561198000000003", 987L, "Guest clan leader");
                guestDestination.Members.Add(guestLeader.Id,
                    new ClanMember { Player = guestLeader, Role = ClanRole.Leader });
                guestDestination.Members.Add(other.Id, new ClanMember { Player = other, Role = ClanRole.Guest });
                ClanRegistry.ReplaceState(committed, guestDestination);
            };
            ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, "membership changed during acceptance");
            Check(calls == 1 && ClanRpc.BroadcastCount == 1 && ClanRpc.Recipients.Count == 2 &&
                  ClanRpc.Recipients.Contains(ClanRegistry.Actor.Id) && ClanRpc.Recipients.Contains(newcomer.Id) &&
                  !ClanRpc.Recipients.Contains(other.Id),
                  "Delivery must use the current roster and exclude members whose Guest clan takes precedence");

            clan = Reset();
            calls = 0;
            ClanApi.ServerChatAccepted += (p, id, n, c, cn, m) => calls++;
            ClanRpc.ThrowOnBroadcast = true;
            bool deliveryFailed = false;
            try { ClanRegistry.Submit(ClanRegistry.Actor, clan.ClanId, "accepted before delivery failure"); }
            catch (InvalidOperationException) { deliveryFailed = true; }
            Check(deliveryFailed && calls == 1, "Accepted event must be independent of later delivery failure");
            return "PASS: " + _checks + " source-linked runtime checks; identity, dispatch, emission-site, and disposal source contracts passed.";
        }
    }
}
"@

$compiledTypes = Add-Type -TypeDefinition $harnessSource -Language CSharp -PassThru
$harness = $compiledTypes | Where-Object { $_.FullName -eq "$testNamespace.Harness" }
if (!$harness) { throw 'Smoke harness did not compile.' }
$harness.GetMethod('Run').Invoke($null, @())
