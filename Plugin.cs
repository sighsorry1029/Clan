using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn;
using ServerSync;
using UnityEngine;

namespace Clan;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(Main.ModGuid)]
[BepInIncompatibility("org.bepinex.plugins.groups")]
[BepInIncompatibility("org.bepinex.plugins.guilds")]
public sealed class ClanPlugin : BaseUnityPlugin
{
    public const string ModName = "Clan";
    public const string ModVersion = "1.0.0";
    public const string Author = "sighsorry";
    public const string ModGUID = $"{Author}.valheim.{ModName}";

    internal static ManualLogSource ClanLogger { get; } = BepInEx.Logging.Logger.CreateLogSource(ModName);

    internal static ConfigEntry<Toggle> ServerConfigLocked = null!;
    internal static ConfigEntry<KeyboardShortcut> ClanPingModifierKey = null!;
    internal static ConfigEntry<Toggle> ShareClanPositions = null!;
    internal static ConfigEntry<Toggle> ClanFriendlyFire = null!;
    internal static ConfigEntry<int> MaxClanMembers = null!;
    internal static ConfigEntry<float> ClanChatDockChannelOffsetX = null!;
    internal static ConfigEntry<float> ClanChatDockChannelOffsetY = null!;
    internal static ConfigEntry<float> ClanChatWindowScale = null!;
    internal static ConfigEntry<KeyboardShortcut> ClanPanelShortcut = null!;
    internal static ConfigEntry<Toggle> ShowWhisperChatButton = null!;
    internal static ConfigEntry<Toggle> ShowResetSpawnButton = null!;
    internal static ConfigEntry<Toggle> ShowDieButton = null!;
    internal static ConfigEntry<ChatSubmitMode> ChatAfterSend = null!;
    internal static ConfigEntry<Toggle> ShowClanHud = null!;
    internal static ConfigEntry<Toggle> ClanHudPlayerListCollapsed = null!;
    internal static ConfigEntry<Vector2> ClanHudPosition = null!;

    private static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };

    private static readonly string ConfigFileName = $"{ModGUID}.cfg";
    private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

    private readonly Harmony _harmony = new(ModGUID);
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastConfigReloadTime;

    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;

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

    private void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        try
        {
            Config.SaveOnConfigSet = false;
            ServerConfigLocked = ConfigEntry("1 - General", "Lock Configuration", Toggle.On, "If on, only server admins can change synced configuration.");
            ConfigSync.AddLockingConfigEntry(ServerConfigLocked);

            ClanPingModifierKey = ConfigEntry(
                "1 - General",
                "Clan Ping Modifier Key",
                new KeyboardShortcut(KeyCode.LeftShift),
                "Modifier key reserved for clan-only pings.",
                synchronizedSetting: false);

            ShareClanPositions = ConfigEntry("1 - General", "Share Clan Positions", Toggle.On, "If on, clan members and guests can share position visibility.");
            ClanFriendlyFire = ConfigEntry("1 - General", "Clan Friendly Fire", Toggle.Off, "If on, clan-connected players can damage each other.");
            MaxClanMembers = ConfigEntry(
                "1 - General",
                "Maximum Clan Players",
                ClanDataRules.MaxMembersPerClan,
                new ConfigDescription(
                    "Maximum combined number of members and guests in a clan. Existing clans above a lowered limit remain valid, but cannot add players.",
                    new AcceptableValueRange<int>(1, ClanDataRules.MaxMembersPerClan)));

            ClanChatDockChannelOffsetX = ConfigEntry("2 - UI", "Clan Chat Dock Channel Offset X", 0f, "Extra X offset for the channel buttons attached to the vanilla chat input.", synchronizedSetting: false);
            ClanChatDockChannelOffsetY = ConfigEntry("2 - UI", "Clan Chat Dock Channel Offset Y", 0f, "Extra Y offset for the channel buttons attached to the vanilla chat input.", synchronizedSetting: false);
            ClanChatWindowScale = ConfigEntry(
                "2 - UI",
                "Clan Chat Window Scale",
                1f,
                new ConfigDescription(
                    "Uniform scale for the vanilla chat window, including text and inline emojis. The upper-left resize handle updates this value.",
                    new AcceptableValueRange<float>(1f, 1.75f)),
                synchronizedSetting: false);
            ClanPanelShortcut = ConfigEntry(
                "2 - UI",
                "Clan Panel Shortcut",
                new KeyboardShortcut(KeyCode.G),
                "Client-only shortcut that opens or closes the Clan panel. Set the main key to None to disable it.",
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
                "If on, keeps the Clan HUD header visible while hiding its player list.",
                synchronizedSetting: false);
            ClanHudPosition = ConfigEntry(
                "2 - UI",
                "Clan HUD Position",
                new Vector2(0.02f, 0.28f),
                "Normalized safe-area position of the Clan HUD's upper-left corner. Drag the Clan header to update it.",
                synchronizedSetting: false);

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }

        ClanEmoji.Init();
        ClanRegistry.Init();
        ClanRecentPlayers.Init();
        ClanApi.Initialize();

        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        ClanVanillaChatDock.Init();
        ClanHud.Init();
        SetupWatcher();

        ClanLogger.LogInfo($"{ModName} {ModVersion} loaded with Jotunn dependency {Main.ModGuid}.");
    }

    private void OnDestroy()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        try
        {
            SaveWithRespectToConfigSet();
        }
        catch (Exception ex)
        {
            ClanLogger.LogWarning($"Failed to save configuration during shutdown: {ex.Message}");
        }
        finally
        {
            ClanVanillaChatDock.Dispose();
            ClanUiFeedback.Dispose();
            ClanApi.Dispose();
            ClanRecentPlayers.Dispose();
            ClanEmoji.Dispose();
            ClanHud.Dispose();
            ClanMap.ResetSession();
            _harmony.UnpatchSelf();
        }
    }

    private void Update()
    {
        ClanEmoji.Tick();
        ClanRpc.Tick();
        ClanApi.Tick();
        ClanRecentPlayers.Tick();
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
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        if (now.Ticks - _lastConfigReloadTime.Ticks < ReloadDelayTicks)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                ClanLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                SaveWithRespectToConfigSet(reload: true);
                ClanLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                ClanLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        try
        {
            Config.SaveOnConfigSet = false;
            if (reload)
            {
                Config.Reload();
            }
            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
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
        return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
    }
}

internal static class ClanToggleExtensions
{
    public static bool IsOn(this ClanPlugin.Toggle value)
    {
        return value == ClanPlugin.Toggle.On;
    }
}
