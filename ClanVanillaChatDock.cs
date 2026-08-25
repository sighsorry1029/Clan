using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Jotunn.Managers;
using Splatform;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Clan;

internal static class ClanVanillaChatDock
{
    private const float EdgeGap = 4f;
    private const float FeatureDockWidth = 66f;
    private const float ChannelButtonHeight = 30f;
    private const float ChannelButtonSpacing = 4f;
    private const float FeatureDockHeight = ChannelButtonHeight + 10f;
    private const float ChannelDockHeight = ChannelButtonHeight + 10f;
    private const float ShoutButtonWidth = 74f;
    private const float ClanChatButtonWidth = 104f;
    private const float WhisperButtonWidth = 86f;
    private const float ResetSpawnButtonWidth = 104f;
    private const float DieButtonWidth = 54f;
    private const int EmojiColumnCount = 5;
    private const int EmojiVisibleRowCount = 5;
    private const int MinimumEmojiVisibleRowCount = 1;
    private const float EmojiCellSize = 52f;
    private const float EmojiCellSpacing = 4f;
    private const float EmojiTrayPadding = 6f;
    private const float EmojiScrollbarWidth = 2f;
    private const float ScrollSensitivity = 196f;
    private const float UnderfilledScrollSensitivity = 35f;
    private const float UnderfilledScrollElasticity = 0.07f;
    private const float ScrollOverflowEpsilon = 0.5f;
    private const float EmojiTrayWidth =
        EmojiTrayPadding * 2f + EmojiCellSize * EmojiColumnCount +
        EmojiCellSpacing * (EmojiColumnCount - 1);
    private const float EmojiTrayHeight =
        EmojiTrayPadding * 2f + EmojiCellSize * EmojiVisibleRowCount +
        EmojiCellSpacing * (EmojiVisibleRowCount - 1);
    private const float ResizeHandleSize = 42f;
    private const float ResizeIconSize = 28f;
    private const float MinimumChatScale = 1f;
    private const float FallbackChatInputWidth = 500f;
    private const float FallbackChatInputHeight = 36f;
    private const float MaximumChatScale = 1.75f;
    private const float ResizeCursorReleaseGrace = 0.2f;
    private const float InactiveChatRectRefreshIntervalSeconds = 0.5f;
    private const float CommandConfirmationSeconds = 3f;
    private const int ChatRefocusTimeoutFrames = 4;
    private const int MaximumTrackedChatLines = 300;
    private const int NotificationSuppressConsole = 1 << 0;
    private const int NotificationSuppressTextInput = 1 << 1;
    private const int NotificationSuppressMapTextInput = 1 << 2;
    private const int NotificationSuppressMap = 1 << 3;
    private const int NotificationSuppressMenu = 1 << 4;
    private const int NotificationSuppressInventory = 1 << 5;
    private const int NotificationSuppressStore = 1 << 6;
    private const int NotificationSuppressPopup = 1 << 7;

    private enum Channel
    {
        Say,
        Shout,
        Whisper,
        Clan
    }

    private enum CommandAction
    {
        None,
        ResetSpawn,
        Die
    }

    private enum ChatViewMode
    {
        All,
        Clan,
        Local,
        Global
    }

    private enum ChatLineKind
    {
        System,
        Clan,
        Local,
        Global
    }

    private enum ChatPlacementSource
    {
        Live,
        Cached,
        Fallback
    }

    private readonly struct ChatLineKindScope
    {
        public ChatLineKindScope(ChatLineKind previous)
        {
            Previous = previous;
        }

        public ChatLineKind Previous { get; }
    }

    private static readonly Dictionary<Channel, Button> ChannelButtons = new();
    private static readonly Vector3[] WorldCornersA = new Vector3[4];
    private static readonly Vector3[] WorldCornersB = new Vector3[4];
    private static readonly Color DockBackgroundColor = new(0f, 0f, 0f, 0.34f);
    private static readonly Color ButtonColor = new(0.78f, 0.78f, 0.78f, 0.96f);
    private static readonly Color ActiveToggleButtonColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color InactiveToggleButtonColor = new(0.5f, 0.5f, 0.5f, 0.95f);
    private static readonly Color DisabledButtonColor = new(0.25f, 0.25f, 0.25f, 0.72f);
    private static readonly Color ButtonHoverColor = new(1f, 0.72f, 0.32f, 1f);
    private static readonly Color ConfirmationButtonColor = new(1f, 0.48f, 0.24f, 1f);
    private static readonly Color NotificationButtonColor = new(1f, 0.64f, 0.2f, 1f);
    private static readonly Color NotificationBadgeColor = new(1f, 0.56f, 0.12f, 1f);
    private static readonly Color InactiveButtonLabelColor = Color.gray;
    private static readonly Color DisabledButtonLabelColor = new(0.4f, 0.4f, 0.4f, 0.9f);

    private static GameObject? _root;
    private static GameObject? _chatChromeRoot;
    private static RectTransform? _rootRect;
    private static RectTransform? _emojiButtonDockRect;
    private static RectTransform? _clanButtonDockRect;
    private static RectTransform? _chatViewDockRect;
    private static RectTransform? _channelDockRect;
    private static RectTransform? _commandDockRect;
    private static RectTransform? _emojiTrayRect;
    private static RectTransform? _resizeHandleRect;
    private static RectTransform? _chatPanelRect;
    private static CanvasGroup? _clanButtonDockCanvasGroup;
    private static GameObject? _clanNotificationBadge;
    private static bool? _clanButtonDockInteractive;
    private static int _lastNotificationDiagnosticMask = int.MinValue;
    private static int _lastNotificationDiagnosticApplications = -1;
    private static int _lastNotificationDiagnosticRootId;
    private static int _lastNotificationDiagnosticDockId;
    private static bool _notificationPositionDiagnosticInitialized;
    private static Vector2 _lastNotificationDiagnosticPosition;
    private static bool _lastNotificationDiagnosticInputVisible;
    private static bool _lastNotificationDiagnosticStandalone;
    private static bool _lastNotificationDiagnosticLeftEdge;
    private static bool _lastNotificationDiagnosticGeometryValid;
    private static int _lastNotificationDiagnosticPanelId;
    private static ChatPlacementSource _lastNotificationDiagnosticPlacementSource;
    private static bool _canResizeChatPanel;
    private static GameObject? _emojiTray;
    private static RectTransform? _emojiContent;
    private static Button? _clanPanelButton;
    private static Button? _emojiTrayButton;
    private static Button? _chatViewButton;
    private static Button? _whisperButton;
    private static Button? _resetSpawnButton;
    private static Button? _dieButton;
    private static Channel _activeChannel = Channel.Say;
    private static bool _emojiTrayOpen;
    private static float _nextAttachAttempt;
    private static RectTransform? _chatScaleTarget;
    private static Vector3 _chatBaseLocalScale = Vector3.one;
    private static float _chatScaleFactor = 1f;
    private static bool _hasCachedChatInputBounds;
    private static Rect _cachedChatInputBounds;
    private static float _cachedChatDockScale = 1f;
    private static int _chatPlacementCacheScreenWidth;
    private static int _chatPlacementCacheScreenHeight;
    private static Rect _chatPlacementCacheSafeArea;
    private static float _chatPlacementCacheConfiguredScale;
    private static float _chatPlacementCacheCanvasScaleFactor;
    private static int _chatPlacementCacheCanvasId;
    private static int _chatPlacementCacheInputId;
    private static float _nextInactiveChatRectRefreshAt;
    private static bool _isChatScaleDragging;
    private static bool _isChatScalePointerActive;
    private static float _keepResizeCursorReleasedUntil;
    private static int _chatRefocusFrame = -1;
    private static int _chatRefocusDeadlineFrame = -1;
    private static int _clanPanelCancelFrame = -1;
    private static Chat? _chatRefocusOwner;
    private static CommandAction _pendingCommand;
    private static float _commandConfirmationExpiresAt;
    private static Chat? _unsupportedChatWarningOwner;
    private static Chat? _trackedChat;
    private static ChatViewMode _chatViewMode = ChatViewMode.All;
    private static readonly List<ChatLineKind> ChatLineKinds = new();
    [ThreadStatic]
    private static ChatLineKind _scopedChatLineKind;
    private static bool _chatHistoryMismatchWarned;
    private static bool _chatFilterUnavailableWarned;
    private static bool _keepChatVisibleForClanPanel;
    private static bool _shortcutSettingSubscribed;
    private static readonly FieldInfo? ChatBufferField =
        AccessTools.Field(typeof(Terminal), "m_chatBuffer");
    private static readonly FieldInfo? ChatScrollHeightField =
        AccessTools.Field(typeof(Terminal), "m_scrollHeight");
    private static readonly FieldInfo? ChatVisibleBufferLengthField =
        AccessTools.Field(typeof(Terminal), "m_maxVisibleBufferLength");
    private static readonly MethodInfo? TerminalUpdateChatMethod =
        AccessTools.Method(typeof(Terminal), "UpdateChat");
    private static AccessTools.FieldRef<Chat, float>? _chatHideTimer =
        CreateChatHideTimerAccessor();

    public static void Init()
    {
        GUIManager.OnCustomGUIAvailable += Rebuild;
        ClanRpc.SnapshotChanged += OnSnapshotChanged;
        ClanRpc.DirectoryChanged += OnDirectoryChanged;
        ClanRpc.StatusReceived += OnStatusReceived;
        ClanRpc.ChatReceived += OnClanChatReceived;
        ClanEmoji.EmojiChanged += OnEmojiChanged;
        ClanEmoji.EmblemsChanged += OnEmblemsChanged;
        ClanPanelController.OpenStateChanged += OnClanPanelOpenStateChanged;
        ClanPlugin.ClanPanelShortcut.SettingChanged += OnClanPanelShortcutChanged;
        _shortcutSettingSubscribed = true;
        Rebuild();
    }

    public static void Dispose()
    {
        GUIManager.OnCustomGUIAvailable -= Rebuild;
        ClanRpc.SnapshotChanged -= OnSnapshotChanged;
        ClanRpc.DirectoryChanged -= OnDirectoryChanged;
        ClanRpc.StatusReceived -= OnStatusReceived;
        ClanRpc.ChatReceived -= OnClanChatReceived;
        ClanEmoji.EmojiChanged -= OnEmojiChanged;
        ClanEmoji.EmblemsChanged -= OnEmblemsChanged;
        ClanPanelController.OpenStateChanged -= OnClanPanelOpenStateChanged;
        if (_shortcutSettingSubscribed)
        {
            ClanPlugin.ClanPanelShortcut.SettingChanged -= OnClanPanelShortcutChanged;
            _shortcutSettingSubscribed = false;
        }
        ReleaseChatScaleTarget(restoreScale: true);
        DestroyRoot();
        ResetChatHistoryTracking(null);
        _clanPanelCancelFrame = -1;
        _unsupportedChatWarningOwner = null;
    }

    public static void Tick()
    {
        if (ZNet.instance == null || Player.m_localPlayer == null || Chat.instance == null)
        {
            if (HasDockState())
            {
                DestroyRoot();
            }
            else
            {
                CancelPendingChatRefocus();
            }
            return;
        }

        Chat chat = Chat.instance;
        if (IsUnsupportedReplacementChat(chat))
        {
            DisableDockForUnsupportedChat(chat);
            return;
        }
        _unsupportedChatWarningOwner = null;

        if (_root == null && Time.time >= _nextAttachAttempt)
        {
            _nextAttachAttempt = Time.time + 1f;
            Rebuild();
        }

        if (_root == null)
        {
            return;
        }

        RefreshConfiguredChannelVisibility();
        RefreshConfiguredCommandVisibility();
        ExpireCommandConfirmation();

        if ((_isChatScalePointerActive || _isChatScaleDragging) &&
            !Input.GetMouseButton(0))
        {
            _isChatScalePointerActive = false;
            _keepResizeCursorReleasedUntil =
                Time.unscaledTime + ResizeCursorReleaseGrace;
            EndChatScaleDrag(save: true);
        }

        TMP_InputField? input = GetInputField();
        if (input == null)
        {
            CancelPendingChatRefocus();
            CloseOverlays();
            _root.SetActive(false);
            return;
        }

        bool handledPanelCancel = TryHandleClanPanelCancel();
        if (!handledPanelCancel && ShouldToggleClanPanelFromShortcut(chat, input))
        {
            ToggleClanPanel(
                keepChatVisibleWhenOpening: IsChatInputOpen(input));
        }

        ApplyPendingChatRefocus(input);

        bool panelOpen = ClanPanelController.IsOpen;
        bool keepChatOpen = panelOpen && _keepChatVisibleForClanPanel;
        if (keepChatOpen)
        {
            KeepChatOpenForClanPanel(input);
        }

        bool inputVisible = IsChatInputOpen(input);
        if (!inputVisible)
        {
            if (panelOpen)
            {
                CloseChatOverlays();
            }
            else
            {
                CloseOverlays();
            }
        }

        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        int notificationSuppressionMask =
            GetStandaloneClanNotificationSuppressionMask();
        bool showStandaloneClanNotification =
            HasActionableClanNotification(snapshot) &&
            !inputVisible &&
            !panelOpen &&
            notificationSuppressionMask == 0;
        bool showRoot = inputVisible || panelOpen || showStandaloneClanNotification;
        _root.SetActive(showRoot);
        if (_chatChromeRoot != null)
        {
            _chatChromeRoot.SetActive(inputVisible);
        }
        SetClanButtonDockVisibility(inputVisible, showStandaloneClanNotification);
        LogStandaloneNotificationStateIfChanged(
            snapshot,
            inputVisible,
            panelOpen,
            notificationSuppressionMask,
            showStandaloneClanNotification);
        if (showRoot)
        {
            if (inputVisible || showStandaloneClanNotification)
            {
                RefreshDockPositions(input);
            }
            else if (_rootRect != null)
            {
                ClanPanelController.RefreshPosition(_rootRect);
            }
            ClanPanelController.Tick();
        }
    }

    private static void Rebuild()
    {
        if (GUIManager.IsHeadless() || Chat.instance == null)
        {
            return;
        }

        Chat chat = Chat.instance;
        if (IsUnsupportedReplacementChat(chat))
        {
            DisableDockForUnsupportedChat(chat);
            return;
        }
        _unsupportedChatWarningOwner = null;
        EnsureTrackedChat(chat);

        TMP_InputField? input = GetInputField(chat);
        if (input == null)
        {
            return;
        }

        DestroyRoot();

        RectTransform inputRect = input.GetComponent<RectTransform>();
        _chatPanelRect = ResolveChatPanelRect(inputRect);
        _canResizeChatPanel = CanResizeDeclaredChatWindow(inputRect, _chatPanelRect);
        RefreshChatScaleTarget(_chatPanelRect, _canResizeChatPanel);
        Transform parent = GetOverlayParent(inputRect);
        _root = ClanUiFactory.CreateObject("ClanVanillaChatDock", parent);
        LayoutElement rootElement = _root.AddComponent<LayoutElement>();
        rootElement.ignoreLayout = true;

        RectTransform rect = _root.GetComponent<RectTransform>();
        _rootRect = rect;
        StretchRoot(rect);
        _root.transform.SetAsLastSibling();

        _chatChromeRoot = ClanUiFactory.CreateObject(
            "ClanVanillaChatChrome",
            _root.transform);
        StretchRoot(_chatChromeRoot.GetComponent<RectTransform>());

        BuildChannelDock(_chatChromeRoot.transform, _root.transform);
        BuildCommandDock(_chatChromeRoot.transform);
        BuildEmojiTray(_chatChromeRoot.transform);
        ClanPanelController.Build(_root.transform, rect);
        BuildResizeHandle(_chatChromeRoot.transform);
        ApplyClanDockScale();
        RefreshDockPositions(input);

        RefreshUi(ClanRpc.CurrentSnapshot);
        ClanPanelController.RefreshDirectory(ClanRpc.CurrentDirectory);
        bool inputVisible = IsChatInputOpen(input);
        bool panelOpen = ClanPanelController.IsOpen;
        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        int notificationSuppressionMask =
            GetStandaloneClanNotificationSuppressionMask();
        bool showStandaloneClanNotification =
            HasActionableClanNotification(snapshot) &&
            !inputVisible &&
            !panelOpen &&
            notificationSuppressionMask == 0;
        _chatChromeRoot.SetActive(inputVisible);
        SetClanButtonDockVisibility(inputVisible, showStandaloneClanNotification);
        _root.SetActive(inputVisible || panelOpen || showStandaloneClanNotification);
        LogChatDockLifecycle("rebuild", input);
        LogStandaloneNotificationStateIfChanged(
            snapshot,
            inputVisible,
            panelOpen,
            notificationSuppressionMask,
            showStandaloneClanNotification);
    }

    private static bool HasDockState()
    {
        return _root != null ||
               _chatChromeRoot != null ||
               ChannelButtons.Count > 0 ||
               _emojiButtonDockRect != null ||
               _clanButtonDockRect != null ||
               _clanButtonDockCanvasGroup != null ||
               _clanNotificationBadge != null ||
               _clanButtonDockInteractive != null ||
               _chatViewDockRect != null ||
               _commandDockRect != null ||
               _clanPanelButton != null ||
               _emojiTrayButton != null ||
               _chatViewButton != null ||
               _whisperButton != null ||
               _resetSpawnButton != null ||
               _dieButton != null ||
               _emojiTrayOpen ||
               _canResizeChatPanel ||
               _isChatScaleDragging ||
               _isChatScalePointerActive ||
               ClanPanelController.IsOpen;
    }

    private static void DestroyRoot()
    {
        if (_isChatScaleDragging)
        {
            EndChatScaleDrag(save: true);
        }

        if (_root != null)
        {
            LogChatDockLifecycle("destroy", GetInputField());
        }

        ClearClanButtonDockSelection();

        GameObject? root = _root;
        _root = null;
        _chatChromeRoot = null;
        _keepChatVisibleForClanPanel = false;
        if (root != null)
        {
            root.SetActive(false);
        }

        _emojiTrayOpen = false;
        _rootRect = null;
        _emojiButtonDockRect = null;
        _clanButtonDockRect = null;
        _chatViewDockRect = null;
        _channelDockRect = null;
        _commandDockRect = null;
        _emojiTrayRect = null;
        _resizeHandleRect = null;
        _chatPanelRect = null;
        _clanButtonDockCanvasGroup = null;
        _clanNotificationBadge = null;
        _clanButtonDockInteractive = null;
        _emojiTray = null;
        _emojiContent = null;
        _clanPanelButton = null;
        _emojiTrayButton = null;
        _chatViewButton = null;
        _whisperButton = null;
        _resetSpawnButton = null;
        _dieButton = null;
        _pendingCommand = CommandAction.None;
        _commandConfirmationExpiresAt = 0f;
        _canResizeChatPanel = false;
        _isChatScaleDragging = false;
        _isChatScalePointerActive = false;
        _keepResizeCursorReleasedUntil = 0f;
        CancelPendingChatRefocus();
        ChannelButtons.Clear();
        ClanPanelController.DestroyView();

        if (root != null)
        {
            UnityEngine.Object.Destroy(root);
        }
        ResetStandaloneNotificationDiagnostics();
    }

    private static void CloseOverlays()
    {
        CloseEmojiTray();
        ClanPanelController.Close();
        CancelCommandConfirmation();
        RefreshButtonStates();
    }

    private static void CloseChatOverlays()
    {
        bool emojiWasOpen = _emojiTrayOpen;
        CloseEmojiTray();
        CancelCommandConfirmation();
        if (emojiWasOpen)
        {
            RefreshButtonStates();
        }
    }

    private static void CloseEmojiTray()
    {
        _emojiTrayOpen = false;
        if (_emojiTray != null)
        {
            _emojiTray.SetActive(false);
        }
    }

    private static KeyCode FilterChatMouseKeyCode(KeyCode keyCode)
    {
        if (IsPointerOverDock() &&
            (keyCode == KeyCode.Mouse0 || keyCode == KeyCode.Mouse1))
        {
            return KeyCode.None;
        }

        return keyCode;
    }

    private static bool IsPointerOverDock()
    {
        if (!SupportsCurrentChatUi)
        {
            return false;
        }

        Vector2 pointerPosition = Input.mousePosition;
        return ContainsPointer(_emojiButtonDockRect, pointerPosition) ||
               (_clanPanelButton != null &&
                _clanPanelButton.interactable &&
                ContainsPointer(_clanButtonDockRect, pointerPosition)) ||
               ContainsPointer(_chatViewDockRect, pointerPosition) ||
               ContainsPointer(_channelDockRect, pointerPosition) ||
               ContainsPointer(_commandDockRect, pointerPosition) ||
               ContainsPointer(_emojiTrayRect, pointerPosition) ||
               ClanPanelController.ContainsPointer(pointerPosition) ||
               ClanHud.CapturesChatPointer(pointerPosition) ||
               ContainsPointer(_resizeHandleRect, pointerPosition);
    }

    private static bool IsChatInputOpen(TMP_InputField input)
    {
        return input.gameObject.activeInHierarchy &&
               input.isActiveAndEnabled &&
               input.interactable;
    }

    private static bool HasActionableClanNotification(ClanClientSnapshot snapshot)
    {
        return snapshot.Invite != null ||
               (snapshot.CanModerate && snapshot.Applications.Count > 0);
    }

    private static int GetStandaloneClanNotificationSuppressionMask()
    {
        int mask = 0;
        if (Console.IsVisible())
        {
            mask |= NotificationSuppressConsole;
        }
        if (TextInput.IsVisible())
        {
            mask |= NotificationSuppressTextInput;
        }
        if (Minimap.InTextInput())
        {
            mask |= NotificationSuppressMapTextInput;
        }
        if (Minimap.IsOpen())
        {
            mask |= NotificationSuppressMap;
        }
        if (Menu.IsVisible())
        {
            mask |= NotificationSuppressMenu;
        }
        if (InventoryGui.IsVisible())
        {
            mask |= NotificationSuppressInventory;
        }
        if (StoreGui.IsVisible())
        {
            mask |= NotificationSuppressStore;
        }
        if (UnifiedPopup.IsVisible())
        {
            mask |= NotificationSuppressPopup;
        }
        return mask;
    }

    private static void LogStandaloneNotificationStateIfChanged(
        ClanClientSnapshot snapshot,
        bool inputVisible,
        bool panelOpen,
        int suppressionMask,
        bool showStandalone)
    {
        bool actionable = HasActionableClanNotification(snapshot);
        bool hasInvite = snapshot.Invite != null;
        bool hasRoot = _root != null;
        bool rootActiveSelf = hasRoot && _root!.activeSelf;
        bool rootActiveInHierarchy = hasRoot && _root!.activeInHierarchy;
        bool hasDock = _clanButtonDockRect != null;
        bool dockActiveSelf = hasDock && _clanButtonDockRect!.gameObject.activeSelf;
        bool dockActiveInHierarchy = hasDock &&
                                     _clanButtonDockRect!.gameObject.activeInHierarchy;
        bool hasBadge = _clanNotificationBadge != null;
        bool badgeActiveSelf = hasBadge && _clanNotificationBadge!.activeSelf;
        bool badgeActiveInHierarchy = hasBadge &&
                                      _clanNotificationBadge!.activeInHierarchy;
        int mask =
            (actionable ? 1 << 0 : 0) |
            (hasInvite ? 1 << 1 : 0) |
            (snapshot.CanModerate ? 1 << 2 : 0) |
            (inputVisible ? 1 << 3 : 0) |
            (panelOpen ? 1 << 4 : 0) |
            (showStandalone ? 1 << 5 : 0) |
            (hasRoot ? 1 << 6 : 0) |
            (rootActiveSelf ? 1 << 7 : 0) |
            (rootActiveInHierarchy ? 1 << 8 : 0) |
            (hasDock ? 1 << 9 : 0) |
            (dockActiveSelf ? 1 << 10 : 0) |
            (dockActiveInHierarchy ? 1 << 11 : 0) |
            (hasBadge ? 1 << 12 : 0) |
            (badgeActiveSelf ? 1 << 13 : 0) |
            (badgeActiveInHierarchy ? 1 << 14 : 0) |
            (suppressionMask << 16);
        int rootId = hasRoot ? _root!.GetInstanceID() : 0;
        int dockId = hasDock ? _clanButtonDockRect!.GetInstanceID() : 0;
        bool changed =
            mask != _lastNotificationDiagnosticMask ||
            snapshot.Applications.Count != _lastNotificationDiagnosticApplications ||
            rootId != _lastNotificationDiagnosticRootId ||
            dockId != _lastNotificationDiagnosticDockId;
        if (!changed)
        {
            return;
        }

        bool previousActionable =
            _lastNotificationDiagnosticMask != int.MinValue &&
            (_lastNotificationDiagnosticMask & (1 << 0)) != 0;
        _lastNotificationDiagnosticMask = mask;
        _lastNotificationDiagnosticApplications = snapshot.Applications.Count;
        _lastNotificationDiagnosticRootId = rootId;
        _lastNotificationDiagnosticDockId = dockId;
        if (!actionable && !previousActionable)
        {
            _notificationPositionDiagnosticInitialized = false;
            return;
        }

        if (!actionable)
        {
            _notificationPositionDiagnosticInitialized = false;
        }

        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Clan alert state frame={Time.frameCount}; " +
            $"actionable={actionable}; invite={hasInvite}; " +
            $"canModerate={snapshot.CanModerate}; " +
            $"applications={snapshot.Applications.Count}; " +
            $"inputVisible={inputVisible}; panelOpen={panelOpen}; " +
            $"showStandalone={showStandalone}; suppression={suppressionMask}; " +
            $"console={(suppressionMask & NotificationSuppressConsole) != 0}; " +
            $"textInput={(suppressionMask & NotificationSuppressTextInput) != 0}; " +
            $"mapText={(suppressionMask & NotificationSuppressMapTextInput) != 0}; " +
            $"map={(suppressionMask & NotificationSuppressMap) != 0}; " +
            $"menu={(suppressionMask & NotificationSuppressMenu) != 0}; " +
            $"inventory={(suppressionMask & NotificationSuppressInventory) != 0}; " +
            $"store={(suppressionMask & NotificationSuppressStore) != 0}; " +
            $"popup={(suppressionMask & NotificationSuppressPopup) != 0}; " +
            $"rootId={rootId}; rootActiveSelf={rootActiveSelf}; " +
            $"rootActiveHierarchy={rootActiveInHierarchy}; " +
            $"dockId={dockId}; dockActiveSelf={dockActiveSelf}; " +
            $"dockActiveHierarchy={dockActiveInHierarchy}; " +
            $"badgeActiveSelf={badgeActiveSelf}; " +
            $"badgeActiveHierarchy={badgeActiveInHierarchy}.");
    }

    private static void SetClanButtonDockVisibility(
        bool chatInputVisible,
        bool showStandaloneNotification)
    {
        if (_clanButtonDockRect == null)
        {
            return;
        }

        bool visible = chatInputVisible || showStandaloneNotification;
        _clanButtonDockRect.gameObject.SetActive(visible);
        if (_clanButtonDockCanvasGroup != null)
        {
            _clanButtonDockCanvasGroup.alpha = 1f;
            _clanButtonDockCanvasGroup.interactable = chatInputVisible;
            _clanButtonDockCanvasGroup.blocksRaycasts = chatInputVisible;
            _clanButtonDockCanvasGroup.ignoreParentGroups = false;
        }
        if (_clanPanelButton != null)
        {
            _clanPanelButton.interactable = chatInputVisible;
        }

        if (_clanButtonDockInteractive == chatInputVisible)
        {
            return;
        }

        _clanButtonDockInteractive = chatInputVisible;
        if (chatInputVisible)
        {
            RefreshClanPanelTooltip();
        }
        else
        {
            if (_clanPanelButton != null)
            {
                ClanUiFeedback.SetTooltip(_clanPanelButton, "");
            }
            ClearClanButtonDockSelection();
        }
    }

    private static void ClearClanButtonDockSelection()
    {
        EventSystem? eventSystem = EventSystem.current;
        GameObject? selected = eventSystem?.currentSelectedGameObject;
        if (eventSystem != null &&
            selected != null &&
            _clanButtonDockRect != null &&
            (selected == _clanButtonDockRect.gameObject ||
             selected.transform.IsChildOf(_clanButtonDockRect)))
        {
            eventSystem.SetSelectedGameObject(null);
        }
    }

    private static bool ShouldReleaseMouseCursor()
    {
        if (!SupportsCurrentChatUi || !ZInput.IsMouseActive())
        {
            return false;
        }

        Chat? chat = Chat.instance;
        if (chat != null && HasPendingChatRefocus(chat))
        {
            return true;
        }

        if (ClanHud.CapturesChatPointer(Input.mousePosition))
        {
            return true;
        }

        if (_root == null || !_root.activeInHierarchy)
        {
            return false;
        }

        if (ClanPanelController.CapturesGameplayInput)
        {
            return true;
        }

        TMP_InputField? input = GetInputField();
        return input != null &&
               IsChatInputOpen(input) &&
               (input.isFocused ||
                 _emojiTrayOpen ||
                 _isChatScalePointerActive ||
                 Time.unscaledTime < _keepResizeCursorReleasedUntil);
    }

    private static void KeepChatOpenForClanPanel(TMP_InputField input)
    {
        Chat? chat = Chat.instance;
        if (chat == null)
        {
            return;
        }

        EnsureChatVisible(chat, input);
    }

    private static bool ContainsPointer(RectTransform? rect, Vector2 pointerPosition)
    {
        return rect != null &&
               rect.gameObject.activeInHierarchy &&
               RectTransformUtility.RectangleContainsScreenPoint(
                   rect,
                   pointerPosition,
                   null);
    }

    private static Transform GetOverlayParent(RectTransform inputRect)
    {
        if (GUIManager.CustomGUIFront != null)
        {
            return GUIManager.CustomGUIFront.transform;
        }

        Canvas? canvas = inputRect.GetComponentInParent<Canvas>(true);
        return canvas != null ? canvas.transform : inputRect.root;
    }

    private static RectTransform? ResolveChatPanelRect(RectTransform inputRect)
    {
        return Chat.instance == null
            ? null
            : ResolveDeclaredChatContentRoot(
                inputRect,
                ((Terminal)Chat.instance).m_chatWindow);
    }

    private static bool CanResizeDeclaredChatWindow(
        RectTransform inputRect,
        RectTransform? target)
    {
        if (Chat.instance == null || target == null)
        {
            return false;
        }

        RectTransform? declaredWindow = ((Terminal)Chat.instance).m_chatWindow;
        RectTransform? contentRoot = ResolveDeclaredChatContentRoot(
            inputRect,
            declaredWindow);
        return contentRoot == target &&
               declaredWindow != null;
    }

    private static RectTransform? ResolveDeclaredChatContentRoot(
        RectTransform inputRect,
        RectTransform? declaredWindow)
    {
        if (declaredWindow == null ||
            (inputRect != declaredWindow &&
             !inputRect.transform.IsChildOf(declaredWindow.transform)))
        {
            return null;
        }

        Component? output = ((Terminal)Chat.instance).m_output as Component;
        RectTransform? outputRect = output?.GetComponent<RectTransform>();
        if (outputRect == null ||
            (outputRect != declaredWindow &&
             !outputRect.transform.IsChildOf(declaredWindow.transform)))
        {
            return null;
        }

        for (Transform? current = inputRect.transform;
             current != null;
             current = current.parent)
        {
            if (current is RectTransform candidate &&
                (outputRect == candidate ||
                 outputRect.transform.IsChildOf(candidate.transform)))
            {
                return candidate;
            }

            if (current == declaredWindow.transform)
            {
                break;
            }
        }

        return null;
    }

    private static void StretchRoot(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void BuildChannelDock(Transform parent, Transform persistentParent)
    {
        _emojiButtonDockRect = BuildRailButtonDock(
            parent,
            "ClanDockEmoji",
            "Emoji",
            ToggleEmojiTray,
            out _emojiTrayButton);
        _clanButtonDockRect = BuildRailButtonDock(
            persistentParent,
            "ClanDockClan",
            "Clan",
            ToggleClanPanelFromDock,
            out _clanPanelButton);
        _clanButtonDockCanvasGroup =
            _clanButtonDockRect.gameObject.AddComponent<CanvasGroup>();
        BuildClanNotificationBadge(_clanButtonDockRect);
        RefreshClanPanelTooltip();
        _chatViewDockRect = BuildRailButtonDock(
            parent,
            "ClanDockViewFilter",
            GetChatViewLabel(),
            CycleChatViewMode,
            out _chatViewButton);
        RefreshChatViewButton(refreshTooltip: true);

        GameObject dock = ClanUiFactory.CreateObject(
            "ClanDockChannels",
            parent,
            typeof(Image),
            typeof(HorizontalLayoutGroup),
            typeof(ContentSizeFitter),
            typeof(DockPointerFocusKeeper));
        RectTransform rect = dock.GetComponent<RectTransform>();
        _channelDockRect = rect;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(ShoutButtonWidth + ClanChatButtonWidth + 18f, ChannelDockHeight);

        dock.GetComponent<Image>().color = DockBackgroundColor;
        HorizontalLayoutGroup layout = dock.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(5, 5, 5, 5);
        layout.spacing = ChannelButtonSpacing;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = dock.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        AddChannelButton(dock.transform, Channel.Shout, "Shout", ShoutButtonWidth);
        AddChannelButton(dock.transform, Channel.Clan, "Clan chat", ClanChatButtonWidth);
        _whisperButton = AddChannelButton(
            dock.transform,
            Channel.Whisper,
            "Whisper",
            WhisperButtonWidth);
        _whisperButton.gameObject.SetActive(ClanPlugin.ShowWhisperChatButton.Value.IsOn());
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
    }

    private static void BuildClanNotificationBadge(Transform parent)
    {
        Text badge = CreateLabel(parent, "!", 18, TextAnchor.MiddleCenter);
        badge.name = "ClanNotificationBadge";
        badge.color = NotificationBadgeColor;
        badge.raycastTarget = false;
        RectTransform rect = badge.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(5f, 0f);
        rect.sizeDelta = new Vector2(12f, 24f);

        LayoutElement element = badge.gameObject.AddComponent<LayoutElement>();
        element.ignoreLayout = true;
        _clanNotificationBadge = badge.gameObject;
        _clanNotificationBadge.SetActive(false);
    }

    private static RectTransform BuildRailButtonDock(
        Transform parent,
        string name,
        string label,
        Action onClick,
        out Button button)
    {
        GameObject dock = ClanUiFactory.CreateObject(
            name,
            parent,
            typeof(Image),
            typeof(VerticalLayoutGroup),
            typeof(DockPointerFocusKeeper));
        RectTransform rect = dock.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(FeatureDockWidth, FeatureDockHeight);
        dock.GetComponent<Image>().color = DockBackgroundColor;

        VerticalLayoutGroup layout = dock.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(3, 3, 5, 5);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        button = AddRailButton(dock.transform, label, onClick);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        return rect;
    }

    private static void BuildCommandDock(Transform parent)
    {
        GameObject dock = ClanUiFactory.CreateObject(
            "ClanDockCommands",
            parent,
            typeof(Image),
            typeof(HorizontalLayoutGroup),
            typeof(ContentSizeFitter),
            typeof(DockPointerFocusKeeper));
        _commandDockRect = dock.GetComponent<RectTransform>();
        _commandDockRect.anchorMin = new Vector2(0.5f, 0.5f);
        _commandDockRect.anchorMax = new Vector2(0.5f, 0.5f);
        _commandDockRect.sizeDelta = new Vector2(
            ResetSpawnButtonWidth + DieButtonWidth + 18f,
            ChannelDockHeight);

        dock.GetComponent<Image>().color = DockBackgroundColor;
        HorizontalLayoutGroup layout = dock.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(5, 5, 5, 5);
        layout.spacing = ChannelButtonSpacing;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = dock.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        _resetSpawnButton = AddCommandButton(
            dock.transform,
            "Reset spawn",
            CommandAction.ResetSpawn,
            ResetSpawnButtonWidth);
        _dieButton = AddCommandButton(
            dock.transform,
            "Die",
            CommandAction.Die,
            DieButtonWidth);
        RefreshConfiguredCommandVisibility();
        RefreshCommandButtonStates();
        if (dock.activeSelf)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_commandDockRect);
        }
    }

    private static void PlaceChannelDock(
        RectTransform channelRect,
        RectTransform rootRect,
        RectTransform inputRect,
        bool railPositioned,
        bool railOnLeft,
        float railHorizontalPoint)
    {
        Rect chatBounds = GetChatVisualScreenBounds(_chatPanelRect ?? inputRect);
        Rect channelBounds = GetScreenBounds(channelRect);
        Rect safeArea = GetSafeArea();
        float visualWidth = channelBounds.width;
        float visualHeight = channelBounds.height;
        bool placeBelow = chatBounds.yMin - safeArea.yMin >= visualHeight + EdgeGap;
        float maximumLeft = Mathf.Max(safeArea.xMin, safeArea.xMax - visualWidth);
        RectTransform? viewFilter = _chatViewDockRect;
        Rect viewFilterBounds = viewFilter == null
            ? default
            : GetScreenBounds(viewFilter);
        float left = railPositioned && viewFilter != null
            ? railHorizontalPoint +
              (railOnLeft ? EdgeGap : viewFilterBounds.width + EdgeGap)
            : chatBounds.xMin;
        left = Mathf.Clamp(left, safeArea.xMin, maximumLeft);
        float verticalPoint = placeBelow
            ? Mathf.Clamp(
                chatBounds.yMin - EdgeGap,
                safeArea.yMin + visualHeight,
                safeArea.yMax)
            : Mathf.Clamp(
                chatBounds.yMin + EdgeGap,
                safeArea.yMin,
                Mathf.Max(safeArea.yMin, safeArea.yMax - visualHeight));
        channelRect.pivot = new Vector2(0f, placeBelow ? 1f : 0f);
        PlaceAtScreenPoint(
            channelRect,
            rootRect,
            new Vector2(left, verticalPoint));

        if (viewFilter == null)
        {
            return;
        }

        bool placeViewOnLeft = railPositioned
            ? railOnLeft
            : chatBounds.xMin - safeArea.xMin >=
              viewFilterBounds.width + EdgeGap;
        float viewHorizontalPoint = railPositioned
            ? railHorizontalPoint
            : placeViewOnLeft
                ? Mathf.Clamp(
                    chatBounds.xMin - EdgeGap,
                    safeArea.xMin + viewFilterBounds.width,
                    safeArea.xMax)
                : Mathf.Clamp(
                    chatBounds.xMax + EdgeGap,
                    safeArea.xMin,
                    Mathf.Max(
                        safeArea.xMin,
                        safeArea.xMax - viewFilterBounds.width));
        viewFilter.pivot = new Vector2(
            placeViewOnLeft ? 1f : 0f,
            placeBelow ? 1f : 0f);
        PlaceAtScreenPoint(
            viewFilter,
            rootRect,
            new Vector2(viewHorizontalPoint, verticalPoint));
    }

    private static void PlaceCommandDock(
        RectTransform commandRect,
        RectTransform rootRect,
        RectTransform inputRect)
    {
        if (!commandRect.gameObject.activeSelf)
        {
            return;
        }

        Rect chatBounds = GetChatVisualScreenBounds(_chatPanelRect ?? inputRect);
        Rect commandBounds = GetScreenBounds(commandRect);
        Rect safeArea = GetSafeArea();
        float visualWidth = commandBounds.width;
        float visualHeight = commandBounds.height;
        bool placeBelow = chatBounds.yMin - safeArea.yMin >= visualHeight + EdgeGap;
        float minimumRight = Mathf.Min(safeArea.xMax, safeArea.xMin + visualWidth);
        float right = Mathf.Clamp(chatBounds.xMax, minimumRight, safeArea.xMax);
        float verticalPoint = placeBelow
            ? Mathf.Clamp(
                chatBounds.yMin - EdgeGap,
                safeArea.yMin + visualHeight,
                safeArea.yMax)
            : Mathf.Clamp(
                chatBounds.yMin + EdgeGap,
                safeArea.yMin,
                Mathf.Max(safeArea.yMin, safeArea.yMax - visualHeight));
        commandRect.pivot = new Vector2(1f, placeBelow ? 1f : 0f);
        PlaceAtScreenPoint(
            commandRect,
            rootRect,
            new Vector2(right, verticalPoint));
    }

    private static bool PlaceLeftRailStack(
        RectTransform rootRect,
        RectTransform inputRect,
        Rect inputBounds,
        out bool placeLeft,
        out float horizontalPoint)
    {
        placeLeft = true;
        horizontalPoint = 0f;
        RectTransform? emojiDock = _emojiButtonDockRect;
        RectTransform? clanDock = _clanButtonDockRect;
        RectTransform? tray = _emojiTrayRect;
        RectTransform? resize = _resizeHandleRect;
        if (emojiDock == null || clanDock == null || tray == null || resize == null)
        {
            return false;
        }

        bool showResize = _chatPanelRect != null && _canResizeChatPanel;
        resize.gameObject.SetActive(showResize);
        SetEmojiTrayVisibleRows(EmojiVisibleRowCount);

        Rect safeArea = GetSafeArea();
        Rect emojiBounds = GetScreenBounds(emojiDock);
        Rect clanBounds = GetScreenBounds(clanDock);
        Rect trayBounds = GetScreenBounds(tray);
        Rect resizeBounds = showResize ? GetScreenBounds(resize) : default;
        float desiredBottom = inputBounds.yMin;
        if (_chatViewDockRect != null && _channelDockRect != null)
        {
            Rect measuredChatBounds =
                GetChatVisualScreenBounds(_chatPanelRect ?? inputRect);
            Rect chatBounds = IsUsableChatPlacementBounds(
                measuredChatBounds,
                safeArea)
                ? measuredChatBounds
                : inputBounds;
            Rect channelBounds = GetScreenBounds(_channelDockRect);
            Rect viewFilterBounds = GetScreenBounds(_chatViewDockRect);
            bool channelBelow = chatBounds.yMin - safeArea.yMin >=
                                channelBounds.height + EdgeGap;
            if (!channelBelow)
            {
                float bottomRowHeight = Mathf.Max(
                    channelBounds.height,
                    viewFilterBounds.height);
                float bottomRowBottom = Mathf.Clamp(
                    chatBounds.yMin + EdgeGap,
                    safeArea.yMin,
                    Mathf.Max(
                        safeArea.yMin,
                        safeArea.yMax - bottomRowHeight));
                desiredBottom = Mathf.Max(
                    desiredBottom,
                    bottomRowBottom + bottomRowHeight + EdgeGap);
            }
        }

        float fixedHeight = emojiBounds.height + clanBounds.height + EdgeGap * 2f;
        if (showResize)
        {
            fixedHeight += resizeBounds.height + EdgeGap;
        }

        int visibleRows = EmojiVisibleRowCount;
        float availableHeight = Mathf.Max(
            0f,
            safeArea.yMax - Mathf.Max(safeArea.yMin, desiredBottom));
        while (visibleRows > MinimumEmojiVisibleRowCount &&
               fixedHeight + trayBounds.height > availableHeight)
        {
            visibleRows--;
            SetEmojiTrayVisibleRows(visibleRows);
            trayBounds = GetScreenBounds(tray);
        }
        RefreshEmojiScrollBehavior();

        float totalHeight = fixedHeight + trayBounds.height;
        float stackWidth = Mathf.Max(
            Mathf.Max(emojiBounds.width, clanBounds.width),
            trayBounds.width);
        if (showResize)
        {
            stackWidth = Mathf.Max(stackWidth, resizeBounds.width);
        }

        placeLeft = inputBounds.xMin - safeArea.xMin >= stackWidth + EdgeGap;
        float reservedWidth = stackWidth;
        if (!placeLeft &&
            _chatViewDockRect != null &&
            _channelDockRect != null)
        {
            reservedWidth = Mathf.Max(
                reservedWidth,
                GetScreenBounds(_chatViewDockRect).width +
                EdgeGap +
                GetScreenBounds(_channelDockRect).width);
        }

        horizontalPoint = placeLeft
            ? Mathf.Clamp(
                inputBounds.xMin - EdgeGap,
                safeArea.xMin + stackWidth,
                safeArea.xMax)
            : Mathf.Clamp(
                inputBounds.xMin + EdgeGap,
                safeArea.xMin,
                Mathf.Max(safeArea.xMin, safeArea.xMax - reservedWidth));
        float bottom = Mathf.Clamp(
            desiredBottom,
            safeArea.yMin,
            Mathf.Max(safeArea.yMin, safeArea.yMax - totalHeight));

        PlaceLeftRailElement(
            emojiDock,
            rootRect,
            placeLeft,
            horizontalPoint,
            bottom);
        bottom += emojiBounds.height + EdgeGap;
        PlaceLeftRailElement(
            tray,
            rootRect,
            placeLeft,
            horizontalPoint,
            bottom);
        bottom += trayBounds.height + EdgeGap;
        PlaceLeftRailElement(
            clanDock,
            rootRect,
            placeLeft,
            horizontalPoint,
            bottom);

        if (showResize)
        {
            bottom += clanBounds.height + EdgeGap;
            PlaceLeftRailElement(
                resize,
                rootRect,
                placeLeft,
                horizontalPoint,
                bottom);
        }

        return true;
    }

    private static void PlaceLeftRailElement(
        RectTransform rect,
        RectTransform rootRect,
        bool placeLeft,
        float horizontalPoint,
        float bottom)
    {
        rect.pivot = new Vector2(placeLeft ? 1f : 0f, 0f);
        PlaceAtScreenPoint(
            rect,
            rootRect,
            new Vector2(horizontalPoint, bottom));
    }

    private static void SetEmojiTrayVisibleRows(int rowCount)
    {
        if (_emojiTrayRect == null)
        {
            return;
        }

        int rows = Mathf.Clamp(
            rowCount,
            MinimumEmojiVisibleRowCount,
            EmojiVisibleRowCount);
        float height = EmojiTrayPadding * 2f +
                       EmojiCellSize * rows +
                       EmojiCellSpacing * (rows - 1);
        _emojiTrayRect.sizeDelta = new Vector2(EmojiTrayWidth, height);
    }

    private static void PlaceAtScreenPoint(
        RectTransform rect,
        RectTransform rootRect,
        Vector2 screenPoint)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rootRect,
            screenPoint,
            null,
            out Vector2 localPoint);
        rect.anchoredPosition = localPoint + new Vector2(
            ClanPlugin.ClanChatDockChannelOffsetX.Value,
            ClanPlugin.ClanChatDockChannelOffsetY.Value);
    }

    private static void BuildEmojiTray(Transform parent)
    {
        _emojiTray = ClanUiFactory.CreateObject(
            "ClanEmojiTray",
            parent,
            typeof(Image),
            typeof(ScrollRect),
            typeof(DockPointerFocusKeeper));
        _emojiTrayRect = _emojiTray.GetComponent<RectTransform>();
        _emojiTrayRect.anchorMin = new Vector2(0.5f, 0.5f);
        _emojiTrayRect.anchorMax = new Vector2(0.5f, 0.5f);
        _emojiTrayRect.sizeDelta = new Vector2(EmojiTrayWidth, EmojiTrayHeight);
        _emojiTray.GetComponent<Image>().color = DockBackgroundColor;

        GameObject viewport = ClanUiFactory.CreateObject(
            "Viewport",
            _emojiTray.transform,
            typeof(Image),
            typeof(Mask));
        RectTransform viewportRect = viewport.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(EmojiTrayPadding, EmojiTrayPadding);
        viewportRect.offsetMax = new Vector2(-EmojiTrayPadding, -EmojiTrayPadding);
        Image viewportImage = viewport.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
        viewportImage.raycastTarget = false;
        viewport.GetComponent<Mask>().showMaskGraphic = false;

        GameObject content = ClanUiFactory.CreateObject(
            "Content",
            viewport.transform,
            typeof(GridLayoutGroup),
            typeof(ContentSizeFitter));
        _emojiContent = content.GetComponent<RectTransform>();
        _emojiContent.anchorMin = new Vector2(0f, 1f);
        _emojiContent.anchorMax = new Vector2(1f, 1f);
        _emojiContent.pivot = new Vector2(0.5f, 1f);
        _emojiContent.anchoredPosition = Vector2.zero;
        _emojiContent.sizeDelta = Vector2.zero;

        GridLayoutGroup grid = content.GetComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(EmojiCellSize, EmojiCellSize);
        grid.spacing = new Vector2(EmojiCellSpacing, EmojiCellSpacing);
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = EmojiColumnCount;
        ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scrollRect = _emojiTray.GetComponent<ScrollRect>();
        scrollRect.content = _emojiContent;
        scrollRect.viewport = viewportRect;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = ScrollSensitivity;

        GameObject scrollbarObject = ClanUiFactory.CreateObject(
            "Scrollbar",
            _emojiTray.transform,
            typeof(Image),
            typeof(Scrollbar));
        RectTransform scrollbarRect = scrollbarObject.GetComponent<RectTransform>();
        scrollbarRect.anchorMin = new Vector2(0f, 0f);
        scrollbarRect.anchorMax = new Vector2(0f, 1f);
        scrollbarRect.offsetMin = new Vector2(0f, EmojiTrayPadding);
        scrollbarRect.offsetMax = new Vector2(
            EmojiScrollbarWidth,
            -EmojiTrayPadding);
        Image scrollbarTrack = scrollbarObject.GetComponent<Image>();
        scrollbarTrack.color = new Color(0f, 0f, 0f, 0.42f);

        GameObject handleObject = ClanUiFactory.CreateObject(
            "Handle",
            scrollbarObject.transform,
            typeof(Image));
        RectTransform handleRect = handleObject.GetComponent<RectTransform>();
        handleRect.anchorMin = Vector2.zero;
        handleRect.anchorMax = Vector2.one;
        handleRect.offsetMin = Vector2.zero;
        handleRect.offsetMax = Vector2.zero;
        Image handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(1f, 0.63f, 0.24f, 0.96f);

        Scrollbar scrollbar = scrollbarObject.GetComponent<Scrollbar>();
        scrollbar.handleRect = handleRect;
        scrollbar.targetGraphic = handleImage;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.transition = Selectable.Transition.None;
        scrollbar.value = 1f;
        scrollRect.verticalScrollbar = scrollbar;
        scrollRect.verticalScrollbarVisibility =
            ScrollRect.ScrollbarVisibility.Permanent;
        scrollRect.verticalScrollbarSpacing = 0f;

        RebuildEmojiTrayContent();
        _emojiTray.SetActive(_emojiTrayOpen && ClanEmoji.IsReady);
    }

    private static void RebuildEmojiTrayContent()
    {
        if (_emojiContent == null)
        {
            return;
        }

        ClanUiFactory.ClearChildren(_emojiContent);
        for (int index = 0; index < ClanEmoji.EmojiCount; index++)
        {
            int emojiIndex = index;
            Button button = CreateButton(
                _emojiContent,
                "",
                () => AppendEmojiToken(emojiIndex),
                EmojiCellSize);
            button.gameObject.name = $"EmojiButton{emojiIndex}";
            GameObject iconObject = ClanUiFactory.CreateObject(
                "Icon",
                button.transform,
                typeof(Image));
            Image icon = iconObject.GetComponent<Image>();
            icon.sprite = ClanEmoji.GetPickerSprite(emojiIndex);
            icon.color = Color.white;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(44f, 44f);

            if (ClanEmoji.IsGifEmoji(emojiIndex))
            {
                GameObject badgeObject = ClanUiFactory.CreateObject(
                    "GifBadge",
                    button.transform,
                    typeof(Image));
                Image badgeBackground = badgeObject.GetComponent<Image>();
                badgeBackground.color = new Color(0.04f, 0.04f, 0.04f, 0.82f);
                badgeBackground.raycastTarget = false;

                RectTransform badgeRect = badgeObject.GetComponent<RectTransform>();
                badgeRect.anchorMin = Vector2.one;
                badgeRect.anchorMax = Vector2.one;
                badgeRect.pivot = Vector2.one;
                badgeRect.anchoredPosition = new Vector2(-2f, -2f);
                badgeRect.sizeDelta = new Vector2(23f, 13f);

                Text badgeLabel = CreateLabel(
                    badgeObject.transform,
                    "GIF",
                    9,
                    TextAnchor.MiddleCenter);
                badgeLabel.raycastTarget = false;
                RectTransform badgeLabelRect = badgeLabel.GetComponent<RectTransform>();
                badgeLabelRect.anchorMin = Vector2.zero;
                badgeLabelRect.anchorMax = Vector2.one;
                badgeLabelRect.offsetMin = Vector2.zero;
                badgeLabelRect.offsetMax = Vector2.zero;
            }
        }

        RefreshEmojiScrollBehavior();
    }

    private static void RefreshEmojiScrollBehavior()
    {
        if (_emojiTray == null || _emojiTrayRect == null || _emojiContent == null)
        {
            return;
        }

        ScrollRect? scroll = _emojiTray.GetComponent<ScrollRect>();
        if (scroll == null)
        {
            return;
        }

        int rowCount = (ClanEmoji.EmojiCount + EmojiColumnCount - 1) /
                       EmojiColumnCount;
        float contentHeight = rowCount == 0
            ? 0f
            : rowCount * EmojiCellSize + (rowCount - 1) * EmojiCellSpacing;
        float viewportHeight = Mathf.Max(
            0f,
            _emojiTrayRect.sizeDelta.y - EmojiTrayPadding * 2f);
        bool hasOverflow = contentHeight > viewportHeight + ScrollOverflowEpsilon;
        ScrollRect.MovementType movementType = hasOverflow
            ? ScrollRect.MovementType.Clamped
            : ScrollRect.MovementType.Elastic;
        bool movementTypeChanged = scroll.movementType != movementType;
        scroll.movementType = movementType;
        scroll.scrollSensitivity = hasOverflow
            ? ScrollSensitivity
            : UnderfilledScrollSensitivity;
        scroll.elasticity = UnderfilledScrollElasticity;

        if (movementTypeChanged)
        {
            scroll.StopMovement();
            if (!hasOverflow)
            {
                _emojiContent.anchoredPosition = Vector2.zero;
            }
        }
    }

    private static Button AddRailButton(
        Transform parent,
        string label,
        Action onClick)
    {
        Button button = CreateButton(parent, label, onClick, FeatureDockWidth - 6f);
        AddPreferredHeight(button.gameObject, ChannelButtonHeight);
        return button;
    }

    private static bool ShouldToggleClanPanelFromShortcut(
        Chat chat,
        TMP_InputField input)
    {
        if (!ClanPlugin.ClanPanelShortcut.Value.IsKeyDown())
        {
            return false;
        }

        return !input.isFocused &&
               !HasPendingChatRefocus(chat) &&
               !ClanPanelController.OwnsFocusedTextInput &&
               !Console.IsVisible() &&
               !TextInput.IsVisible() &&
               !Minimap.InTextInput() &&
               !Minimap.IsOpen() &&
               !Menu.IsVisible() &&
               !InventoryGui.IsVisible() &&
               !StoreGui.IsVisible() &&
               !UnifiedPopup.IsVisible();
    }

    private static bool TryHandleClanPanelCancel()
    {
        if (!ClanPanelController.IsOpen ||
            (!ZInput.GetKeyDown(KeyCode.Escape) &&
             !ZInput.GetButtonDown("JoyButtonB")) ||
            Console.IsVisible() ||
            TextInput.IsVisible() ||
            Minimap.InTextInput() ||
            Menu.IsVisible() ||
            InventoryGui.IsVisible() ||
            StoreGui.IsVisible() ||
            UnifiedPopup.IsVisible())
        {
            return false;
        }

        if (!ClanPanelController.HandleCancelInput())
        {
            return false;
        }

        _clanPanelCancelFrame = Time.frameCount;
        CancelPendingChatRefocus();
        return true;
    }

    private static void ToggleClanPanelFromDock()
    {
        ToggleClanPanel(keepChatVisibleWhenOpening: true);
    }

    private static void ToggleClanPanel(bool keepChatVisibleWhenOpening)
    {
        bool opening = !ClanPanelController.IsOpen;
        CloseEmojiTray();
        if (opening)
        {
            _keepChatVisibleForClanPanel = keepChatVisibleWhenOpening;
            ClanPanelController.Toggle();
            if (!ClanPanelController.IsOpen)
            {
                _keepChatVisibleForClanPanel = false;
                return;
            }
            ClanRpc.RequestSnapshot("panel-open");
            ClanRpc.RequestDirectory();
        }
        else
        {
            ClanPanelController.Close();
        }
    }

    private static void ToggleEmojiTray()
    {
        if (!ClanEmoji.IsReady)
        {
            ClanRpc.NotifyStatus("Chat emojis are not available.");
            return;
        }

        _emojiTrayOpen = !_emojiTrayOpen;
        ClanPanelController.Close();
        if (_emojiTray != null)
        {
            _emojiTray.SetActive(_emojiTrayOpen);
        }
        RefreshButtonStates();
    }

    private static void BuildResizeHandle(Transform parent)
    {
        GameObject handle = ClanUiFactory.CreateObject(
            "ClanChatResizeHandle",
            parent,
            typeof(Image),
            typeof(Button),
            typeof(ChatScaleDragHandle));
        _resizeHandleRect = handle.GetComponent<RectTransform>();
        _resizeHandleRect.anchorMin = new Vector2(0.5f, 0.5f);
        _resizeHandleRect.anchorMax = new Vector2(0.5f, 0.5f);
        _resizeHandleRect.sizeDelta = new Vector2(ResizeHandleSize, ResizeHandleSize);

        Image background = handle.GetComponent<Image>();
        background.color = Color.white;
        background.raycastTarget = true;

        Button button = handle.GetComponent<Button>();
        button.targetGraphic = background;
        Navigation navigation = button.navigation;
        navigation.mode = Navigation.Mode.None;
        button.navigation = navigation;

        Text label = CreateLabel(handle.transform, "", 12, TextAnchor.MiddleCenter);
        label.raycastTarget = false;
        RectTransform labelRect = label.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        LayoutElement element = handle.AddComponent<LayoutElement>();
        element.ignoreLayout = true;
        TryApplyButtonStyle(button);
        SetButtonColor(button, ButtonColor, Color.white);
        ClanUiFeedback.ApplyIcon(
            button,
            ClanActionIcon.Resize,
            "Drag diagonally to resize chat",
            displaySize: ResizeIconSize);
    }

    private static void RefreshDockPositions(TMP_InputField input)
    {
        if (_rootRect == null)
        {
            return;
        }

        RectTransform inputRect = input.GetComponent<RectTransform>();
        bool inputActive = inputRect.gameObject.activeInHierarchy;
        bool refreshInactiveRects = !inputActive &&
                                    !_hasCachedChatInputBounds &&
                                    Time.unscaledTime >=
                                    _nextInactiveChatRectRefreshAt;
        if (refreshInactiveRects)
        {
            _nextInactiveChatRectRefreshAt =
                Time.unscaledTime + InactiveChatRectRefreshIntervalSeconds;
            ForceUpdateChatRectTransforms(inputRect);
        }
        RefreshChatPanelTarget(inputRect);
        if (refreshInactiveRects)
        {
            ForceUpdateChatRectTransforms(inputRect);
        }
        Rect inputBounds = ResolveChatPlacementBounds(
            inputRect,
            out ChatPlacementSource placementSource,
            out float dockScale);
        ApplyClanDockScale(dockScale);
        bool railPositioned = PlaceLeftRailStack(
            _rootRect,
            inputRect,
            inputBounds,
            out bool railOnLeft,
            out float railHorizontalPoint);
        if (_channelDockRect != null)
        {
            PlaceChannelDock(
                _channelDockRect,
                _rootRect,
                inputRect,
                railPositioned,
                railOnLeft,
                railHorizontalPoint);
        }
        if (_commandDockRect != null)
        {
            PlaceCommandDock(_commandDockRect, _rootRect, inputRect);
        }
        ClanPanelController.RefreshPosition(_rootRect);
        LogStandaloneNotificationPositionIfChanged(
            input,
            railPositioned,
            railOnLeft,
            railHorizontalPoint,
            placementSource);
    }

    private static void RefreshChatPanelTarget(RectTransform inputRect)
    {
        RectTransform? target = ResolveChatPanelRect(inputRect);
        bool canResize = CanResizeDeclaredChatWindow(inputRect, target);
        if (_chatPanelRect != target || _canResizeChatPanel != canResize)
        {
            _chatPanelRect = target;
            _canResizeChatPanel = canResize;
        }
        RefreshChatScaleTarget(target, canResize);
    }

    private static void ForceUpdateChatRectTransforms(RectTransform inputRect)
    {
        Canvas? canvas = inputRect.GetComponentInParent<Canvas>(true);
        if (canvas?.transform is RectTransform canvasRect)
        {
            canvasRect.ForceUpdateRectTransforms();
        }

        Chat? chat = Chat.instance;
        RectTransform? declaredWindow = chat != null
            ? ((Terminal)chat).m_chatWindow
            : null;
        declaredWindow?.ForceUpdateRectTransforms();
        ResolveDeclaredChatContentRoot(inputRect, declaredWindow)?
            .ForceUpdateRectTransforms();
        Component? output = chat != null
            ? ((Terminal)chat).m_output as Component
            : null;
        output?.GetComponent<RectTransform>()?.ForceUpdateRectTransforms();
        inputRect.ForceUpdateRectTransforms();
        _rootRect?.ForceUpdateRectTransforms();
    }

    private static Rect ResolveChatPlacementBounds(
        RectTransform inputRect,
        out ChatPlacementSource source,
        out float dockScale)
    {
        Rect safeArea = GetSafeArea();
        Canvas? canvas = inputRect.GetComponentInParent<Canvas>(true);
        int canvasId = canvas != null ? canvas.GetInstanceID() : 0;
        float canvasScaleFactor = canvas != null &&
                                  IsFinitePositive(canvas.scaleFactor)
            ? canvas.scaleFactor
            : 1f;
        float configuredScale = GetConfiguredChatScale();
        EnsureChatPlacementCacheEnvironment(
            inputRect,
            canvasId,
            canvasScaleFactor,
            safeArea,
            configuredScale);

        Rect liveBounds = GetScreenBounds(inputRect);
        if (inputRect.gameObject.activeInHierarchy &&
            IsUsableChatPlacementBounds(liveBounds, safeArea))
        {
            dockScale = _canResizeChatPanel && _chatScaleTarget == _chatPanelRect
                ? _chatScaleFactor
                : 1f;
            _hasCachedChatInputBounds = true;
            _cachedChatInputBounds = liveBounds;
            _cachedChatDockScale = dockScale;
            source = ChatPlacementSource.Live;
            return liveBounds;
        }

        if (_hasCachedChatInputBounds &&
            IsUsableChatPlacementBounds(_cachedChatInputBounds, safeArea))
        {
            dockScale = _cachedChatDockScale;
            source = ChatPlacementSource.Cached;
            return _cachedChatInputBounds;
        }

        source = ChatPlacementSource.Fallback;
        return BuildFallbackChatInputBounds(
            inputRect,
            canvasScaleFactor,
            liveBounds,
            safeArea,
            configuredScale,
            out dockScale);
    }

    private static void EnsureChatPlacementCacheEnvironment(
        RectTransform inputRect,
        int canvasId,
        float canvasScaleFactor,
        Rect safeArea,
        float configuredScale)
    {
        bool unchanged =
            _chatPlacementCacheScreenWidth == Screen.width &&
            _chatPlacementCacheScreenHeight == Screen.height &&
            Approximately(_chatPlacementCacheSafeArea, safeArea) &&
            Mathf.Approximately(
                _chatPlacementCacheConfiguredScale,
                configuredScale) &&
            Mathf.Approximately(
                _chatPlacementCacheCanvasScaleFactor,
                canvasScaleFactor) &&
            _chatPlacementCacheCanvasId == canvasId &&
            _chatPlacementCacheInputId == inputRect.GetInstanceID();
        if (unchanged)
        {
            return;
        }

        _hasCachedChatInputBounds = false;
        _cachedChatInputBounds = default;
        _cachedChatDockScale = 1f;
        _chatPlacementCacheScreenWidth = Screen.width;
        _chatPlacementCacheScreenHeight = Screen.height;
        _chatPlacementCacheSafeArea = safeArea;
        _chatPlacementCacheConfiguredScale = configuredScale;
        _chatPlacementCacheCanvasScaleFactor = canvasScaleFactor;
        _chatPlacementCacheCanvasId = canvasId;
        _chatPlacementCacheInputId = inputRect.GetInstanceID();
    }

    private static Rect BuildFallbackChatInputBounds(
        RectTransform inputRect,
        float canvasScale,
        Rect measuredBounds,
        Rect safeArea,
        float configuredScale,
        out float dockScale)
    {
        Vector2 localSize = inputRect.rect.size;
        float localWidth = Mathf.Abs(localSize.x);
        float localHeight = Mathf.Abs(localSize.y);
        if (!IsFinitePositive(localWidth) || localWidth <= 1f)
        {
            localWidth = FallbackChatInputWidth;
        }
        if (!IsFinitePositive(localHeight) || localHeight <= 1f)
        {
            localHeight = FallbackChatInputHeight;
        }
        float baseWidth = localWidth * canvasScale;
        float baseHeight = localHeight * canvasScale;
        float bottom = IsFinite(measuredBounds.yMin) &&
                       measuredBounds.yMin >= safeArea.yMin &&
                       measuredBounds.yMin < safeArea.yMax
            ? measuredBounds.yMin
            : safeArea.yMin + EdgeGap;
        float availableWidth = Mathf.Max(1f, safeArea.width - EdgeGap * 2f);
        float availableHeight = Mathf.Max(
            1f,
            safeArea.yMax - bottom - EdgeGap);
        float maximumScale = Mathf.Min(
            availableWidth / baseWidth,
            availableHeight / baseHeight);
        dockScale = _canResizeChatPanel
            ? Mathf.Clamp(
                Mathf.Min(configuredScale, maximumScale),
                MinimumChatScale,
                MaximumChatScale)
            : 1f;
        float width = Mathf.Min(availableWidth, baseWidth * dockScale);
        float height = Mathf.Min(availableHeight, baseHeight * dockScale);
        bottom = Mathf.Clamp(
            bottom,
            safeArea.yMin,
            Mathf.Max(safeArea.yMin, safeArea.yMax - height));
        float right = safeArea.xMax - EdgeGap;
        return new Rect(right - width, bottom, width, height);
    }

    private static bool IsUsableChatPlacementBounds(Rect bounds, Rect safeArea)
    {
        if (!IsDiagnosticRectValid(bounds))
        {
            return false;
        }

        float overlapWidth = Mathf.Min(bounds.xMax, safeArea.xMax) -
                             Mathf.Max(bounds.xMin, safeArea.xMin);
        float overlapHeight = Mathf.Min(bounds.yMax, safeArea.yMax) -
                              Mathf.Max(bounds.yMin, safeArea.yMin);
        return overlapWidth > 1f && overlapHeight > 1f;
    }

    private static bool HasUsableChatVisualGeometry(RectTransform target)
    {
        return IsUsableChatPlacementBounds(
            GetChatVisualScreenBounds(target),
            GetSafeArea());
    }

    private static float GetConfiguredChatScale()
    {
        float configuredScale = ClanPlugin.ClanChatWindowScale.Value;
        if (!IsFinite(configuredScale))
        {
            return MinimumChatScale;
        }
        return Mathf.Clamp(
            configuredScale,
            MinimumChatScale,
            MaximumChatScale);
    }

    private static void ResetChatPlacementCache()
    {
        _hasCachedChatInputBounds = false;
        _cachedChatInputBounds = default;
        _cachedChatDockScale = 1f;
        _chatPlacementCacheScreenWidth = 0;
        _chatPlacementCacheScreenHeight = 0;
        _chatPlacementCacheSafeArea = default;
        _chatPlacementCacheConfiguredScale = 0f;
        _chatPlacementCacheCanvasScaleFactor = 0f;
        _chatPlacementCacheCanvasId = 0;
        _chatPlacementCacheInputId = 0;
        _nextInactiveChatRectRefreshAt = 0f;
    }

    private static bool Approximately(Rect left, Rect right)
    {
        return Mathf.Approximately(left.x, right.x) &&
               Mathf.Approximately(left.y, right.y) &&
               Mathf.Approximately(left.width, right.width) &&
               Mathf.Approximately(left.height, right.height);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinitePositive(float value)
    {
        return IsFinite(value) && value > 0f;
    }

    private static Rect GetChatVisualScreenBounds(RectTransform target)
    {
        float left = float.PositiveInfinity;
        float top = float.NegativeInfinity;
        float right = float.NegativeInfinity;
        float bottom = float.PositiveInfinity;
        bool found = false;

        Component? output = Chat.instance != null
            ? ((Terminal)Chat.instance).m_output as Component
            : null;
        IncludeChatVisualRect(
            output?.GetComponent<RectTransform>(),
            target,
            ref left,
            ref top,
            ref right,
            ref bottom,
            ref found);
        IncludeChatVisualRect(
            GetInputField()?.GetComponent<RectTransform>(),
            target,
            ref left,
            ref top,
            ref right,
            ref bottom,
            ref found);

        if (!found)
        {
            target.GetWorldCorners(WorldCornersB);
            IncludeWorldCorners(
                ref left,
                ref top,
                ref right,
                ref bottom);
        }

        return Rect.MinMaxRect(left, bottom, right, top);
    }

    private static Rect GetScreenBounds(RectTransform rect)
    {
        rect.GetWorldCorners(WorldCornersA);
        Vector2 first = RectTransformUtility.WorldToScreenPoint(null, WorldCornersA[0]);
        float left = first.x;
        float right = first.x;
        float bottom = first.y;
        float top = first.y;
        for (int index = 1; index < WorldCornersA.Length; index++)
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(null, WorldCornersA[index]);
            left = Mathf.Min(left, point.x);
            right = Mathf.Max(right, point.x);
            bottom = Mathf.Min(bottom, point.y);
            top = Mathf.Max(top, point.y);
        }

        return Rect.MinMaxRect(left, bottom, right, top);
    }

    private static Rect GetSafeArea()
    {
        Rect safeArea = Screen.safeArea;
        return safeArea.width > 0f && safeArea.height > 0f
            ? safeArea
            : new Rect(0f, 0f, Screen.width, Screen.height);
    }

    private static void LogStandaloneNotificationPositionIfChanged(
        TMP_InputField input,
        bool railPositioned,
        bool railOnLeft,
        float railHorizontalPoint,
        ChatPlacementSource placementSource)
    {
        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        bool actionable = HasActionableClanNotification(snapshot);
        if (!actionable)
        {
            _notificationPositionDiagnosticInitialized = false;
            return;
        }

        bool inputVisible = IsChatInputOpen(input);
        int suppressionMask = GetStandaloneClanNotificationSuppressionMask();
        bool standalone = !inputVisible &&
                          !ClanPanelController.IsOpen &&
                          suppressionMask == 0;
        if (!inputVisible && !standalone)
        {
            return;
        }

        RectTransform inputRect = input.GetComponent<RectTransform>();
        Rect inputBounds = GetScreenBounds(inputRect);
        Rect dockBounds = GetDiagnosticScreenBounds(_clanButtonDockRect);
        Rect safeArea = GetSafeArea();
        Vector2 dockCenter = dockBounds.center;
        bool geometryValid = IsUsableChatPlacementBounds(inputBounds, safeArea) &&
                             IsUsableChatPlacementBounds(dockBounds, safeArea);
        bool leftEdge = geometryValid &&
                        dockBounds.xMin <= safeArea.xMin + 8f;
        int panelId = _chatPanelRect != null
            ? _chatPanelRect.GetInstanceID()
            : 0;
        bool moved = _notificationPositionDiagnosticInitialized &&
                     Vector2.Distance(
                         dockCenter,
                         _lastNotificationDiagnosticPosition) >= 8f;
        bool shouldLog =
            !_notificationPositionDiagnosticInitialized ||
            inputVisible != _lastNotificationDiagnosticInputVisible ||
            standalone != _lastNotificationDiagnosticStandalone ||
            leftEdge != _lastNotificationDiagnosticLeftEdge ||
            geometryValid != _lastNotificationDiagnosticGeometryValid ||
            panelId != _lastNotificationDiagnosticPanelId ||
            placementSource != _lastNotificationDiagnosticPlacementSource ||
            moved;
        if (!shouldLog)
        {
            return;
        }

        _notificationPositionDiagnosticInitialized = true;
        _lastNotificationDiagnosticPosition = dockCenter;
        _lastNotificationDiagnosticInputVisible = inputVisible;
        _lastNotificationDiagnosticStandalone = standalone;
        _lastNotificationDiagnosticLeftEdge = leftEdge;
        _lastNotificationDiagnosticGeometryValid = geometryValid;
        _lastNotificationDiagnosticPanelId = panelId;
        _lastNotificationDiagnosticPlacementSource = placementSource;

        Chat? chat = Chat.instance;
        RectTransform? declaredWindow = chat != null
            ? ((Terminal)chat).m_chatWindow
            : null;
        Component? output = chat != null
            ? ((Terminal)chat).m_output as Component
            : null;
        RectTransform? outputRect = output != null
            ? output.GetComponent<RectTransform>()
            : null;
        Rect windowBounds = GetDiagnosticScreenBounds(declaredWindow);
        Rect panelBounds = GetDiagnosticScreenBounds(_chatPanelRect);
        Rect outputBounds = GetDiagnosticScreenBounds(outputRect);
        Rect rootBounds = GetDiagnosticScreenBounds(_rootRect);
        Vector2 dockAnchoredPosition = _clanButtonDockRect != null
            ? _clanButtonDockRect.anchoredPosition
            : Vector2.zero;
        Vector2 dockPivot = _clanButtonDockRect != null
            ? _clanButtonDockRect.pivot
            : Vector2.zero;
        Canvas? canvas = _rootRect != null
            ? _rootRect.GetComponentInParent<Canvas>(true)
            : null;
        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Clan alert position frame={Time.frameCount}; " +
            $"inputVisible={inputVisible}; standalone={standalone}; " +
            $"geometryValid={geometryValid}; atSafeLeft={leftEdge}; " +
            $"railPositioned={railPositioned}; railOnLeft={railOnLeft}; " +
            $"railPoint={railHorizontalPoint:F1}; placement={placementSource}; " +
            $"inputActiveSelf={input.gameObject.activeSelf}; " +
            $"inputActiveHierarchy={input.gameObject.activeInHierarchy}; " +
            $"input={FormatDiagnosticRect(inputBounds)}; " +
            $"windowActiveSelf={declaredWindow != null && declaredWindow.gameObject.activeSelf}; " +
            $"windowActiveHierarchy={declaredWindow != null && declaredWindow.gameObject.activeInHierarchy}; " +
            $"window={FormatDiagnosticRect(windowBounds)}; " +
            $"panelId={panelId}; panel={FormatDiagnosticRect(panelBounds)}; " +
            $"outputActiveSelf={output != null && output.gameObject.activeSelf}; " +
            $"outputActiveHierarchy={output != null && output.gameObject.activeInHierarchy}; " +
            $"output={FormatDiagnosticRect(outputBounds)}; " +
            $"dock={FormatDiagnosticRect(dockBounds)}; " +
            $"dockAnchor=({dockAnchoredPosition.x:F1},{dockAnchoredPosition.y:F1}); " +
            $"dockPivot=({dockPivot.x:F1},{dockPivot.y:F1}); " +
            $"safe={FormatDiagnosticRect(safeArea)}; " +
            $"root={FormatDiagnosticRect(rootBounds)}; " +
            $"canvasId={(canvas != null ? canvas.GetInstanceID() : 0)}; " +
            $"canvasMode={(canvas != null ? canvas.renderMode.ToString() : "none")}; " +
            $"canvasScale={(canvas != null ? canvas.scaleFactor : 0f):F2}; " +
            $"canvasCamera={canvas != null && canvas.worldCamera != null}.");
    }

    private static void LogChatDockLifecycle(
        string action,
        TMP_InputField? input)
    {
        RectTransform? inputRect = input != null
            ? input.GetComponent<RectTransform>()
            : null;
        Canvas? canvas = _rootRect != null
            ? _rootRect.GetComponentInParent<Canvas>(true)
            : null;
        Transform? parent = _root != null ? _root.transform.parent : null;
        ClanPlugin.ClanLogger.LogInfo(
            $"[Clan.Diag] Chat dock {action} frame={Time.frameCount}; " +
            $"rootId={(_root != null ? _root.GetInstanceID() : 0)}; " +
            $"rootActiveSelf={_root != null && _root.activeSelf}; " +
            $"rootActiveHierarchy={_root != null && _root.activeInHierarchy}; " +
            $"parentId={(parent != null ? parent.GetInstanceID() : 0)}; " +
            $"parentActiveSelf={parent != null && parent.gameObject.activeSelf}; " +
            $"parentActiveHierarchy={parent != null && parent.gameObject.activeInHierarchy}; " +
            $"dockId={(_clanButtonDockRect != null ? _clanButtonDockRect.GetInstanceID() : 0)}; " +
            $"panelId={(_chatPanelRect != null ? _chatPanelRect.GetInstanceID() : 0)}; " +
            $"inputActiveSelf={input != null && input.gameObject.activeSelf}; " +
            $"inputActiveHierarchy={input != null && input.gameObject.activeInHierarchy}; " +
            $"input={FormatDiagnosticRect(GetDiagnosticScreenBounds(inputRect))}; " +
            $"canvasId={(canvas != null ? canvas.GetInstanceID() : 0)}; " +
            $"canvasMode={(canvas != null ? canvas.renderMode.ToString() : "none")}; " +
            $"canvasScale={(canvas != null ? canvas.scaleFactor : 0f):F2}.");
    }

    private static Rect GetDiagnosticScreenBounds(RectTransform? rect)
    {
        return rect != null ? GetScreenBounds(rect) : default;
    }

    private static bool IsDiagnosticRectValid(Rect rect)
    {
        return rect.width > 1f &&
               rect.height > 1f &&
               IsFinite(rect.xMin) &&
               IsFinite(rect.yMin) &&
               IsFinite(rect.xMax) &&
               IsFinite(rect.yMax);
    }

    private static string FormatDiagnosticRect(Rect rect)
    {
        return $"({rect.xMin:F1},{rect.yMin:F1},{rect.width:F1},{rect.height:F1})";
    }

    private static void ResetStandaloneNotificationDiagnostics()
    {
        _lastNotificationDiagnosticMask = int.MinValue;
        _lastNotificationDiagnosticApplications = -1;
        _lastNotificationDiagnosticRootId = 0;
        _lastNotificationDiagnosticDockId = 0;
        _notificationPositionDiagnosticInitialized = false;
        _lastNotificationDiagnosticPosition = Vector2.zero;
        _lastNotificationDiagnosticInputVisible = false;
        _lastNotificationDiagnosticStandalone = false;
        _lastNotificationDiagnosticLeftEdge = false;
        _lastNotificationDiagnosticGeometryValid = false;
        _lastNotificationDiagnosticPanelId = 0;
        _lastNotificationDiagnosticPlacementSource = default;
    }

    private static void IncludeChatVisualRect(
        RectTransform? rect,
        RectTransform target,
        ref float left,
        ref float top,
        ref float right,
        ref float bottom,
        ref bool found)
    {
        if (rect == null ||
            (rect != target && !rect.transform.IsChildOf(target.transform)))
        {
            return;
        }

        rect.GetWorldCorners(WorldCornersB);
        IncludeWorldCorners(
            ref left,
            ref top,
            ref right,
            ref bottom);
        found = true;
    }

    private static void IncludeWorldCorners(
        ref float left,
        ref float top,
        ref float right,
        ref float bottom)
    {
        for (int i = 0; i < WorldCornersB.Length; i++)
        {
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(
                null,
                WorldCornersB[i]);
            left = Mathf.Min(left, screenPoint.x);
            top = Mathf.Max(top, screenPoint.y);
            right = Mathf.Max(right, screenPoint.x);
            bottom = Mathf.Min(bottom, screenPoint.y);
        }
    }

    private static void RefreshChatScaleTarget(RectTransform? target, bool canResize)
    {
        if (!canResize || target == null)
        {
            ReleaseChatScaleTarget(restoreScale: true);
            return;
        }

        if (_chatScaleTarget != target)
        {
            ReleaseChatScaleTarget(restoreScale: true);
            _chatScaleTarget = target;
            _chatBaseLocalScale = target.localScale;
            _chatScaleFactor = 1f;
        }

        if (_isChatScaleDragging)
        {
            return;
        }

        TMP_InputField? input = GetInputField();
        if (input == null ||
            !input.gameObject.activeInHierarchy ||
            !HasUsableChatVisualGeometry(target))
        {
            return;
        }

        SetChatScale(ClanPlugin.ClanChatWindowScale.Value);
    }

    private static void ReleaseChatScaleTarget(bool restoreScale)
    {
        RectTransform? target = _chatScaleTarget;
        if (restoreScale && target != null &&
            Mathf.Abs(_chatScaleFactor - 1f) > 0.001f)
        {
            SetTargetScaleKeepingVisualBottomRight(target, _chatBaseLocalScale);
        }

        _chatScaleTarget = null;
        _chatBaseLocalScale = Vector3.one;
        _chatScaleFactor = 1f;
        _isChatScaleDragging = false;
    }

    private static void BeginChatScaleDrag()
    {
        if (_chatScaleTarget == null || !_canResizeChatPanel)
        {
            return;
        }

        _isChatScaleDragging = true;
        RefocusChatInput();
    }

    private static void DragChatScale(Vector2 screenDelta)
    {
        RectTransform? target = _chatScaleTarget;
        if (!_isChatScaleDragging || target == null || !_canResizeChatPanel)
        {
            return;
        }

        Rect visualBounds = GetChatVisualScreenBounds(target);
        float width = Mathf.Max(1f, visualBounds.width);
        float height = Mathf.Max(1f, visualBounds.height);
        float horizontalGrowth = -screenDelta.x / width;
        float verticalGrowth = screenDelta.y / height;
        float multiplier = Mathf.Max(0.1f, 1f + (horizontalGrowth + verticalGrowth) * 0.5f);

        SetChatScale(_chatScaleFactor * multiplier);
        TMP_InputField? input = GetInputField();
        if (input != null)
        {
            RefreshDockPositions(input);
        }
    }

    private static void EndChatScaleDrag(bool save)
    {
        if (!_isChatScaleDragging)
        {
            return;
        }

        _isChatScaleDragging = false;
        if (save)
        {
            ClanPlugin.ClanChatWindowScale.Value = _chatScaleFactor;
        }

        RefocusChatInput();
    }

    private static void SetChatScale(float requestedScale)
    {
        RectTransform? target = _chatScaleTarget;
        if (target == null)
        {
            return;
        }

        if (float.IsNaN(requestedScale) || float.IsInfinity(requestedScale))
        {
            requestedScale = 1f;
        }

        float maximumScale = CalculateMaximumChatScale(target);
        float scale = Mathf.Clamp(
            requestedScale,
            MinimumChatScale,
            maximumScale);
        if (Mathf.Abs(scale - _chatScaleFactor) <= 0.0001f)
        {
            return;
        }

        SetTargetScaleKeepingVisualBottomRight(target, new Vector3(
            _chatBaseLocalScale.x * scale,
            _chatBaseLocalScale.y * scale,
            _chatBaseLocalScale.z));
        _chatScaleFactor = scale;
    }

    private static float CalculateMaximumChatScale(RectTransform target)
    {
        Rect visualBounds = GetChatVisualScreenBounds(target);
        Rect safeArea = GetSafeArea();
        if (!IsUsableChatPlacementBounds(visualBounds, safeArea))
        {
            return MaximumChatScale;
        }

        Vector2 pivot = new(visualBounds.xMax, visualBounds.yMin);
        float baseLeftExtent = Mathf.Max(1f, pivot.x - visualBounds.xMin) /
                               Mathf.Max(0.001f, _chatScaleFactor);
        float baseTopExtent = Mathf.Max(1f, visualBounds.yMax - pivot.y) /
                              Mathf.Max(0.001f, _chatScaleFactor);
        float availableWidth = Mathf.Max(1f, pivot.x - safeArea.xMin - EdgeGap);
        float availableHeight = Mathf.Max(1f, safeArea.yMax - pivot.y - EdgeGap);
        float screenLimitedScale = Mathf.Min(
            availableWidth / baseLeftExtent,
            availableHeight / baseTopExtent);
        return Mathf.Clamp(
            screenLimitedScale,
            MinimumChatScale,
            MaximumChatScale);
    }

    private static void SetTargetScaleKeepingVisualBottomRight(
        RectTransform target,
        Vector3 localScale)
    {
        Rect beforeBounds = GetChatVisualScreenBounds(target);
        target.GetWorldCorners(WorldCornersA);
        Vector3 bottomRightBefore = WorldCornersA[3];
        target.localScale = localScale;
        Rect afterBounds = GetChatVisualScreenBounds(target);

        if (target.parent is RectTransform parentRect &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRect,
                new Vector2(beforeBounds.xMax, beforeBounds.yMin),
                null,
                out Vector2 beforeLocal) &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRect,
                new Vector2(afterBounds.xMax, afterBounds.yMin),
                null,
                out Vector2 afterLocal))
        {
            target.anchoredPosition += beforeLocal - afterLocal;
            return;
        }

        target.GetWorldCorners(WorldCornersB);
        target.position += bottomRightBefore - WorldCornersB[3];
    }

    private static void ApplyClanDockScale(float? scaleOverride = null)
    {
        float factor = scaleOverride ??
            (_canResizeChatPanel && _chatScaleTarget == _chatPanelRect
                ? _chatScaleFactor
                : 1f);
        Vector3 scale = new(factor, factor, 1f);
        SetDockScale(_emojiButtonDockRect, scale);
        SetDockScale(_clanButtonDockRect, scale);
        SetDockScale(_chatViewDockRect, scale);
        SetDockScale(_channelDockRect, scale);
        SetDockScale(_commandDockRect, scale);
        SetDockScale(_emojiTrayRect, scale);
    }

    private static void SetDockScale(RectTransform? rect, Vector3 scale)
    {
        if (rect != null)
        {
            rect.localScale = scale;
        }
    }

    private static void CycleChatViewMode()
    {
        _chatViewMode = _chatViewMode switch
        {
            ChatViewMode.All => ChatViewMode.Clan,
            ChatViewMode.Clan => ChatViewMode.Local,
            ChatViewMode.Local => ChatViewMode.Global,
            _ => ChatViewMode.All
        };
        RefreshChatViewButton(refreshTooltip: true);
        RefreshChatViewOutput(resetScroll: true);
    }

    private static void RefreshChatViewButton(bool refreshTooltip)
    {
        SetButtonLabel(_chatViewButton, GetChatViewLabel());
        bool filtering = _chatViewMode != ChatViewMode.All;
        SetButtonColor(
            _chatViewButton,
            filtering ? ActiveToggleButtonColor : InactiveToggleButtonColor,
            filtering ? Color.white : InactiveButtonLabelColor);
        if (refreshTooltip && _chatViewButton != null)
        {
            ClanUiFeedback.SetTooltip(
                _chatViewButton,
                GetChatViewTooltip());
        }
    }

    private static string GetChatViewLabel()
    {
        return _chatViewMode switch
        {
            ChatViewMode.Clan => "Clan",
            ChatViewMode.Local => "Local",
            ChatViewMode.Global => "Global",
            _ => "All"
        };
    }

    private static string GetChatViewTooltip()
    {
        string visible = _chatViewMode switch
        {
            ChatViewMode.Clan => "Clan messages",
            ChatViewMode.Local => "Say and Whisper messages",
            ChatViewMode.Global => "Shout messages",
            _ => "Say, Whisper, Shout, and Clan messages"
        };
        string next = GetNextChatViewModeLabel();
        return $"View filter: {visible}. System messages are always shown. Click for {next}.";
    }

    private static string GetNextChatViewModeLabel()
    {
        return _chatViewMode switch
        {
            ChatViewMode.All => "Clan",
            ChatViewMode.Clan => "Local",
            ChatViewMode.Local => "Global",
            _ => "All"
        };
    }

    private static Button AddChannelButton(Transform parent, Channel channel, string label, float width)
    {
        Button button = CreateButton(parent, label, () =>
        {
            _activeChannel = _activeChannel == channel ? Channel.Say : channel;
            NormalizeUiState(ClanRpc.CurrentSnapshot);
            UpdatePlaceholder();
            RefreshButtonStates();
        }, width);
        ChannelButtons[channel] = button;
        AddPreferredHeight(button.gameObject, ChannelButtonHeight);
        return button;
    }

    private static Button AddCommandButton(
        Transform parent,
        string label,
        CommandAction action,
        float width)
    {
        Button button = CreateButton(
            parent,
            label,
            () => ConfirmOrRunCommand(action),
            width);
        AddPreferredHeight(button.gameObject, ChannelButtonHeight);
        return button;
    }

    private static void RefreshConfiguredChannelVisibility()
    {
        if (_whisperButton == null)
        {
            return;
        }

        bool showWhisper = ClanPlugin.ShowWhisperChatButton.Value.IsOn();
        if (_whisperButton.gameObject.activeSelf == showWhisper)
        {
            return;
        }

        _whisperButton.gameObject.SetActive(showWhisper);
        if (!showWhisper && _activeChannel == Channel.Whisper)
        {
            _activeChannel = Channel.Say;
            UpdatePlaceholder();
        }

        if (_channelDockRect != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_channelDockRect);
        }
        RefreshButtonStates();
    }

    private static void RefreshConfiguredCommandVisibility()
    {
        if (_commandDockRect == null ||
            _resetSpawnButton == null ||
            _dieButton == null)
        {
            return;
        }

        bool showResetSpawn = ClanPlugin.ShowResetSpawnButton.Value.IsOn();
        bool showDie = ClanPlugin.ShowDieButton.Value.IsOn();
        bool changed = false;
        if (_resetSpawnButton.gameObject.activeSelf != showResetSpawn)
        {
            _resetSpawnButton.gameObject.SetActive(showResetSpawn);
            changed = true;
        }
        if (_dieButton.gameObject.activeSelf != showDie)
        {
            _dieButton.gameObject.SetActive(showDie);
            changed = true;
        }

        bool showDock = showResetSpawn || showDie;
        if (_commandDockRect.gameObject.activeSelf != showDock)
        {
            _commandDockRect.gameObject.SetActive(showDock);
            changed = true;
        }

        if ((_pendingCommand == CommandAction.ResetSpawn && !showResetSpawn) ||
            (_pendingCommand == CommandAction.Die && !showDie))
        {
            CancelCommandConfirmation();
        }

        if (changed && showDock)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_commandDockRect);
        }
    }

    private static void ConfirmOrRunCommand(CommandAction action)
    {
        if (action == CommandAction.None)
        {
            return;
        }

        if (_pendingCommand != action ||
            Time.unscaledTime > _commandConfirmationExpiresAt)
        {
            _pendingCommand = action;
            _commandConfirmationExpiresAt =
                Time.unscaledTime + CommandConfirmationSeconds;
            RefreshCommandButtonStates();
            return;
        }

        _pendingCommand = CommandAction.None;
        _commandConfirmationExpiresAt = 0f;
        RefreshCommandButtonStates();

        Chat? chat = Chat.instance;
        if (chat == null)
        {
            return;
        }

        string command = action == CommandAction.ResetSpawn
            ? "resetspawn"
            : "die";
        chat.TryRunCommand(command, silentFail: true);
        ResetChatHideTimer(chat);
    }

    private static void ExpireCommandConfirmation()
    {
        if (_pendingCommand != CommandAction.None &&
            Time.unscaledTime > _commandConfirmationExpiresAt)
        {
            CancelCommandConfirmation();
        }
    }

    private static void CancelCommandConfirmation()
    {
        if (_pendingCommand == CommandAction.None)
        {
            return;
        }

        _pendingCommand = CommandAction.None;
        _commandConfirmationExpiresAt = 0f;
        RefreshCommandButtonStates();
    }

    private static void RefreshCommandButtonStates()
    {
        bool resetPending = _pendingCommand == CommandAction.ResetSpawn;
        bool diePending = _pendingCommand == CommandAction.Die;
        SetButtonLabel(
            _resetSpawnButton,
            resetPending ? "Reset spawn?" : "Reset spawn");
        SetButtonLabel(_dieButton, diePending ? "Die?" : "Die");
        SetButtonColor(
            _resetSpawnButton,
            resetPending ? ConfirmationButtonColor : ButtonColor,
            Color.white);
        SetButtonColor(
            _dieButton,
            diePending ? ConfirmationButtonColor : ButtonColor,
            Color.white);
    }

    private static void RefreshButtonStates()
    {
        if (_root == null)
        {
            return;
        }

        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        foreach (KeyValuePair<Channel, Button> pair in ChannelButtons)
        {
            Button button = pair.Value;
            if (button == null)
            {
                continue;
            }

            bool enabled = pair.Key != Channel.Clan || snapshot.HasClan;
            bool active = enabled && pair.Key == _activeChannel;
            button.interactable = enabled;
            SetButtonColor(
                button,
                !enabled
                    ? DisabledButtonColor
                    : active
                        ? ActiveToggleButtonColor
                        : InactiveToggleButtonColor,
                !enabled
                    ? DisabledButtonLabelColor
                    : active
                        ? Color.white
                        : InactiveButtonLabelColor);
        }

        if (_clanPanelButton != null)
        {
            bool active = ClanPanelController.IsOpen;
            bool hasNotification = HasActionableClanNotification(snapshot);
            SetButtonColor(
                _clanPanelButton,
                active
                    ? ActiveToggleButtonColor
                    : hasNotification
                        ? NotificationButtonColor
                        : InactiveToggleButtonColor,
                active || hasNotification ? Color.white : InactiveButtonLabelColor);
            if (_clanNotificationBadge != null)
            {
                _clanNotificationBadge.SetActive(hasNotification);
            }
        }

        if (_emojiTrayButton != null)
        {
            bool enabled = ClanEmoji.IsReady;
            bool active = enabled && _emojiTrayOpen;
            _emojiTrayButton.interactable = enabled;
            SetButtonColor(
                _emojiTrayButton,
                !enabled
                    ? DisabledButtonColor
                    : active
                        ? ActiveToggleButtonColor
                        : InactiveToggleButtonColor,
                !enabled
                    ? DisabledButtonLabelColor
                    : active
                        ? Color.white
                        : InactiveButtonLabelColor);
        }

        RefreshChatViewButton(refreshTooltip: false);
        RefreshCommandButtonStates();
    }

    private static void NormalizeUiState(ClanClientSnapshot snapshot)
    {
        if (!snapshot.HasClan && _activeChannel == Channel.Clan)
        {
            _activeChannel = Channel.Say;
        }

        if (!ClanPlugin.ShowWhisperChatButton.Value.IsOn() &&
            _activeChannel == Channel.Whisper)
        {
            _activeChannel = Channel.Say;
        }

        if (!ClanEmoji.IsReady && _emojiTrayOpen)
        {
            _emojiTrayOpen = false;
            if (_emojiTray != null)
            {
                _emojiTray.SetActive(false);
            }
        }
    }

    private static void RefreshUi(ClanClientSnapshot snapshot)
    {
        NormalizeUiState(snapshot);
        RefreshButtonStates();
        ClanPanelController.RefreshSnapshot(snapshot);
        UpdatePlaceholder();
    }

    private static void RouteInput(Chat chat)
    {
        TMP_InputField? input = GetInputField(chat);
        if (input == null)
        {
            return;
        }

        string message = input.text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        switch (_activeChannel)
        {
            case Channel.Shout:
                chat.SendText(Talker.Type.Shout, message);
                break;
            case Channel.Whisper:
                chat.SendText(Talker.Type.Whisper, message);
                break;
            case Channel.Clan:
                ClanRpc.Send(new ClanRequest
                {
                    Type = ClanRequestType.SendClanChat,
                    ClanId = ClanRpc.CurrentSnapshot.ClanId,
                    Message = message
                });
                break;
            case Channel.Say:
            default:
                chat.SendText(Talker.Type.Normal, message);
                break;
        }

        CompleteChatSubmission(chat);
    }

    private static bool ShouldRouteInput(Chat chat)
    {
        TMP_InputField? input = GetInputField(chat);
        if (_root == null ||
            !_root.activeInHierarchy ||
            input == null ||
            !IsChatInputOpen(input))
        {
            return false;
        }

        string text = input.text.TrimStart();
        return !string.IsNullOrWhiteSpace(text) && !text.StartsWith("/", StringComparison.Ordinal);
    }

    private static bool IsVanillaChatCommand(Chat chat)
    {
        TMP_InputField? input = GetInputField(chat);
        if (input == null)
        {
            return false;
        }

        string text = input.text.TrimStart();
        int separator = text.IndexOf(' ');
        if (separator <= 1 || separator >= text.Length - 1 || text[0] != '/')
        {
            return false;
        }

        string command = text.Substring(1, separator - 1);
        string message = text.Substring(separator + 1);
        return !string.IsNullOrWhiteSpace(message) &&
               (StringComparer.OrdinalIgnoreCase.Equals(command, "say") ||
                StringComparer.OrdinalIgnoreCase.Equals(command, "s") ||
                StringComparer.OrdinalIgnoreCase.Equals(command, "w"));
    }

    private static void CompleteChatSubmission(Chat chat)
    {
        if (ClanPanelController.IsOpen)
        {
            CloseEmojiTray();
            RefreshButtonStates();
        }
        else
        {
            CloseOverlays();
        }

        if (ClanPlugin.ChatAfterSend.Value == ClanPlugin.ChatSubmitMode.KeepOpen)
        {
            ScheduleChatRefocus(chat);
        }
        else
        {
            CancelPendingChatRefocus();
        }
    }

    private static void UpdatePlaceholder()
    {
        if (!SupportsCurrentChatUi)
        {
            return;
        }

        TMP_InputField? input = GetInputField();
        if (input?.placeholder is TMP_Text placeholder)
        {
            placeholder.text = _activeChannel switch
            {
                Channel.Say => "Say nearby",
                Channel.Shout => "Shout",
                Channel.Whisper => "Whisper nearby",
                Channel.Clan => "Clan message",
                _ => "Message"
            };
        }
    }

    private static TMP_InputField? GetInputField()
    {
        return Chat.instance == null ? null : GetInputField(Chat.instance);
    }

    private static TMP_InputField? GetInputField(Chat chat)
    {
        return ((Terminal)chat).m_input as TMP_InputField;
    }

    internal static bool SupportsCurrentChatUi
    {
        get
        {
            Chat? chat = Chat.instance;
            return chat != null && !IsUnsupportedReplacementChat(chat);
        }
    }

    private static bool IsUnsupportedReplacementChat(Chat chat)
    {
        if (StringComparer.Ordinal.Equals(chat.gameObject.name, "KGChat"))
        {
            return true;
        }

        TMP_InputField? input = GetInputField(chat);
        RectTransform? declaredWindow = ((Terminal)chat).m_chatWindow;
        Component? output = ((Terminal)chat).m_output as Component;
        if (input == null || declaredWindow == null || output == null)
        {
            return true;
        }

        Transform inputTransform = input.transform;
        Transform outputTransform = output.transform;
        Transform windowTransform = declaredWindow.transform;
        bool inputInsideWindow = inputTransform == windowTransform ||
                                 inputTransform.IsChildOf(windowTransform);
        bool outputInsideWindow = outputTransform == windowTransform ||
                                  outputTransform.IsChildOf(windowTransform);
        return !inputInsideWindow || !outputInsideWindow;
    }

    private static void DisableDockForUnsupportedChat(Chat chat)
    {
        if (HasDockState())
        {
            DestroyRoot();
        }
        else
        {
            CancelPendingChatRefocus();
        }

        if (ReferenceEquals(_unsupportedChatWarningOwner, chat))
        {
            return;
        }

        _unsupportedChatWarningOwner = chat;
        const string warning =
            "Unsupported replacement chat UI is active. Disable Chatter " +
            "(_Global.isModEnabled=false) or Marketplace KGChat " +
            "(EnableKGChat=false). Restart the client after disabling KGChat.";
        ClanPlugin.ClanLogger.LogWarning(warning);
        ClanRpc.NotifyStatus(warning);
    }

    private static void OnSnapshotChanged(ClanClientSnapshot snapshot)
    {
        RefreshUi(snapshot);
    }

    private static void OnDirectoryChanged(ClanDirectorySnapshot directory)
    {
        ClanPanelController.RefreshDirectory(directory);
    }

    private static void OnEmojiChanged()
    {
        RebuildEmojiTrayContent();
        NormalizeUiState(ClanRpc.CurrentSnapshot);
        RefreshButtonStates();
        UpdatePlaceholder();
    }

    private static void OnEmblemsChanged()
    {
        ClanPanelController.OnEmblemsChanged();
    }

    private static void OnClanPanelOpenStateChanged(bool isOpen)
    {
        if (!isOpen)
        {
            _keepChatVisibleForClanPanel = false;
        }
        RefreshButtonStates();
        RefreshClanPanelTooltip();
    }

    private static void OnClanPanelShortcutChanged(object sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        RefreshClanPanelTooltip();
    }

    private static void RefreshClanPanelTooltip()
    {
        if (_clanPanelButton == null)
        {
            return;
        }

        var shortcut = ClanPlugin.ClanPanelShortcut.Value;
        string action = ClanPanelController.IsOpen ? "close" : "open";
        if (shortcut.MainKey == KeyCode.None)
        {
            ClanUiFeedback.SetTooltip(
                _clanPanelButton,
                $"Click to {action} Clan panel");
            return;
        }

        string shortcutLabel = shortcut.ToString();
        if (string.IsNullOrWhiteSpace(shortcutLabel))
        {
            shortcutLabel = shortcut.MainKey.ToString();
        }
        ClanUiFeedback.SetTooltip(
            _clanPanelButton,
            $"Press <color=orange>{shortcutLabel}</color> to {action} Clan panel",
            richText: true);
    }

    private static void OnStatusReceived(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        string cleanStatus = Clean(status).Replace("\n", " ");
        AddVanillaLine(cleanStatus, ChatLineKind.System);
    }

    private static void OnClanChatReceived(string senderName, string message)
    {
        AddVanillaLine(
            $"<color=orange>{Clean(senderName)}</color>: <color=#{ColorUtility.ToHtmlStringRGBA(ClanUiFactory.GetClanColor())}>{Clean(message)}</color>",
            ChatLineKind.Clan);
    }

    private static void AddVanillaLine(string message, ChatLineKind kind)
    {
        Chat? chat = Chat.instance;
        if (chat == null)
        {
            return;
        }

        ChatLineKindScope scope = PushChatLineKind(kind);
        try
        {
            ((Terminal)chat).AddString($"<color=#66d9c8>[Clan]</color> {message}");
        }
        finally
        {
            RestoreChatLineKind(scope);
        }

        if (IsChatLineVisible(kind))
        {
            ResetChatHideTimer(chat);
        }
    }

    private static ChatLineKindScope PushChatLineKind(ChatLineKind kind)
    {
        ChatLineKindScope scope = new(_scopedChatLineKind);
        _scopedChatLineKind = kind;
        return scope;
    }

    private static ChatLineKindScope PushChatLineKind(Talker.Type type)
    {
        ChatLineKind kind = type switch
        {
            Talker.Type.Normal => ChatLineKind.Local,
            Talker.Type.Whisper => ChatLineKind.Local,
            Talker.Type.Shout => ChatLineKind.Global,
            _ => ChatLineKind.System
        };
        return PushChatLineKind(kind);
    }

    private static void RestoreChatLineKind(ChatLineKindScope scope)
    {
        _scopedChatLineKind = scope.Previous;
    }

    private static void ResetChatHistoryTracking(Chat? chat)
    {
        ResetChatPlacementCache();
        _trackedChat = chat;
        ChatLineKinds.Clear();
        _chatHistoryMismatchWarned = false;
        _chatFilterUnavailableWarned = false;
        _chatViewMode = ChatViewMode.All;

        if (chat != null && TryGetChatBuffer(chat, out List<string> buffer))
        {
            for (int index = 0; index < buffer.Count; index++)
            {
                ChatLineKinds.Add(ChatLineKind.System);
            }
        }

        RefreshChatViewButton(refreshTooltip: true);
    }

    private static bool EnsureTrackedChat(Terminal terminal)
    {
        if (terminal is not Chat chat ||
            Chat.instance == null ||
            !ReferenceEquals(chat, Chat.instance))
        {
            return false;
        }

        if (!ReferenceEquals(_trackedChat, chat))
        {
            ResetChatHistoryTracking(chat);
        }

        return true;
    }

    private static bool TryGetChatBuffer(
        Terminal terminal,
        out List<string> buffer)
    {
        buffer = null!;
        if (ChatBufferField == null)
        {
            WarnChatFilterUnavailable(
                "Chat history filtering is unavailable because the vanilla chat buffer was not found.");
            return false;
        }

        try
        {
            if (ChatBufferField.GetValue(terminal) is List<string> value)
            {
                buffer = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            WarnChatFilterUnavailable(
                $"Chat history filtering is unavailable: {ex.GetBaseException().Message}");
            return false;
        }

        WarnChatFilterUnavailable(
            "Chat history filtering is unavailable because the vanilla chat buffer has an unexpected type.");
        return false;
    }

    private static void EnsureChatHistoryAligned(List<string> buffer)
    {
        if (ChatLineKinds.Count == buffer.Count)
        {
            return;
        }

        if (buffer.Count == 0)
        {
            ChatLineKinds.Clear();
            return;
        }

        int trackedCount = ChatLineKinds.Count;
        ChatLineKinds.Clear();
        for (int index = 0; index < buffer.Count; index++)
        {
            ChatLineKinds.Add(ChatLineKind.System);
        }

        if (_chatHistoryMismatchWarned)
        {
            return;
        }

        _chatHistoryMismatchWarned = true;
        ClanPlugin.ClanLogger.LogWarning(
            $"Chat history changed outside Clan ({trackedCount} tracked, {buffer.Count} actual). Existing lines were kept visible in every filter; new lines will be classified normally.");
    }

    private static void PrepareTrackedChatLine(Terminal terminal)
    {
        if (!EnsureTrackedChat(terminal) ||
            !TryGetChatBuffer(terminal, out List<string> buffer))
        {
            return;
        }

        EnsureChatHistoryAligned(buffer);
        while (ChatLineKinds.Count >= MaximumTrackedChatLines)
        {
            ChatLineKinds.RemoveAt(0);
        }
        ChatLineKinds.Add(_scopedChatLineKind);
    }

    private static bool IsChatLineVisible(ChatLineKind kind)
    {
        if (_chatViewMode == ChatViewMode.All || kind == ChatLineKind.System)
        {
            return true;
        }

        return _chatViewMode switch
        {
            ChatViewMode.Clan => kind == ChatLineKind.Clan,
            ChatViewMode.Local => kind == ChatLineKind.Local,
            ChatViewMode.Global => kind == ChatLineKind.Global,
            _ => true
        };
    }

    private static void RefreshChatViewOutput(bool resetScroll)
    {
        Chat? chat = Chat.instance;
        if (chat == null || IsUnsupportedReplacementChat(chat))
        {
            return;
        }

        try
        {
            if (resetScroll && ChatScrollHeightField != null)
            {
                ChatScrollHeightField.SetValue(chat, 0);
            }

            if (TerminalUpdateChatMethod == null)
            {
                WarnChatFilterUnavailable(
                    "Chat history filtering is unavailable because the vanilla renderer was not found.");
                return;
            }

            TerminalUpdateChatMethod.Invoke(chat, null);
        }
        catch (Exception ex)
        {
            WarnChatFilterUnavailable(
                $"Chat history filtering could not refresh the chat panel: {ex.GetBaseException().Message}");
        }
    }

    private static bool TryRenderFilteredChat(Terminal terminal)
    {
        if (_chatViewMode == ChatViewMode.All)
        {
            return false;
        }

        if (!EnsureTrackedChat(terminal) ||
            _chatViewMode == ChatViewMode.All ||
            terminal is not Chat chat ||
            IsUnsupportedReplacementChat(chat))
        {
            return false;
        }

        try
        {
            if (!TryGetChatBuffer(terminal, out List<string> buffer) ||
                ChatScrollHeightField == null ||
                ChatVisibleBufferLengthField == null ||
                terminal.m_output is not TMP_Text output)
            {
                WarnChatFilterUnavailable(
                    "Chat history filtering is unavailable because a vanilla chat field was not found.");
                return false;
            }

            EnsureChatHistoryAligned(buffer);
            List<string> visibleLines = new(buffer.Count);
            for (int index = 0; index < buffer.Count; index++)
            {
                if (IsChatLineVisible(ChatLineKinds[index]))
                {
                    visibleLines.Add(buffer[index]);
                }
            }

            int scrollHeight = ChatScrollHeightField.GetValue(terminal) is int scroll
                ? scroll
                : 0;
            int maximumVisible = ChatVisibleBufferLengthField.GetValue(terminal) is int maximum
                ? maximum
                : 30;
            int clampedScroll = Mathf.Clamp(
                scrollHeight,
                0,
                Mathf.Max(0, visibleLines.Count - 5));
            if (clampedScroll != scrollHeight)
            {
                ChatScrollHeightField.SetValue(terminal, clampedScroll);
            }

            int end = Mathf.Min(
                visibleLines.Count,
                Mathf.Max(5, visibleLines.Count - clampedScroll));
            int start = Mathf.Max(0, end - maximumVisible);
            StringBuilder builder = new();
            for (int index = start; index < end; index++)
            {
                builder.Append(visibleLines[index]);
                builder.Append('\n');
            }

            output.text = builder.ToString();
            return true;
        }
        catch (Exception ex)
        {
            WarnChatFilterUnavailable(
                $"Chat history filtering was bypassed after an error: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static void WarnChatFilterUnavailable(string message)
    {
        _chatViewMode = ChatViewMode.All;
        RefreshChatViewButton(refreshTooltip: true);
        if (_chatFilterUnavailableWarned)
        {
            return;
        }

        _chatFilterUnavailableWarned = true;
        ClanPlugin.ClanLogger.LogWarning(message);
    }

    private static string Clean(string value)
    {
        return (value ?? "").Replace("<", " ").Replace(">", " ").Trim();
    }

    private static Text CreateLabel(Transform parent, string text, int fontSize, TextAnchor alignment)
    {
        GameObject labelObject = ClanUiFactory.CreateObject("Text", parent, typeof(Text));
        Text label = labelObject.GetComponent<Text>();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = Color.white;
        label.font = GetFont(bold: true);
        label.supportRichText = true;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        return label;
    }

    private static Button CreateButton(Transform parent, string text, Action onClick, float width)
    {
        GameObject buttonObject = ClanUiFactory.CreateObject("Button", parent, typeof(Image), typeof(Button));
        Image image = buttonObject.GetComponent<Image>();
        image.color = Color.white;

        Button button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        buttonObject.AddComponent<DockPointerFocusKeeper>();
        button.onClick.AddListener(new UnityAction(() =>
        {
            onClick();
            RefocusChatInput();
        }));

        Text label = CreateLabel(buttonObject.transform, text, 12, TextAnchor.MiddleCenter);
        RectTransform labelRect = label.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        LayoutElement element = buttonObject.AddComponent<LayoutElement>();
        element.preferredWidth = width;
        element.minWidth = width;

        TryApplyButtonStyle(button);
        SetButtonColor(button, ButtonColor, Color.white);
        return button;
    }

    private static void SetButtonLabel(Button? button, string text)
    {
        if (button == null)
        {
            return;
        }

        foreach (Text label in button.GetComponentsInChildren<Text>(includeInactive: true))
        {
            if (label.transform.parent == button.transform)
            {
                label.text = text;
                return;
            }
        }
    }

    private static void RemoveVanillaStartupHelp(Chat chat)
    {
        if (Localization.instance == null)
        {
            return;
        }

        string emoteList = "Emotes: " + string.Join(
            ", ",
            Enumerable.Range(0, 25)
                .Select(index => ((Emotes)index).ToString().ToLower()));
        string[] expected =
        {
            Localization.instance.Localize("/w [text] - $chat_whisper"),
            Localization.instance.Localize("/s [text] - $chat_shout"),
            Localization.instance.Localize("/die - $chat_kill"),
            Localization.instance.Localize("/resetspawn - $chat_resetspawn"),
            Localization.instance.Localize("/[emote]"),
            Localization.instance.Localize(emoteList),
            ""
        };

        if (ChatBufferField == null || TerminalUpdateChatMethod == null)
        {
            ClanPlugin.ClanLogger.LogWarning(
                "Vanilla startup chat help could not be removed because the chat buffer API was not found.");
            return;
        }

        if (ChatBufferField.GetValue(chat) is not List<string> buffer)
        {
            ClanPlugin.ClanLogger.LogWarning(
                "Vanilla startup chat help could not be removed because the chat buffer was unavailable.");
            return;
        }

        EnsureTrackedChat(chat);
        EnsureChatHistoryAligned(buffer);

        for (int start = 0; start <= buffer.Count - expected.Length; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < expected.Length; offset++)
            {
                if (!string.Equals(
                        buffer[start + offset],
                        expected[offset],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            buffer.RemoveRange(start, expected.Length);
            ChatLineKinds.RemoveRange(start, expected.Length);
            TerminalUpdateChatMethod.Invoke(chat, null);
            return;
        }
    }

    private static void AppendEmojiToken(int emojiIndex)
    {
        if (!ClanEmoji.IsReady || emojiIndex < 0 ||
            emojiIndex >= ClanEmoji.EmojiCount)
        {
            return;
        }

        TMP_InputField? input = GetInputField();
        if (input == null)
        {
            return;
        }

        string token = ClanEmoji.TokenFor(emojiIndex);
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        string addition = token + " ";
        if (ClanEmoji.CountMessageEmojiTokens(input.text) >= ClanEmoji.MessageEmojiLimit)
        {
            ClanRpc.NotifyStatus(
                $"Chat messages can contain up to {ClanEmoji.MessageEmojiLimit} emojis.");
            return;
        }

        int effectiveLimit = ClanDataRules.MaxChatMessageLength;
        if (input.characterLimit > 0)
        {
            effectiveLimit = Math.Min(effectiveLimit, input.characterLimit);
        }
        if (input.text.Length > effectiveLimit - addition.Length)
        {
            ClanRpc.NotifyStatus($"Chat messages are limited to {effectiveLimit} characters.");
            return;
        }

        input.text += addition;
        MoveCaretToEnd(input);
    }

    internal static bool HasFocusedChatInputForHud()
    {
        TMP_InputField? input = GetInputField();
        return input != null &&
               IsChatInputOpen(input) &&
               input.isFocused;
    }

    internal static void RestoreChatInputAfterHudInteraction()
    {
        RefocusChatInput();
    }

    private static void RefocusChatInput()
    {
        TMP_InputField? input = GetInputField();
        if (input == null || !input.gameObject.activeInHierarchy)
        {
            return;
        }

        FocusChatInputAtEnd(input);
    }

    private static void ScheduleChatRefocus(Chat chat)
    {
        _chatRefocusOwner = chat;
        _chatRefocusFrame = Time.frameCount + 1;
        _chatRefocusDeadlineFrame = Time.frameCount + ChatRefocusTimeoutFrames;
    }

    private static void CancelPendingChatRefocus()
    {
        _chatRefocusOwner = null;
        _chatRefocusFrame = -1;
        _chatRefocusDeadlineFrame = -1;
    }

    private static bool HasPendingChatRefocus(Chat chat)
    {
        return ClanPlugin.ChatAfterSend.Value == ClanPlugin.ChatSubmitMode.KeepOpen &&
               _chatRefocusFrame >= 0 &&
               ReferenceEquals(_chatRefocusOwner, chat);
    }

    private static void ApplyPendingChatRefocus(TMP_InputField input)
    {
        if (_chatRefocusFrame < 0 || Time.frameCount < _chatRefocusFrame)
        {
            return;
        }

        if (ClanPlugin.ChatAfterSend.Value != ClanPlugin.ChatSubmitMode.KeepOpen)
        {
            CancelPendingChatRefocus();
            return;
        }

        Chat? chat = _chatRefocusOwner;
        if (chat == null ||
            Chat.instance != chat ||
            ClanPanelController.OwnsSelectedControl ||
            ZInput.GetKeyDown(KeyCode.Escape) ||
            ZInput.GetButtonDown("JoyButtonB") ||
            Console.IsVisible() ||
            TextInput.IsVisible() ||
            Minimap.InTextInput() ||
            Menu.IsVisible() ||
            InventoryGui.IsVisible())
        {
            CancelPendingChatRefocus();
            return;
        }

        if (!input.enabled || !input.interactable)
        {
            CancelPendingChatRefocus();
            return;
        }

        if (input.isFocused)
        {
            MoveCaretToEnd(input);
            CancelPendingChatRefocus();
            return;
        }

        if (_chatRefocusDeadlineFrame >= 0 &&
            Time.frameCount > _chatRefocusDeadlineFrame)
        {
            CancelPendingChatRefocus();
            return;
        }

        EnsureChatVisible(chat, input);
        FocusChatInputAtEnd(input);
        _chatRefocusFrame = Time.frameCount + 1;
    }

    private static void EnsureChatVisible(Chat chat, TMP_InputField input)
    {
        ResetChatHideTimer(chat);
        RectTransform? chatWindow = ((Terminal)chat).m_chatWindow;
        if (chatWindow != null && !chatWindow.gameObject.activeSelf)
        {
            chatWindow.gameObject.SetActive(true);
        }
        if (!input.gameObject.activeSelf)
        {
            input.gameObject.SetActive(true);
        }
    }

    private static AccessTools.FieldRef<Chat, float>? CreateChatHideTimerAccessor()
    {
        try
        {
            return AccessTools.FieldRefAccess<Chat, float>("m_hideTimer");
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Vanilla chat visibility integration is unavailable: {ex.Message}");
            return null;
        }
    }

    private static void ResetChatHideTimer(Chat chat)
    {
        AccessTools.FieldRef<Chat, float>? hideTimer = _chatHideTimer;
        if (hideTimer == null)
        {
            return;
        }

        try
        {
            hideTimer(chat) = 0f;
        }
        catch (Exception ex)
        {
            _chatHideTimer = null;
            ClanPlugin.ClanLogger.LogWarning(
                $"Vanilla chat visibility integration was disabled: {ex.Message}");
        }
    }

    private static void FocusChatInputAtEnd(TMP_InputField input)
    {
        input.ActivateInputField();
        EventSystem.current?.SetSelectedGameObject(input.gameObject);
        MoveCaretToEnd(input);
    }

    private static void MoveCaretToEnd(TMP_InputField input)
    {
        input.caretPosition = input.text.Length;
        input.selectionAnchorPosition = input.text.Length;
        input.selectionFocusPosition = input.text.Length;
        input.MoveTextEnd(false);
    }

    private sealed class ChatScaleDragHandle :
        MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private bool _leftPointerDown;
        private bool _dragging;

        public void OnPointerDown(PointerEventData eventData)
        {
            _leftPointerDown = eventData.button == PointerEventData.InputButton.Left;
            if (_leftPointerDown)
            {
                _isChatScalePointerActive = true;
                _keepResizeCursorReleasedUntil =
                    Time.unscaledTime + ResizeCursorReleaseGrace;
            }
            RefocusChatInput();
            eventData.Use();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _leftPointerDown = false;
            _isChatScalePointerActive = false;
            _keepResizeCursorReleasedUntil =
                Time.unscaledTime + ResizeCursorReleaseGrace;
            RefocusChatInput();
            eventData.Use();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_leftPointerDown ||
                eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            _dragging = true;
            _isChatScalePointerActive = true;
            BeginChatScaleDrag();
            eventData.Use();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging ||
                eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            DragChatScale(eventData.delta);
            _keepResizeCursorReleasedUntil =
                Time.unscaledTime + ResizeCursorReleaseGrace;
            eventData.Use();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            _isChatScalePointerActive = false;
            _keepResizeCursorReleasedUntil =
                Time.unscaledTime + ResizeCursorReleaseGrace;
            EndChatScaleDrag(save: true);
            eventData.Use();
        }

        private void OnDisable()
        {
            _leftPointerDown = false;
            _isChatScalePointerActive = false;
            _keepResizeCursorReleasedUntil =
                Time.unscaledTime + ResizeCursorReleaseGrace;
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            EndChatScaleDrag(save: true);
        }
    }

    private sealed class DockPointerFocusKeeper :
        MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler
    {
        public void OnPointerDown(PointerEventData eventData)
        {
            KeepChatFocus(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            KeepChatFocus(eventData);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            KeepChatFocus(eventData);
        }

        private static void KeepChatFocus(PointerEventData eventData)
        {
            RefocusChatInput();
            eventData.Use();
        }
    }

    private static void SetButtonColor(
        Button? button,
        Color color,
        Color? labelColor = null)
    {
        if (button == null)
        {
            return;
        }

        if (button.targetGraphic is Image image)
        {
            image.color = Color.white;
        }

        ColorBlock colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = Color.Lerp(color, ButtonHoverColor, 0.16f);
        colors.pressedColor = new Color(
            color.r * 0.72f,
            color.g * 0.72f,
            color.b * 0.72f,
            color.a);
        colors.selectedColor = color;
        colors.disabledColor = color;
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.1f;
        button.colors = colors;

        if (!labelColor.HasValue)
        {
            return;
        }

        foreach (Text label in button.GetComponentsInChildren<Text>(includeInactive: true))
        {
            if (label.transform.parent == button.transform)
            {
                label.color = labelColor.Value;
            }
        }
    }

    private static void AddPreferredHeight(GameObject gameObject, float height)
    {
        LayoutElement element = gameObject.GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        element.minHeight = height;
    }

    private static Font GetFont(bool bold)
    {
        try
        {
            return bold ? GUIManager.Instance.AveriaSerifBold : GUIManager.Instance.AveriaSerif;
        }
        catch (Exception)
        {
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }

    private static void TryApplyButtonStyle(Button button)
    {
        try
        {
            GUIManager.Instance.ApplyButtonStyle(button, 12);
        }
        catch (Exception)
        {
        }
    }

    [HarmonyPatch(
        typeof(Terminal),
        nameof(Terminal.AddString),
        new[]
        {
            typeof(PlatformUserID),
            typeof(string),
            typeof(Talker.Type),
            typeof(bool)
        })]
    private static class PlatformChatLineKindPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Talker.Type type, out ChatLineKindScope __state)
        {
            __state = PushChatLineKind(type);
        }

        private static Exception? Finalizer(
            Exception? __exception,
            ChatLineKindScope __state)
        {
            RestoreChatLineKind(__state);
            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(Terminal),
        nameof(Terminal.AddString),
        new[]
        {
            typeof(string),
            typeof(string),
            typeof(Talker.Type),
            typeof(bool)
        })]
    private static class NamedChatLineKindPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Talker.Type type, out ChatLineKindScope __state)
        {
            __state = PushChatLineKind(type);
        }

        private static Exception? Finalizer(
            Exception? __exception,
            ChatLineKindScope __state)
        {
            RestoreChatLineKind(__state);
            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(Terminal),
        nameof(Terminal.AddString),
        new[] { typeof(string) })]
    private static class TrackChatLinePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Terminal __instance)
        {
            PrepareTrackedChatLine(__instance);
        }
    }

    [HarmonyPatch(typeof(Terminal), "UpdateChat")]
    private static class ChatViewFilterPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Terminal __instance)
        {
            return !TryRenderFilteredChat(__instance);
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.Awake))]
    private static class ChatAwakePatch
    {
        private static void Postfix(Chat __instance)
        {
            try
            {
                RemoveVanillaStartupHelp(__instance);
            }
            catch (Exception ex)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Vanilla startup chat help removal was skipped: {ex.GetBaseException().Message}");
            }

            try
            {
                Rebuild();
            }
            catch (Exception ex)
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Clan chat controls will retry after Chat.Awake: {ex.GetBaseException().Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.InputText))]
    private static class RouteInputPatch
    {
        private static bool Prefix(Chat __instance)
        {
            if (!SupportsCurrentChatUi)
            {
                return true;
            }

            if (ClanPanelController.OwnsSelectedControl)
            {
                return false;
            }

            bool shouldRoute = ShouldRouteInput(__instance);
            bool isVanillaChatCommand = IsVanillaChatCommand(__instance);
            TMP_InputField? input = GetInputField(__instance);
            bool isPlainChatMessage = input != null &&
                                      !string.IsNullOrWhiteSpace(input.text) &&
                                      !input.text.TrimStart().StartsWith(
                                          "/",
                                          StringComparison.Ordinal);
            if (input != null &&
                (isPlainChatMessage || isVanillaChatCommand) &&
                !ClanEmoji.IsWithinMessageEmojiLimit(input.text))
            {
                ClanRpc.NotifyStatus(
                    $"Chat messages can contain up to {ClanEmoji.MessageEmojiLimit} emojis.");
                return false;
            }

            if (!shouldRoute)
            {
                if (isVanillaChatCommand)
                {
                    CompleteChatSubmission(__instance);
                }
                return true;
            }

            RouteInput(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.HasFocus))]
    private static class ChatHasFocusPatch
    {
        private static void Postfix(Chat __instance, ref bool __result)
        {
            if (SupportsCurrentChatUi)
            {
                __result |= ClanPanelController.CapturesGameplayInput ||
                            _clanPanelCancelFrame == Time.frameCount ||
                            HasPendingChatRefocus(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Menu), "Update")]
    private static class MenuUpdatePatch
    {
        private static bool Prefix()
        {
            if (!SupportsCurrentChatUi || Menu.IsVisible())
            {
                return true;
            }

            bool consumingPanelCancel =
                _clanPanelCancelFrame == Time.frameCount ||
                (ClanPanelController.IsOpen &&
                 ZInput.GetKeyDown(KeyCode.Escape));
            return !consumingPanelCancel;
        }
    }

    [HarmonyPatch(typeof(TMP_InputField), nameof(TMP_InputField.ActivateInputField))]
    private static class ChatInputActivationPatch
    {
        private static bool Prefix(TMP_InputField __instance)
        {
            if (!SupportsCurrentChatUi)
            {
                return true;
            }

            TMP_InputField? chatInput = GetInputField();
            return chatInput == null ||
                   __instance != chatInput ||
                   !ClanPanelController.OwnsFocusedTextInput;
        }
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
    private static class GameCameraCursorPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(out bool __state)
        {
            // Capture the request before vanilla locks and hides the cursor.
            __state = ShouldReleaseMouseCursor();
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(bool __state)
        {
            if (!__state && !ShouldReleaseMouseCursor())
            {
                return;
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
            }
            if (!Cursor.visible)
            {
                Cursor.visible = true;
            }
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.Update))]
    private static class ChatUpdatePatch
    {
        private static void Prefix(Chat __instance)
        {
            if (SupportsCurrentChatUi &&
                ClanPanelController.IsOpen &&
                _keepChatVisibleForClanPanel)
            {
                ResetChatHideTimer(__instance);
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> source = instructions.ToList();
            var getKeyDown = AccessTools.Method(
                typeof(ZInput),
                nameof(ZInput.GetKeyDown),
                new[] { typeof(KeyCode), typeof(bool) });
            var getKey = AccessTools.Method(
                typeof(ZInput),
                nameof(ZInput.GetKey),
                new[] { typeof(KeyCode), typeof(bool) });
            int matchedKeyReads = 0;

            for (int index = 0; index < source.Count; index++)
            {
                CodeInstruction instruction = source[index];
                yield return instruction;
                if (instruction.opcode != OpCodes.Ldc_I4 ||
                    instruction.operand is not int keyCode ||
                    (keyCode != (int)KeyCode.Mouse0 && keyCode != (int)KeyCode.Mouse1) ||
                    index + 2 >= source.Count ||
                    source[index + 1].opcode != OpCodes.Ldc_I4_1 ||
                    !((getKeyDown != null && source[index + 2].Calls(getKeyDown)) ||
                      (getKey != null && source[index + 2].Calls(getKey))))
                {
                    continue;
                }

                matchedKeyReads++;
                yield return new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(
                        typeof(ClanVanillaChatDock),
                        nameof(FilterChatMouseKeyCode)));
            }

            const int expectedKeyReads = 2;
            if (matchedKeyReads == expectedKeyReads || matchedKeyReads == 0)
            {
                ClanPlugin.ClanLogger.LogDebug(
                    matchedKeyReads == 0
                        ? "Another patch or game version already handles Chat mouse-close input."
                        : $"Chat mouse input patch matched {matchedKeyReads} key reads.");
            }
            else
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Chat mouse input patch matched {matchedKeyReads}/{expectedKeyReads} expected key reads; clicking Clan UI may close chat.");
            }
        }
    }

}
