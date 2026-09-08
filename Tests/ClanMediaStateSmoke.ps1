#requires -Version 7.0
<#
Production-linked media state smoke tests. The manifest parser, Tick, catalog
handoff, build orchestration, cancellation, runtime replacement, and cache writer
are compiled unchanged from the current sources. Unity objects/rendering, server
watchers, and transport/file discovery are controlled substitutes. These tests do
not validate Harmony installation, PNG/GIF decoding, network transfer, or graphics.
Run: pwsh -NoProfile -File Tests/ClanMediaStateSmoke.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$clanRoot = Split-Path -Parent $PSScriptRoot
$emojiSource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanEmoji.cs') -Raw
$syncSource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanEmojiSync.cs') -Raw

function Get-SourceBlock([string] $Source, [string] $Declaration) {
    $start = $Source.IndexOf($Declaration, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production declaration: $Declaration" }
    $open = $Source.IndexOf('{', $start)
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

$emojiDeclarations = @(
    'private static void ResetMediaSessionState(', 'public static void Tick()',
    'private static void QueueManifest(', 'private static void StartBuild(',
    'private static void StartReadyEmojiBuild()', 'private static void CancelBuild()',
    'private static void ProcessBuildStep()', 'private static void AddBuildSource(',
    'private static void RecordBuildFailure(', 'private static string SourceCacheKey(',
    'private static void ReplaceRuntime(', 'private static string SerializeManifest(',
    'private static List<ManifestRecord> ParseManifest(', 'private static int GetMaximumSourceBytes(',
    'private static string ComputeSha256(', 'private static bool IsSha256(',
    'private static bool IsSafeName(', 'private static HashSet<string> BuildReservedWindowsNames()',
    'private enum MediaRole', 'private enum EmojiFileKind', 'private sealed class ManifestRecord',
    'private sealed class PendingManifestState', 'private sealed class BuildState',
    'private sealed class SourceEmoji'
)
$syncDeclarations = @(
    'private static void ResetEmojiSyncSession(', 'private static void BeginEmojiClientCatalog(',
    'private static void CancelEmojiClientCatalog()', 'private static void TickEmojiSync()',
    'private static void FailEmojiClientCatalog(', 'private static void WriteEmojiCacheAtomically(',
    'private static string EmojiCachePath(', 'private static void TryDeleteFile(',
    'private enum SyncedMediaFileSource', 'private sealed class ClientEmojiCatalog'
)
$production = @(
    foreach ($declaration in $emojiDeclarations) { Get-SourceBlock $emojiSource $declaration }
    foreach ($declaration in $syncDeclarations) { Get-SourceBlock $syncSource $declaration }
) -join "`n"
$constants = [regex]::Matches($emojiSource, 'private const (?:int|long|string) \w+ = [^;]+;').Value -join "`n"
$stateStart = $emojiSource.IndexOf('private static ZNet? _session;', [StringComparison]::Ordinal)
$stateEnd = $emojiSource.IndexOf('private static TMP_Text? _attachedOutput;', [StringComparison]::Ordinal)
if ($stateStart -lt 0 -or $stateEnd -le $stateStart) { throw 'Media state declarations were not found.' }
$state = $emojiSource.Substring($stateStart, $stateEnd - $stateStart)
$safeNameRegex = [regex]::Match($emojiSource, 'private static readonly Regex SafeNameRegex = new\([\s\S]*?;').Value
if (!$safeNameRegex) { throw 'Production filename validation expression was not found.' }
$testNamespace = 'ClanMediaSmoke_' + [Guid]::NewGuid().ToString('N')

$harnessSource = @"
#nullable enable
#pragma warning disable CS0649, CS0414, CS8600
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace $testNamespace
{
    internal sealed class ZNet
    {
        public static ZNet? instance;
        public bool IsServer() => false;
    }
    internal static class GUIManager { public static bool IsHeadless() => false; }
    internal static class Time { public static float realtimeSinceStartup; }
    internal static class ClanPlugin { public static readonly Logger ClanLogger = new(); }
    internal sealed class Logger
    {
        public readonly List<string> Warnings = new();
        public void LogWarning(string value) => Warnings.Add(value);
        public void LogInfo(string value) { }
    }
    public static class Harness
    {
        $constants
        $state
        $safeNameRegex
        private static readonly HashSet<string> ReservedWindowsNames = BuildReservedWindowsNames();
        private static bool _initialized;
        private static bool _serverManifestPublishRetryPending;
        private static ClientEmojiCatalog? _clientEmojiCatalog;
        private static object? _serverEmojiCatalog;
        private static readonly Dictionary<object, object> EmojiPeerBudgets = new();
        private static readonly Budget EmojiGlobalTransferBudget = new();
        private static long _emojiReloadRequestedAtTicks;
        private static int _emojiReloadFailures, _emojiWatcherNeedsRestart, _emojiWatcherRestartFailures;
        private static string EmojiCacheDirectory = "";
        private static bool _filesAvailable, _failSource, _failRuntime;
        private static int _checks, _notifications, _destroyedRuntimes, _prunes;
        private static IReadOnlyList<ManifestRecord>? _lastPrunedRecords;
        private sealed class Budget { public void Reset() { } }
        private sealed class RuntimeLibrary { public bool Destroyed; }
        private sealed class RuntimeBuildState
        {
            public bool Destroyed, Released;
            public RuntimeBuildState(string manifest, IReadOnlyList<SourceEmoji> sources) { }
            public void Destroy() => Destroyed = true;
            public void ReleaseOwnership() => Released = true;
        }
        private sealed class ClientEmojiDownload
        {
            public readonly ManifestRecord Record;
            public ClientEmojiDownload(ManifestRecord record) => Record = record;
        }
        private static void DisposeEmojiFileWatcher() { }
        private static void CreateEmojiFileWatcher() { }
        private static bool EnsureMediaCacheInitialized() => true;
        private static bool ConsumeEmojiServerReloadRequest() => false;
        private static void LoadServerManifest(bool preserveLastGood) { }
        private static void EnsureOutputAttachment() { }
        private static void DetachOutput() { }
        private static void NotifyChanged() => _notifications++;
        private static void DestroyRuntime(RuntimeLibrary? runtime)
        {
            if (runtime != null) { runtime.Destroyed = true; _destroyedRuntimes++; }
        }
        private static void TickEmojiDownload(ClientEmojiCatalog catalog, ClientEmojiDownload download) { }
        private static bool TryReadSyncedEmojiFile(ManifestRecord record, out byte[] data, out SyncedMediaFileSource source)
        {
            data = new byte[] { 1 };
            source = SyncedMediaFileSource.Cache;
            return _filesAvailable;
        }
        private static byte[] ReadSyncedEmojiFile(ManifestRecord record) => new byte[] { 1 };
        private static SourceEmoji LoadPngSource(ManifestRecord record, byte[] data)
        {
            if (_failSource) throw new InvalidDataException("Injected decode failure");
            return new SourceEmoji(record, 1, 1, new[] { new byte[] { 1, 2, 3, 4 } }, 0);
        }
        private static SourceEmoji LoadGifSourceSerialized(ManifestRecord record, byte[] data, CancellationToken token)
            => throw new InvalidOperationException("GIF decoding is outside this smoke test");
        private static RuntimeLibrary? ProcessRuntimeBuildStep(RuntimeBuildState build)
        {
            if (_failRuntime) throw new InvalidOperationException("Injected Unity build failure");
            return new RuntimeLibrary();
        }
        private static void PruneEmojiCache(IReadOnlyList<ManifestRecord> records)
        {
            _prunes++;
            _lastPrunedRecords = records;
        }
        private static bool TryReadEmojiCacheFile(ManifestRecord record, out byte[] data)
        {
            data = Array.Empty<byte>();
            return false;
        }
        private static void PublishEmojiCacheFile(ManifestRecord record, string temporary, string destination)
            => File.Move(temporary, destination);

        $production

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            _checks++;
        }
        private static string Manifest(string name) => SerializeManifest(new[]
        {
            new ManifestRecord(MediaRole.Emoji, EmojiFileKind.Png, name, 1, new string('a', 64))
        });
        private static RuntimeLibrary Reset()
        {
            ZNet.instance = new ZNet();
            _session = ZNet.instance;
            _initialized = true;
            _build = null;
            _clientEmojiCatalog = null;
            _pendingManifest = null;
            _activeManifest = Manifest("old");
            _activeRecords = ParseManifest(_activeManifest);
            _preparedSources = new(StringComparer.Ordinal);
            _runtime = new RuntimeLibrary();
            _filesAvailable = true;
            _failSource = _failRuntime = false;
            _notifications = _destroyedRuntimes = _prunes = 0;
            _lastPrunedRecords = null;
            ClanPlugin.ClanLogger.Warnings.Clear();
            return _runtime;
        }
        private static BuildState Ready(string manifest)
        {
            StartBuild(manifest);
            _clientEmojiCatalog!.Ready = true;
            StartReadyEmojiBuild();
            return _build ?? throw new InvalidOperationException("Ready catalog was not handed off");
        }
        private static void FinishBuild()
        {
            for (int step = 0; step < 10 && _build != null; step++) ProcessBuildStep();
            Check(_build == null, "A build must finish or fail within the controlled steps");
        }
        public static string Run(string cacheDirectory)
        {
            EmojiCacheDirectory = cacheDirectory;
            string a = Manifest("a"), b = Manifest("b");
            RuntimeLibrary old = Reset();
            IReadOnlyList<ManifestRecord> oldRecords = _activeRecords;
            _filesAvailable = false;
            StartBuild(a);
            ClientEmojiCatalog stale = _clientEmojiCatalog!;
            TickEmojiSync();
            Check(stale.Download != null, "A must be downloading before B replaces it");
            StartBuild(b);
            Check(_clientEmojiCatalog != stale && _clientEmojiCatalog!.Manifest == b, "B replaces the whole A catalog");
            stale.Ready = true;
            StartReadyEmojiBuild();
            Check(_build == null, "Late readiness on A cannot start a build for pending B");
            ClientEmojiCatalog ready = _clientEmojiCatalog!;
            ready.Ready = true;
            StartReadyEmojiBuild();
            BuildState handed = _build!;
            Check(_clientEmojiCatalog == null && handed.Manifest == b && ReferenceEquals(handed.Records, ready.Records)
                && ReferenceEquals(handed.Session, ready.Session), "Handoff consumes exactly the ready catalog and its session");
            Check(_runtime == old && ReferenceEquals(_activeRecords, oldRecords), "Handoff must retain active runtime/cache records");
            StartReadyEmojiBuild();
            Check(_build == handed, "The same ready catalog cannot overwrite the active build on the next Tick");
            FinishBuild();
            Check(_activeManifest == b && _runtime != old && old.Destroyed && _notifications == 1,
                "A completed B build commits once and then destroys the old runtime");
            Check(_prunes == 1 && ReferenceEquals(_lastPrunedRecords, ready.Records)
                && ReferenceEquals(_activeRecords, ready.Records), "Committed records remain available to cache maintenance");

            old = Reset();
            BuildState canceled = Ready(a);
            RuntimeBuildState canceledResources = new(a, canceled.Sources);
            canceled.RuntimeBuild = canceledResources;
            StartBuild(b);
            Check(canceled.Cancellation.IsCancellationRequested && canceledResources.Destroyed,
                "Replacing a building catalog cancels its worker and tears down its owned sheets");
            ProcessBuildStep();
            Check(_build == null && _runtime == old && _clientEmojiCatalog!.Manifest == b,
                "Canceled A cannot commit while B is downloading");

            Reset();
            StartBuild(a);
            _clientEmojiCatalog!.Ready = true;
            ZNet.instance = new ZNet();
            StartReadyEmojiBuild();
            Check(_build == null, "A ready catalog from another session must not be handed off");
            Tick();
            Check(_clientEmojiCatalog == null && _runtime == null, "The session transition discards the stale ready catalog and its old runtime");
            old = Reset();
            canceled = Ready(a);
            ZNet.instance = new ZNet();
            ProcessBuildStep();
            Check(_build == null && canceled.Cancellation.IsCancellationRequested && _runtime == old,
                "A build that observes a new session is canceled before any commit");

            old = Reset();
            canceled = Ready(b);
            StartBuild(_activeManifest);
            Check(canceled.Cancellation.IsCancellationRequested && _build == null && _clientEmojiCatalog == null
                && _runtime == old && _notifications == 0, "The already-active manifest cancels pending work without rebuilding");
            StartBuild(b);
            StartBuild("invalid");
            Check(_clientEmojiCatalog == null && _runtime == old && ClanPlugin.ClanLogger.Warnings.Count == 1,
                "An invalid replacement cancels pending work but retains the last valid runtime");
            _preparedSources["retained"] = LoadPngSource(_activeRecords[0], new byte[] { 1 });
            StartBuild("");
            Check(_runtime == null && old.Destroyed && _build == null && _clientEmojiCatalog == null
                && _activeManifest == "" && _activeRecords.Count == 0 && _preparedSources.Count == 0,
                "An empty manifest clears pending and active media, including cached decoded sources");

            old = Reset();
            oldRecords = _activeRecords;
            canceled = Ready(b);
            _failSource = true;
            FinishBuild();
            Check(_runtime == old && !old.Destroyed && ReferenceEquals(_activeRecords, oldRecords)
                && canceled.Cancellation.IsCancellationRequested && _prunes == 0,
                "A source failure preserves the previous runtime and active cache protection");
            old = Reset();
            oldRecords = _activeRecords;
            canceled = Ready(b);
            ProcessBuildStep();
            ProcessBuildStep();
            RuntimeBuildState failedResources = canceled.RuntimeBuild!;
            _failRuntime = true;
            FinishBuild();
            Check(_runtime == old && !old.Destroyed && ReferenceEquals(_activeRecords, oldRecords)
                && failedResources.Destroyed && !failedResources.Released && _notifications == 0,
                "A Unity build failure destroys only the uncommitted build and retains previous media");
            StartReadyEmojiBuild();
            Check(_build == null && _clientEmojiCatalog == null, "A failed handed-off build is not silently recreated");

            old = Reset();
            QueueManifest(a);
            QueueManifest(b);
            Tick();
            Check(_pendingManifest == null && _clientEmojiCatalog!.Manifest == b && _runtime == old,
                "Tick consumes only the latest queued manifest for the same session");
            Tick();
            Check(_clientEmojiCatalog == null && _build!.Manifest == b && _runtime == old,
                "Tick hands the finished download to the build without clearing active media");
            old = Reset();
            QueueManifest(a);
            ZNet.instance = new ZNet();
            Tick();
            Check(_pendingManifest == null && _clientEmojiCatalog == null && _build == null && _runtime == null
                && old.Destroyed && _activeRecords.Count == 0, "A session transition discards queued work and resets old resources");

            byte[] bytes = { 1, 2, 3 };
            ManifestRecord bad = new(MediaRole.Emoji, EmojiFileKind.Png, "bad", bytes.Length, new string('b', 64));
            bool rejected = false;
            try { WriteEmojiCacheAtomically(bad, bytes); }
            catch (InvalidDataException error) { rejected = error.Message == "Downloaded SHA-256 does not match the manifest."; }
            Check(rejected && !Directory.Exists(cacheDirectory), "The writer rejects corrupt bytes before I/O with the original download diagnostic");
            rejected = false;
            try { WriteEmojiCacheAtomically(bad, Array.Empty<byte>()); }
            catch (InvalidDataException error) { rejected = error.Message == "Cache data for 'bad.png' does not match its manifest."; }
            Check(rejected && !Directory.Exists(cacheDirectory), "Length validation remains at the cache writer boundary");
            ManifestRecord good = new(MediaRole.Emoji, EmojiFileKind.Png, "good", bytes.Length, ComputeSha256(bytes));
            WriteEmojiCacheAtomically(good, bytes);
            Check(File.ReadAllBytes(EmojiCachePath(good)).SequenceEqual(bytes)
                && Directory.GetFiles(cacheDirectory, "*.part").Length == 0, "Valid bytes are flushed and published without an orphan temporary file");
            return "PASS: " + _checks + " production-linked media state/cache checks; Unity, transport, watchers, and decoder execution remain untested.";
        }
    }
}
"@

$compiledTypes = Add-Type -TypeDefinition $harnessSource -Language CSharp -PassThru
$harness = $compiledTypes | Where-Object { $_.FullName -eq "$testNamespace.Harness" }
if (!$harness) { throw 'Media smoke harness did not compile.' }
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$mediaTestPath = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('ClanMediaSmoke-' + [Guid]::NewGuid().ToString('N'))))
try {
    $harness.GetMethod('Run').Invoke($null, @($mediaTestPath))
}
finally {
    if ([IO.Path]::GetDirectoryName($mediaTestPath) -ne $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw 'Refused to clean a media smoke directory outside the temporary root.'
    }
    if (Test-Path -LiteralPath $mediaTestPath) { Remove-Item -LiteralPath $mediaTestPath -Recurse -Force }
}
