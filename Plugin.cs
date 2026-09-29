using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace Clan;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.groups")]
[BepInIncompatibility("org.bepinex.plugins.guilds")]
[BepInDependency(EpicMmoCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(QuestForgeCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class ClanPlugin : BaseUnityPlugin
{
    public const string ModName = "Clan";
    public const string ModVersion = "1.1.2";
    public const string Author = "sighsorry";
    public const string ModGUID = $"{Author}.{ModName}";

    internal static ManualLogSource ClanLogger { get; } = BepInEx.Logging.Logger.CreateLogSource(ModName);
    internal static string DataDirectory =>
        Path.Combine(
            Utils.GetSaveDataPath(FileHelpers.FileSource.Local),
            ModName);
    internal static string MediaDirectory =>
        Path.Combine(Paths.ConfigPath, ModName);

    internal static ConfigEntry<Toggle> ServerConfigLocked = null!;
    internal static ConfigEntry<KeyboardShortcut> ClanPingModifierKey = null!;
    internal static ConfigEntry<Toggle> ShareClanPositions = null!;
    internal static ConfigEntry<Toggle> ClanFriendlyFire = null!;
    internal static ConfigEntry<float> ClanChatWindowScale = null!;
    internal static ConfigEntry<KeyboardShortcut> ClanPanelShortcut = null!;
    internal static ConfigEntry<KeyboardShortcut> EmojiPanelShortcut = null!;
    internal static ConfigEntry<Toggle> ShowWhisperChatButton = null!;
    internal static ConfigEntry<Toggle> ShowResetSpawnButton = null!;
    internal static ConfigEntry<Toggle> ShowDieButton = null!;
    internal static ConfigEntry<ChatSubmitMode> ChatAfterSend = null!;
    internal static ConfigEntry<Toggle> ShowClanHud = null!;
    internal static ConfigEntry<Toggle> ClanHudPlayerListCollapsed = null!;
    internal static ConfigEntry<Vector2> ClanHudPosition = null!;
    internal static ConfigEntry<Toggle> ShareEpicMmoExperience = null!;
    internal static ConfigEntry<Toggle> ShareQuestForgeKills = null!;
    internal static ConfigEntry<float> QuestForgeShareRange = null!;

    private static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };

    private static readonly string ConfigFileName = $"{ModGUID}.cfg";
    private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

    private readonly Harmony _harmony = new(ModGUID);
    private FileSystemWatcher? _watcher;
    private string? _lastConfigContent;
    private bool _configReloadPending;
    private DateTime _configReloadAfter;
    private int _configReloadFailures;

    private const int ConfigReloadDelayMilliseconds = 250;
    private const int ConfigReloadRetryLimit = 4;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public enum ChatSubmitMode
    {
        CloseAfterSend = 0,
        KeepOpen = 1
    }

    private sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? Browsable;
    }

    private void Awake()
    {
        ClanUiFactory.Initialize();
        ClanLocalization.Initialize(this, _harmony);
        BindConfiguration();

        ClanEmoji.Init();
        ClanRegistry.Init();
        ClanRecentPlayers.Init();
        ClanApi.Initialize();

        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        EpicMmoCompat.Initialize(_harmony);
        QuestForgeCompat.Initialize(_harmony);
        ClanVanillaChatDock.Init();
        ClanHud.Init();
        SetupWatcher();

        ClanLogger.LogInfo($"{ModName} {ModVersion} loaded for Valheim 1.0.");
    }

    private void BindConfiguration()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        try
        {
            Config.SaveOnConfigSet = false;
            ServerConfigLocked = ConfigEntry(
                "1 - General",
                "Lock Configuration",
                Toggle.On,
                new ConfigDescription(
                    "If on, only server admins can change synced configuration.",
                    null,
                    new ConfigurationManagerAttributes { Order = 4 }));
            ConfigSync.AddLockingConfigEntry(ServerConfigLocked);

            ClanFriendlyFire = ConfigEntry(
                "1 - General",
                "Clan Friendly Fire",
                Toggle.On,
                new ConfigDescription(
                    "If on, clan-connected players can damage each other.",
                    null,
                    new ConfigurationManagerAttributes { Order = 3 }));
            ShareClanPositions = ConfigEntry(
                "1 - General",
                "Share Clan Positions",
                Toggle.On,
                new ConfigDescription(
                    "If on, clan members and guests can share position visibility.",
                    null,
                    new ConfigurationManagerAttributes { Order = 2 }));
            ClanPingModifierKey = ConfigEntry(
                "1 - General",
                "Clan Ping Modifier Key",
                new KeyboardShortcut(KeyCode.LeftShift),
                new ConfigDescription(
                    "Modifier key reserved for clan-only pings.",
                    null,
                    new ConfigurationManagerAttributes { Order = 1 }),
                synchronizedSetting: false);

            ClanChatWindowScale = ConfigEntry(
                "2 - UI",
                "Clan Chat Window Scale",
                1f,
                new ConfigDescription(
                    "Uniform scale for the vanilla chat window, including text and inline emojis. The upper-left resize handle updates this value.",
                    new AcceptableValueRange<float>(1f, 2f)),
                synchronizedSetting: false);
            ClanPanelShortcut = ConfigEntry(
                "2 - UI",
                "Clan Panel Shortcut",
                new KeyboardShortcut(KeyCode.J),
                "Client-only shortcut that opens or closes the Clan panel. Set the main key to None to disable it.",
                synchronizedSetting: false);
            EmojiPanelShortcut = ConfigEntry(
                "2 - UI",
                "Emoji Panel Shortcut",
                new KeyboardShortcut(KeyCode.H),
                "Client-only shortcut that opens the chat and Emoji panel. Set the main key to None to disable it.",
                synchronizedSetting: false);
            ShowWhisperChatButton = ConfigEntry(
                "2 - UI",
                "Show Whisper Chat Button",
                Toggle.Off,
                "If on, shows the optional Whisper channel button below the chat panel.",
                synchronizedSetting: false);
            ShowResetSpawnButton = ConfigEntry(
                "2 - UI",
                "Show Reset Spawn Button",
                Toggle.Off,
                "If on, shows the Reset spawn command button at the lower-right of the chat panel.",
                synchronizedSetting: false);
            ShowDieButton = ConfigEntry(
                "2 - UI",
                "Show Die Button",
                Toggle.On,
                "If on, shows the Die command button at the lower-right of the chat panel.",
                synchronizedSetting: false);
            ChatAfterSend = ConfigEntry(
                "2 - UI",
                "Chat After Send",
                ChatSubmitMode.KeepOpen,
                "Controls the chat input after sending a message. CloseAfterSend restores Valheim's default behavior; KeepOpen immediately focuses the chat input again.",
                synchronizedSetting: false);
            ShowClanHud = ConfigEntry("2 - UI", "Show Clan Member HUD", Toggle.On, "If on, shows a compact clan member HUD.", synchronizedSetting: false);
            ClanHudPlayerListCollapsed = ConfigEntry(
                "2 - UI",
                "Clan HUD Player List Collapsed",
                Toggle.Off,
                new ConfigDescription(
                    "Stores whether the Clan HUD player list is collapsed.",
                    null,
                    new ConfigurationManagerAttributes { Browsable = false }),
                synchronizedSetting: false);
            ClanHudPosition = ConfigEntry(
                "2 - UI",
                "Clan HUD Position",
                new Vector2(0.015f, 0.32f),
                "Normalized safe-area position of the Clan HUD's upper-left corner. Drag the Clan header to update it.",
                synchronizedSetting: false);

            ShareEpicMmoExperience = ConfigEntry(
                "3 - Compatibility", "Share Epic MMO Experience", Toggle.On,
                "Share combat XP with online members of the effective clan when WackyEpicMMOSystem is installed. Preserves its group multiplier, range and level rules.");
            ShareQuestForgeKills = ConfigEntry(
                "3 - Compatibility", "Share QuestForge Kills", Toggle.On,
                "Share kill objective credit with nearby living members of the effective clan when RtDQuestForge is installed. Each player must accept their own quests; rewards and gathering remain personal.");
            QuestForgeShareRange = ConfigEntry(
                "3 - Compatibility", "QuestForge Share Range", 70f,
                new ConfigDescription("Maximum distance from the killed creature for shared quest credit.",
                    new AcceptableValueRange<float>(1f, 200f)));

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        DisposeWatcher();
        TryShutdown(ClanGroupSharing.ResetSession, "reset group sharing");
        TryShutdown(EpicMmoCompat.Dispose, "dispose Epic MMO compatibility");
        TryShutdown(QuestForgeCompat.Dispose, "dispose QuestForge compatibility");
        TryShutdown(Config.Save, "save configuration");
        TryShutdown(ClanVanillaChatDock.Dispose, "dispose chat dock");
        TryShutdown(ClanPanelController.Dispose, "dispose Clan panel");
        TryShutdown(ClanHud.Dispose, "dispose HUD");
        TryShutdown(ClanUiFeedback.Dispose, "dispose UI feedback");
        TryShutdown(ClanUiFactory.Dispose, "dispose UI resources");
        TryShutdown(ClanApi.Dispose, "dispose API");
        TryShutdown(ClanRecentPlayers.Dispose, "dispose recent-player storage");
        TryShutdown(ClanEmoji.Dispose, "dispose media runtime");
        TryShutdown(ClanMap.ResetSession, "reset map state");
        TryShutdown(ClanLocalization.Dispose, "dispose localization runtime");
        TryShutdown(_harmony.UnpatchSelf, "remove Harmony patches");
    }

    private void DisposeWatcher()
    {
        FileSystemWatcher? watcher = _watcher;
        _watcher = null;
        Config.SettingChanged -= InvalidateConfigSnapshot;
        _configReloadPending = false;
        _lastConfigContent = null;
        if (watcher == null)
        {
            return;
        }

        TryShutdown(
            () => watcher.EnableRaisingEvents = false,
            "stop configuration watcher");
        TryShutdown(watcher.Dispose, "dispose configuration watcher");
    }

    private static void TryShutdown(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ClanLogger.LogWarning($"Failed to {operation} during shutdown: {exception.Message}");
        }
    }

    private void Update()
    {
        ReloadPendingConfig();
        ClanUiFactory.Tick();
        ClanEmoji.Tick();
        ClanRpc.Tick();
        ClanApi.Tick();
        ClanRecentPlayers.Tick();
        ClanPanelController.Tick();
        ClanVanillaChatDock.Tick();
        ClanHud.Tick();
        ClanMap.Tick();
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
        // Both watcher callbacks and ReloadPendingConfig run on the Unity main thread.
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        Config.SettingChanged += InvalidateConfigSnapshot;
        _watcher.EnableRaisingEvents = true;
    }

    private void InvalidateConfigSnapshot(object sender, SettingChangedEventArgs e)
    {
        // An in-game change can be saved and externally reverted before the next
        // watcher tick. Matching old file bytes must not hide that external edit.
        _lastConfigContent = null;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        // A callback already queued by ThreadingHelper can outlive the watcher.
        if (!ReferenceEquals(sender, _watcher))
        {
            return;
        }

        _configReloadPending = true;
        _configReloadFailures = 0;
        _configReloadAfter = DateTime.UtcNow.AddMilliseconds(ConfigReloadDelayMilliseconds);
    }

    private void ReloadPendingConfig()
    {
        if (!_configReloadPending || DateTime.UtcNow < _configReloadAfter)
        {
            return;
        }

        _configReloadPending = false;
        try
        {
            string content = File.ReadAllText(ConfigFileFullPath);
            if (string.Equals(content, _lastConfigContent, StringComparison.Ordinal))
            {
                return;
            }

            bool saveOnSet = Config.SaveOnConfigSet;
            _lastConfigContent = null;
            try
            {
                // Reload still uses BepInEx/ServerSync deserialization and locking.
                // Saving here (including per-entry auto-save) feeds our own watcher.
                Config.SaveOnConfigSet = false;
                Config.Reload();
            }
            finally
            {
                Config.SaveOnConfigSet = saveOnSet;
            }

            // Reload opens the file itself. If an editor wrote between the two
            // reads, no snapshot is known to match the applied settings yet.
            if (!string.Equals(content, File.ReadAllText(ConfigFileFullPath), StringComparison.Ordinal))
            {
                _configReloadPending = true;
                _configReloadAfter = DateTime.UtcNow.AddMilliseconds(ConfigReloadDelayMilliseconds);
                return;
            }

            _lastConfigContent = content;
            _configReloadFailures = 0;
            ClanLogger.LogInfo("Configuration reload complete.");
        }
        catch (IOException ex)
        {
            // Editors may briefly lock or replace the file; retry without needing
            // another filesystem event, but do not retry/log forever on failure.
            if (++_configReloadFailures <= ConfigReloadRetryLimit)
            {
                _configReloadPending = true;
                _configReloadAfter = DateTime.UtcNow.AddMilliseconds(ConfigReloadDelayMilliseconds);
                return;
            }

            ClanLogger.LogError($"Error reloading configuration: {ex.Message}");
        }
        catch (Exception ex)
        {
            ClanLogger.LogError($"Error reloading configuration: {ex.Message}");
        }
    }

    private ConfigEntry<T> ConfigEntry<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            description.Tags);

        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
        return configEntry;
    }

    private ConfigEntry<T> ConfigEntry<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return ConfigEntry(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }
}

internal static class ClanKeyboardExtensions
{
    public static bool IsKeyDown(this KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None &&
               Input.GetKeyDown(shortcut.MainKey) &&
               shortcut.Modifiers.All(Input.GetKey);
    }

    public static bool IsKeyHeld(this KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None &&
               Input.GetKey(shortcut.MainKey) &&
               shortcut.Modifiers.All(Input.GetKey);
    }
}

internal static class ClanToggleExtensions
{
    public static bool IsOn(this ClanPlugin.Toggle value)
    {
        return value == ClanPlugin.Toggle.On;
    }
}
