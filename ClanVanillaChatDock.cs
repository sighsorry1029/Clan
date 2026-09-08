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
    private const float MaximumChatScale = 2f;
    private const float ResizeCursorReleaseGrace = 0.2f;
    private const float CommandConfirmationSeconds = 3f;
    private const int ChatRefocusTimeoutFrames = 4;
    private const int MaximumTrackedChatLines = 300;

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

    private readonly struct ChatLineKindScope
    {
        public ChatLineKindScope(ChatLineKind previous)
        {
            Previous = previous;
        }

        public ChatLineKind Previous { get; }
    }

    private readonly struct MouseCapturePatchState
    {
        public MouseCapturePatchState(
            bool releaseRequested,
            bool originalMouseCapture)
        {
            ReleaseRequested = releaseRequested;
            OriginalMouseCapture = originalMouseCapture;
        }

        public bool ReleaseRequested { get; }
        public bool OriginalMouseCapture { get; }
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
    private static bool? _clanButtonDockInteractive;
    private static bool _canResizeChatPanel;
    private static GameObject? _emojiTray;
    private static RectTransform? _emojiContent;
    private static Button? _clanPanelButton;
    private static Text? _clanPanelButtonLabel;
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
    private static bool _hasCachedClanDockPlacement;
    private static Vector2 _cachedClanDockScreenPoint;
    private static Vector2 _cachedClanDockPivot;
    private static Vector3 _cachedClanDockLocalScale = Vector3.one;
    private static int _clanDockCacheScreenWidth;
    private static int _clanDockCacheScreenHeight;
    private static Rect _clanDockCacheSafeArea;
    private static float _clanDockCacheConfiguredScale;
    private static int _clanDockCacheChatId;
    private static int _clanDockCacheInputId;
    private static bool _isChatScaleDragging;
    private static bool _isChatScalePointerActive;
    private static float _keepResizeCursorReleasedUntil;
    private static int _chatRefocusFrame = -1;
    private static int _chatRefocusDeadlineFrame = -1;
    private static int _emojiShortcutFocusFrame = -1;
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
    private static bool _reopenPanelAfterRebuild;
    private static bool _keepChatVisibleAfterPanelRebuild;
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
        ClanLocalization.LanguageChanged += OnLanguageChanged;
        ClanPlugin.ClanPanelShortcut.SettingChanged += OnPanelShortcutChanged;
        ClanPlugin.EmojiPanelShortcut.SettingChanged += OnPanelShortcutChanged;
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
        ClanLocalization.LanguageChanged -= OnLanguageChanged;
        if (_shortcutSettingSubscribed)
        {
            ClanPlugin.ClanPanelShortcut.SettingChanged -= OnPanelShortcutChanged;
            ClanPlugin.EmojiPanelShortcut.SettingChanged -= OnPanelShortcutChanged;
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
        if (ZNet.instance == null || Chat.instance == null)
        {
            ResetChatPlacementCache();
        }
        if (ZNet.instance == null || Player.m_localPlayer == null || Chat.instance == null)
        {
            DestroyRoot();
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
        bool handledShortcut = false;
        if (!handledPanelCancel && ShouldToggleClanPanelFromShortcut(chat, input))
        {
            ToggleClanPanel(
                keepChatVisibleWhenOpening: IsChatInputOpen(input));
            handledShortcut = true;
        }
        if (!handledPanelCancel &&
            !handledShortcut &&
            ShouldOpenEmojiTrayFromShortcut(chat, input))
        {
            OpenEmojiTrayFromShortcut(chat, input);
        }

        ApplyPendingEmojiShortcutFocus(input);
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
        RefreshClanNotificationPulse(snapshot);
        bool showStandaloneClanNotification = ShouldShowStandaloneClanNotification(
            snapshot,
            inputVisible,
            panelOpen);
        bool showRoot = inputVisible || panelOpen || showStandaloneClanNotification;
        _root.SetActive(showRoot);
        if (_chatChromeRoot != null)
        {
            _chatChromeRoot.SetActive(inputVisible);
        }
        SetClanButtonDockVisibility(inputVisible, showStandaloneClanNotification);
        if (showRoot)
        {
            if (inputVisible)
            {
                RefreshDockPositions(input);
            }
            else if (showStandaloneClanNotification)
            {
                if (_rootRect != null)
                {
                    PlaceStandaloneClanNotification(_rootRect);
                }
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
        Rebuild(preservePanelInteractionState: _reopenPanelAfterRebuild);
    }

    private static void Rebuild(bool preservePanelInteractionState)
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

        bool preserveInteractionState =
            preservePanelInteractionState || _reopenPanelAfterRebuild;
        try
        {
            DestroyRoot(preserveInteractionState);

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
            ClanPanelController.Build(
                _root.transform,
                rect,
                preserveInteractionState: preserveInteractionState);
            BuildResizeHandle(_chatChromeRoot.transform);
            ApplyClanDockScale();
            RefreshDockPositions(input);

            RefreshUi(ClanRpc.CurrentSnapshot);
            ClanPanelController.RefreshDirectory(ClanRpc.CurrentDirectory);
            RestorePanelAfterRebuild();
            bool inputVisible = IsChatInputOpen(input);
            bool panelOpen = ClanPanelController.IsOpen;
            ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
            bool showStandaloneClanNotification = ShouldShowStandaloneClanNotification(
                snapshot,
                inputVisible,
                panelOpen);
            if (showStandaloneClanNotification && _rootRect != null)
            {
                PlaceStandaloneClanNotification(_rootRect);
            }
            _chatChromeRoot.SetActive(inputVisible);
            SetClanButtonDockVisibility(inputVisible, showStandaloneClanNotification);
            _root.SetActive(inputVisible || panelOpen || showStandaloneClanNotification);
        }
        catch
        {
            DestroyRoot(preserveInteractionState);
            throw;
        }
    }

    private static void OnLanguageChanged()
    {
        if (ClanPanelController.IsOpen)
        {
            _reopenPanelAfterRebuild = true;
            _keepChatVisibleAfterPanelRebuild = _keepChatVisibleForClanPanel;
        }

        Rebuild(preservePanelInteractionState: _reopenPanelAfterRebuild);
    }

    private static void RestorePanelAfterRebuild()
    {
        if (!_reopenPanelAfterRebuild)
        {
            return;
        }

        if (!ClanPanelController.IsOpen)
        {
            ClanPanelController.Toggle();
        }
        _keepChatVisibleForClanPanel = _keepChatVisibleAfterPanelRebuild;
        _reopenPanelAfterRebuild = false;
        _keepChatVisibleAfterPanelRebuild = false;
    }

    private static void ClearPanelRebuildRecovery()
    {
        _reopenPanelAfterRebuild = false;
        _keepChatVisibleAfterPanelRebuild = false;
    }

    private static void DestroyRoot(bool preservePanelInteractionState = false)
    {
        if (!preservePanelInteractionState)
        {
            ClearPanelRebuildRecovery();
        }

        if (_isChatScaleDragging)
        {
            EndChatScaleDrag(save: true);
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
        _clanButtonDockInteractive = null;
        _emojiTray = null;
        _emojiContent = null;
        _clanPanelButton = null;
        _clanPanelButtonLabel = null;
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
        _emojiShortcutFocusFrame = -1;
        CancelPendingChatRefocus();
        ChannelButtons.Clear();
        ClanPanelController.DestroyView(preservePanelInteractionState);

        if (root != null)
        {
            UnityEngine.Object.Destroy(root);
        }
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
        bool wasOpen = _emojiTrayOpen;
        _emojiTrayOpen = false;
        if (_emojiTray != null)
        {
            _emojiTray.SetActive(false);
        }
        if (wasOpen)
        {
            _emojiShortcutFocusFrame = -1;
            RefreshEmojiPanelTooltip();
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

    private static bool ShouldShowStandaloneClanNotification(
        ClanClientSnapshot snapshot,
        bool chatInputVisible,
        bool panelOpen)
    {
        return HasActionableClanNotification(snapshot) &&
               !chatInputVisible &&
               !panelOpen &&
               !IsBlockingModalVisible();
    }

    private static bool IsBlockingModalVisible()
    {
        return Console.IsVisible() ||
               TextInput.IsVisible() ||
               Minimap.InTextInput() ||
               Minimap.IsOpen() ||
               Menu.IsVisible() ||
               InventoryGui.IsVisible() ||
               StoreGui.IsVisible() ||
               UnifiedPopup.IsVisible();
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
            ClanLocalization.Text("chat_button_emoji"),
            ToggleEmojiTray,
            out _emojiTrayButton);
        RefreshEmojiPanelTooltip();
        _clanButtonDockRect = BuildRailButtonDock(
            persistentParent,
            "ClanDockClan",
            ClanLocalization.Text("chat_button_clan"),
            ToggleClanPanelFromDock,
            out _clanPanelButton);
        _clanPanelButtonLabel =
            _clanPanelButton.GetComponentInChildren<Text>(includeInactive: true);
        _clanButtonDockCanvasGroup =
            _clanButtonDockRect.gameObject.AddComponent<CanvasGroup>();
        RefreshClanPanelTooltip();
        _chatViewDockRect = BuildRailButtonDock(
            parent,
            "ClanDockViewFilter",
            GetChatViewPresentation().Label,
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

        AddChannelButton(
            dock.transform,
            Channel.Shout,
            ClanLocalization.Text("chat_channel_shout"),
            ShoutButtonWidth);
        AddChannelButton(
            dock.transform,
            Channel.Clan,
            ClanLocalization.Text("chat_channel_clan"),
            ClanChatButtonWidth);
        _whisperButton = AddChannelButton(
            dock.transform,
            Channel.Whisper,
            ClanLocalization.Text("chat_channel_whisper"),
            WhisperButtonWidth);
        _whisperButton.gameObject.SetActive(ClanPlugin.ShowWhisperChatButton.Value.IsOn());
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
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
            ClanLocalization.Text("chat_command_reset_spawn"),
            CommandAction.ResetSpawn,
            ResetSpawnButtonWidth);
        _dieButton = AddCommandButton(
            dock.transform,
            ClanLocalization.Text("chat_command_die"),
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
        Rect chatBounds,
        bool railPositioned,
        bool railOnLeft,
        float railHorizontalPoint)
    {
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
        Rect chatBounds)
    {
        if (!commandRect.gameObject.activeSelf)
        {
            return;
        }

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
        Rect measuredChatBounds,
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

    private static void PlaceStandaloneClanNotification(RectTransform rootRect)
    {
        RectTransform? clanDock = _clanButtonDockRect;
        if (clanDock == null)
        {
            return;
        }

        if (TryPlaceCachedClanDockNotification(clanDock, rootRect))
        {
            return;
        }

        ApplyClanDockScale(GetConfiguredChatScale());
        clanDock.pivot = new Vector2(0.5f, 0.5f);
        PlaceAtScreenPoint(
            clanDock,
            rootRect,
            GetSafeArea().center);
    }

    private static bool TryPlaceCachedClanDockNotification(
        RectTransform clanDock,
        RectTransform rootRect)
    {
        Rect safeArea = GetSafeArea();
        TMP_InputField? input = GetInputField();
        int chatId = Chat.instance != null ? Chat.instance.GetInstanceID() : 0;
        int inputId = input != null ? input.GetInstanceID() : 0;
        bool environmentMatches =
            _clanDockCacheScreenWidth == Screen.width &&
            _clanDockCacheScreenHeight == Screen.height &&
            Approximately(_clanDockCacheSafeArea, safeArea) &&
            Mathf.Approximately(
                _clanDockCacheConfiguredScale,
                GetConfiguredChatScale()) &&
            _clanDockCacheChatId == chatId &&
            _clanDockCacheInputId == inputId;
        if (!_hasCachedClanDockPlacement)
        {
            return false;
        }
        if (!environmentMatches ||
            !IsFinite(_cachedClanDockScreenPoint.x) ||
            !IsFinite(_cachedClanDockScreenPoint.y) ||
            !IsFinitePositive(_cachedClanDockLocalScale.x) ||
            !IsFinitePositive(_cachedClanDockLocalScale.y) ||
            !IsFinitePositive(_cachedClanDockLocalScale.z))
        {
            ResetClanDockPlacementCache();
            return false;
        }

        SetDockScale(clanDock, _cachedClanDockLocalScale);
        clanDock.pivot = _cachedClanDockPivot;
        PlaceAtScreenPoint(
            clanDock,
            rootRect,
            _cachedClanDockScreenPoint);
        return true;
    }

    private static void CacheClanDockPlacement()
    {
        RectTransform? clanDock = _clanButtonDockRect;
        TMP_InputField? input = GetInputField();
        if (clanDock == null || input == null || Chat.instance == null)
        {
            return;
        }

        Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(
            null,
            clanDock.position);
        if (!IsFinite(screenPoint.x) || !IsFinite(screenPoint.y))
        {
            return;
        }

        _hasCachedClanDockPlacement = true;
        _cachedClanDockScreenPoint = screenPoint;
        _cachedClanDockPivot = clanDock.pivot;
        _cachedClanDockLocalScale = clanDock.localScale;
        _clanDockCacheScreenWidth = Screen.width;
        _clanDockCacheScreenHeight = Screen.height;
        _clanDockCacheSafeArea = GetSafeArea();
        _clanDockCacheConfiguredScale = GetConfiguredChatScale();
        _clanDockCacheChatId = Chat.instance.GetInstanceID();
        _clanDockCacheInputId = input.GetInstanceID();
    }

    private static void ResetClanDockPlacementCache()
    {
        _hasCachedClanDockPlacement = false;
        _cachedClanDockScreenPoint = default;
        _cachedClanDockPivot = default;
        _cachedClanDockLocalScale = Vector3.one;
        _clanDockCacheScreenWidth = 0;
        _clanDockCacheScreenHeight = 0;
        _clanDockCacheSafeArea = default;
        _clanDockCacheConfiguredScale = 0f;
        _clanDockCacheChatId = 0;
        _clanDockCacheInputId = 0;
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
        rect.anchoredPosition = localPoint;
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
        ClanUiFactory.ConfigureVerticalScroll(scrollRect, hasOverflow: true);

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
                    ClanLocalization.Text("chat_emoji_gif_badge"),
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
        ClanUiFactory.ConfigureVerticalScroll(scroll, hasOverflow, _emojiContent);
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
               !IsBlockingModalVisible();
    }

    private static bool ShouldOpenEmojiTrayFromShortcut(
        Chat chat,
        TMP_InputField input)
    {
        if (!ClanPlugin.EmojiPanelShortcut.Value.IsKeyDown())
        {
            return false;
        }

        return !input.isFocused &&
               !HasPendingChatRefocus(chat) &&
               !ClanPanelController.OwnsFocusedTextInput &&
               !IsBlockingModalVisible();
    }

    private static bool TryHandleClanPanelCancel()
    {
        if (!ClanPanelController.IsOpen ||
            (!ZInput.GetKeyDown(KeyCode.Escape) &&
             !ZInput.GetButtonDown("JoyButtonB")) ||
            IsBlockingModalVisible())
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
            ClanRpc.RequestSnapshot();
            if (!ClanRpc.IsDirectoryRequestPending &&
                !ClanRpc.IsDirectoryRefreshScheduled)
            {
                ClanRpc.RequestDirectory();
            }
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
            ClanRpc.NotifyStatus(ClanLocalization.Text("chat_emoji_unavailable"));
            return;
        }

        _emojiTrayOpen = !_emojiTrayOpen;
        ClanPanelController.Close();
        if (_emojiTray != null)
        {
            _emojiTray.SetActive(_emojiTrayOpen);
        }
        RefreshButtonStates();
        RefreshEmojiPanelTooltip();
    }

    private static void OpenEmojiTrayFromShortcut(
        Chat chat,
        TMP_InputField input)
    {
        if (!_emojiTrayOpen)
        {
            ToggleEmojiTray();
        }
        if (!_emojiTrayOpen)
        {
            return;
        }

        EnsureChatVisible(chat, input);
        EventSystem.current?.SetSelectedGameObject(null);
        _emojiShortcutFocusFrame = Time.frameCount + 1;
    }

    private static void ApplyPendingEmojiShortcutFocus(TMP_InputField input)
    {
        if (_emojiShortcutFocusFrame < 0 ||
            Time.frameCount < _emojiShortcutFocusFrame)
        {
            return;
        }

        _emojiShortcutFocusFrame = -1;
        Chat? chat = Chat.instance;
        if (!_emojiTrayOpen || chat == null || IsBlockingModalVisible())
        {
            return;
        }

        EnsureChatVisible(chat, input);
        FocusChatInputAtEnd(input);
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
            string.Empty,
            displaySize: ResizeIconSize);
    }

    private static void RefreshDockPositions(TMP_InputField input)
    {
        if (_rootRect == null)
        {
            return;
        }

        RectTransform inputRect = input.GetComponent<RectTransform>();
        RefreshChatPanelTarget(inputRect);
        Rect inputBounds = ResolveChatPlacementBounds(
            inputRect,
            out float dockScale);
        ApplyClanDockScale(dockScale);
        Rect chatBounds = GetChatVisualScreenBounds(_chatPanelRect ?? inputRect);
        bool railPositioned = PlaceLeftRailStack(
            _rootRect,
            chatBounds,
            inputBounds,
            out bool railOnLeft,
            out float railHorizontalPoint);
        if (_channelDockRect != null)
        {
            PlaceChannelDock(
                _channelDockRect,
                _rootRect,
                chatBounds,
                railPositioned,
                railOnLeft,
                railHorizontalPoint);
        }
        if (_commandDockRect != null)
        {
            PlaceCommandDock(_commandDockRect, _rootRect, chatBounds);
        }
        if (railPositioned && IsChatInputOpen(input))
        {
            CacheClanDockPlacement();
        }
        ClanPanelController.RefreshPosition(_rootRect);
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

    private static Rect ResolveChatPlacementBounds(
        RectTransform inputRect,
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
            return liveBounds;
        }

        if (_hasCachedChatInputBounds &&
            IsUsableChatPlacementBounds(_cachedChatInputBounds, safeArea))
        {
            dockScale = _cachedChatDockScale;
            return _cachedChatInputBounds;
        }

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
        if (!IsFiniteNonTrivialRect(bounds))
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
        ResetClanDockPlacementCache();
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
        return ClanUiFactory.GetScreenBounds(rect, camera: null);
    }

    private static Rect GetSafeArea()
    {
        Rect safeArea = Screen.safeArea;
        return safeArea.width > 0f && safeArea.height > 0f
            ? safeArea
            : new Rect(0f, 0f, Screen.width, Screen.height);
    }

    private static bool IsFiniteNonTrivialRect(Rect rect)
    {
        return rect.width > 1f &&
               rect.height > 1f &&
               IsFinite(rect.xMin) &&
               IsFinite(rect.yMin) &&
               IsFinite(rect.xMax) &&
               IsFinite(rect.yMax);
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
        bool hasClan = ClanRpc.CurrentSnapshot.HasClan;
        _chatViewMode = _chatViewMode switch
        {
            ChatViewMode.All => hasClan
                ? ChatViewMode.Clan
                : ChatViewMode.Local,
            ChatViewMode.Clan => ChatViewMode.Local,
            ChatViewMode.Local => ChatViewMode.Global,
            _ => ChatViewMode.All
        };
        RefreshChatViewButton(refreshTooltip: true);
        RefreshChatViewOutput(resetScroll: true);
    }

    private static void RefreshChatViewButton(bool refreshTooltip)
    {
        var presentation = GetChatViewPresentation();
        SetButtonLabel(_chatViewButton, presentation.Label);
        bool filtering = _chatViewMode != ChatViewMode.All;
        SetButtonColor(
            _chatViewButton,
            filtering ? ActiveToggleButtonColor : ButtonColor,
            Color.white);
        if (refreshTooltip && _chatViewButton != null)
        {
            ClanUiFeedback.SetTooltip(
                _chatViewButton,
                presentation.Tooltip,
                placement: ClanTooltipPlacement.LeftOfTarget);
        }
        if (_chatViewButton != null)
        {
            UpdatePlaceholder();
        }
    }

    private static (string Label, string Tooltip, string PlaceholderSummary)
        GetChatViewPresentation()
    {
        return _chatViewMode switch
        {
            ChatViewMode.Clan => (
                ClanLocalization.Text("chat_filter_clan"),
                ClanLocalization.Text("chat_filter_tooltip_clan"),
                ClanLocalization.Text("chat_filter_view_clan")),
            ChatViewMode.Local => (
                ClanLocalization.Text("chat_filter_local"),
                ClanLocalization.Text("chat_filter_tooltip_local"),
                ClanLocalization.Text("chat_filter_view_local")),
            ChatViewMode.Global => (
                ClanLocalization.Text("chat_filter_global"),
                ClanLocalization.Text("chat_filter_tooltip_global"),
                ClanLocalization.Text("chat_filter_view_global")),
            _ => (
                ClanLocalization.Text("chat_filter_all"),
                ClanLocalization.Text("chat_filter_tooltip_all"),
                ClanLocalization.Text("chat_filter_view_all"))
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
            resetPending
                ? ClanLocalization.Text("chat_command_reset_spawn_confirm")
                : ClanLocalization.Text("chat_command_reset_spawn"));
        SetButtonLabel(
            _dieButton,
            diePending
                ? ClanLocalization.Text("chat_command_die_confirm")
                : ClanLocalization.Text("chat_command_die"));
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
                active ? ActiveToggleButtonColor : InactiveToggleButtonColor,
                hasNotification
                    ? ClanUiFeedback.GetNotificationPulseColor()
                    : active
                        ? Color.white
                        : InactiveButtonLabelColor);
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

    private static void RefreshClanNotificationPulse(ClanClientSnapshot snapshot)
    {
        if (_clanPanelButton == null || _clanPanelButtonLabel == null)
        {
            return;
        }

        bool active = ClanPanelController.IsOpen;
        _clanPanelButtonLabel.color = HasActionableClanNotification(snapshot)
            ? ClanUiFeedback.GetNotificationPulseColor()
            : active
                ? Color.white
                : InactiveButtonLabelColor;
    }

    private static void NormalizeUiState(ClanClientSnapshot snapshot)
    {
        if (!snapshot.HasClan && _chatViewMode == ChatViewMode.Clan)
        {
            _chatViewMode = ChatViewMode.All;
            RefreshChatViewButton(refreshTooltip: true);
            RefreshChatViewOutput(resetScroll: true);
        }

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
            string channelPlaceholder = _activeChannel switch
            {
                Channel.Say => ClanLocalization.Text("chat_placeholder_say"),
                Channel.Shout => ClanLocalization.Text("chat_placeholder_shout"),
                Channel.Whisper => ClanLocalization.Text("chat_placeholder_whisper"),
                Channel.Clan => ClanLocalization.Text("chat_placeholder_clan"),
                _ => ClanLocalization.Text("chat_placeholder_default")
            };
            placeholder.text = ClanLocalization.Format(
                "chat_placeholder_with_filter",
                channelPlaceholder,
                GetChatViewPresentation().PlaceholderSummary);
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
        DestroyRoot();

        if (ReferenceEquals(_unsupportedChatWarningOwner, chat))
        {
            return;
        }

        _unsupportedChatWarningOwner = chat;
        string warning = ClanLocalization.Text("chat_replacement_unsupported");
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

    private static void OnPanelShortcutChanged(object sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        RefreshClanPanelTooltip();
        RefreshEmojiPanelTooltip();
    }

    private static void RefreshEmojiPanelTooltip()
    {
        if (_emojiTrayButton == null)
        {
            return;
        }

        if (_emojiTrayOpen)
        {
            ClanUiFeedback.SetTooltip(
                _emojiTrayButton,
                ClanLocalization.Text("emoji_panel_tooltip_click_close"),
                placement: ClanTooltipPlacement.LeftOfTarget);
            return;
        }

        var shortcut = ClanPlugin.EmojiPanelShortcut.Value;
        if (shortcut.MainKey == KeyCode.None)
        {
            ClanUiFeedback.SetTooltip(
                _emojiTrayButton,
                ClanLocalization.Text("emoji_panel_tooltip_click_open"),
                placement: ClanTooltipPlacement.LeftOfTarget);
            return;
        }

        string shortcutLabel = shortcut.ToString();
        if (string.IsNullOrWhiteSpace(shortcutLabel))
        {
            shortcutLabel = shortcut.MainKey.ToString();
        }
        ClanUiFeedback.SetTooltip(
            _emojiTrayButton,
            ClanLocalization.Format(
                "emoji_panel_tooltip_press_open",
                shortcutLabel),
            richText: true,
            placement: ClanTooltipPlacement.LeftOfTarget);
    }

    private static void RefreshClanPanelTooltip()
    {
        if (_clanPanelButton == null)
        {
            return;
        }

        var shortcut = ClanPlugin.ClanPanelShortcut.Value;
        bool closing = ClanPanelController.IsOpen;
        if (shortcut.MainKey == KeyCode.None)
        {
            ClanUiFeedback.SetTooltip(
                _clanPanelButton,
                ClanLocalization.Text(
                    closing
                        ? "clan_panel_tooltip_click_close"
                        : "clan_panel_tooltip_click_open"),
                placement: ClanTooltipPlacement.LeftOfTarget);
            return;
        }

        string shortcutLabel = shortcut.ToString();
        if (string.IsNullOrWhiteSpace(shortcutLabel))
        {
            shortcutLabel = shortcut.MainKey.ToString();
        }
        ClanUiFeedback.SetTooltip(
            _clanPanelButton,
            ClanLocalization.Format(
                closing
                    ? "clan_panel_tooltip_press_close"
                    : "clan_panel_tooltip_press_open",
                shortcutLabel),
            richText: true,
            placement: ClanTooltipPlacement.LeftOfTarget);
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
            ((Terminal)chat).AddString(
                $"<color=#66d9c8>[{ClanLocalization.Text("chat_clan_tag")}]</color> {message}");
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
        label.font = ClanUiFactory.GetBoldFont();
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
                ClanLocalization.Format(
                    "chat_emoji_limit",
                    ClanEmoji.MessageEmojiLimit));
            return;
        }

        int effectiveLimit = ClanDataRules.MaxChatMessageLength;
        if (input.characterLimit > 0)
        {
            effectiveLimit = Math.Min(effectiveLimit, input.characterLimit);
        }
        if (input.text.Length > effectiveLimit - addition.Length)
        {
            ClanRpc.NotifyStatus(
                ClanLocalization.Format("chat_character_limit", effectiveLimit));
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
            IsBlockingModalVisible())
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
        Color labelColor)
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

        SetButtonLabelColor(button, labelColor);
    }

    private static void SetButtonLabelColor(Button button, Color color)
    {
        foreach (Text label in button.GetComponentsInChildren<Text>(includeInactive: true))
        {
            if (label.transform.parent == button.transform)
            {
                label.color = color;
            }
        }
    }

    private static void AddPreferredHeight(GameObject gameObject, float height)
    {
        LayoutElement element = gameObject.GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        element.minHeight = height;
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

    [HarmonyPatch]
    private static class ChatLineKindPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(Terminal),
                nameof(Terminal.AddString),
                new[]
                {
                    typeof(PlatformUserID),
                    typeof(string),
                    typeof(Talker.Type),
                    typeof(bool)
                })!;
            yield return AccessTools.Method(
                typeof(Terminal),
                nameof(Terminal.AddString),
                new[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(Talker.Type),
                    typeof(bool)
                })!;
        }

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
                    ClanLocalization.Format(
                        "chat_emoji_limit",
                        ClanEmoji.MessageEmojiLimit));
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
        private static void Prefix(
            ref bool ___m_mouseCapture,
            out MouseCapturePatchState __state)
        {
            bool releaseRequested = ShouldReleaseMouseCursor();
            __state = new MouseCapturePatchState(
                releaseRequested,
                ___m_mouseCapture);
            if (releaseRequested)
            {
                // Enter chat is not one of vanilla's release conditions. Force
                // this call through vanilla's release branch so the pointer is
                // never hidden and immediately shown again within one frame.
                ___m_mouseCapture = false;
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            ref bool ___m_mouseCapture,
            MouseCapturePatchState __state)
        {
            if (__state.ReleaseRequested)
            {
                // Starting the original call from false exposes its Ctrl+F1
                // capture toggle as true. Apply that toggle to the saved value,
                // then restore the persistent preference.
                bool captureWasToggled = ___m_mouseCapture;
                ___m_mouseCapture = captureWasToggled
                    ? !__state.OriginalMouseCapture
                    : __state.OriginalMouseCapture;
            }

            if (!__state.ReleaseRequested && !ShouldReleaseMouseCursor())
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

        private static Exception? Finalizer(
            ref bool ___m_mouseCapture,
            MouseCapturePatchState __state,
            Exception? __exception)
        {
            if (__exception != null && __state.ReleaseRequested)
            {
                ___m_mouseCapture = __state.OriginalMouseCapture;
            }

            return __exception;
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
