using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using YamlDotNet.Serialization;

namespace Clan;

internal readonly struct ClanRecentPlayerEntry
{
    internal ClanRecentPlayerEntry(ClanPlayerRef player, bool isOnline, long lastSeenUtcTicks)
    {
        Player = player;
        IsOnline = isOnline;
        LastSeenUtcTicks = lastSeenUtcTicks;
    }

    internal ClanPlayerRef Player { get; }
    internal bool IsOnline { get; }
    internal long LastSeenUtcTicks { get; }
}

internal static class ClanRecentPlayers
{
    private const int FormatVersion = 1;
    private const int MaximumPlayers = 4096;
    private const int MaximumSaveBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan RecentLifetime = TimeSpan.FromDays(28);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, StoredRecentPlayer> PlayersById =
        new(StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .DisableAliases()
        .WithIndentedSequences()
        .Build();
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .EnablePrivateConstructors()
        .WithDuplicateKeyChecking()
        .Build();

    private static bool _initialized;
    private static ZNet? _loadedSession;
    private static long _loadedWorldUid;
    private static string? _loadedSaveFile;
    private static bool _dirty;
    private static bool _saveFailed;
    private static bool _primaryRecoveryRequired;
    private static bool _capacityWarningLogged;
    private static DateTime _nextPollUtc = DateTime.MinValue;
    private static DateTime _nextPruneUtc = DateTime.MinValue;
    private static DateTime _saveAfterUtc = DateTime.MaxValue;

    internal static void Init()
    {
        if (_initialized)
        {
            Dispose();
        }

        _initialized = true;
        ResetMemory();
    }

    internal static void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        CloseLoadedSession(DateTime.UtcNow);
        _initialized = false;
        ResetMemory();
    }

    internal static void Tick()
    {
        if (!_initialized)
        {
            Init();
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (!EnsureServerSession(nowUtc))
        {
            return;
        }

        if (nowUtc >= _nextPollUtc)
        {
            _nextPollUtc = nowUtc.Add(PollInterval);
            PollOnlinePlayers(nowUtc);
        }

        if (nowUtc >= _nextPruneUtc)
        {
            _nextPruneUtc = nowUtc.Add(PruneInterval);
            RefreshOnlineLastSeen(nowUtc);
            PruneExpiredPlayers(nowUtc);
        }

        if (_dirty && nowUtc >= _saveAfterUtc)
        {
            TrySave(nowUtc, force: false);
        }
    }

    internal static IReadOnlyList<ClanRecentPlayerEntry> GetRecentPlayers()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (!_initialized || !EnsureServerSession(nowUtc))
        {
            return Array.Empty<ClanRecentPlayerEntry>();
        }

        List<ClanRecentPlayerEntry> entries = new(PlayersById.Count);
        foreach (StoredRecentPlayer stored in PlayersById.Values)
        {
            if (!stored.IsOnline && !IsRecent(stored, nowUtc))
            {
                continue;
            }

            entries.Add(new ClanRecentPlayerEntry(
                stored.Player,
                stored.IsOnline,
                Math.Min(stored.LastSeenUtcTicks, nowUtc.Ticks)));
        }

        entries.Sort(CompareEntries);
        return entries;
    }

    internal static bool TryGetPlayer(string? playerId, out ClanPlayerRef player)
    {
        player = default;
        string normalizedId = ClanDataRules.NormalizePlayerKey(playerId);
        if (normalizedId.Length == 0)
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (!_initialized || !EnsureServerSession(nowUtc) ||
            !PlayersById.TryGetValue(normalizedId, out StoredRecentPlayer stored) ||
            (!stored.IsOnline && !IsRecent(stored, nowUtc)))
        {
            return false;
        }

        player = stored.Player;
        return player.IsValid;
    }

    internal static void RememberPlayer(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (!_initialized || !EnsureServerSession(nowUtc))
        {
            return;
        }

        _ = TryUpsertOnlinePlayer(player, nowUtc);
    }

    internal static void MarkPlayerOffline(ClanPlayerRef player)
    {
        if (!player.IsValid)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (!_initialized ||
            !EnsureServerSession(nowUtc) ||
            !PlayersById.TryGetValue(player.Id, out StoredRecentPlayer stored) ||
            !stored.IsOnline)
        {
            return;
        }

        stored.IsOnline = false;
        stored.LastSeenUtcTicks = nowUtc.Ticks;
        MarkDirty(nowUtc);
    }

    private static bool EnsureServerSession(DateTime nowUtc)
    {
        ZNet? session = ZNet.instance;
        World? world = ZNet.World;
        if (session == null || !session.IsServer() || world == null)
        {
            if (_loadedSaveFile != null)
            {
                CloseLoadedSession(nowUtc);
            }

            return false;
        }

        long worldUid = world.m_uid;
        if (ReferenceEquals(_loadedSession, session) &&
            _loadedWorldUid == worldUid &&
            _loadedSaveFile != null)
        {
            return true;
        }

        if (!CloseLoadedSession(nowUtc))
        {
            return false;
        }

        _loadedSession = session;
        _loadedWorldUid = worldUid;
        _loadedSaveFile = ResolveSaveFile();
        LoadGlobalSave(nowUtc);
        _nextPollUtc = DateTime.MinValue;
        _nextPruneUtc = nowUtc.Add(PruneInterval);
        return true;
    }

    private static bool CloseLoadedSession(DateTime nowUtc)
    {
        if (_loadedSaveFile != null)
        {
            MarkAllOffline(nowUtc);
            if (!TrySave(nowUtc, force: true))
            {
                return false;
            }
        }

        ResetMemory();
        return true;
    }

    private static void PollOnlinePlayers(DateTime nowUtc)
    {
        Dictionary<string, ClanPlayerRef> onlinePlayers = new(StringComparer.Ordinal);
        foreach (ClanPlayerRef player in ClanIdentity.GetOnlinePlayerRefs())
        {
            if (player.IsValid)
            {
                onlinePlayers[player.Id] = player;
            }
        }

        foreach (ClanPlayerRef player in onlinePlayers.Values)
        {
            if (!TryUpsertOnlinePlayer(player, nowUtc))
            {
                if (!_capacityWarningLogged)
                {
                    _capacityWarningLogged = true;
                    ClanPlugin.ClanLogger.LogWarning(
                        $"Recent-player storage reached its {MaximumPlayers}-player limit; " +
                        "additional online players cannot be recorded until an offline entry expires.");
                }
            }
        }

        foreach (StoredRecentPlayer stored in PlayersById.Values)
        {
            if (!stored.IsOnline || onlinePlayers.ContainsKey(stored.Player.Id))
            {
                continue;
            }

            stored.IsOnline = false;
            stored.LastSeenUtcTicks = nowUtc.Ticks;
            MarkDirty(nowUtc);
        }

        if (PlayersById.Count < MaximumPlayers)
        {
            _capacityWarningLogged = false;
        }
    }

    private static bool TryUpsertOnlinePlayer(ClanPlayerRef player, DateTime nowUtc)
    {
        if (!PlayersById.TryGetValue(player.Id, out StoredRecentPlayer stored))
        {
            if (!MakeRoomForPlayer(nowUtc))
            {
                return false;
            }

            PlayersById.Add(player.Id, new StoredRecentPlayer
            {
                Player = player,
                LastSeenUtcTicks = nowUtc.Ticks,
                IsOnline = true
            });
            MarkDirty(nowUtc);
            return true;
        }

        bool changed = false;
        if (!StringComparer.Ordinal.Equals(stored.Player.Name, player.Name))
        {
            stored.Player = player;
            changed = true;
        }
        if (!stored.IsOnline)
        {
            stored.IsOnline = true;
            stored.LastSeenUtcTicks = nowUtc.Ticks;
            changed = true;
        }
        if (changed)
        {
            MarkDirty(nowUtc);
        }
        return true;
    }

    private static void RefreshOnlineLastSeen(DateTime nowUtc)
    {
        bool changed = false;
        foreach (StoredRecentPlayer stored in PlayersById.Values)
        {
            if (!stored.IsOnline || stored.LastSeenUtcTicks == nowUtc.Ticks)
            {
                continue;
            }

            stored.LastSeenUtcTicks = nowUtc.Ticks;
            changed = true;
        }

        if (changed)
        {
            MarkDirty(nowUtc);
        }
    }

    private static void MarkAllOffline(DateTime nowUtc)
    {
        bool changed = false;
        foreach (StoredRecentPlayer stored in PlayersById.Values)
        {
            if (!stored.IsOnline)
            {
                continue;
            }

            stored.IsOnline = false;
            stored.LastSeenUtcTicks = nowUtc.Ticks;
            changed = true;
        }

        if (changed)
        {
            MarkDirty(nowUtc);
        }
    }

    private static bool MakeRoomForPlayer(DateTime nowUtc)
    {
        PruneExpiredPlayers(nowUtc);
        if (PlayersById.Count < MaximumPlayers)
        {
            return true;
        }

        StoredRecentPlayer? oldestOffline = null;
        foreach (StoredRecentPlayer candidate in PlayersById.Values)
        {
            if (candidate.IsOnline ||
                (oldestOffline != null && CompareOldest(candidate, oldestOffline) >= 0))
            {
                continue;
            }

            oldestOffline = candidate;
        }

        if (oldestOffline == null)
        {
            return false;
        }

        PlayersById.Remove(oldestOffline.Player.Id);
        MarkDirty(nowUtc);
        return true;
    }

    private static int CompareOldest(StoredRecentPlayer left, StoredRecentPlayer right)
    {
        int seenComparison = left.LastSeenUtcTicks.CompareTo(right.LastSeenUtcTicks);
        return seenComparison != 0
            ? seenComparison
            : StringComparer.Ordinal.Compare(left.Player.Id, right.Player.Id);
    }

    private static void PruneExpiredPlayers(DateTime nowUtc)
    {
        List<string>? expiredIds = null;
        bool clampedFutureTimestamp = false;
        foreach (StoredRecentPlayer stored in PlayersById.Values)
        {
            if (stored.LastSeenUtcTicks > nowUtc.Ticks)
            {
                stored.LastSeenUtcTicks = nowUtc.Ticks;
                clampedFutureTimestamp = true;
            }
            if (stored.IsOnline || IsRecent(stored, nowUtc))
            {
                continue;
            }

            expiredIds ??= new List<string>();
            expiredIds.Add(stored.Player.Id);
        }

        if (expiredIds == null)
        {
            if (clampedFutureTimestamp)
            {
                MarkDirty(nowUtc);
            }
            return;
        }

        foreach (string playerId in expiredIds)
        {
            PlayersById.Remove(playerId);
        }

        MarkDirty(nowUtc);
    }

    private static bool IsRecent(StoredRecentPlayer player, DateTime nowUtc)
    {
        long lastSeenUtcTicks = Math.Min(player.LastSeenUtcTicks, nowUtc.Ticks);
        return lastSeenUtcTicks > DateTime.MinValue.Ticks &&
               nowUtc.Ticks - lastSeenUtcTicks <= RecentLifetime.Ticks;
    }

    private static int CompareEntries(ClanRecentPlayerEntry left, ClanRecentPlayerEntry right)
    {
        int onlineComparison = right.IsOnline.CompareTo(left.IsOnline);
        if (onlineComparison != 0)
        {
            return onlineComparison;
        }

        int seenComparison = right.LastSeenUtcTicks.CompareTo(left.LastSeenUtcTicks);
        if (seenComparison != 0)
        {
            return seenComparison;
        }

        int nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Player.Name, right.Player.Name);
        return nameComparison != 0
            ? nameComparison
            : StringComparer.Ordinal.Compare(left.Player.Id, right.Player.Id);
    }

    private static void LoadGlobalSave(DateTime nowUtc)
    {
        PlayersById.Clear();
        _dirty = false;
        _saveFailed = false;
        _primaryRecoveryRequired = false;
        _saveAfterUtc = DateTime.MaxValue;

        string saveFile = _loadedSaveFile!;
        string backupFile = saveFile + ".bak";
        if (TryLoadSave(
                saveFile,
                nowUtc,
                out Dictionary<string, StoredRecentPlayer>? loaded,
                out bool primaryNeedsRewrite,
                out Exception? primaryError))
        {
            ReplacePlayers(loaded!);
            PruneExpiredPlayers(nowUtc);
            if (primaryNeedsRewrite)
            {
                MarkDirty(nowUtc);
            }
            return;
        }

        if (primaryError != null)
        {
            string quarantine = TryQuarantine(saveFile);
            _primaryRecoveryRequired = quarantine.Length == 0 && File.Exists(saveFile);
            ClanPlugin.ClanLogger.LogWarning(
                $"Recent-player save '{saveFile}' was invalid and " +
                (quarantine.Length == 0 ? "could not be quarantined" : $"was moved to '{quarantine}'") +
                $": {primaryError.Message}");
        }

        if (TryLoadSave(
                backupFile,
                nowUtc,
                out loaded,
                out _,
                out Exception? backupError))
        {
            ReplacePlayers(loaded!);
            if (_primaryRecoveryRequired)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Loaded recent-player data from the validated backup '{backupFile}', but the " +
                    "invalid primary could not be quarantined. Recovery will be retried without " +
                    "overwriting the valid backup.");
                ScheduleSaveRetry(nowUtc);
            }
            else
            {
                try
                {
                    WriteAtomically(saveFile, SerializePlayers());
                    ClanPlugin.ClanLogger.LogWarning(
                        $"Restored recent-player data from the validated backup '{backupFile}'.");
                }
                catch (Exception restoreError)
                {
                    ClanPlugin.ClanLogger.LogWarning(
                        $"Loaded recent-player backup '{backupFile}', but failed to restore the primary save: " +
                        restoreError.Message);
                    ScheduleSaveRetry(nowUtc);
                }
            }

            PruneExpiredPlayers(nowUtc);
            return;
        }

        if (backupError != null)
        {
            string quarantine = TryQuarantine(backupFile);
            ClanPlugin.ClanLogger.LogWarning(
                $"Recent-player backup '{backupFile}' was invalid and " +
                (quarantine.Length == 0 ? "could not be quarantined" : $"was moved to '{quarantine}'") +
                $": {backupError.Message}");
        }

        PlayersById.Clear();
        _dirty = false;
        _saveAfterUtc = DateTime.MaxValue;
    }

    private static bool TryLoadSave(
        string path,
        DateTime nowUtc,
        out Dictionary<string, StoredRecentPlayer>? players,
        out bool needsRewrite,
        out Exception? error)
    {
        players = null;
        needsRewrite = false;
        error = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            FileInfo info = new(path);
            if (info.Length < 0L || info.Length > MaximumSaveBytes)
            {
                throw new InvalidDataException(
                    $"Recent-player save exceeds the {MaximumSaveBytes}-byte limit.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            players = ParseSave(bytes, nowUtc, out needsRewrite);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static Dictionary<string, StoredRecentPlayer> ParseSave(
        byte[] bytes,
        DateTime nowUtc,
        out bool needsRewrite)
    {
        needsRewrite = false;
        string yaml = StrictUtf8.GetString(bytes);
        RecentPlayersYaml? document = YamlDeserializer.Deserialize<RecentPlayersYaml>(yaml);
        if (document == null)
        {
            throw new InvalidDataException("Recent-player save is empty.");
        }
        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                $"Recent-player format version {document.FormatVersion} is unsupported; " +
                $"only version {FormatVersion} is accepted.");
        }
        List<RecentPlayerYaml> serializedPlayers = document.Players ??
            throw new InvalidDataException("Recent-player save is missing the players list.");
        if (serializedPlayers.Count > MaximumPlayers)
        {
            throw new InvalidDataException(
                $"Recent-player count exceeds the {MaximumPlayers}-player limit.");
        }

        Dictionary<string, StoredRecentPlayer> players =
            new(serializedPlayers.Count, StringComparer.Ordinal);
        bool clampedFutureTimestamp = false;
        for (int index = 0; index < serializedPlayers.Count; index++)
        {
            RecentPlayerYaml? serialized = serializedPlayers[index];
            if (serialized == null)
            {
                throw new InvalidDataException(
                    $"Recent-player entry {index} cannot be null.");
            }

            string platformId = ClanDataRules.RequirePlatformId(
                serialized.PlatformId,
                $"recent player {index} platform_id");
            long characterPlayerId = ClanDataRules.RequireCharacterPlayerId(
                serialized.PlayerId,
                $"recent player {index} player_id");
            if (serialized.Name == null)
            {
                throw new InvalidDataException(
                    $"Recent-player entry {index} is missing name.");
            }
            string name = ClanDataRules.RequireText(
                serialized.Name,
                ClanDataRules.MaxPlayerNameLength,
                $"recent player {index} name");
            ClanPlayerRef player = new(platformId, characterPlayerId, name);

            string lastSeenText = ClanDataRules.RequireText(
                serialized.LastSeenUtc,
                64,
                $"recent player '{player.Id}' last_seen_utc",
                allowEmpty: false);
            if (!DateTime.TryParseExact(
                    lastSeenText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTime lastSeenUtc) ||
                lastSeenUtc.Kind != DateTimeKind.Utc ||
                !StringComparer.Ordinal.Equals(
                    lastSeenText,
                    lastSeenUtc.ToString("O", CultureInfo.InvariantCulture)) ||
                lastSeenUtc.Ticks <= DateTime.MinValue.Ticks)
            {
                throw new InvalidDataException(
                    $"Recent player '{player.Id}' has an invalid last-seen timestamp.");
            }

            if (players.ContainsKey(player.Id))
            {
                throw new InvalidDataException(
                    $"Recent-player save contains duplicate identity '{player.Id}'.");
            }

            if (lastSeenUtc.Ticks > nowUtc.Ticks)
            {
                lastSeenUtc = nowUtc;
                clampedFutureTimestamp = true;
                needsRewrite = true;
            }

            players.Add(player.Id, new StoredRecentPlayer
            {
                Player = player,
                LastSeenUtcTicks = lastSeenUtc.Ticks,
                IsOnline = false
            });
        }

        if (clampedFutureTimestamp)
        {
            ClanPlugin.ClanLogger.LogWarning(
                "Recent-player save contained future last-seen timestamps; they were clamped to the current UTC time.");
        }

        return players;
    }

    private static void ReplacePlayers(Dictionary<string, StoredRecentPlayer> loaded)
    {
        PlayersById.Clear();
        foreach (KeyValuePair<string, StoredRecentPlayer> entry in loaded)
        {
            PlayersById.Add(entry.Key, entry.Value);
        }

        _dirty = false;
        _saveAfterUtc = DateTime.MaxValue;
    }

    private static bool TrySave(DateTime nowUtc, bool force)
    {
        if (!_dirty || _loadedSaveFile == null)
        {
            return true;
        }

        if (nowUtc < _saveAfterUtc && (!force || _saveFailed))
        {
            return false;
        }

        try
        {
            if (_primaryRecoveryRequired && File.Exists(_loadedSaveFile))
            {
                string quarantine = TryQuarantine(_loadedSaveFile);
                if (quarantine.Length == 0 && File.Exists(_loadedSaveFile))
                {
                    throw new IOException(
                        "The invalid recent-player primary save could not be quarantined.");
                }

                _primaryRecoveryRequired = false;
            }

            WriteAtomically(_loadedSaveFile, SerializePlayers());
            _dirty = false;
            _saveFailed = false;
            _saveAfterUtc = DateTime.MaxValue;
            return true;
        }
        catch (Exception ex)
        {
            _saveFailed = true;
            _saveAfterUtc = nowUtc.Add(SaveRetryDelay);
            ClanPlugin.ClanLogger.LogWarning(
                $"Failed to save global recent-player data: {ex.Message}");
            return false;
        }
    }

    private static byte[] SerializePlayers()
    {
        if (PlayersById.Count > MaximumPlayers)
        {
            throw new InvalidDataException(
                $"Recent-player count exceeds the {MaximumPlayers}-player limit.");
        }

        List<StoredRecentPlayer> ordered = new(PlayersById.Values);
        ordered.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Player.Id, right.Player.Id));

        List<RecentPlayerYaml> serializedPlayers = new(ordered.Count);
        RecentPlayersYaml document = new()
        {
            FormatVersion = FormatVersion,
            Players = serializedPlayers
        };
        foreach (StoredRecentPlayer stored in ordered)
        {
            string platformId = ClanDataRules.RequirePlatformId(stored.Player.PlatformId);
            long characterPlayerId = ClanDataRules.RequireCharacterPlayerId(
                stored.Player.CharacterPlayerId);
            string name = ClanDataRules.RequireText(
                stored.Player.Name,
                ClanDataRules.MaxPlayerNameLength,
                "player name");
            if (stored.LastSeenUtcTicks <= DateTime.MinValue.Ticks ||
                stored.LastSeenUtcTicks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidDataException(
                    $"Recent player '{stored.Player.Id}' has an invalid last-seen timestamp.");
            }

            serializedPlayers.Add(new RecentPlayerYaml
            {
                PlatformId = platformId,
                PlayerId = characterPlayerId,
                Name = name,
                LastSeenUtc = new DateTime(stored.LastSeenUtcTicks, DateTimeKind.Utc)
                    .ToString("O", CultureInfo.InvariantCulture)
            });
        }

        string yaml = YamlSerializer.Serialize(document)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        if (!yaml.EndsWith("\n", StringComparison.Ordinal))
        {
            yaml += "\n";
        }

        byte[] bytes = StrictUtf8.GetBytes(yaml);
        if (bytes.Length > MaximumSaveBytes)
        {
            throw new InvalidDataException(
                $"Recent-player save exceeds the {MaximumSaveBytes}-byte limit.");
        }

        return bytes;
    }

    private static void WriteAtomically(string saveFile, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(saveFile);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Recent-player save directory is invalid.");
        }

        Directory.CreateDirectory(directory);
        string temporaryFile = saveFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(
                       temporaryFile,
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
                File.Replace(temporaryFile, saveFile, backupFile, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryFile, saveFile);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }
            }
            catch (Exception cleanupError)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Failed to clean temporary recent-player save '{temporaryFile}': " +
                    cleanupError.Message);
            }
        }
    }

    private static string TryQuarantine(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string quarantineFile = path +
                                    ".unsupported-or-corrupt-" +
                                    timestamp +
                                    "-" +
                                    Guid.NewGuid().ToString("N");
            try
            {
                File.Move(path, quarantineFile);
                return quarantineFile;
            }
            catch (IOException) when (File.Exists(path) && File.Exists(quarantineFile))
            {
                // A generated destination was claimed concurrently. Retry with a new name.
            }
            catch (Exception ex)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Failed to quarantine recent-player save '{path}': {ex.Message}");
                return "";
            }
        }

        ClanPlugin.ClanLogger.LogWarning(
            $"Could not allocate a quarantine path for recent-player save '{path}'.");
        return "";
    }

    private static string ResolveSaveFile()
    {
        return Path.Combine(
            Paths.ConfigPath,
            ClanPlugin.ModName,
            "recent-players.yml");
    }

    private static void MarkDirty(DateTime nowUtc)
    {
        _dirty = true;
        if (_saveFailed)
        {
            return;
        }

        DateTime candidate = nowUtc.Add(SaveDebounce);
        if (_saveAfterUtc == DateTime.MaxValue || candidate < _saveAfterUtc)
        {
            _saveAfterUtc = candidate;
        }
    }

    private static void ScheduleSaveRetry(DateTime nowUtc)
    {
        _dirty = true;
        _saveFailed = true;
        _saveAfterUtc = nowUtc.Add(SaveRetryDelay);
    }

    private static void ResetMemory()
    {
        PlayersById.Clear();
        _loadedSession = null;
        _loadedWorldUid = 0L;
        _loadedSaveFile = null;
        _dirty = false;
        _saveFailed = false;
        _primaryRecoveryRequired = false;
        _capacityWarningLogged = false;
        _nextPollUtc = DateTime.MinValue;
        _nextPruneUtc = DateTime.MinValue;
        _saveAfterUtc = DateTime.MaxValue;
    }

    private sealed class RecentPlayersYaml
    {
        [YamlMember(Alias = "format_version", Order = 1)]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "players", Order = 2)]
        public List<RecentPlayerYaml>? Players { get; set; }
    }

    private sealed class RecentPlayerYaml
    {
        [YamlMember(Alias = "platform_id", Order = 1)]
        public string? PlatformId { get; set; }

        [YamlMember(Alias = "player_id", Order = 2)]
        public long PlayerId { get; set; }

        [YamlMember(Alias = "name", Order = 3)]
        public string? Name { get; set; }

        [YamlMember(Alias = "last_seen_utc", Order = 4)]
        public string? LastSeenUtc { get; set; }
    }

    private sealed class StoredRecentPlayer
    {
        internal ClanPlayerRef Player;
        internal long LastSeenUtcTicks;
        internal bool IsOnline;
    }
}
