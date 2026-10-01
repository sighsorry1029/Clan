using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

using ServerSync;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace Clan;

internal static partial class ClanEmoji
{
    // Select the byte[] overload once: Unity 6 also exposes Span overloads that
    // are not part of the net48 compilation surface. No per-image reflection.
    private static readonly Func<Texture2D, byte[], bool> LoadPngImage =
        (Func<Texture2D, byte[], bool>)Delegate.CreateDelegate(
            typeof(Func<Texture2D, byte[], bool>),
            typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) })!);
    private const int MaximumEmojiFiles = 100;
    private const int MaximumPngFiles = MaximumEmojiFiles;
    private const int MaximumGifFiles = 50;
    private const int MaximumEmblemFiles = 50;
    private const int MaximumPngSourceBytes = 512 * 1024;
    private const int MaximumGifSourceBytes = 2 * 1024 * 1024;
    private const int MaximumSourceDimension = 512;
    private const int MaximumGifFrames = 180;
    private const int MaximumTotalGifFrames = 3_000;
    private const long MaximumGifDecodedPixels = 8_388_608;
    private const long MaximumGifDurationMilliseconds = 10_000;
    private const int MaximumRenderedDimension = 96;
    private const int MaximumGifPlaybackFps = 50;
    private const int MaximumEmojiOccurrencesPerMessage = 5;
    private const int MaximumAnimatedOccurrencesPerChatPanel = 15;
    private const int EmojiSizePercent = 200;
    private const int AtlasGutter = 2;
    private const int AtlasColumns = 5;
    private const long MediaCacheRetryDelayTicks = TimeSpan.TicksPerSecond;
    private const int MaximumManifestCharacters = 24 * 1024;
    private const string ManifestVersion = "v4";
    private const string SpriteAssetVersion = "1.1.0";
    private const string TokenPrefix = ":clan_";
    private const string SpriteNamePrefix = "clan_emoji_";
    private const string PngSpriteNamePrefix = "clan_emoji_png_";
    private const string GifSpriteNamePrefix = "clan_emoji_gif_";
    private const string EmblemSpriteNamePrefix = "clan_emblem_";

    private static readonly byte[] PngSignature =
    {
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
    };

    private static readonly Regex SafeNameRegex = new(
        "^[a-z0-9](?:[a-z0-9_-]{0,30}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedWindowsNames = BuildReservedWindowsNames();

    private static readonly StringComparer FilePathComparer =
        Environment.OSVersion.Platform == PlatformID.Win32NT
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly object ServerMediaDirtyLock = new();
    private static readonly SemaphoreSlim GifDecodeGate = new(1, 1);
    private static readonly Dictionary<string, long> DirtyServerMediaPaths =
        new(FilePathComparer);
    private static Dictionary<string, ServerFileStamp> _serverFileStamps =
        new(StringComparer.Ordinal);
    private static long _serverMediaDirtyGeneration;

    private static readonly FieldInfo? SpriteAssetVersionField = typeof(TMP_Asset).GetField(
        "m_Version",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConfigSync MediaConfigSync = new($"{ClanPlugin.ModGUID}.media")
    {
        DisplayName = "Clan Media",
        CurrentVersion = ClanPlugin.ModVersion,
        MinimumRequiredVersion = ClanPlugin.ModVersion,
        ModRequired = false,
        IsLocked = true
    };

    private static readonly CustomSyncedValue<string> SyncedManifest =
        new(MediaConfigSync, "manifest", "");

    private static bool _initialized;
    private static bool _mediaCacheInitialized;
    private static bool _mediaCacheWarningLogged;
    private static long _mediaCacheRetryAtTicks;
    private static bool _serverManifestPublishRetryPending;
    private static ZNet? _session;
    private static RuntimeLibrary? _runtime;
    private static BuildState? _build;
    private static Dictionary<string, SourceEmoji> _preparedSources =
        new(StringComparer.Ordinal);
    private static string _activeManifest = "";
    private static IReadOnlyList<ManifestRecord> _activeRecords = Array.Empty<ManifestRecord>();
    private static PendingManifestState? _pendingManifest;
    private static TMP_Text? _attachedOutput;
    private static TMP_SpriteAsset? _fallbackOwner;
    private static bool _installedAsPrimary;
    private static bool _addedFallback;

    public static event Action? EmojiChanged;
    public static event Action? EmblemsChanged;

    public static bool IsReady => _runtime != null && _runtime.Entries.Count > 0;

    public static int EmojiCount => _runtime?.Entries.Count ?? 0;

    internal static int MessageEmojiLimit => MaximumEmojiOccurrencesPerMessage;

    internal static IReadOnlyList<ClanEmblemPickerItem> GetEmblemPickerItems()
    {
        if (_runtime == null)
        {
            return Array.Empty<ClanEmblemPickerItem>();
        }

        return _runtime.Emblems;
    }

    internal static Sprite? GetEmblemSprite(string? name)
    {
        if (_runtime == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _runtime.EmblemsByName.TryGetValue(name!, out ClanEmblemPickerItem emblem)
            ? emblem.Sprite
            : null;
    }

    internal static bool IsAvailableEmblemKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }
        if (!IsSafeName(name!))
        {
            return false;
        }

        if (_serverEmojiCatalog != null)
        {
            return _serverEmojiCatalog.Records.Any(record =>
                record.Role == MediaRole.Emblem &&
                StringComparer.Ordinal.Equals(record.Name, name));
        }

        return _runtime != null && _runtime.EmblemsByName.ContainsKey(name!);
    }

    private static string EmojiDirectory =>
        Path.Combine(ClanPlugin.MediaDirectory, "emoji");

    private static string EmblemDirectory =>
        Path.Combine(ClanPlugin.MediaDirectory, "emblems");

    internal static void MarkEmojiServerFileDirty(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            lock (ServerMediaDirtyLock)
            {
                DirtyServerMediaPaths[fullPath] = ++_serverMediaDirtyGeneration;
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // The normal metadata scan still detects ordinary file changes.
        }

        MarkEmojiServerFilesDirty();
    }

    private static Dictionary<string, long> SnapshotDirtyServerMediaPaths()
    {
        lock (ServerMediaDirtyLock)
        {
            return new Dictionary<string, long>(DirtyServerMediaPaths, FilePathComparer);
        }
    }

    private static bool IsServerMediaPathDirty(
        string path,
        IReadOnlyDictionary<string, long> dirtyPaths)
    {
        return dirtyPaths.ContainsKey(Path.GetFullPath(path));
    }

    private static void AcknowledgeDirtyServerMediaPaths(
        IReadOnlyDictionary<string, long> dirtyPaths,
        IEnumerable<string> retryablePaths)
    {
        HashSet<string> retryable = retryablePaths
            .Select(Path.GetFullPath)
            .ToHashSet(FilePathComparer);
        lock (ServerMediaDirtyLock)
        {
            foreach (KeyValuePair<string, long> dirtyPath in dirtyPaths)
            {
                if (retryable.Contains(dirtyPath.Key))
                {
                    continue;
                }
                if (DirtyServerMediaPaths.TryGetValue(
                        dirtyPath.Key,
                        out long currentGeneration) &&
                    currentGeneration == dirtyPath.Value)
                {
                    DirtyServerMediaPaths.Remove(dirtyPath.Key);
                }
            }
        }
    }

    public static void Init()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SyncedManifest.ValueChanged += OnSyncedManifestChanged;
    }

    private static bool EnsureMediaCacheInitialized()
    {
        if (_mediaCacheInitialized)
        {
            return true;
        }

        long now = DateTime.UtcNow.Ticks;
        if (now < _mediaCacheRetryAtTicks)
        {
            return false;
        }
        _mediaCacheRetryAtTicks = now + MediaCacheRetryDelayTicks;

        try
        {
            InitializeEmojiCache();
            _mediaCacheInitialized = true;
            _mediaCacheRetryAtTicks = 0L;
            if (_mediaCacheWarningLogged)
            {
                ClanPlugin.ClanLogger.LogInfo(
                    "Clan media cache initialization recovered.");
                _mediaCacheWarningLogged = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (!_mediaCacheWarningLogged)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan media cache is not ready and will be retried: {ex.Message}");
                _mediaCacheWarningLogged = true;
            }
            return false;
        }
    }

    public static void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        SyncedManifest.ValueChanged -= OnSyncedManifestChanged;
        _session = null;
        _pendingManifest = null;
        _mediaCacheInitialized = false;
        _mediaCacheWarningLogged = false;
        _mediaCacheRetryAtTicks = 0L;
        ResetMediaSessionState(null);
        _initialized = false;
    }

    private static void ResetMediaSessionState(ZNet? session)
    {
        CancelBuild();
        _preparedSources.Clear();
        _activeManifest = "";
        _activeRecords = Array.Empty<ManifestRecord>();
        ResetEmojiSyncSession(session);
        ReplaceRuntime(null);
    }

    public static void Tick()
    {
        if (!_initialized)
        {
            return;
        }

        ZNet? currentSession = ZNet.instance;
        if (!ReferenceEquals(currentSession, _session))
        {
            _session = currentSession;
            if (_pendingManifest != null &&
                !ReferenceEquals(_pendingManifest.Session, currentSession))
            {
                _pendingManifest = null;
            }
            ResetMediaSessionState(currentSession);

            if (currentSession?.IsServer() == true)
            {
                LoadServerManifest(preserveLastGood: false);
            }
        }

        // A client needs the shared cache before it can download synchronized
        // media. Servers serve their validated in-memory catalog directly, so a
        // LocalLow cache failure must not block session reset or catalog loading.
        if (currentSession != null &&
            !currentSession.IsServer() &&
            !EnsureMediaCacheInitialized())
        {
            return;
        }

        if (currentSession?.IsServer() == true && ConsumeEmojiServerReloadRequest())
        {
            LoadServerManifest(preserveLastGood: true);
        }

        if (ClanUiFactory.IsHeadless)
        {
            return;
        }

        PendingManifestState? pendingManifest = _pendingManifest;
        if (pendingManifest != null)
        {
            _pendingManifest = null;

            if (ReferenceEquals(pendingManifest.Session, ZNet.instance))
            {
                StartBuild(pendingManifest.Manifest);
            }
        }

        TickEmojiSync();
        StartReadyEmojiBuild();
        ProcessBuildStep();
        EnsureOutputAttachment();
    }

    public static Sprite? GetPickerSprite(int index)
    {
        if (_runtime == null || index < 0 || index >= _runtime.Entries.Count)
        {
            return null;
        }

        return _runtime.Entries[index].PickerSprite;
    }

    internal static bool IsGifEmoji(int index)
    {
        return _runtime != null &&
               index >= 0 &&
               index < _runtime.Entries.Count &&
               _runtime.Entries[index].IsGif;
    }

    public static string TokenFor(int index)
    {
        if (_runtime == null || index < 0 || index >= _runtime.Entries.Count)
        {
            return "";
        }

        return _runtime.Entries[index].Token;
    }

    internal static int CountMessageEmojiTokens(string? text)
    {
        if (_runtime == null || string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return _runtime.TokenRegex.Matches(text!).Count;
    }

    internal static bool IsWithinMessageEmojiLimit(string? text)
    {
        return CountMessageEmojiTokens(text) <= MaximumEmojiOccurrencesPerMessage;
    }

    public static string RenderTokens(string text)
    {
        if (_runtime == null ||
            string.IsNullOrEmpty(text) ||
            text.IndexOf(TokenPrefix, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return text;
        }

        int emojiOccurrences = 0;
        int previousMatchEnd = 0;
        return _runtime.TokenRegex.Replace(text, match =>
        {
            if (ContainsLineBreak(text, previousMatchEnd, match.Index))
            {
                emojiOccurrences = 0;
            }
            previousMatchEnd = match.Index + match.Length;

            if (!_runtime.RenderTags.TryGetValue(match.Value, out RuntimeRenderTag renderTag))
            {
                return match.Value;
            }

            emojiOccurrences++;
            if (emojiOccurrences > MaximumEmojiOccurrencesPerMessage)
            {
                return EscapeTokenForDisplay(match.Value);
            }

            string replacement = renderTag.IsAnimated
                ? renderTag.AnimatedTag
                : renderTag.StaticTag;
            return $"<size={EmojiSizePercent}%>{replacement}</size>";
        });
    }

    private static bool ContainsLineBreak(string text, int startIndex, int endIndex)
    {
        for (int index = startIndex; index < endIndex; index++)
        {
            if (text[index] is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string EscapeTokenForDisplay(string token)
    {
        return token.Length == 0
            ? token
            : token.Insert(1, "\u200B");
    }

    private static int CountAnimatedSpriteOccurrences(string text)
    {
        RuntimeLibrary? runtime = _runtime;
        if (runtime == null || string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        foreach (Match match in runtime.SpriteTagRegex.Matches(text))
        {
            if (runtime.RenderTagsBySpriteName.TryGetValue(
                    match.Groups["name"].Value,
                    out RuntimeRenderTag renderTag) &&
                renderTag.IsAnimated)
            {
                count++;
            }
        }

        return count;
    }

    private static string ApplyAnimatedSpriteBudget(string text, int budget)
    {
        int totalAnimatedOccurrences = CountAnimatedSpriteOccurrences(text);
        int animatedOccurrencesToFreeze = Math.Max(0, totalAnimatedOccurrences - budget);
        RuntimeLibrary? runtime = _runtime;
        if (runtime == null || string.IsNullOrEmpty(text))
        {
            return text;
        }

        int ordinal = 0;
        return runtime.SpriteTagRegex.Replace(text, match =>
        {
            if (!runtime.RenderTagsBySpriteName.TryGetValue(
                    match.Groups["name"].Value,
                    out RuntimeRenderTag renderTag))
            {
                return match.Value;
            }

            if (!renderTag.IsAnimated)
            {
                return renderTag.StaticTag;
            }

            return ordinal++ < animatedOccurrencesToFreeze
                ? renderTag.StaticTag
                : renderTag.AnimatedTag;
        });
    }

    private static void LoadServerManifest(bool preserveLastGood)
    {
        Dictionary<string, long> dirtyPaths;
        List<ServerEmojiBlob> blobs;
        Dictionary<string, ServerFileStamp> fileStamps;
        List<string> fileWarnings;
        List<Exception> retryableFileErrors;
        List<string> retryableFilePaths;
        List<ManifestRecord> records;
        string manifest;
        bool catalogChanged;
        ServerEmojiCatalog? preparedCatalog;
        bool publishRetryPending;

        try
        {
            Directory.CreateDirectory(EmojiDirectory);
            Directory.CreateDirectory(EmblemDirectory);
            dirtyPaths = SnapshotDirtyServerMediaPaths();
            blobs = new List<ServerEmojiBlob>();
            fileStamps = new Dictionary<string, ServerFileStamp>(StringComparer.Ordinal);
            fileWarnings = new List<string>();
            retryableFileErrors = new List<Exception>();
            retryableFilePaths = new List<string>();
            records = DiscoverServerFiles(
                preserveLastGood,
                dirtyPaths,
                blobs,
                fileStamps,
                fileWarnings,
                retryableFileErrors,
                retryableFilePaths);
            manifest = SerializeManifest(records);
            catalogChanged = !IsCurrentEmojiServerManifest(manifest);
            preparedCatalog = catalogChanged
                ? PrepareEmojiServerCatalog(manifest, records, blobs)
                : null;
            publishRetryPending = _serverManifestPublishRetryPending;

            if (!preserveLastGood ||
                publishRetryPending ||
                !StringComparer.Ordinal.Equals(SyncedManifest.Value, manifest))
            {
                try
                {
                    SyncedManifest.Value = manifest;
                    _serverManifestPublishRetryPending = false;
                }
                catch (Exception publishError)
                {
                    _serverManifestPublishRetryPending = true;
                    bool targetManifestWasAssigned = StringComparer.Ordinal.Equals(
                        SyncedManifest.Value,
                        manifest);
                    if (targetManifestWasAssigned && preparedCatalog != null)
                    {
                        // ServerSync assigns BoxedValue before it invokes the
                        // broadcast callback. Keep the server catalog consistent
                        // with that observable value even when the callback fails.
                        CommitEmojiServerCatalog(preparedCatalog);
                        preparedCatalog = null;
                    }

                    ScheduleEmojiServerReloadRetry(
                        publishError,
                        retryAnyError: true);
                    if (targetManifestWasAssigned && !ClanUiFactory.IsHeadless)
                    {
                        QueueManifest(manifest);
                    }

                    ClanPlugin.ClanLogger.LogWarning(
                        targetManifestWasAssigned
                            ? $"Clan media manifest was assigned, but its synchronization failed and will be retried: {publishError.Message}"
                            : $"Clan media manifest synchronization failed; the last valid catalog remains active and publication will be retried: {publishError.Message}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            if (preserveLastGood && HasEmojiServerCatalog)
            {
                ScheduleEmojiServerReloadRetry(ex);
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan media hot reload was rejected; the last valid catalog remains active: {ex.Message}");
                return;
            }

            if (_serverManifestPublishRetryPending ||
                !StringComparer.Ordinal.Equals(SyncedManifest.Value, ""))
            {
                try
                {
                    SyncedManifest.Value = "";
                    _serverManifestPublishRetryPending = false;
                }
                catch (Exception publishError)
                {
                    _serverManifestPublishRetryPending = true;
                    bool emptyManifestWasAssigned = StringComparer.Ordinal.Equals(
                        SyncedManifest.Value,
                        "");
                    ScheduleEmojiServerReloadRetry(
                        publishError,
                        retryAnyError: true);
                    if (emptyManifestWasAssigned)
                    {
                        ClearEmojiServerCatalog();
                        if (!ClanUiFactory.IsHeadless)
                        {
                            QueueManifest("");
                        }
                    }
                    ClanPlugin.ClanLogger.LogWarning(
                        emptyManifestWasAssigned
                            ? $"Clan media was disabled after catalog loading failed, but empty-manifest synchronization also failed and remains pending: {publishError.Message}"
                            : $"Clan media catalog loading failed and the previous manifest could not be replaced; publication remains pending: {publishError.Message}");
                    return;
                }
            }

            ClearEmojiServerCatalog();
            if (!ClanUiFactory.IsHeadless)
            {
                QueueManifest("");
            }
            ClanPlugin.ClanLogger.LogError(
                $"Clan media manifest was disabled: {ex.Message}");
            return;
        }

        if (preparedCatalog != null)
        {
            CommitEmojiServerCatalog(preparedCatalog);
        }
        if ((catalogChanged || publishRetryPending) && !ClanUiFactory.IsHeadless)
        {
            QueueManifest(manifest);
        }

        try
        {
            AcknowledgeDirtyServerMediaPaths(dirtyPaths, retryableFilePaths);
            _serverFileStamps = fileStamps;

            if (retryableFileErrors.Count == 0)
            {
                ResetEmojiServerReloadFailures();
            }
            else
            {
                ScheduleEmojiServerReloadRetry(retryableFileErrors[0]);
            }
            foreach (string warning in fileWarnings)
            {
                ClanPlugin.ClanLogger.LogWarning(warning);
            }
            if (!catalogChanged)
            {
                return;
            }

            if (records.Count == 0)
            {
                if (fileWarnings.Count == 0)
                {
                    ClanPlugin.ClanLogger.LogInfo(
                        $"Clan media folders are empty. Put up to {MaximumEmojiFiles} total PNG/GIF " +
                        $"emoji files (up to {MaximumPngFiles} PNG or {MaximumGifFiles} GIF) " +
                        $"in '{EmojiDirectory}', and up to " +
                        $"{MaximumEmblemFiles} PNG emblem files in '{EmblemDirectory}'.");
                }
                else
                {
                    ClanPlugin.ClanLogger.LogWarning(
                        "No valid Clan media files are available; invalid files were skipped.");
                }
                return;
            }

            int pngCount = records.Count(record =>
                record.Role == MediaRole.Emoji && record.Kind == EmojiFileKind.Png);
            int gifCount = records.Count(record =>
                record.Role == MediaRole.Emoji && record.Kind == EmojiFileKind.Gif);
            int emblemCount = records.Count(record => record.Role == MediaRole.Emblem);
            ClanPlugin.ClanLogger.LogInfo(
                $"Published Clan media catalog for {pngCount} PNG emoji, {gifCount} GIF emoji, " +
                $"and {emblemCount} PNG emblem files. " +
                "Clients download only content missing from verified local media and their SHA-256 cache.");
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan media catalog is active, but post-publish maintenance could not be completed: {ex.Message}");
        }
    }

    private static List<ManifestRecord> DiscoverServerFiles(
        bool preserveLastGood,
        IReadOnlyDictionary<string, long> dirtyPaths,
        ICollection<ServerEmojiBlob> blobs,
        IDictionary<string, ServerFileStamp> fileStamps,
        ICollection<string> warnings,
        ICollection<Exception> retryableFileErrors,
        ICollection<string> retryableFilePaths)
    {
        List<ManifestRecord> records = new();
        Dictionary<string, ServerFileCandidate> previousByKey =
            GetPreviousServerFiles(preserveLastGood);
        DiscoverServerFiles(
            MediaRole.Emoji,
            EmojiDirectory,
            MaximumPngFiles,
            MaximumGifFiles,
            MaximumEmojiFiles,
            dirtyPaths,
            previousByKey,
            blobs,
            fileStamps,
            records,
            warnings,
            retryableFileErrors,
            retryableFilePaths);
        DiscoverServerFiles(
            MediaRole.Emblem,
            EmblemDirectory,
            MaximumEmblemFiles,
            0,
            MaximumEmblemFiles,
            dirtyPaths,
            previousByKey,
            blobs,
            fileStamps,
            records,
            warnings,
            retryableFileErrors,
            retryableFilePaths);
        return records;
    }

    private static Dictionary<string, ServerFileCandidate> GetPreviousServerFiles(
        bool preserveLastGood)
    {
        Dictionary<string, ServerFileCandidate> previous = new(StringComparer.Ordinal);
        if (!preserveLastGood || _serverEmojiCatalog == null)
        {
            return previous;
        }

        foreach (ManifestRecord record in _serverEmojiCatalog.Records)
        {
            if (_serverEmojiCatalog.Files.TryGetValue(
                    record.Hash,
                    out ServerEmojiBlob blob) &&
                blob.Data.Length == record.Length)
            {
                string key = MediaKey(record.Role, record.Name);
                _serverFileStamps.TryGetValue(key, out ServerFileStamp? stamp);
                previous[key] = new ServerFileCandidate(
                    new ServerEmojiBlob(record, blob.Data, blob.GifFrameCount),
                    stamp);
            }
        }
        return previous;
    }

    private static void DiscoverServerFiles(
        MediaRole role,
        string sourceDirectory,
        int maximumPngFiles,
        int maximumGifFiles,
        int maximumTotalFiles,
        IReadOnlyDictionary<string, long> dirtyPaths,
        IReadOnlyDictionary<string, ServerFileCandidate> previousByKey,
        ICollection<ServerEmojiBlob> blobs,
        IDictionary<string, ServerFileStamp> fileStamps,
        ICollection<ManifestRecord> records,
        ICollection<string> warnings,
        ICollection<Exception> retryableFileErrors,
        ICollection<string> retryableFilePaths)
    {
        DirectoryInfo directory = new(sourceDirectory);
        List<FileInfo> supportedFiles = directory
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file =>
                StringComparer.Ordinal.Equals(file.Extension, ".png") ||
                StringComparer.Ordinal.Equals(file.Extension, ".gif"))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
        string label = role == MediaRole.Emoji ? "emoji" : "emblem";
        List<(FileInfo File, string Name, EmojiFileKind Kind)> candidates = new();
        foreach (FileInfo file in supportedFiles)
        {
            string name = Path.GetFileNameWithoutExtension(file.Name);
            if (!IsSafeName(name))
            {
                warnings.Add(
                    $"Skipped {label} file '{file.Name}': filename must use lowercase ASCII " +
                    "letters, digits, '_' or '-' only.");
                continue;
            }

            EmojiFileKind kind = StringComparer.Ordinal.Equals(file.Extension, ".png")
                ? EmojiFileKind.Png
                : EmojiFileKind.Gif;
            candidates.Add((file, name, kind));
        }

        List<ServerFileCandidate> discovered = new();
        foreach (IGrouping<string, (FileInfo File, string Name, EmojiFileKind Kind)> group in
                 candidates
                     .GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            (FileInfo File, string Name, EmojiFileKind Kind)[] matching = group.ToArray();
            if (matching.Length != 1)
            {
                RetainPreviousServerFile(
                    role,
                    group.Key,
                    previousByKey,
                    discovered,
                    warnings,
                    $"{label} basename '{group.Key}' is used by more than one file");
                continue;
            }

            (FileInfo file, string name, EmojiFileKind kind) = matching[0];
            try
            {
                if (kind == EmojiFileKind.Gif && maximumGifFiles == 0)
                {
                    throw new InvalidDataException("Clan emblems must be PNG files.");
                }

                ServerFileStamp initialStamp = ServerFileStamp.Capture(file);
                int maximumBytes = GetMaximumSourceBytes(kind);
                if (initialStamp.Length <= 0 || initialStamp.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"file size must be between 1 and {maximumBytes} bytes");
                }

                string key = MediaKey(role, name);
                if (!IsServerMediaPathDirty(file.FullName, dirtyPaths) &&
                    previousByKey.TryGetValue(key, out ServerFileCandidate previous) &&
                    previous.Blob.Record.Kind == kind &&
                    previous.Stamp != null &&
                    previous.Stamp.Matches(initialStamp))
                {
                    discovered.Add(previous);
                    continue;
                }

                byte[] data = ReadStableFile(file.FullName, initialStamp.Length);
                int gifFrameCount = 0;
                if (kind == EmojiFileKind.Png)
                {
                    ValidatePngHeader(data, out _, out _);
                }
                else
                {
                    ClanGifDecoder.Animation animation = ClanGifDecoder.Decode(
                        data,
                        MaximumGifSourceBytes,
                        MaximumSourceDimension,
                        MaximumGifFrames,
                        MaximumGifDecodedPixels);
                    ValidateGifAnimation(animation);
                    gifFrameCount = animation.Frames.Count;
                }

                file.Refresh();
                ServerFileStamp validatedStamp = ServerFileStamp.Capture(file);
                if (!initialStamp.Matches(validatedStamp))
                {
                    throw new IOException(
                        $"Media file '{file.Name}' changed while it was being validated.");
                }

                ManifestRecord record = new(
                    role,
                    kind,
                    name,
                    data.Length,
                    ComputeSha256(data));
                discovered.Add(new ServerFileCandidate(
                    new ServerEmojiBlob(record, data, gifFrameCount),
                    validatedStamp));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (ex is IOException or InvalidDataException)
                {
                    retryableFileErrors.Add(ex);
                    retryableFilePaths.Add(file.FullName);
                }
                RetainPreviousServerFile(
                    role,
                    name,
                    previousByKey,
                    discovered,
                    warnings,
                    $"Rejected {label} file '{file.Name}': {ex.Message}");
            }
        }

        AddServerFilesWithinLimits(
            role,
            label,
            maximumPngFiles,
            maximumGifFiles,
            maximumTotalFiles,
            previousByKey,
            discovered,
            blobs,
            fileStamps,
            records,
            warnings);
    }

    private static void RetainPreviousServerFile(
        MediaRole role,
        string name,
        IReadOnlyDictionary<string, ServerFileCandidate> previousByKey,
        ICollection<ServerFileCandidate> discovered,
        ICollection<string> warnings,
        string failure)
    {
        if (previousByKey.TryGetValue(MediaKey(role, name), out ServerFileCandidate previous))
        {
            discovered.Add(previous);
            warnings.Add(failure + "; keeping the previous valid version.");
            return;
        }

        warnings.Add(failure + "; the file was skipped.");
    }

    private static void AddServerFilesWithinLimits(
        MediaRole role,
        string label,
        int maximumPngFiles,
        int maximumGifFiles,
        int maximumTotalFiles,
        IReadOnlyDictionary<string, ServerFileCandidate> previousByKey,
        IEnumerable<ServerFileCandidate> discovered,
        ICollection<ServerEmojiBlob> blobs,
        IDictionary<string, ServerFileStamp> fileStamps,
        ICollection<ManifestRecord> records,
        ICollection<string> warnings)
    {
        List<ServerFileCandidate> existingFiles = new();
        List<ServerFileCandidate> newFiles = new();
        foreach (ServerFileCandidate candidate in discovered)
        {
            if (!previousByKey.TryGetValue(
                    MediaKey(role, candidate.Blob.Record.Name),
                    out _))
            {
                newFiles.Add(candidate);
            }
            else
            {
                existingFiles.Add(candidate);
            }
        }

        int pngCount = 0;
        int gifCount = 0;
        int gifFrameCount = 0;
        Dictionary<string, ServerFileCandidate> accepted = new(StringComparer.Ordinal);

        static bool IsSameContent(
            ServerFileCandidate first,
            ServerFileCandidate second)
        {
            return first.Blob.Record.Kind == second.Blob.Record.Kind &&
                   first.Blob.Record.Length == second.Blob.Record.Length &&
                   StringComparer.Ordinal.Equals(
                       first.Blob.Record.Hash,
                       second.Blob.Record.Hash) &&
                   first.Blob.GifFrameCount == second.Blob.GifFrameCount;
        }

        static int GifSlotCost(ServerFileCandidate candidate)
        {
            return candidate.Blob.Record.Kind == EmojiFileKind.Gif ? 1 : 0;
        }

        static int GifFrameCost(ServerFileCandidate candidate)
        {
            return candidate.Blob.Record.Kind == EmojiFileKind.Gif
                ? candidate.Blob.GifFrameCount
                : 0;
        }

        bool TryAccept(ServerFileCandidate candidate)
        {
            if (accepted.Count >= maximumTotalFiles)
            {
                return false;
            }

            ServerEmojiBlob blob = candidate.Blob;
            if (blob.Record.Kind == EmojiFileKind.Png)
            {
                if (pngCount >= maximumPngFiles)
                {
                    return false;
                }
                pngCount++;
            }
            else
            {
                int candidateFrames = blob.GifFrameCount;
                if (gifCount >= maximumGifFiles ||
                    candidateFrames <= 0 ||
                    candidateFrames > MaximumTotalGifFrames - gifFrameCount)
                {
                    return false;
                }
                gifCount++;
                gifFrameCount += candidateFrames;
            }
            accepted.Add(blob.Record.Name, candidate);
            return true;
        }

        void RemoveAccepted(ServerFileCandidate candidate)
        {
            ServerEmojiBlob blob = candidate.Blob;
            if (!accepted.Remove(blob.Record.Name))
            {
                throw new InvalidOperationException(
                    $"Clan {label} selection lost '{blob.Record.Name}'.");
            }

            if (blob.Record.Kind == EmojiFileKind.Png)
            {
                pngCount--;
            }
            else
            {
                gifCount--;
                gifFrameCount -= blob.GifFrameCount;
            }
        }

        string DescribeLimit(ServerFileCandidate candidate)
        {
            ServerEmojiBlob blob = candidate.Blob;
            if (accepted.Count >= maximumTotalFiles)
            {
                return $"at most {maximumTotalFiles} total {label} files are supported";
            }
            if (blob.Record.Kind == EmojiFileKind.Png)
            {
                return pngCount >= maximumPngFiles
                    ? $"at most {maximumPngFiles} PNG files are supported"
                    : "the file could not be admitted to the resolved catalog";
            }
            if (gifCount >= maximumGifFiles)
            {
                return $"at most {maximumGifFiles} GIF files are supported";
            }
            if (blob.GifFrameCount <= 0)
            {
                return "the validated GIF frame count is unavailable";
            }
            if (blob.GifFrameCount > MaximumTotalGifFrames - gifFrameCount)
            {
                return $"the catalog would exceed its {MaximumTotalGifFrames} total GIF frame budget";
            }
            return "the file could not be admitted to the resolved catalog";
        }

        // Reserve every still-present file from the last valid catalog first.
        // A larger replacement can then fail without evicting an unrelated file.
        foreach (ServerFileCandidate candidate in existingFiles.OrderBy(
                     item => item.Blob.Record.Name,
                     StringComparer.Ordinal))
        {
            ServerFileCandidate previous = previousByKey[
                MediaKey(role, candidate.Blob.Record.Name)];
            _ = TryAccept(previous);
        }

        // Apply replacements that release GIF slots or frames before replacements
        // that consume them. This admits every simultaneous change whose final
        // catalog fits, independent of filename order.
        foreach (ServerFileCandidate candidate in existingFiles
                     .OrderBy(item =>
                     {
                         ServerFileCandidate previous = previousByKey[
                             MediaKey(role, item.Blob.Record.Name)];
                         return GifSlotCost(item) - GifSlotCost(previous);
                     })
                     .ThenBy(item =>
                     {
                         ServerFileCandidate previous = previousByKey[
                             MediaKey(role, item.Blob.Record.Name)];
                         return GifFrameCost(item) - GifFrameCost(previous);
                     })
                     .ThenBy(
                         item => item.Blob.Record.Name,
                         StringComparer.Ordinal))
        {
            ServerEmojiBlob blob = candidate.Blob;
            ServerFileCandidate previous = previousByKey[
                MediaKey(role, blob.Record.Name)];
            if (IsSameContent(candidate, previous) &&
                accepted.ContainsKey(blob.Record.Name))
            {
                accepted[blob.Record.Name] = candidate;
                continue;
            }

            if (accepted.TryGetValue(blob.Record.Name, out ServerFileCandidate selected))
            {
                RemoveAccepted(selected);
            }
            if (TryAccept(candidate))
            {
                continue;
            }

            string rejection = DescribeLimit(candidate);
            if (TryAccept(previous))
            {
                warnings.Add(
                    $"Rejected {label} update for '{blob.Record.Name + blob.Record.Extension}': " +
                    rejection + "; keeping the previous valid version.");
            }
            else
            {
                warnings.Add(
                    $"Skipped {label} file '{blob.Record.Name + blob.Record.Extension}': " +
                    rejection + ".");
            }
        }

        foreach (ServerFileCandidate candidate in newFiles.OrderBy(
                     item => item.Blob.Record.Name,
                     StringComparer.Ordinal))
        {
            if (!TryAccept(candidate))
            {
                ServerEmojiBlob blob = candidate.Blob;
                warnings.Add(
                    $"Skipped {label} file '{blob.Record.Name + blob.Record.Extension}': " +
                    DescribeLimit(candidate) + ".");
            }
        }

        foreach (ServerFileCandidate candidate in accepted.Values.OrderBy(
                     item => item.Blob.Record.Name,
                     StringComparer.Ordinal))
        {
            ServerEmojiBlob blob = candidate.Blob;
            records.Add(blob.Record);
            blobs.Add(blob);
            if (candidate.Stamp != null)
            {
                fileStamps[MediaKey(role, blob.Record.Name)] = candidate.Stamp;
            }
        }
    }

    private static int GetMaximumSourceBytes(EmojiFileKind kind)
    {
        return kind == EmojiFileKind.Gif
            ? MaximumGifSourceBytes
            : MaximumPngSourceBytes;
    }

    private static string MediaKey(MediaRole role, string name)
    {
        return ((int)role).ToString(CultureInfo.InvariantCulture) + "|" + name;
    }

    private static byte[] ReadStableFile(string path, long expectedLength)
    {
        byte[] data;
        using (FileStream stream = new(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            if (stream.Length != expectedLength)
            {
                throw new IOException($"Media file '{Path.GetFileName(path)}' changed while it was being scanned.");
            }
            data = new byte[stream.Length];
            int offset = 0;
            while (offset < data.Length)
            {
                int read = stream.Read(data, offset, data.Length - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"Media file '{Path.GetFileName(path)}' ended before its declared length.");
                }
                offset += read;
            }
            if (stream.Length != expectedLength)
            {
                throw new IOException($"Media file '{Path.GetFileName(path)}' changed while it was being scanned.");
            }
        }

        return data;
    }

    private static string SerializeManifest(IReadOnlyList<ManifestRecord> records)
    {
        if (records.Count == 0)
        {
            return "";
        }

        StringBuilder builder = new(ManifestVersion);
        foreach (ManifestRecord record in records
                     .OrderBy(record => record.Role)
                     .ThenBy(record => record.Name, StringComparer.Ordinal))
        {
            builder.Append('\n')
                .Append(record.Role == MediaRole.Emoji ? 'e' : 'm')
                .Append('|')
                .Append(record.Kind == EmojiFileKind.Png ? 'p' : 'g')
                .Append('|')
                .Append(record.Name)
                .Append('|')
                .Append(record.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(record.Hash);
        }

        if (builder.Length > MaximumManifestCharacters)
        {
            throw new InvalidDataException("Clan media manifest exceeds its supported size.");
        }

        return builder.ToString();
    }

    private static List<ManifestRecord> ParseManifest(string manifest)
    {
        if (string.IsNullOrEmpty(manifest))
        {
            return new List<ManifestRecord>();
        }
        if (manifest.Length > MaximumManifestCharacters ||
            manifest.IndexOf('\r') >= 0)
        {
            throw new InvalidDataException("Clan media manifest is too large or is not canonical.");
        }

        string[] lines = manifest.Split('\n');
        if (lines.Length == 0 || !StringComparer.Ordinal.Equals(lines[0], ManifestVersion))
        {
            throw new InvalidDataException("Clan media manifest has an unsupported version.");
        }

        List<ManifestRecord> records = new();
        HashSet<string> keys = new(StringComparer.Ordinal);
        MediaRole previousRole = MediaRole.Emoji;
        string previousName = "";
        int emojiPngCount = 0;
        int emojiGifCount = 0;
        int emblemCount = 0;

        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            if (line.Length == 0)
            {
                throw new InvalidDataException("Clan media manifest contains an empty record.");
            }

            string[] fields = line.Split('|');
            if (fields.Length != 5)
            {
                throw new InvalidDataException("Clan media manifest record has an invalid field count.");
            }

            MediaRole role = fields[0] switch
            {
                "e" => MediaRole.Emoji,
                "m" => MediaRole.Emblem,
                _ => throw new InvalidDataException("Clan media manifest contains an unknown role.")
            };
            EmojiFileKind kind = fields[1] switch
            {
                "p" => EmojiFileKind.Png,
                "g" => EmojiFileKind.Gif,
                _ => throw new InvalidDataException("Clan media manifest contains an unknown file type.")
            };
            if (role == MediaRole.Emblem && kind != EmojiFileKind.Png)
            {
                throw new InvalidDataException("Clan media manifest contains a non-PNG emblem.");
            }

            string name = fields[2];
            string key = fields[0] + "|" + name;
            if (!IsSafeName(name) || !keys.Add(key))
            {
                throw new InvalidDataException("Clan media manifest contains an invalid or duplicate name.");
            }
            if (records.Count > 0 &&
                (role < previousRole ||
                 (role == previousRole && StringComparer.Ordinal.Compare(previousName, name) >= 0)))
            {
                throw new InvalidDataException("Clan media manifest records are not strictly sorted.");
            }
            previousRole = role;
            previousName = name;

            if (!int.TryParse(
                    fields[3],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int length) ||
                length <= 0 ||
                length > GetMaximumSourceBytes(kind) ||
                !StringComparer.Ordinal.Equals(
                    fields[3],
                    length.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidDataException("Clan media manifest contains an invalid file length.");
            }
            if (!IsSha256(fields[4]))
            {
                throw new InvalidDataException("Clan media manifest contains an invalid SHA-256 hash.");
            }

            if (role == MediaRole.Emblem)
            {
                emblemCount++;
            }
            else if (kind == EmojiFileKind.Png)
            {
                emojiPngCount++;
            }
            else
            {
                emojiGifCount++;
            }

            records.Add(new ManifestRecord(role, kind, name, length, fields[4]));
        }

        if (emojiPngCount > MaximumPngFiles ||
            emojiGifCount > MaximumGifFiles ||
            emojiPngCount + emojiGifCount > MaximumEmojiFiles ||
            emblemCount > MaximumEmblemFiles)
        {
            throw new InvalidDataException("Clan media manifest contains too many files.");
        }
        if (!StringComparer.Ordinal.Equals(manifest, SerializeManifest(records)))
        {
            throw new InvalidDataException("Clan media manifest is not in canonical form.");
        }

        return records;
    }

    private static void OnSyncedManifestChanged()
    {
        ZNet? currentSession = ZNet.instance;
        if (!_initialized ||
            ClanUiFactory.IsHeadless ||
            currentSession == null ||
            currentSession.IsServer())
        {
            return;
        }

        QueueManifest(SyncedManifest.Value);
    }

    private static void QueueManifest(string manifest)
    {
        _pendingManifest = new PendingManifestState(manifest, ZNet.instance);
    }

    private static void StartBuild(string manifest)
    {
        CancelBuild();
        CancelEmojiClientCatalog();

        try
        {
            List<ManifestRecord> records = ParseManifest(manifest);
            if (records.Count == 0)
            {
                ReplaceRuntime(null);
                _preparedSources.Clear();
                _activeManifest = "";
                _activeRecords = Array.Empty<ManifestRecord>();
                return;
            }
            if (StringComparer.Ordinal.Equals(_activeManifest, manifest) && _runtime != null)
            {
                return;
            }

            BeginEmojiClientCatalog(manifest, records);
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Synchronized Clan media manifest was rejected: {ex.Message}");
        }
    }

    private static void StartReadyEmojiBuild()
    {
        ClientEmojiCatalog? catalog = _clientEmojiCatalog;
        if (catalog == null ||
            !catalog.Ready ||
            !ReferenceEquals(catalog.Session, ZNet.instance))
        {
            return;
        }

        _build = new BuildState(catalog.Manifest, catalog.Records, catalog.Session);
        _clientEmojiCatalog = null;
    }

    private static void CancelBuild()
    {
        BuildState? build = _build;
        _build = null;
        build?.Cancel();
    }

    private static void ProcessBuildStep()
    {
        BuildState? build = _build;
        if (build == null)
        {
            return;
        }
        if (!ReferenceEquals(build.Session, ZNet.instance))
        {
            CancelBuild();
            return;
        }

        if (build.PendingGifTask != null)
        {
            if (!build.PendingGifTask.IsCompleted)
            {
                return;
            }

            ManifestRecord record = build.PendingGifRecord!;
            Task<SourceEmoji> pending = build.PendingGifTask;
            build.PendingGifRecord = null;
            build.PendingGifTask = null;
            try
            {
                AddBuildSource(build, pending.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                RecordBuildFailure(build, record, ex);
            }
            return;
        }

        if (build.NextRecordIndex < build.Records.Count)
        {
            ManifestRecord record = build.Records[build.NextRecordIndex++];
            if (build.PreparedSources.TryGetValue(
                    SourceCacheKey(record),
                    out SourceEmoji prepared))
            {
                AddBuildSource(build, prepared.WithRecord(record));
                return;
            }

            if (record.Kind == EmojiFileKind.Gif)
            {
                try
                {
                    byte[] data = ReadSyncedEmojiFile(record);
                    build.PendingGifRecord = record;
                    CancellationToken cancellationToken = build.Cancellation.Token;
                    build.PendingGifTask = Task.Run(() =>
                        LoadGifSourceSerialized(record, data, cancellationToken),
                        cancellationToken);
                    _ = build.PendingGifTask.ContinueWith(
                        completed => _ = completed.Exception,
                        TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception ex)
                {
                    RecordBuildFailure(build, record, ex);
                }
                return;
            }

            try
            {
                byte[] data = ReadSyncedEmojiFile(record);
                SourceEmoji source = LoadPngSource(record, data);
                AddBuildSource(build, source);
            }
            catch (Exception ex)
            {
                RecordBuildFailure(build, record, ex);
            }
            return;
        }

        if (build.Failures > 0 ||
            build.Sources.Count + build.GifFrameBudgetSkips != build.Records.Count)
        {
            CancelBuild();
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan media update was not applied because {build.Failures} synchronized files failed validation. " +
                "The previous media set remains active.");
            return;
        }
        if (build.Sources.Count == 0)
        {
            CancelBuild();
            ClanPlugin.ClanLogger.LogWarning(
                "Clan media update contained no files within the client GIF frame budget. " +
                "The previous media set remains active.");
            return;
        }

        if (build.RuntimeBuild == null)
        {
            try
            {
                build.RuntimeBuild = new RuntimeBuildState(build.Manifest, build.Sources);
            }
            catch (Exception ex)
            {
                CancelBuild();
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan media runtime could not be prepared; the previous set remains active: {ex.Message}");
            }
            return;
        }

        try
        {
            RuntimeLibrary? next = ProcessRuntimeBuildStep(build.RuntimeBuild);
            if (next == null)
            {
                return;
            }

            Dictionary<string, SourceEmoji> preparedSources = build.Sources
                .GroupBy(source => SourceCacheKey(source.Record), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.Ordinal);
            build.RuntimeBuild.ReleaseOwnership();
            try
            {
                ReplaceRuntime(next);
            }
            catch
            {
                if (!ReferenceEquals(_runtime, next))
                {
                    DestroyRuntime(next);
                }
                throw;
            }
            _build = null;
            _activeManifest = build.Manifest;
            _activeRecords = build.Records;
            _preparedSources = preparedSources;
            PruneEmojiCache(build.Records);
            ClanPlugin.ClanLogger.LogInfo(
                $"Applied {build.Sources.Count} synchronized Clan media files" +
                (build.GifFrameBudgetSkips == 0
                    ? "."
                    : $"; skipped {build.GifFrameBudgetSkips} GIF file(s) outside the " +
                      $"{MaximumTotalGifFrames}-frame client budget."));
        }
        catch (Exception ex)
        {
            CancelBuild();
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan media runtime could not be built; the previous set remains active: {ex.Message}");
        }
    }

    private static void AddBuildSource(BuildState build, SourceEmoji source)
    {
        if (source.Record.Kind == EmojiFileKind.Gif)
        {
            int sourceFrames = source.Frames.Count;
            if (sourceFrames > MaximumTotalGifFrames - build.TotalGifFrames)
            {
                build.GifFrameBudgetSkips++;
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan emoji '{source.Record.Name + source.Record.Extension}' was skipped on this client: " +
                    $"the synchronized catalog would exceed its {MaximumTotalGifFrames} total GIF frame budget.");
                return;
            }
            build.TotalGifFrames += sourceFrames;
        }

        build.Sources.Add(source);
        build.PreparedSources[SourceCacheKey(source.Record)] = source;
    }

    private static SourceEmoji LoadGifSourceSerialized(
        ManifestRecord record,
        byte[] data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GifDecodeGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceEmoji source = LoadGifSource(record, data);
            cancellationToken.ThrowIfCancellationRequested();
            return source;
        }
        finally
        {
            GifDecodeGate.Release();
        }
    }

    private static void RecordBuildFailure(
        BuildState build,
        ManifestRecord record,
        Exception error)
    {
        build.Failures++;
        ClanPlugin.ClanLogger.LogWarning(
            $"Clan {(record.Role == MediaRole.Emoji ? "emoji" : "emblem")} " +
            $"'{record.Name}' is unavailable on this client: {error.Message}");
    }

    private static string SourceCacheKey(ManifestRecord record)
    {
        return record.Kind + ":" + record.Hash;
    }

    private static SourceEmoji LoadPngSource(ManifestRecord record, byte[] data)
    {
        ValidatePngHeader(data, out int expectedWidth, out int expectedHeight);

        Texture2D? sourceTexture = null;
        try
        {
            sourceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "Clan Emoji Source " + record.Name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };

            if (!LoadPngImage(sourceTexture, data))
            {
                throw new InvalidDataException("Unity could not decode the PNG.");
            }
            if (sourceTexture.width != expectedWidth ||
                sourceTexture.height != expectedHeight)
            {
                throw new InvalidDataException("Decoded PNG dimensions do not match its header.");
            }

            byte[] rgba = GetTopDownRgba(sourceTexture);
            ResizeToRenderedBounds(
                rgba,
                sourceTexture.width,
                sourceTexture.height,
                out byte[] rendered,
                out int width,
                out int height);

            return new SourceEmoji(
                record,
                width,
                height,
                new List<byte[]> { rendered },
                0);
        }
        finally
        {
            SafeDestroy(sourceTexture);
        }
    }

    private static SourceEmoji LoadGifSource(ManifestRecord record, byte[] data)
    {
        ClanGifDecoder.Animation animation = ClanGifDecoder.Decode(
            data,
            MaximumGifSourceBytes,
            MaximumSourceDimension,
            MaximumGifFrames,
            MaximumGifDecodedPixels);

        long totalDuration = ValidateGifAnimation(animation);
        int frameCount = animation.Frames.Count;
        int framesPerSecond = frameCount == 1
            ? 0
            : Clamp(
                (int)Math.Round(
                    frameCount * 1000d / totalDuration,
                    MidpointRounding.AwayFromZero),
                1,
                MaximumGifPlaybackFps);

        List<byte[]> renderedFrames = new(frameCount);
        int renderedWidth = 0;
        int renderedHeight = 0;
        foreach (ClanGifDecoder.Frame sourceFrame in animation.Frames)
        {
            ResizeToRenderedBounds(
                sourceFrame.Rgba32,
                animation.Width,
                animation.Height,
                out byte[] rendered,
                out renderedWidth,
                out renderedHeight);
            renderedFrames.Add(rendered);
        }

        return new SourceEmoji(
            record,
            renderedWidth,
            renderedHeight,
            renderedFrames,
            framesPerSecond);
    }

    private static long ValidateGifAnimation(ClanGifDecoder.Animation animation)
    {
        if (animation.Frames.Count == 0)
        {
            throw new InvalidDataException("GIF does not contain an image frame.");
        }
        if (animation.Frames.Any(frame => frame.RequiresUserInput))
        {
            throw new InvalidDataException("GIF frames requiring user input are not supported.");
        }
        if (animation.Frames.Count > 1 && !animation.LoopsForever)
        {
            throw new InvalidDataException("Animated GIF must declare infinite looping.");
        }

        long totalDuration = animation.Frames.Aggregate<ClanGifDecoder.Frame, long>(
            0,
            (current, frame) => current + Math.Max(20, frame.DelayMilliseconds));
        if (totalDuration <= 0 || totalDuration > MaximumGifDurationMilliseconds)
        {
            throw new InvalidDataException(
                $"GIF duration must be between 1 and {MaximumGifDurationMilliseconds} milliseconds.");
        }

        return totalDuration;
    }

    private static void ResizeToRenderedBounds(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        out byte[] rendered,
        out int renderedWidth,
        out int renderedHeight)
    {
        float scale =
            MaximumRenderedDimension / (float)Math.Max(sourceWidth, sourceHeight);
        renderedWidth = Math.Max(1, RoundPositive(sourceWidth * scale));
        renderedHeight = Math.Max(1, RoundPositive(sourceHeight * scale));

        rendered = renderedWidth == sourceWidth && renderedHeight == sourceHeight
            ? source
            : ResizeRgbaBilinear(
                source,
                sourceWidth,
                sourceHeight,
                renderedWidth,
                renderedHeight);
    }

    private static byte[] GetTopDownRgba(Texture2D texture)
    {
        Color32[] colors = texture.GetPixels32();
        byte[] result = new byte[checked(texture.width * texture.height * 4)];
        for (int topY = 0; topY < texture.height; topY++)
        {
            int sourceY = texture.height - 1 - topY;
            for (int x = 0; x < texture.width; x++)
            {
                Color32 color = colors[sourceY * texture.width + x];
                int offset = (topY * texture.width + x) * 4;
                result[offset] = color.r;
                result[offset + 1] = color.g;
                result[offset + 2] = color.b;
                result[offset + 3] = color.a;
            }
        }

        return result;
    }

    private static byte[] ResizeRgbaBilinear(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        byte[] result = new byte[checked(targetWidth * targetHeight * 4)];
        float xScale = sourceWidth / (float)targetWidth;
        float yScale = sourceHeight / (float)targetHeight;

        for (int targetY = 0; targetY < targetHeight; targetY++)
        {
            float sourceY = (targetY + 0.5f) * yScale - 0.5f;
            int y0 = Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            float yWeight = Clamp01(sourceY - y0);

            for (int targetX = 0; targetX < targetWidth; targetX++)
            {
                float sourceX = (targetX + 0.5f) * xScale - 0.5f;
                int x0 = Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                float xWeight = Clamp01(sourceX - x0);

                int topLeft = (y0 * sourceWidth + x0) * 4;
                int topRight = (y0 * sourceWidth + x1) * 4;
                int bottomLeft = (y1 * sourceWidth + x0) * 4;
                int bottomRight = (y1 * sourceWidth + x1) * 4;
                int target = (targetY * targetWidth + targetX) * 4;
                float topLeftWeight = (1f - xWeight) * (1f - yWeight);
                float topRightWeight = xWeight * (1f - yWeight);
                float bottomLeftWeight = (1f - xWeight) * yWeight;
                float bottomRightWeight = xWeight * yWeight;
                float alpha =
                    source[topLeft + 3] * topLeftWeight +
                    source[topRight + 3] * topRightWeight +
                    source[bottomLeft + 3] * bottomLeftWeight +
                    source[bottomRight + 3] * bottomRightWeight;

                result[target + 3] = (byte)Clamp(RoundPositive(alpha), 0, 255);
                if (alpha <= 0.001f)
                {
                    result[target] = 0;
                    result[target + 1] = 0;
                    result[target + 2] = 0;
                    continue;
                }

                for (int channel = 0; channel < 3; channel++)
                {
                    float premultiplied =
                        source[topLeft + channel] * source[topLeft + 3] * topLeftWeight +
                        source[topRight + channel] * source[topRight + 3] * topRightWeight +
                        source[bottomLeft + channel] * source[bottomLeft + 3] * bottomLeftWeight +
                        source[bottomRight + channel] * source[bottomRight + 3] * bottomRightWeight;
                    result[target + channel] = (byte)Clamp(
                        RoundPositive(premultiplied / alpha),
                        0,
                        255);
                }
            }
        }

        return result;
    }

    private static int RoundPositive(float value)
    {
        return (int)Math.Floor(value + 0.5f);
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return value < minimum
            ? minimum
            : value > maximum
                ? maximum
                : value;
    }

    private static float Clamp01(float value)
    {
        return value < 0f
            ? 0f
            : value > 1f
                ? 1f
                : value;
    }

    private static RuntimeLibrary? ProcessRuntimeBuildStep(RuntimeBuildState build)
    {
        switch (build.Stage)
        {
            case RuntimeBuildStage.StaticEmoji:
                BuildStaticEmojiSheet(build);
                build.Stage = RuntimeBuildStage.AnimatedEmoji;
                return null;
            case RuntimeBuildStage.AnimatedEmoji:
                if (build.NextAnimatedSourceIndex < build.AnimatedSources.Count)
                {
                    BuildAnimatedEmojiSheet(
                        build,
                        build.AnimatedSources[build.NextAnimatedSourceIndex++]);
                }
                if (build.NextAnimatedSourceIndex >= build.AnimatedSources.Count)
                {
                    build.Stage = RuntimeBuildStage.Emblems;
                }
                return null;
            case RuntimeBuildStage.Emblems:
                BuildEmblemSheet(build);
                build.Stage = RuntimeBuildStage.Finalize;
                return null;
            case RuntimeBuildStage.Finalize:
                RuntimeLibrary library = FinalizeRuntimeLibrary(build);
                build.Stage = RuntimeBuildStage.Completed;
                return library;
            default:
                throw new InvalidOperationException("Clan media runtime build is already complete.");
        }
    }

    private static void BuildStaticEmojiSheet(RuntimeBuildState build)
    {
        if (build.StillSources.Count == 0)
        {
            return;
        }

        List<FrameSpec> frames = build.StillSources
            .Select(source => new FrameSpec(
                source,
                0,
                SpriteName(source.Record, 0, animated: false)))
            .ToList();
        RuntimeSheet sheet = BuildRuntimeSheet(
            $"ClanEmoji_Still_{build.ManifestHash}",
            frames);
        TrackRuntimeSheet(build, sheet, isEmoji: true);
        for (int index = 0; index < build.StillSources.Count; index++)
        {
            build.References[build.StillSources[index]] =
                new SourceRuntimeReference(sheet, index);
        }
    }

    private static void BuildAnimatedEmojiSheet(
        RuntimeBuildState build,
        SourceEmoji source)
    {
        List<FrameSpec> frames = Enumerable
            .Range(0, source.Frames.Count)
            .Select(index => new FrameSpec(
                source,
                index,
                SpriteName(source.Record, index, animated: true)))
            .ToList();
        RuntimeSheet sheet = BuildRuntimeSheet(
            $"ClanEmoji_{source.Record.Name}_{source.Record.Hash.Substring(0, 12)}",
            frames);
        TrackRuntimeSheet(build, sheet, isEmoji: true);
        build.References[source] = new SourceRuntimeReference(sheet, 0);
    }

    private static void BuildEmblemSheet(RuntimeBuildState build)
    {
        if (build.EmblemSources.Count == 0)
        {
            return;
        }

        List<FrameSpec> frames = build.EmblemSources
            .Select(source => new FrameSpec(
                source,
                0,
                SpriteName(source.Record, 0, animated: false)))
            .ToList();
        RuntimeSheet sheet = BuildRuntimeSheet(
            $"ClanEmblem_Still_{build.ManifestHash}",
            frames);
        TrackRuntimeSheet(build, sheet, isEmoji: false);
        for (int index = 0; index < build.EmblemSources.Count; index++)
        {
            build.References[build.EmblemSources[index]] =
                new SourceRuntimeReference(sheet, index);
        }
    }

    private static void TrackRuntimeSheet(
        RuntimeBuildState build,
        RuntimeSheet sheet,
        bool isEmoji)
    {
        try
        {
            build.Sheets.Add(sheet);
            if (isEmoji)
            {
                build.EmojiSheets.Add(sheet);
            }
        }
        catch
        {
            build.EmojiSheets.Remove(sheet);
            build.Sheets.Remove(sheet);
            DestroyRuntimeSheet(sheet);
            throw;
        }
    }

    private static RuntimeLibrary FinalizeRuntimeLibrary(RuntimeBuildState build)
    {
        List<RuntimeSheet> sheets = build.Sheets;
        List<RuntimeSheet> emojiSheets = build.EmojiSheets;
        Dictionary<SourceEmoji, SourceRuntimeReference> references = build.References;
        IReadOnlyList<SourceEmoji> emojiSources = build.EmojiSources;
        IReadOnlyList<SourceEmoji> emblemSources = build.EmblemSources;

        if (sheets.Count == 0)
        {
            throw new InvalidOperationException("No Clan media sprite sheets were produced.");
        }

        TMP_SpriteAsset? root = emojiSheets.Count == 0
            ? null
            : emojiSheets[0].SpriteAsset;
        if (root != null)
        {
            root.fallbackSpriteAssets ??= new List<TMP_SpriteAsset>();
            for (int index = 1; index < emojiSheets.Count; index++)
            {
                root.fallbackSpriteAssets.Add(emojiSheets[index].SpriteAsset);
            }
        }

        List<RuntimeEmoji> entries = new();
        Dictionary<string, RuntimeRenderTag> renderTags = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RuntimeRenderTag> renderTagsBySpriteName =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceEmoji source in emojiSources)
        {
            SourceRuntimeReference reference = references[source];
            Sprite picker = reference.Sheet.Sprites[reference.FirstFrameIndex];
            string firstSpriteName = picker.name;
            string staticTag = $"<sprite name=\"{firstSpriteName}\">";
            string animatedTag = source.Frames.Count == 1
                ? staticTag
                : $"<sprite name=\"{firstSpriteName}\" anim=\"0,{source.Frames.Count - 1},{source.FramesPerSecond}\">";
            string token = TokenForName(source.Record.Name);
            entries.Add(new RuntimeEmoji(
                token,
                picker,
                source.Record.Kind == EmojiFileKind.Gif));
            RuntimeRenderTag renderTag = new(
                animatedTag,
                staticTag,
                source.Frames.Count > 1);
            renderTags[token] = renderTag;
            renderTagsBySpriteName[firstSpriteName] = renderTag;
        }

        Regex tokenRegex = entries.Count == 0
            ? new Regex("(?!)", RegexOptions.Compiled | RegexOptions.CultureInvariant)
            : new Regex(
                string.Join("|", entries
                .Select(entry => entry.Token)
                .OrderByDescending(token => token.Length)
                .Select(Regex.Escape)),
                RegexOptions.Compiled |
                RegexOptions.CultureInvariant |
                RegexOptions.IgnoreCase);

        Regex spriteTagRegex = renderTagsBySpriteName.Count == 0
            ? new Regex("(?!)", RegexOptions.Compiled | RegexOptions.CultureInvariant)
            : new Regex(
                "<sprite\\s+name=\"(?<name>" +
                string.Join("|", renderTagsBySpriteName.Keys
                    .OrderByDescending(name => name.Length)
                    .Select(Regex.Escape)) +
                ")\"(?:\\s+anim=\"[^\"]*\")?\\s*/?>",
                RegexOptions.Compiled |
                RegexOptions.CultureInvariant |
                RegexOptions.IgnoreCase);

        List<ClanEmblemPickerItem> emblems = emblemSources
            .Select(source =>
            {
                SourceRuntimeReference reference = references[source];
                return new ClanEmblemPickerItem(
                    source.Record.Name,
                    reference.Sheet.Sprites[reference.FirstFrameIndex]);
            })
            .ToList();
        Dictionary<string, ClanEmblemPickerItem> emblemsByName = emblems
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);

        return new RuntimeLibrary(
            root,
            sheets.ToArray(),
            entries,
            emblems.AsReadOnly(),
            emblemsByName,
            renderTags,
            tokenRegex,
            renderTagsBySpriteName,
            spriteTagRegex);
    }

    private static RuntimeSheet BuildRuntimeSheet(
        string assetName,
        IReadOnlyList<FrameSpec> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("A sprite sheet requires at least one frame.", nameof(frames));
        }
        if (SpriteAssetVersionField == null)
        {
            throw new MissingFieldException(typeof(TMP_Asset).FullName, "m_Version");
        }

        int cellWidth = frames.Max(frame => frame.Source.Width);
        int cellHeight = frames.Max(frame => frame.Source.Height);
        int columns = Math.Min(
            AtlasColumns,
            Math.Max(1, (int)Math.Ceiling(Math.Sqrt(frames.Count))));
        int rows = (frames.Count + columns - 1) / columns;
        int strideWidth = cellWidth + AtlasGutter * 2;
        int strideHeight = cellHeight + AtlasGutter * 2;
        int atlasWidth = checked(columns * strideWidth);
        int atlasHeight = checked(rows * strideHeight);
        byte[] atlas = new byte[checked(atlasWidth * atlasHeight * 4)];
        GlyphRect[] glyphRects = new GlyphRect[frames.Count];

        for (int index = 0; index < frames.Count; index++)
        {
            FrameSpec frame = frames[index];
            int column = index % columns;
            int rowFromTop = index / columns;
            int contentX =
                column * strideWidth +
                AtlasGutter +
                (cellWidth - frame.Source.Width) / 2;
            int contentY =
                atlasHeight -
                (rowFromTop + 1) * strideHeight +
                AtlasGutter +
                (cellHeight - frame.Source.Height) / 2;
            BlitWithGutter(
                frame.Pixels,
                frame.Source.Width,
                frame.Source.Height,
                atlas,
                atlasWidth,
                atlasHeight,
                contentX,
                contentY,
                AtlasGutter);
            glyphRects[index] = new GlyphRect(
                contentX,
                contentY,
                frame.Source.Width,
                frame.Source.Height);
        }

        Texture2D? texture = null;
        TMP_SpriteAsset? spriteAsset = null;
        Material? material = null;
        Sprite[] sprites = Array.Empty<Sprite>();
        try
        {
            texture = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false)
            {
                name = assetName + " Texture",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };
            texture.LoadRawTextureData(atlas);
            texture.Apply(false, true);

            spriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            spriteAsset.name = assetName;
            spriteAsset.hideFlags = HideFlags.HideAndDontSave;
            SpriteAssetVersionField.SetValue(spriteAsset, SpriteAssetVersion);
            spriteAsset.spriteSheet = texture;
            spriteAsset.spriteInfoList = new List<TMP_Sprite>();
            spriteAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
            spriteAsset.hashCode = TMP_TextUtilities.GetSimpleHashCode(assetName);
            spriteAsset.faceInfo = new FaceInfo
            {
                familyName = assetName,
                styleName = "Regular",
                pointSize = MaximumRenderedDimension,
                scale = 1f,
                lineHeight = MaximumRenderedDimension,
                ascentLine = MaximumRenderedDimension,
                capLine = MaximumRenderedDimension,
                meanLine = MaximumRenderedDimension * 0.5f,
                baseline = 0f,
                descentLine = 0f
            };

            ShaderUtilities.GetShaderPropertyIDs();
            Shader shader = Shader.Find("TextMeshPro/Sprite");
            if (shader == null)
            {
                throw new InvalidOperationException("TextMeshPro/Sprite shader was not found.");
            }

            material = new Material(shader)
            {
                name = assetName + " Material",
                hideFlags = HideFlags.HideAndDontSave
            };
            material.SetTexture(ShaderUtilities.ID_MainTex, texture);
            spriteAsset.material = material;
            spriteAsset.materialHashCode = TMP_TextUtilities.GetSimpleHashCode(material.name);

            List<TMP_SpriteGlyph> glyphs = spriteAsset.spriteGlyphTable;
            List<TMP_SpriteCharacter> characters = spriteAsset.spriteCharacterTable;
            glyphs.Clear();
            characters.Clear();
            sprites = new Sprite[frames.Count];

            for (int index = 0; index < frames.Count; index++)
            {
                FrameSpec frame = frames[index];
                GlyphRect glyphRect = glyphRects[index];
                Rect spriteRect = new(
                    glyphRect.x,
                    glyphRect.y,
                    glyphRect.width,
                    glyphRect.height);
                Sprite sprite = Sprite.Create(
                    texture,
                    spriteRect,
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect,
                    Vector4.zero,
                    false);
                sprite.name = frame.SpriteName;
                sprite.hideFlags = HideFlags.HideAndDontSave;
                sprites[index] = sprite;

                GlyphMetrics metrics = new(
                    frame.Source.Width,
                    frame.Source.Height,
                    0f,
                    frame.Source.Height,
                    frame.Source.Width);
                TMP_SpriteGlyph glyph = new(
                    (uint)index,
                    metrics,
                    glyphRect,
                    1f,
                    0,
                    sprite);
                glyphs.Add(glyph);

                TMP_SpriteCharacter character = new(0xfffe, spriteAsset, glyph)
                {
                    name = frame.SpriteName,
                    scale = 1f
                };
                characters.Add(character);
            }

            spriteAsset.UpdateLookupTables();
            return new RuntimeSheet(
                texture,
                spriteAsset,
                material,
                sprites);
        }
        catch
        {
            foreach (Sprite sprite in sprites)
            {
                SafeDestroy(sprite);
            }
            SafeDestroy(material);
            SafeDestroy(spriteAsset);
            SafeDestroy(texture);
            throw;
        }
    }

    private static void BlitWithGutter(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        byte[] atlas,
        int atlasWidth,
        int atlasHeight,
        int contentX,
        int contentY,
        int gutter)
    {
        for (int topY = -gutter; topY < sourceHeight + gutter; topY++)
        {
            int sourceY = Mathf.Clamp(topY, 0, sourceHeight - 1);
            int destinationY = contentY + sourceHeight - 1 - topY;
            if (destinationY < 0 || destinationY >= atlasHeight)
            {
                continue;
            }

            for (int relativeX = -gutter; relativeX < sourceWidth + gutter; relativeX++)
            {
                int sourceX = Mathf.Clamp(relativeX, 0, sourceWidth - 1);
                int destinationX = contentX + relativeX;
                if (destinationX < 0 || destinationX >= atlasWidth)
                {
                    continue;
                }

                int sourceOffset = (sourceY * sourceWidth + sourceX) * 4;
                int destinationOffset = (destinationY * atlasWidth + destinationX) * 4;
                atlas[destinationOffset] = source[sourceOffset];
                atlas[destinationOffset + 1] = source[sourceOffset + 1];
                atlas[destinationOffset + 2] = source[sourceOffset + 2];
                atlas[destinationOffset + 3] = source[sourceOffset + 3];
            }
        }
    }

    private static void ReplaceRuntime(RuntimeLibrary? next)
    {
        if (ReferenceEquals(_runtime, next))
        {
            return;
        }

        RuntimeLibrary? previous = _runtime;
        DetachOutput();
        _runtime = next;
        try
        {
            try
            {
                EnsureOutputAttachment();
            }
            catch (Exception ex)
            {
                try
                {
                    ClanPlugin.ClanLogger.LogWarning(
                        $"Clan media UI attachment will be retried: {ex.Message}");
                }
                catch
                {
                    // A third-party log listener must not suppress the committed
                    // runtime notification or the previous runtime cleanup.
                }
            }

            NotifyChanged();
        }
        finally
        {
            DestroyRuntime(previous);
        }
    }

    private static void EnsureOutputAttachment()
    {
        if (!ClanVanillaChatDock.SupportsCurrentChatUi)
        {
            DetachOutput();
            return;
        }

        TMP_Text? output = Chat.instance == null
            ? null
            : ((Terminal)Chat.instance).m_output as TMP_Text;

        TMP_SpriteAsset? root = _runtime?.RootSpriteAsset;
        if (root == null || output == null)
        {
            DetachOutput();
            return;
        }

        if (_attachedOutput == output &&
            IsRuntimeAttached(output, root))
        {
            return;
        }

        DetachOutput();
        _attachedOutput = output;

        if (output.spriteAsset == null)
        {
            output.spriteAsset = root;
            _installedAsPrimary = true;
        }
        else if (output.spriteAsset != root)
        {
            TMP_SpriteAsset owner = output.spriteAsset;
            owner.fallbackSpriteAssets ??= new List<TMP_SpriteAsset>();
            bool fallbackAlreadyAttached =
                owner.fallbackSpriteAssets.Contains(root);
            if (!fallbackAlreadyAttached)
            {
                owner.fallbackSpriteAssets.Add(root);
                _addedFallback = true;
            }
            _fallbackOwner = owner;
        }

        RefreshOutput(output);
    }

    private static bool IsRuntimeAttached(TMP_Text output, TMP_SpriteAsset root)
    {
        if (output.spriteAsset == root)
        {
            return true;
        }

        return output.spriteAsset != null &&
               output.spriteAsset.fallbackSpriteAssets != null &&
               output.spriteAsset.fallbackSpriteAssets.Contains(root);
    }

    private static void DetachOutput()
    {
        TMP_Text? output = _attachedOutput;
        TMP_SpriteAsset? root = _runtime?.RootSpriteAsset;
        if (output != null)
        {
            output.GetComponent<TMP_SpriteAnimator>()?.StopAllAnimations();
        }

        if (_addedFallback &&
            _fallbackOwner != null &&
            root != null &&
            _fallbackOwner.fallbackSpriteAssets != null)
        {
            _fallbackOwner.fallbackSpriteAssets.Remove(root);
        }

        if (_installedAsPrimary &&
            output != null &&
            root != null &&
            output.spriteAsset == root)
        {
            output.spriteAsset = null;
        }

        if (output != null)
        {
            RefreshOutput(output);
            output.GetComponent<TMP_SpriteAnimator>()?.StopAllAnimations();
        }

        _fallbackOwner = null;
        _attachedOutput = null;
        _installedAsPrimary = false;
        _addedFallback = false;
    }

    private static void RefreshOutput(TMP_Text output)
    {
        output.havePropertiesChanged = true;
        output.SetVerticesDirty();
        try
        {
            output.ForceMeshUpdate(true, true);
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogDebug(
                $"Clan emoji output refresh was deferred: {ex.Message}");
        }
    }

    private static bool ContainsEmojiMarker(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text!.IndexOf(TokenPrefix, StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf(SpriteNamePrefix, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void StopChatOutputAnimationsBeforeRewrite(Terminal terminal)
    {
        TMP_Text? output = terminal.m_output as TMP_Text;
        if (output != null && output == _attachedOutput)
        {
            output.GetComponent<TMP_SpriteAnimator>()?.StopAllAnimations();
        }
    }

    private static void NotifyChanged()
    {
        PublishMediaChanged(EmojiChanged, "emoji");
        PublishMediaChanged(EmblemsChanged, "emblem");
    }

    private static void PublishMediaChanged(Action? handlers, string mediaKind)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan {mediaKind} UI refresh failed: {ex.Message}");
            }
        }
    }

    private static void DestroyRuntime(RuntimeLibrary? runtime)
    {
        if (runtime == null)
        {
            return;
        }

        runtime.RootSpriteAsset?.fallbackSpriteAssets?.Clear();
        foreach (RuntimeSheet sheet in runtime.Sheets)
        {
            DestroyRuntimeSheet(sheet);
        }
    }

    private static void DestroyRuntimeSheet(RuntimeSheet sheet)
    {
        foreach (Sprite sprite in sheet.Sprites)
        {
            SafeDestroy(sprite);
        }
        SafeDestroy(sheet.Material);
        SafeDestroy(sheet.SpriteAsset);
        SafeDestroy(sheet.Texture);
    }

    private static void SafeDestroy(UnityEngine.Object? value)
    {
        if (value != null)
        {
            UnityEngine.Object.Destroy(value);
        }
    }

    private static void ValidatePngHeader(byte[] data, out int width, out int height)
    {
        if (data.Length < 33 || data.Length > MaximumPngSourceBytes)
        {
            throw new InvalidDataException("PNG file has an invalid size.");
        }
        for (int index = 0; index < PngSignature.Length; index++)
        {
            if (data[index] != PngSignature[index])
            {
                throw new InvalidDataException("File does not have a valid PNG signature.");
            }
        }
        if (ReadUInt32BigEndian(data, 8) != 13 ||
            data[12] != (byte)'I' ||
            data[13] != (byte)'H' ||
            data[14] != (byte)'D' ||
            data[15] != (byte)'R')
        {
            throw new InvalidDataException("PNG IHDR must be the first chunk.");
        }

        uint rawWidth = ReadUInt32BigEndian(data, 16);
        uint rawHeight = ReadUInt32BigEndian(data, 20);
        if (rawWidth == 0 ||
            rawHeight == 0 ||
            rawWidth > MaximumSourceDimension ||
            rawHeight > MaximumSourceDimension)
        {
            throw new InvalidDataException("PNG dimensions are outside the supported range.");
        }

        int bitDepth = data[24];
        int colorType = data[25];
        if (bitDepth != 8 ||
            (colorType != 0 &&
             colorType != 2 &&
             colorType != 3 &&
             colorType != 4 &&
             colorType != 6) ||
            data[26] != 0 ||
            data[27] != 0 ||
            data[28] != 0)
        {
            throw new InvalidDataException(
                "PNG must use 8-bit color, standard compression/filtering, and no interlace.");
        }

        width = (int)rawWidth;
        height = (int)rawHeight;
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static string ComputeSha256(byte[] data)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(data);
        return string.Concat(hash.Select(value =>
            value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool digit = character >= '0' && character <= '9';
            bool lowerHex = character >= 'a' && character <= 'f';
            if (!digit && !lowerHex)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeName(string value)
    {
        return SafeNameRegex.IsMatch(value) &&
               !ReservedWindowsNames.Contains(value);
    }

    private static HashSet<string> BuildReservedWindowsNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal)
        {
            "con",
            "prn",
            "aux",
            "nul"
        };
        for (int index = 1; index <= 9; index++)
        {
            names.Add("com" + index.ToString(CultureInfo.InvariantCulture));
            names.Add("lpt" + index.ToString(CultureInfo.InvariantCulture));
        }
        return names;
    }

    private static string TokenForName(string name)
    {
        return TokenPrefix + name + ":";
    }

    private static string SpriteName(ManifestRecord record, int frameIndex, bool animated)
    {
        string prefix = record.Role switch
        {
            MediaRole.Emblem => EmblemSpriteNamePrefix,
            _ when record.Kind == EmojiFileKind.Gif => GifSpriteNamePrefix,
            _ => PngSpriteNamePrefix
        };
        return animated
            ? prefix + record.Name + "_f" + frameIndex.ToString("000", CultureInfo.InvariantCulture)
            : prefix + record.Name;
    }

    [HarmonyPatch(typeof(Terminal), "UpdateChat")]
    private static class TerminalUpdateChatPatch
    {
        private static void Prefix(Terminal __instance)
        {
            StopChatOutputAnimationsBeforeRewrite(__instance);
        }
    }

    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
    private static class ChatTextEmojiPatch
    {
        [HarmonyPrefix]
        private static void Prefix(TMP_Text __instance, ref string value)
        {
            if (__instance == null ||
                Chat.instance == null ||
                ((Terminal)Chat.instance).m_output is not TMP_Text output ||
                !ReferenceEquals(__instance, output) ||
                !ClanVanillaChatDock.SupportsCurrentChatUi)
            {
                return;
            }

            __instance.GetComponent<TMP_SpriteAnimator>()?.StopAllAnimations();
            if (!ContainsEmojiMarker(value))
            {
                return;
            }

            value = RenderTokens(value);
            int animationBudget = __instance.gameObject.activeInHierarchy
                ? MaximumAnimatedOccurrencesPerChatPanel
                : 0;
            value = ApplyAnimatedSpriteBudget(value, animationBudget);
        }
    }

    private enum MediaRole
    {
        Emoji,
        Emblem
    }

    private enum EmojiFileKind
    {
        Png,
        Gif
    }

    private sealed class ServerFileStamp
    {
        public readonly string FullPath;
        public readonly long Length;
        public readonly long LastWriteTimeUtcTicks;

        private ServerFileStamp(
            string fullPath,
            long length,
            long lastWriteTimeUtcTicks)
        {
            FullPath = fullPath;
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        }

        public static ServerFileStamp Capture(FileInfo file)
        {
            file.Refresh();
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    $"Media file '{file.Name}' disappeared while it was being scanned.",
                    file.FullName);
            }

            return new ServerFileStamp(
                Path.GetFullPath(file.FullName),
                file.Length,
                file.LastWriteTimeUtc.Ticks);
        }

        public bool Matches(ServerFileStamp other)
        {
            return FilePathComparer.Equals(FullPath, other.FullPath) &&
                   Length == other.Length &&
                   LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
        }
    }

    private sealed class ServerFileCandidate
    {
        public readonly ServerEmojiBlob Blob;
        public readonly ServerFileStamp? Stamp;

        public ServerFileCandidate(ServerEmojiBlob blob, ServerFileStamp? stamp)
        {
            Blob = blob;
            Stamp = stamp;
        }
    }

    private sealed class ManifestRecord
    {
        public readonly MediaRole Role;
        public readonly EmojiFileKind Kind;
        public readonly string Name;
        public readonly int Length;
        public readonly string Hash;

        public ManifestRecord(
            MediaRole role,
            EmojiFileKind kind,
            string name,
            int length,
            string hash)
        {
            Role = role;
            Kind = kind;
            Name = name;
            Length = length;
            Hash = hash;
        }

        public string Extension => Kind == EmojiFileKind.Png ? ".png" : ".gif";
    }

    private sealed class PendingManifestState
    {
        public readonly string Manifest;
        public readonly ZNet? Session;

        public PendingManifestState(string manifest, ZNet? session)
        {
            Manifest = manifest;
            Session = session;
        }
    }

    private enum RuntimeBuildStage
    {
        StaticEmoji,
        AnimatedEmoji,
        Emblems,
        Finalize,
        Completed
    }

    private sealed class BuildState
    {
        public readonly string Manifest;
        public readonly IReadOnlyList<ManifestRecord> Records;
        public readonly ZNet? Session;
        public readonly List<SourceEmoji> Sources = new();
        public readonly Dictionary<string, SourceEmoji> PreparedSources;
        public readonly CancellationTokenSource Cancellation = new();
        public ManifestRecord? PendingGifRecord;
        public Task<SourceEmoji>? PendingGifTask;
        public RuntimeBuildState? RuntimeBuild;
        public int NextRecordIndex;
        public int Failures;
        public int TotalGifFrames;
        public int GifFrameBudgetSkips;

        public BuildState(
            string manifest,
            IReadOnlyList<ManifestRecord> records,
            ZNet? session)
        {
            Manifest = manifest;
            Records = records;
            Session = session;
            PreparedSources = new Dictionary<string, SourceEmoji>(
                _preparedSources,
                StringComparer.Ordinal);
        }

        public void Cancel()
        {
            Cancellation.Cancel();
            RuntimeBuild?.Destroy();
        }
    }

    private sealed class RuntimeBuildState
    {
        public readonly string ManifestHash;
        public readonly IReadOnlyList<SourceEmoji> EmojiSources;
        public readonly IReadOnlyList<SourceEmoji> EmblemSources;
        public readonly IReadOnlyList<SourceEmoji> StillSources;
        public readonly IReadOnlyList<SourceEmoji> AnimatedSources;
        public readonly List<RuntimeSheet> Sheets = new();
        public readonly List<RuntimeSheet> EmojiSheets = new();
        public readonly Dictionary<SourceEmoji, SourceRuntimeReference> References = new();
        public RuntimeBuildStage Stage = RuntimeBuildStage.StaticEmoji;
        public int NextAnimatedSourceIndex;

        private bool _ownsSheets = true;

        public RuntimeBuildState(
            string manifest,
            IReadOnlyList<SourceEmoji> sources)
        {
            ManifestHash = ComputeSha256(Encoding.UTF8.GetBytes(manifest)).Substring(0, 12);
            EmojiSources = sources
                .Where(source => source.Record.Role == MediaRole.Emoji)
                .ToArray();
            EmblemSources = sources
                .Where(source => source.Record.Role == MediaRole.Emblem)
                .ToArray();
            if (EmblemSources.Any(source => source.Frames.Count != 1))
            {
                throw new InvalidDataException("Clan emblems must contain exactly one PNG frame.");
            }

            StillSources = EmojiSources
                .Where(source => source.Frames.Count == 1)
                .ToArray();
            AnimatedSources = EmojiSources
                .Where(source => source.Frames.Count > 1)
                .ToArray();
        }

        public void ReleaseOwnership()
        {
            _ownsSheets = false;
            Sheets.Clear();
            EmojiSheets.Clear();
            References.Clear();
        }

        public void Destroy()
        {
            if (!_ownsSheets)
            {
                return;
            }

            _ownsSheets = false;
            foreach (RuntimeSheet sheet in Sheets)
            {
                sheet.SpriteAsset.fallbackSpriteAssets?.Clear();
            }
            foreach (RuntimeSheet sheet in Sheets)
            {
                DestroyRuntimeSheet(sheet);
            }
            Sheets.Clear();
            EmojiSheets.Clear();
            References.Clear();
        }
    }

    private sealed class SourceEmoji
    {
        public readonly ManifestRecord Record;
        public readonly int Width;
        public readonly int Height;
        public readonly IReadOnlyList<byte[]> Frames;
        public readonly int FramesPerSecond;

        public SourceEmoji(
            ManifestRecord record,
            int width,
            int height,
            IReadOnlyList<byte[]> frames,
            int framesPerSecond)
        {
            Record = record;
            Width = width;
            Height = height;
            Frames = frames;
            FramesPerSecond = framesPerSecond;
        }

        public SourceEmoji WithRecord(ManifestRecord record)
        {
            return new SourceEmoji(
                record,
                Width,
                Height,
                Frames,
                FramesPerSecond);
        }
    }

    private sealed class FrameSpec
    {
        public readonly SourceEmoji Source;
        public readonly int FrameIndex;
        public readonly string SpriteName;

        public FrameSpec(
            SourceEmoji source,
            int frameIndex,
            string spriteName)
        {
            Source = source;
            FrameIndex = frameIndex;
            SpriteName = spriteName;
        }

        public byte[] Pixels => Source.Frames[FrameIndex];
    }

    private sealed class SourceRuntimeReference
    {
        public readonly RuntimeSheet Sheet;
        public readonly int FirstFrameIndex;

        public SourceRuntimeReference(
            RuntimeSheet sheet,
            int firstFrameIndex)
        {
            Sheet = sheet;
            FirstFrameIndex = firstFrameIndex;
        }
    }

    private sealed class RuntimeEmoji
    {
        public readonly string Token;
        public readonly Sprite PickerSprite;
        public readonly bool IsGif;

        public RuntimeEmoji(
            string token,
            Sprite pickerSprite,
            bool isGif)
        {
            Token = token;
            PickerSprite = pickerSprite;
            IsGif = isGif;
        }
    }

    internal readonly struct ClanEmblemPickerItem
    {
        public readonly string Name;
        public readonly Sprite Sprite;

        public ClanEmblemPickerItem(string name, Sprite sprite)
        {
            Name = name;
            Sprite = sprite;
        }
    }

    private sealed class RuntimeRenderTag
    {
        public readonly string AnimatedTag;
        public readonly string StaticTag;
        public readonly bool IsAnimated;

        public RuntimeRenderTag(
            string animatedTag,
            string staticTag,
            bool isAnimated)
        {
            AnimatedTag = animatedTag;
            StaticTag = staticTag;
            IsAnimated = isAnimated;
        }
    }

    private sealed class RuntimeSheet
    {
        public readonly Texture2D Texture;
        public readonly TMP_SpriteAsset SpriteAsset;
        public readonly Material Material;
        public readonly Sprite[] Sprites;

        public RuntimeSheet(
            Texture2D texture,
            TMP_SpriteAsset spriteAsset,
            Material material,
            Sprite[] sprites)
        {
            Texture = texture;
            SpriteAsset = spriteAsset;
            Material = material;
            Sprites = sprites;
        }
    }

    private sealed class RuntimeLibrary
    {
        public readonly TMP_SpriteAsset? RootSpriteAsset;
        public readonly IReadOnlyList<RuntimeSheet> Sheets;
        public readonly IReadOnlyList<RuntimeEmoji> Entries;
        public readonly IReadOnlyList<ClanEmblemPickerItem> Emblems;
        public readonly IReadOnlyDictionary<string, ClanEmblemPickerItem> EmblemsByName;
        public readonly IReadOnlyDictionary<string, RuntimeRenderTag> RenderTags;
        public readonly Regex TokenRegex;
        public readonly IReadOnlyDictionary<string, RuntimeRenderTag> RenderTagsBySpriteName;
        public readonly Regex SpriteTagRegex;

        public RuntimeLibrary(
            TMP_SpriteAsset? rootSpriteAsset,
            IReadOnlyList<RuntimeSheet> sheets,
            IReadOnlyList<RuntimeEmoji> entries,
            IReadOnlyList<ClanEmblemPickerItem> emblems,
            IReadOnlyDictionary<string, ClanEmblemPickerItem> emblemsByName,
            IReadOnlyDictionary<string, RuntimeRenderTag> renderTags,
            Regex tokenRegex,
            IReadOnlyDictionary<string, RuntimeRenderTag> renderTagsBySpriteName,
            Regex spriteTagRegex)
        {
            RootSpriteAsset = rootSpriteAsset;
            Sheets = sheets;
            Entries = entries;
            Emblems = emblems;
            EmblemsByName = emblemsByName;
            RenderTags = renderTags;
            TokenRegex = tokenRegex;
            RenderTagsBySpriteName = renderTagsBySpriteName;
            SpriteTagRegex = spriteTagRegex;
        }
    }
}
