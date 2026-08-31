using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Clan;

/// <summary>
/// Owns the floating clan browser and administration panel opened from vanilla chat.
/// The view is rebuilt only when its backing snapshots, emblem catalogue, or explicit
/// user navigation changes; positioning may be refreshed independently by the chat dock.
/// </summary>
internal static class ClanPanelController
{
    private const float PanelWidth = 790f;
    private const float PanelHeight = 520f;
    private const float PreferredPanelScale = 2f;
    private const float SafeAreaGap = 8f;
    private const float MutationDirectoryRefreshDelay = 2.1f;
    private const float LeftPaneWidth = 358f;
    private const float RightPaneWidth = 408f;
    private const float RightContentWidth = 412f;
    private const float PanelScrollbarWidth = 2f;
    private const float ScrollOverflowEpsilon = 0.5f;

    private static readonly Color PanelColor = new(0.32f, 0.2f, 0.11f, 0.98f);
    private static readonly Color SectionColor = new(0f, 0f, 0f, 0.08f);
    private static readonly Color RowColor = new(0f, 0f, 0f, 0.18f);
    private static readonly Color AlternateRowColor = new(0f, 0f, 0f, 0.1f);
    private static readonly Color ButtonColor = new(0.78f, 0.78f, 0.78f, 0.96f);
    private static readonly Color InactiveToggleButtonColor = new(0.5f, 0.5f, 0.5f, 0.95f);
    private static readonly Color ActiveButtonColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color DisabledButtonColor = new(0.25f, 0.25f, 0.25f, 0.72f);
    private static readonly Color DangerColor = new(0.9f, 0.42f, 0.36f, 0.96f);
    private static readonly Color ActiveGuestBadgeColor = new(0.55f, 0.31f, 0.08f, 0.98f);
    private static readonly Color PickerButtonColor = new(0.18f, 0.18f, 0.18f, 0.26f);
    private static readonly Color ActivePickerButtonColor = new(0.55f, 0.31f, 0.08f, 0.72f);
    private static readonly Color DisabledPickerButtonColor = new(0f, 0f, 0f, 0.08f);
    private static readonly Color InactiveButtonLabelColor = Color.gray;
    private static readonly Color DisabledButtonLabelColor = new(0.4f, 0.4f, 0.4f, 0.9f);
    private static readonly Color MutedColor = new(0.853f, 0.725f, 0.533f, 1f);
    private static readonly Color AccentColor = new(1f, 0.631f, 0.235f, 1f);

    private enum PanelTab
    {
        Members,
        Players
    }

    private enum ConfirmAction
    {
        None,
        RejectApplication,
        Kick,
        TransferLeadership,
        Leave
    }

    private static GameObject? _root;
    private static RectTransform? _rootRect;
    private static RectTransform? _overlayRoot;
    private static Button? _profileActionButton;
    private static Button? _membersTabButton;
    private static Text? _membersTabButtonLabel;
    private static Button? _playersTabButton;
    private static InputField? _clanSearchInput;
    private static InputField? _playerSearchInput;
    private static ScrollRect? _clanScroll;
    private static RectTransform? _clanScrollContent;
    private static ScrollRect? _peopleScroll;
    private static RectTransform? _peopleScrollContent;
    private static GameObject? _editorErrorBanner;
    private static Text? _editorErrorLabel;
    private static ClanClientSnapshot _snapshot = new();
    private static ClanDirectorySnapshot _directory = new();
    private static PanelTab _tab = PanelTab.Members;
    private static ConfirmAction _confirmAction;
    private static string _confirmTarget = "";
    private static float _clanScrollPosition = 1f;
    private static float _rightScrollPosition = 1f;
    private static float _emblemScrollPosition = 1f;
    private static string _clanSearchQuery = "";
    private static string _playerSearchQuery = "";
    private static bool _resetClanScrollOnNextPopulate;
    private static bool _clanRowsDirty;
    private static bool _peopleRowsDirty;
    private static bool _positionValid;
    private static Rect _positionSafeArea;
    private static Rect _positionOverlayBounds;

    private static bool _editorOpen;
    private static bool _editorCreating;
    private static bool _editorSubmissionPending;
    private static long _editorSubmissionRequestId;
    private static string _draftName = "";
    private static string _draftDescription = "";
    private static string _draftEmblemKey = "";
    private static string _editorError = "";
    private static float _directoryRefreshAt = float.PositiveInfinity;
    private static bool _directoryPendingAtLastBuild;

    public static event Action<bool>? OpenStateChanged;

    public static bool IsOpen => _root != null && _root.activeSelf;

    public static bool CapturesGameplayInput =>
        _root != null && _root.activeInHierarchy;

    public static bool OwnsSelectedControl
    {
        get
        {
            EventSystem? eventSystem = EventSystem.current;
            return IsOpen &&
                   eventSystem != null &&
                   IsOwnedControl(eventSystem.currentSelectedGameObject);
        }
    }

    public static bool OwnsFocusedTextInput
    {
        get
        {
            EventSystem? eventSystem = EventSystem.current;
            if (!IsOpen || eventSystem == null)
            {
                return false;
            }

            GameObject? selected = eventSystem.currentSelectedGameObject;
            if (selected == null || !IsOwnedControl(selected))
            {
                return false;
            }

            InputField? input = selected.GetComponent<InputField>();
            return input != null && input.isFocused;
        }
    }

    private static bool IsOwnedControl(GameObject? selected)
    {
        if (_root == null || selected == null)
        {
            return false;
        }

        return selected == _root || selected.transform.IsChildOf(_root.transform);
    }

    private static void ClearOwnedSelection()
    {
        EventSystem? eventSystem = EventSystem.current;
        if (eventSystem != null && IsOwnedControl(eventSystem.currentSelectedGameObject))
        {
            eventSystem.SetSelectedGameObject(null);
        }
    }

    private static void ClearSelectionWithin(Transform parent)
    {
        EventSystem? eventSystem = EventSystem.current;
        GameObject? selected = eventSystem?.currentSelectedGameObject;
        if (eventSystem != null &&
            selected != null &&
            (selected.transform == parent || selected.transform.IsChildOf(parent)))
        {
            eventSystem.SetSelectedGameObject(null);
        }
    }

    public static void Build(
        Transform parent,
        RectTransform overlayRoot,
        bool preserveInteractionState = false)
    {
        DestroyView(preserveInteractionState);

        _overlayRoot = overlayRoot;
        _snapshot = ClanRpc.CurrentSnapshot;
        _directory = ClanRpc.CurrentDirectory;
        _root = CreateWoodPanelObject(
            "ClanPanel",
            parent,
            PanelWidth,
            PanelHeight,
            draggable: true);
        _rootRect = _root.GetComponent<RectTransform>();
        _rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        _rootRect.pivot = new Vector2(0.5f, 0.5f);
        _rootRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        _rootRect.anchoredPosition = Vector2.zero;

        Image background = _root.GetComponent<Image>();
        background.raycastTarget = true;

        _root.SetActive(false);
    }

    public static void Tick()
    {
        RefreshMembersNotificationPulse();

        if (IsOpen && !_editorOpen)
        {
            if (_clanRowsDirty)
            {
                _clanRowsDirty = false;
                PopulateClanRows();
            }
            if (_peopleRowsDirty)
            {
                _peopleRowsDirty = false;
                PopulatePeopleRows();
            }
        }

        if (!IsOpen ||
            float.IsPositiveInfinity(_directoryRefreshAt) ||
            Time.unscaledTime < _directoryRefreshAt ||
            ClanRpc.IsDirectoryRequestPending)
        {
            return;
        }

        _directoryRefreshAt = ClanRpc.RequestDirectory() > 0L
            ? float.PositiveInfinity
            : Time.unscaledTime + MutationDirectoryRefreshDelay;
    }

    public static void DestroyView(bool preserveInteractionState = false)
    {
        ClanUiFeedback.HideTooltip();
        ClearOwnedSelection();
        GameObject? root = _root;
        bool wasOpen = root != null && root.activeSelf;
        _root = null;
        _rootRect = null;
        _overlayRoot = null;
        _profileActionButton = null;
        _membersTabButton = null;
        _membersTabButtonLabel = null;
        _playersTabButton = null;
        _clanSearchInput = null;
        _playerSearchInput = null;
        _clanScroll = null;
        _clanScrollContent = null;
        _peopleScroll = null;
        _peopleScrollContent = null;
        _editorErrorBanner = null;
        _editorErrorLabel = null;
        _positionValid = false;
        if (!preserveInteractionState)
        {
            _editorOpen = false;
            ClearEditorSubmission();
            ClearEditorError();
            _directoryRefreshAt = float.PositiveInfinity;
            ClearConfirmation();
        }
        _directoryPendingAtLastBuild = false;
        _resetClanScrollOnNextPopulate = false;
        _clanRowsDirty = false;
        _peopleRowsDirty = false;

        if (root != null)
        {
            root.SetActive(false);
            UnityEngine.Object.Destroy(root);
        }

        if (wasOpen)
        {
            OpenStateChanged?.Invoke(false);
        }
    }

    public static void Toggle()
    {
        if (_root == null)
        {
            return;
        }

        if (_root.activeSelf)
        {
            Close();
            return;
        }

        _root.SetActive(true);
        _root.transform.SetAsLastSibling();
        RebuildView();
        PositionPanel();
        OpenStateChanged?.Invoke(true);
    }

    public static void Close()
    {
        if (_root == null)
        {
            return;
        }

        bool wasOpen = _root.activeSelf;
        ClanUiFeedback.HideTooltip();
        ClearOwnedSelection();
        _editorOpen = false;
        ClearEditorSubmission();
        ClearConfirmation();
        _root.SetActive(false);

        if (wasOpen)
        {
            OpenStateChanged?.Invoke(false);
        }
    }

    public static bool HandleCancelInput()
    {
        if (!IsOpen)
        {
            return false;
        }

        ClanUiFeedback.HideTooltip();
        ClearOwnedSelection();
        if (_editorOpen)
        {
            CloseEditor();
            return true;
        }

        if (_confirmAction != ConfirmAction.None)
        {
            ClearConfirmation();
            RebuildView();
            return true;
        }

        Close();
        return true;
    }

    public static bool ContainsPointer(Vector2 pointerPosition)
    {
        return _rootRect != null &&
               _rootRect.gameObject.activeInHierarchy &&
               RectTransformUtility.RectangleContainsScreenPoint(
                   _rootRect,
                   pointerPosition,
                   ClanUiFactory.GetCanvasCamera(_rootRect));
    }

    public static void RefreshPosition(RectTransform overlayRoot)
    {
        _overlayRoot = overlayRoot;
        if (IsOpen)
        {
            PositionPanel();
        }
    }

    public static void RefreshSnapshot(ClanClientSnapshot snapshot)
    {
        ClanClientSnapshot next = snapshot ?? new ClanClientSnapshot();
        string previousInviteId = _snapshot.Invite?.InviteId ?? "";
        string nextInviteId = next.Invite?.InviteId ?? "";
        bool newInviteReceived =
            next.Invite != null &&
            !StringComparer.Ordinal.Equals(previousInviteId, nextInviteId);
        string previousEffectiveClanId = _snapshot.ClanId;
        bool effectiveClanChanged = !StringComparer.Ordinal.Equals(
            previousEffectiveClanId,
            next.ClanId);
        bool directoryRelationshipChanged =
            !StringComparer.Ordinal.Equals(_snapshot.PrimaryClanId, next.PrimaryClanId) ||
            !StringComparer.Ordinal.Equals(_snapshot.GuestClanId, next.GuestClanId) ||
            !StringComparer.Ordinal.Equals(previousInviteId, nextInviteId) ||
            !StringComparer.Ordinal.Equals(
                _snapshot.OwnApplicationClanId,
                next.OwnApplicationClanId);
        bool responseArrived = !ReferenceEquals(_snapshot, next);
        bool presentationChanged = !HasSameSnapshotPresentation(_snapshot, next);
        bool moderationAccessChanged = _snapshot.CanModerate != next.CanModerate;
        bool editorWasOpen = _editorOpen;
        bool editorResponseArrived =
            responseArrived &&
            _editorSubmissionPending &&
            _editorSubmissionRequestId > 0L &&
            next.ResponseRequestId == _editorSubmissionRequestId;
        bool editorResponseSucceeded =
            editorResponseArrived && SubmittedProfileMatches(next);
        _snapshot = next;
        if (newInviteReceived)
        {
            _clanScrollPosition = 1f;
            _resetClanScrollOnNextPopulate = true;
        }
        if (moderationAccessChanged ||
            effectiveClanChanged ||
            directoryRelationshipChanged)
        {
            ClanRpc.InvalidateDirectory();
            _directory = ClanRpc.CurrentDirectory;
            ScheduleDirectoryRefresh();
        }
        if (presentationChanged)
        {
            ClearConfirmation();
        }
        if (responseArrived)
        {
            if (_editorOpen && !_editorCreating &&
                (!_snapshot.HasClan || !_snapshot.IsLeader))
            {
                _editorOpen = false;
                ClearEditorSubmission();
                ClearEditorError();
            }
            else if (editorResponseSucceeded)
            {
                _editorOpen = false;
                ClearEditorSubmission();
                ClearEditorError();
            }
            else if (editorResponseArrived)
            {
                ClearEditorSubmission();
                ShowEditorError(
                    string.IsNullOrWhiteSpace(_snapshot.Status)
                        ? ClanLocalization.Text("panel_profile_save_failed")
                        : ClanLocalization.ResolveStatus(_snapshot.Status));
                if (IsOpen)
                {
                    RebuildView();
                }
            }
        }

        bool editorClosed = editorWasOpen && !_editorOpen;
        if (IsOpen && editorClosed && !_editorOpen)
        {
            RebuildView();
        }
        else if (IsOpen && presentationChanged && !_editorOpen)
        {
            RefreshHeaderState();
            _clanRowsDirty = true;
            _peopleRowsDirty = true;
        }
    }

    public static void RefreshDirectory(ClanDirectorySnapshot directory)
    {
        ClanDirectorySnapshot next = directory ?? new ClanDirectorySnapshot();
        if (next.RequestId > 0L)
        {
            _directoryRefreshAt = float.PositiveInfinity;
        }
        bool presentationChanged = !HasSameDirectoryPresentation(_directory, next);
        _directory = next;
        bool pendingStateChanged =
            IsOpen &&
            _directoryPendingAtLastBuild != ClanRpc.IsDirectoryRequestPending;
        if (IsOpen &&
            (presentationChanged || pendingStateChanged) &&
            !_editorOpen)
        {
            _clanRowsDirty = true;
            if (_tab == PanelTab.Players)
            {
                _peopleRowsDirty = true;
            }
        }
    }

    private static bool HasSameSnapshotPresentation(
        ClanClientSnapshot left,
        ClanClientSnapshot right)
    {
        return StringComparer.Ordinal.Equals(left.ClanId, right.ClanId) &&
               StringComparer.Ordinal.Equals(left.PrimaryClanId, right.PrimaryClanId) &&
               StringComparer.Ordinal.Equals(left.PrimaryClanName, right.PrimaryClanName) &&
               left.PrimaryRole == right.PrimaryRole &&
               StringComparer.Ordinal.Equals(left.GuestClanId, right.GuestClanId) &&
               StringComparer.Ordinal.Equals(left.GuestClanName, right.GuestClanName) &&
               StringComparer.Ordinal.Equals(left.ClanName, right.ClanName) &&
               StringComparer.Ordinal.Equals(left.ClanDescription, right.ClanDescription) &&
               StringComparer.Ordinal.Equals(left.ClanEmblemKey, right.ClanEmblemKey) &&
               left.SelfRole == right.SelfRole &&
               HaveSameItems(left.Roster, right.Roster, HasSameRosterPlayer) &&
               HaveSameItems(left.Applications, right.Applications, HasSameApplication) &&
               HasSameInvite(left.Invite, right.Invite) &&
               StringComparer.Ordinal.Equals(
                   left.OwnApplicationClanId,
                   right.OwnApplicationClanId) &&
               StringComparer.Ordinal.Equals(
                   left.OwnApplicationClanName,
                   right.OwnApplicationClanName);
    }

    private static bool HasSameDirectoryPresentation(
        ClanDirectorySnapshot left,
        ClanDirectorySnapshot right)
    {
        return left.IsTruncated == right.IsTruncated &&
               HaveSameItems(left.PublicClans, right.PublicClans, HasSamePublicClan) &&
               HaveSameItems(left.Players, right.Players, HasSameDirectoryPlayer);
    }

    private static bool HasSameRosterPlayer(
        ClanPlayerSummary left,
        ClanPlayerSummary right)
    {
        return StringComparer.Ordinal.Equals(left.Id, right.Id) &&
               StringComparer.Ordinal.Equals(left.Name, right.Name) &&
               left.Role == right.Role &&
               left.IsSelf == right.IsSelf &&
               left.IsOnline == right.IsOnline;
    }

    private static bool HasSameApplication(
        ClanApplicationSummary left,
        ClanApplicationSummary right)
    {
        return StringComparer.Ordinal.Equals(left.PlayerId, right.PlayerId) &&
               StringComparer.Ordinal.Equals(left.PlayerName, right.PlayerName);
    }

    private static bool HasSameInvite(ClanInviteSummary? left, ClanInviteSummary? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        return left != null &&
               right != null &&
               StringComparer.Ordinal.Equals(left.InviteId, right.InviteId) &&
               StringComparer.Ordinal.Equals(left.ClanId, right.ClanId) &&
               StringComparer.Ordinal.Equals(left.ClanName, right.ClanName) &&
               StringComparer.Ordinal.Equals(left.FromName, right.FromName);
    }

    private static bool HasSamePublicClan(
        ClanPublicSummary left,
        ClanPublicSummary right)
    {
        return StringComparer.Ordinal.Equals(left.ClanId, right.ClanId) &&
               StringComparer.Ordinal.Equals(left.Name, right.Name) &&
               StringComparer.Ordinal.Equals(left.Description, right.Description) &&
               StringComparer.Ordinal.Equals(left.EmblemKey, right.EmblemKey) &&
               StringComparer.Ordinal.Equals(left.LeaderName, right.LeaderName);
    }

    private static bool HasSameDirectoryPlayer(
        ClanDirectoryPlayerSummary left,
        ClanDirectoryPlayerSummary right)
    {
        return StringComparer.Ordinal.Equals(left.PlayerId, right.PlayerId) &&
               StringComparer.Ordinal.Equals(left.PlayerName, right.PlayerName) &&
               left.State == right.State &&
               StringComparer.Ordinal.Equals(left.ClanName, right.ClanName) &&
               left.IsOnline == right.IsOnline &&
               left.IsSelf == right.IsSelf &&
               left.CanInvite == right.CanInvite &&
               left.CanResolveApplication == right.CanResolveApplication &&
               left.LastSeenUtcTicks == right.LastSeenUtcTicks;
    }

    private static bool HaveSameItems<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, T, bool> equals)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (!equals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    public static void OnEmblemsChanged()
    {
        if (!IsOpen)
        {
            return;
        }
        if (_editorOpen)
        {
            RebuildView();
            return;
        }

        _clanRowsDirty = true;
    }

    private static void RebuildView()
    {
        if (_root == null)
        {
            return;
        }

        ClanUiFeedback.HideTooltip();
        ClearOwnedSelection();
        ClanUiFactory.ClearChildren(_root.transform);
        _profileActionButton = null;
        _membersTabButton = null;
        _membersTabButtonLabel = null;
        _playersTabButton = null;
        _clanSearchInput = null;
        _playerSearchInput = null;
        _clanScroll = null;
        _clanScrollContent = null;
        _peopleScroll = null;
        _peopleScrollContent = null;
        _clanRowsDirty = false;
        _peopleRowsDirty = false;
        _editorErrorBanner = null;
        _editorErrorLabel = null;
        _directoryPendingAtLastBuild = ClanRpc.IsDirectoryRequestPending;

        BuildHeader(_root.transform);
        BuildOverview(_root.transform);
        BuildPeoplePane(_root.transform);
        if (_editorOpen)
        {
            BuildEditor(_root.transform);
        }
    }

    private static void BuildHeader(Transform parent)
    {
        const float rightContentX = LeftPaneWidth - 3f;
        GameObject header = CreateRect(
            "Header",
            parent,
            8f,
            464f,
            PanelWidth - 16f,
            48f,
            typeof(Image));
        header.GetComponent<Image>().color = SectionColor;

        Text title = CreateLabel(
            header.transform,
            ClanLocalization.Text("panel_title"),
            10f,
            7f,
            48f,
            34f,
            19,
            TextAnchor.MiddleLeft,
            ClanUiFactory.GetClanColor());
        title.fontStyle = FontStyle.Bold;

        bool canCreateClan = !_snapshot.HasAnyClan;
        bool canEditClan = _snapshot.IsLeader;
        _profileActionButton = CreateIconButton(
            header.transform,
            ClanActionIcon.Edit,
            canCreateClan
                ? ClanLocalization.Text("panel_tooltip_create_profile")
                : canEditClan
                    ? ClanLocalization.Text("panel_tooltip_edit_profile")
                    : _snapshot.HasGuestClan &&
                      _snapshot.PrimaryRole == ClanRole.Leader
                        ? ClanLocalization.Text("panel_tooltip_leave_guest_to_edit")
                        : ClanLocalization.Text("panel_tooltip_leader_only_edit"),
            canCreateClan ? OpenCreateEditor : OpenEditEditor,
            90f,
            7f,
            40f,
            34f);
        _profileActionButton.name = canCreateClan
            ? "CreateClanProfile"
            : "EditClanProfile";
        if (!canCreateClan && !canEditClan)
        {
            _profileActionButton.interactable = false;
            SetButtonContentColor(_profileActionButton, DisabledButtonLabelColor);
        }

        _clanSearchInput = CreateInputField(
            header.transform,
            "ClanSearch",
            _clanSearchQuery,
            ClanLocalization.Text("panel_search_clans"),
            136f,
            7f,
            140f,
            34f,
            64,
            fontSize: 11);
        _clanSearchInput.onValueChanged.AddListener(
            new UnityAction<string>(OnClanSearchChanged));

        if (ZNet.instance?.IsServer() == true)
        {
            Button openMediaDirectoryButton = CreateIconButton(
                header.transform,
                ClanActionIcon.Folder,
                ClanLocalization.Text("panel_tooltip_open_media_folder"),
                OpenClanMediaDirectory,
                282f,
                7f,
                40f,
                34f);
            openMediaDirectoryButton.name = "OpenClanMediaDirectory";
        }

        bool membersActive = _tab == PanelTab.Members;
        _membersTabButton = CreateButton(
            header.transform,
            ClanLocalization.Text("panel_tab_members"),
            () => SelectTab(PanelTab.Members),
            rightContentX,
            7f,
            94f,
            34f,
            membersActive ? ActiveButtonColor : InactiveToggleButtonColor,
            13);
        _membersTabButtonLabel =
            _membersTabButton.GetComponentInChildren<Text>(includeInactive: true);
        RefreshMembersNotificationPulse();

        bool playersActive = _tab == PanelTab.Players;
        _playersTabButton = CreateButton(
            header.transform,
            ClanLocalization.Text("panel_tab_players"),
            () => SelectTab(PanelTab.Players),
            rightContentX + 100f,
            7f,
            94f,
            34f,
            playersActive ? ActiveButtonColor : InactiveToggleButtonColor,
            13);
        SetButtonLabelColor(
            _playersTabButton,
            playersActive ? Color.white : InactiveButtonLabelColor);

        _playerSearchInput = CreateInputField(
            header.transform,
            "PlayerSearch",
            _playerSearchQuery,
            ClanLocalization.Text("panel_search_player_clan"),
            rightContentX + 200f,
            7f,
            140f,
            34f,
            64,
            fontSize: 11);
        _playerSearchInput.onValueChanged.AddListener(
            new UnityAction<string>(OnPlayerSearchChanged));

        CreateButton(
            header.transform,
            "×",
            Close,
            724f,
            7f,
            40f,
            34f,
            ButtonColor,
            20);
    }

    private static void OpenClanMediaDirectory()
    {
        if (GUIManager.IsHeadless() || ZNet.instance?.IsServer() != true)
        {
            return;
        }

        string path = "";
        try
        {
            path = Path.GetFullPath(ClanPlugin.MediaDirectory);
            Directory.CreateDirectory(path);
            ClanUiFeedback.HideTooltip();
            Application.OpenURL(path + Path.DirectorySeparatorChar);
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Could not open Clan media directory '{path}': {exception.Message}");
        }
    }

    private static void RefreshHeaderState()
    {
        Button? profileAction = _profileActionButton;
        if (profileAction != null)
        {
            bool canCreateClan = !_snapshot.HasAnyClan;
            bool canEditClan = _snapshot.IsLeader;
            string tooltip = canCreateClan
                ? ClanLocalization.Text("panel_tooltip_create_profile")
                : canEditClan
                    ? ClanLocalization.Text("panel_tooltip_edit_profile")
                    : _snapshot.HasGuestClan &&
                      _snapshot.PrimaryRole == ClanRole.Leader
                        ? ClanLocalization.Text("panel_tooltip_leave_guest_to_edit")
                        : ClanLocalization.Text("panel_tooltip_leader_only_edit");
            profileAction.name = canCreateClan
                ? "CreateClanProfile"
                : "EditClanProfile";
            profileAction.onClick.RemoveAllListeners();
            UnityAction profileActionHandler = canCreateClan
                ? new UnityAction(OpenCreateEditor)
                : new UnityAction(OpenEditEditor);
            profileAction.onClick.AddListener(profileActionHandler);
            profileAction.interactable = canCreateClan || canEditClan;
            SetButtonContentColor(
                profileAction,
                profileAction.interactable ? Color.white : DisabledButtonLabelColor);
            ClanUiFeedback.SetTooltip(profileAction, tooltip);
        }

        bool membersActive = _tab == PanelTab.Members;
        if (_membersTabButton != null)
        {
            SetButtonColor(
                _membersTabButton,
                membersActive ? ActiveButtonColor : InactiveToggleButtonColor);
            RefreshMembersNotificationPulse();
        }

        bool playersActive = _tab == PanelTab.Players;
        if (_playersTabButton != null)
        {
            SetButtonColor(
                _playersTabButton,
                playersActive ? ActiveButtonColor : InactiveToggleButtonColor);
            SetButtonLabelColor(
                _playersTabButton,
                playersActive ? Color.white : InactiveButtonLabelColor);
        }
    }

    private static bool HasPendingApplications()
    {
        return _snapshot.CanModerate && _snapshot.Applications.Count > 0;
    }

    private static void RefreshMembersNotificationPulse()
    {
        if (_membersTabButton == null || _membersTabButtonLabel == null)
        {
            return;
        }

        _membersTabButtonLabel.color = HasPendingApplications()
            ? ClanUiFeedback.GetNotificationPulseColor()
            : _tab == PanelTab.Members
                ? Color.white
                : InactiveButtonLabelColor;
    }

    private static void BuildOverview(Transform parent)
    {
        GameObject pane = CreateRect(
            "ClanOverview",
            parent,
            8f,
            8f,
            LeftPaneWidth,
            456f,
            typeof(Image));
        pane.GetComponent<Image>().color = SectionColor;

        _clanScroll = CreateScrollView(
            pane.transform,
            "ClanRows",
            8f,
            4f,
            342f,
            448f,
            out _clanScrollContent,
            _clanScrollPosition,
            value => _clanScrollPosition = value,
            scrollbarTopInset: 4f);

        PopulateClanRows();
    }

    private static void PopulateClanRows()
    {
        ScrollRect? scroll = _clanScroll;
        RectTransform? content = _clanScrollContent;
        if (scroll == null || content == null)
        {
            return;
        }

        ClanUiFeedback.HideTooltip();
        ClearSelectionWithin(content);
        scroll.StopMovement();
        ClanUiFactory.ClearChildren(content);
        _clanRowsDirty = false;
        _directoryPendingAtLastBuild = ClanRpc.IsDirectoryRequestPending;
        IReadOnlyList<ClanPublicSummary> source = _directory.PublicClans;
        IReadOnlyList<ClanPublicSummary> clans = source
            .Where(MatchesClanSearch)
            .ToArray();

        const float rowHeight = 76f;
        const float rowGap = 4f;
        const float rowStart = 4f;
        if (clans.Count == 0)
        {
            string emptyText = source.Count > 0 &&
                               !string.IsNullOrEmpty(_clanSearchQuery)
                ? ClanLocalization.Text("panel_no_clans_match")
                : ClanRpc.IsDirectoryRequestPending ||
                  !float.IsPositiveInfinity(_directoryRefreshAt)
                    ? ClanLocalization.Text("panel_loading_clans")
                    : ClanLocalization.Text("panel_no_clans");
            CreateTopLabel(
                content,
                emptyText,
                8f,
                202f,
                326f,
                44f,
                13,
                TextAnchor.MiddleCenter,
                MutedColor);
        }

        for (int index = 0; index < clans.Count; index++)
        {
            BuildClanRow(
                content,
                clans[index],
                rowStart + index * (rowHeight + rowGap),
                rowHeight,
                index % 2 == 1);
        }

        float contentHeight = rowStart + clans.Count * (rowHeight + rowGap);
        SetScrollContentHeight(scroll, content, contentHeight, 448f);
        if (_resetClanScrollOnNextPopulate)
        {
            _clanScrollPosition = 1f;
            _resetClanScrollOnNextPopulate = false;
        }
        scroll.verticalNormalizedPosition = Mathf.Clamp01(_clanScrollPosition);
    }

    private static void BuildClanRow(
        RectTransform content,
        ClanPublicSummary clan,
        float top,
        float height,
        bool alternate)
    {
        GameObject row = ClanUiFactory.CreateObject(
            $"ClanRow.{clan.ClanId}",
            content,
            typeof(Image));
        PlaceTop(row.GetComponent<RectTransform>(), 3f, top, 336f, height);
        bool connected = _snapshot.IsConnectedToClan(clan.ClanId);
        bool effective = StringComparer.Ordinal.Equals(_snapshot.ClanId, clan.ClanId);
        bool activeGuest = effective &&
                           StringComparer.Ordinal.Equals(
                               _snapshot.GuestClanId,
                               clan.ClanId);
        ClanInviteSummary? selectedInvite = !connected &&
                                               _snapshot.Invite != null &&
                                               StringComparer.Ordinal.Equals(
                                                   _snapshot.Invite.ClanId,
                                                   clan.ClanId)
            ? _snapshot.Invite
            : null;
        if (effective)
        {
            Color clanColor = ClanUiFactory.GetClanColor();
            row.GetComponent<Image>().color = new Color(
                clanColor.r * 0.35f,
                clanColor.g * 0.35f,
                clanColor.b * 0.35f,
                0.72f);
        }
        else
        {
            row.GetComponent<Image>().color = connected
                ? new Color(0.18f, 0.14f, 0.1f, 0.72f)
                : alternate ? AlternateRowColor : RowColor;
        }

        AddEmblem(row.transform, clan.EmblemKey, 6f, 7f, 62f, 62f, clan.Name);
        Text name = CreateLabel(
            row.transform,
            clan.Name,
            76f,
            49f,
            100f,
            22f,
            12,
            TextAnchor.MiddleLeft,
            ClanUiFactory.GetClanColor());
        name.resizeTextForBestFit = true;
        name.resizeTextMinSize = 8;
        name.resizeTextMaxSize = 12;

        Text leader = CreateLabel(
            row.transform,
            ClanLocalization.Format("panel_leader_name", clan.LeaderName),
            180f,
            49f,
            76f,
            22f,
            9,
            TextAnchor.MiddleRight,
            MutedColor);
        leader.resizeTextForBestFit = true;
        leader.resizeTextMinSize = 7;
        leader.resizeTextMaxSize = 9;

        Text description = CreateLabel(
            row.transform,
            string.IsNullOrWhiteSpace(clan.Description)
                ? "—"
                : clan.Description,
            76f,
            5f,
            activeGuest ? 166f : 254f,
            42f,
            9,
            TextAnchor.UpperLeft,
            string.IsNullOrWhiteSpace(clan.Description) ? MutedColor : Color.white);
        description.verticalOverflow = VerticalWrapMode.Truncate;

        if (activeGuest)
        {
            GameObject badge = ClanUiFactory.CreateObject(
                "ActiveGuestBadge",
                row.transform,
                typeof(Image));
            Place(badge.GetComponent<RectTransform>(), 246f, 29f, 84f, 16f);
            Image badgeImage = badge.GetComponent<Image>();
            badgeImage.color = ActiveGuestBadgeColor;
            badgeImage.raycastTarget = false;

            Text badgeLabel = CreateLabel(
                badge.transform,
                ClanLocalization.Text("panel_active_guest"),
                3f,
                0f,
                78f,
                16f,
                8,
                TextAnchor.MiddleCenter,
                Color.white);
            badgeLabel.resizeTextForBestFit = true;
            badgeLabel.resizeTextMinSize = 6;
            badgeLabel.resizeTextMaxSize = 8;
        }

        BuildClanRowAction(row.transform, clan, selectedInvite);
    }

    private static void BuildClanRowAction(
        Transform parent,
        ClanPublicSummary clan,
        ClanInviteSummary? selectedInvite)
    {
        const float actionX = 260f;
        const float actionY = 48f;
        const float actionWidth = 70f;
        const float actionHeight = 23f;

        if (_snapshot.IsConnectedToClan(clan.ClanId))
        {
            bool isEffective = StringComparer.Ordinal.Equals(_snapshot.ClanId, clan.ClanId);
            if (!isEffective)
            {
                CreateDisabledClanAction(
                    parent,
                    ClanLocalization.Text("panel_action_inactive"),
                    BuildClanRowTooltip(
                        clan,
                        ClanLocalization.Format(
                            "panel_tooltip_inactive_guest",
                            _snapshot.GuestClanName)),
                    actionX,
                    actionY,
                    actionWidth,
                    actionHeight);
                return;
            }

            bool mustTransferLeadership = _snapshot.IsLeader &&
                                          _snapshot.Roster.Any(member =>
                                              !member.IsSelf &&
                                              member.Role != ClanRole.Guest);
            bool disband = _snapshot.IsLeader && !mustTransferLeadership;
            bool confirming = !mustTransferLeadership &&
                              IsConfirming(ConfirmAction.Leave, clan.ClanId);
            if (mustTransferLeadership)
            {
                CreateDisabledClanAction(
                    parent,
                    ClanLocalization.Text("panel_action_transfer"),
                    BuildClanRowTooltip(
                        clan,
                        ClanLocalization.Text("panel_tooltip_transfer_before_leaving")),
                    actionX,
                    actionY,
                    actionWidth,
                    actionHeight);
                return;
            }

            Button membershipButton = CreateButton(
                parent,
                confirming
                    ? ClanLocalization.Text("common_confirm_question")
                    : disband
                        ? ClanLocalization.Text("panel_action_disband")
                        : ClanLocalization.Text("panel_action_leave"),
                () => ConfirmThen(
                    ConfirmAction.Leave,
                    clan.ClanId,
                    () => SendRequest(
                        new ClanRequest
                        {
                            Type = ClanRequestType.LeaveClan,
                            ClanId = clan.ClanId
                        })),
                actionX,
                actionY,
                actionWidth,
                actionHeight,
                confirming ? DangerColor : ButtonColor,
                9);
            ClanUiFeedback.SetTooltip(
                membershipButton,
                BuildClanRowTooltip(
                    clan,
                    confirming
                        ? ClanLocalization.Text("common_click_again_confirm")
                        : disband
                            ? ClanLocalization.Format(
                                "panel_tooltip_disband",
                                clan.Name)
                            : ClanLocalization.Format(
                                "panel_tooltip_leave",
                                clan.Name)));
            return;
        }

        if (selectedInvite != null)
        {
            ClanInviteSummary invite = selectedInvite;
            Button accept = CreateIconButton(
                parent,
                ClanActionIcon.Accept,
                BuildClanRowTooltip(
                    clan,
                    ClanLocalization.Format(
                        "panel_tooltip_accept_invite",
                        invite.ClanName,
                        invite.FromName)),
                () => SendRequest(
                    new ClanRequest
                    {
                        Type = ClanRequestType.AcceptInvite,
                        InviteId = invite.InviteId
                    }),
                260f,
                48f,
                33f,
                23f,
                ButtonColor);
            accept.name = "AcceptInvite";

            Button decline = CreateIconButton(
                parent,
                ClanActionIcon.Decline,
                BuildClanRowTooltip(
                    clan,
                    ClanLocalization.Format(
                        "panel_tooltip_decline_invite",
                        invite.ClanName,
                        invite.FromName)),
                () => SendRequest(
                    new ClanRequest
                    {
                        Type = ClanRequestType.DeclineInvite,
                        InviteId = invite.InviteId
                    }),
                297f,
                48f,
                33f,
                23f,
                ButtonColor);
            decline.name = "DeclineInvite";
            return;
        }

        if (_snapshot.HasOwnApplication)
        {
            if (StringComparer.Ordinal.Equals(
                    _snapshot.OwnApplicationClanId,
                    clan.ClanId))
            {
                Button cancel = CreateButton(
                    parent,
                    ClanLocalization.Text("common_cancel"),
                    () => SendRequest(
                        ClanRequest.Simple(ClanRequestType.CancelApplication)),
                    actionX,
                    actionY,
                    actionWidth,
                    actionHeight,
                    ButtonColor,
                    9);
                ClanUiFeedback.SetTooltip(
                    cancel,
                    BuildClanRowTooltip(
                        clan,
                        ClanLocalization.Format(
                            "panel_tooltip_cancel_application",
                            clan.Name)));
            }
            else
            {
                CreateDisabledClanAction(
                    parent,
                    ClanLocalization.Text("panel_state_pending"),
                    BuildClanRowTooltip(
                        clan,
                        ClanLocalization.Format(
                            "panel_tooltip_application_pending",
                            _snapshot.OwnApplicationClanName)),
                    actionX,
                    actionY,
                    actionWidth,
                    actionHeight);
            }
            return;
        }

        if (_snapshot.HasGuestClan)
        {
            CreateDisabledClanAction(
                parent,
                ClanLocalization.Text("panel_state_unavailable"),
                BuildClanRowTooltip(
                    clan,
                    ClanLocalization.Format(
                        "panel_tooltip_guest_slot_used",
                        _snapshot.GuestClanName)),
                actionX,
                actionY,
                actionWidth,
                actionHeight);
            return;
        }

        Button apply = CreateButton(
            parent,
            ClanLocalization.Text("panel_action_apply"),
            () => ApplyToClan(clan.ClanId),
            actionX,
            actionY,
            actionWidth,
            actionHeight,
            ActiveButtonColor,
            9);
        ClanUiFeedback.SetTooltip(
            apply,
            BuildClanRowTooltip(
                clan,
                ClanLocalization.Format("panel_tooltip_apply", clan.Name)));
    }

    private static string BuildClanRowTooltip(
        ClanPublicSummary clan,
        string action)
    {
        string description = string.IsNullOrWhiteSpace(clan.Description)
            ? ClanLocalization.Text("panel_no_description")
            : clan.Description;
        return ClanLocalization.Format(
            "panel_clan_tooltip_summary",
            action,
            clan.Name,
            clan.LeaderName,
            description);
    }

    private static void CreateDisabledClanAction(
        Transform parent,
        string text,
        string tooltip,
        float x,
        float y,
        float width,
        float height)
    {
        Button button = CreateButton(
            parent,
            text,
            () => { },
            x,
            y,
            width,
            height,
            ButtonColor,
            8);
        ColorBlock colors = button.colors;
        colors.disabledColor = DisabledButtonColor;
        button.colors = colors;
        button.interactable = false;
        SetButtonLabelColor(button, DisabledButtonLabelColor);
        ClanUiFeedback.SetTooltip(button, tooltip);
    }

    private static void BuildPeoplePane(Transform parent)
    {
        const float scrollHeight = 448f;
        GameObject pane = CreateRect(
            "PeoplePane",
            parent,
            LeftPaneWidth + 8f,
            8f,
            RightPaneWidth,
            456f,
            typeof(Image));
        pane.GetComponent<Image>().color = SectionColor;

        _peopleScroll = CreateScrollView(
            pane.transform,
            "PeopleScroll",
            -3f,
            4f,
            411f,
            scrollHeight,
            out _peopleScrollContent,
            _rightScrollPosition,
            value => _rightScrollPosition = value,
            scrollbarTopInset: 6f);

        PopulatePeopleRows();
    }

    private static void PopulatePeopleRows()
    {
        const float scrollHeight = 448f;
        ScrollRect? scroll = _peopleScroll;
        RectTransform? content = _peopleScrollContent;
        if (scroll == null || content == null)
        {
            return;
        }

        ClanUiFeedback.HideTooltip();
        ClearSelectionWithin(content);
        scroll.StopMovement();
        ClanUiFactory.ClearChildren(content);
        _peopleRowsDirty = false;
        _directoryPendingAtLastBuild = ClanRpc.IsDirectoryRequestPending;
        float usedHeight = _tab == PanelTab.Members
            ? BuildMembersTab(content)
            : BuildPlayersTab(content);
        SetScrollContentHeight(scroll, content, usedHeight, scrollHeight);
        scroll.verticalNormalizedPosition = Mathf.Clamp01(_rightScrollPosition);
    }

    private static float BuildMembersTab(RectTransform content)
    {
        string effectiveClanId = _snapshot.ClanId;
        float top = 6f;
        if (!_snapshot.HasClan)
        {
            CreateTopLabel(
                content,
                ClanLocalization.Text("panel_join_or_create_for_roster"),
                8f,
                top + 12f,
                RightContentWidth - 16f,
                44f,
                13,
                TextAnchor.MiddleCenter,
                MutedColor);
            return top + 64f;
        }

        IReadOnlyList<ClanApplicationSummary> applications = _snapshot.CanModerate
            ? _snapshot.Applications
                .Where(application => SearchMatches(
                    application.PlayerName,
                    _playerSearchQuery))
                .OrderBy(
                    application => application.PlayerName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<ClanApplicationSummary>();
        IReadOnlyList<ClanPlayerSummary> roster = _snapshot.Roster
            .Where(member => SearchMatches(member.Name, _playerSearchQuery))
            .OrderByDescending(member => member.IsSelf)
            .ThenBy(member => member.Role)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrEmpty(_playerSearchQuery) &&
            applications.Count == 0 &&
            roster.Count == 0)
        {
            CreateTopLabel(
                content,
                ClanLocalization.Text("panel_no_players_match"),
                8f,
                top + 12f,
                RightContentWidth - 16f,
                44f,
                13,
                TextAnchor.MiddleCenter,
                MutedColor);
            return top + 64f;
        }

        if (applications.Count > 0)
        {
            foreach (ClanApplicationSummary application in applications)
            {
                GameObject row = CreateTopRow(content, top, 36f, alternate: false);
                Text playerName = CreateLabel(
                    row.transform,
                    application.PlayerName,
                    7f,
                    10f,
                    140f,
                    16f,
                    11,
                    TextAnchor.MiddleLeft,
                    Color.white);
                playerName.resizeTextForBestFit = true;
                playerName.resizeTextMinSize = 8;
                playerName.resizeTextMaxSize = 11;

                Text applicationLabel = CreateLabel(
                    row.transform,
                    ClanLocalization.Text("panel_state_application"),
                    151f,
                    10f,
                    111f,
                    16f,
                    9,
                    TextAnchor.MiddleLeft,
                    MutedColor);
                applicationLabel.resizeTextForBestFit = true;
                applicationLabel.resizeTextMinSize = 8;
                applicationLabel.resizeTextMaxSize = 9;
                BuildApplicationDecisionButtons(
                    row.transform,
                    effectiveClanId,
                    application.PlayerId,
                    application.PlayerName,
                    new Rect(266f, 4f, 64f, 28f),
                    new Rect(334f, 4f, 68f, 28f));
                top += 40f;
            }

            if (roster.Count > 0)
            {
                top += 4f;
            }
        }

        int rowIndex = 0;
        foreach (ClanPlayerSummary member in roster)
        {
            GameObject row = CreateTopRow(content, top, 36f, rowIndex++ % 2 == 1);
            Text memberName = CreateLabel(
                row.transform,
                member.Name,
                7f,
                10f,
                140f,
                16f,
                11,
                TextAnchor.MiddleLeft,
                member.IsSelf ? AccentColor : Color.white);
            memberName.resizeTextForBestFit = true;
            memberName.resizeTextMinSize = 8;
            memberName.resizeTextMaxSize = 11;
            bool canChangeRole = CanAffectMember(_snapshot, member);
            if (canChangeRole)
            {
                if (_snapshot.IsLeader)
                {
                    CreateRoleButton(
                        row.transform,
                        member,
                        ClanRole.Officer,
                        ClanLocalization.Role(ClanRole.Officer),
                        201f,
                        48f);
                }
                CreateRoleButton(
                    row.transform,
                    member,
                    ClanRole.Member,
                    ClanLocalization.Role(ClanRole.Member),
                    253f,
                    52f);
                CreateRoleButton(
                    row.transform,
                    member,
                    ClanRole.Guest,
                    ClanLocalization.Role(ClanRole.Guest),
                    309f,
                    46f);
            }
            else
            {
                CreateLabel(
                    row.transform,
                    ClanLocalization.Role(member.Role),
                    151f,
                    7f,
                    108f,
                    22f,
                    10,
                    TextAnchor.MiddleLeft,
                    MutedColor);
            }

            if (_snapshot.IsLeader &&
                !member.IsSelf &&
                member.Role is ClanRole.Officer or ClanRole.Member)
            {
                bool leadConfirm = IsConfirming(
                    ConfirmAction.TransferLeadership,
                    member.Id);
                Button leadershipButton = CreateButton(
                    row.transform,
                    leadConfirm
                        ? ClanLocalization.Text("common_sure_question")
                        : ClanLocalization.Role(ClanRole.Leader),
                    () => ConfirmThen(
                        ConfirmAction.TransferLeadership,
                        member.Id,
                        () => SendRequest(
                            new ClanRequest
                            {
                                Type = ClanRequestType.TransferLeadership,
                                ClanId = effectiveClanId,
                                TargetId = member.Id
                            })),
                    151f,
                    4f,
                    46f,
                    28f,
                    leadConfirm ? DangerColor : ButtonColor,
                    8);
                ClanUiFeedback.SetTooltip(
                    leadershipButton,
                    leadConfirm
                        ? ClanLocalization.Format(
                            "panel_tooltip_confirm_transfer_leadership",
                            member.Name)
                        : ClanLocalization.Format(
                            "panel_tooltip_transfer_leadership",
                            member.Name));
            }

            if (CanAffectMember(_snapshot, member))
            {
                bool kickConfirm = IsConfirming(ConfirmAction.Kick, member.Id);
                CreateButton(
                    row.transform,
                    kickConfirm
                        ? ClanLocalization.Text("common_sure_question")
                        : ClanLocalization.Text("panel_action_kick"),
                    () => ConfirmThen(
                        ConfirmAction.Kick,
                        member.Id,
                        () => SendRequest(
                            new ClanRequest
                            {
                                Type = ClanRequestType.KickPlayer,
                                ClanId = effectiveClanId,
                                TargetId = member.Id
                            })),
                    359f,
                    4f,
                    43f,
                    28f,
                    kickConfirm ? DangerColor : ButtonColor,
                    9);
            }

            top += 40f;
        }

        return top + 2f;
    }

    private static void CreateRoleButton(
        Transform parent,
        ClanPlayerSummary member,
        ClanRole role,
        string label,
        float x,
        float width)
    {
        string effectiveClanId = _snapshot.ClanId;
        if (ClanDataRules.GetRolePower(_snapshot.SelfRole) <=
            ClanDataRules.GetRolePower(role))
        {
            return;
        }

        bool current = member.Role == role;
        Button button = CreateButton(
            parent,
            label,
            () => SendRequest(
                new ClanRequest
                {
                    Type = ClanRequestType.SetRole,
                    ClanId = effectiveClanId,
                    TargetId = member.Id,
                    Role = role
                }),
            x,
            4f,
            width,
            28f,
            current ? ActiveButtonColor : InactiveToggleButtonColor,
            8);
        SetButtonLabelColor(
            button,
            current ? Color.white : InactiveButtonLabelColor);
        ClanUiFeedback.SetTooltip(
            button,
            current
                ? ClanLocalization.Format(
                    "panel_tooltip_role_already_set",
                    member.Name,
                    ClanLocalization.Role(role))
                : ClanLocalization.Format(
                    "panel_tooltip_set_role",
                    member.Name,
                    ClanLocalization.Role(role)));
        if (!current)
        {
            return;
        }

        ColorBlock colors = button.colors;
        colors.disabledColor = ActiveButtonColor;
        button.colors = colors;
        button.interactable = false;
    }

    private static float BuildPlayersTab(RectTransform content)
    {
        string effectiveClanId = _snapshot.ClanId;
        float top = 6f;

        IReadOnlyList<ClanDirectoryPlayerSummary> source = _directory.Players;
        IReadOnlyList<ClanDirectoryPlayerSummary> players = source
            .Where(player =>
                SearchMatches(player.PlayerName, _playerSearchQuery) ||
                SearchMatches(player.ClanName, _playerSearchQuery))
            .OrderByDescending(player => player.IsSelf)
            .ThenByDescending(player => player.CanResolveApplication)
            .ThenByDescending(player => player.IsOnline)
            .ThenByDescending(player => player.LastSeenUtcTicks)
            .ThenBy(player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (players.Count == 0)
        {
            string emptyText = source.Count > 0 &&
                               !string.IsNullOrEmpty(_playerSearchQuery)
                ? ClanLocalization.Text("panel_no_players_or_clans_match")
                : ClanRpc.IsDirectoryRequestPending
                    ? ClanLocalization.Text("panel_loading_recent_players")
                    : ClanLocalization.Text("panel_no_recent_players");
            CreateTopLabel(
                content,
                emptyText,
                8f,
                top + 12f,
                RightContentWidth - 16f,
                44f,
                13,
                TextAnchor.MiddleCenter,
                MutedColor);
            return top + 64f;
        }

        int index = 0;
        foreach (ClanDirectoryPlayerSummary player in players)
        {
            GameObject row = CreateTopRow(content, top, 36f, index++ % 2 == 1);
            Text playerName = CreateLabel(
                row.transform,
                player.PlayerName,
                7f,
                10f,
                140f,
                16f,
                11,
                TextAnchor.MiddleLeft,
                player.IsSelf ? AccentColor : Color.white);
            playerName.resizeTextForBestFit = true;
            playerName.resizeTextMinSize = 8;
            playerName.resizeTextMaxSize = 11;

            Text clanState = CreateLabel(
                row.transform,
                DirectoryStateText(player),
                151f,
                10f,
                111f,
                16f,
                10,
                TextAnchor.MiddleLeft,
                DirectoryStateColor(player.State));
            clanState.resizeTextForBestFit = true;
            clanState.resizeTextMinSize = 8;
            clanState.resizeTextMaxSize = 10;

            Text lastSeen = CreateLabel(
                row.transform,
                LastSeenText(player),
                266f,
                7f,
                64f,
                21f,
                10,
                TextAnchor.MiddleRight,
                player.IsOnline ? ClanUiFactory.GetClanColor() : MutedColor);
            lastSeen.resizeTextForBestFit = true;
            lastSeen.resizeTextMinSize = 8;
            lastSeen.resizeTextMaxSize = 10;

            if (_snapshot.CanModerate && player.CanInvite)
            {
                Button invite = CreateButton(
                    row.transform,
                    ClanLocalization.Text("panel_action_invite"),
                    () => SendRequest(
                        new ClanRequest
                        {
                            Type = ClanRequestType.Invite,
                            ClanId = effectiveClanId,
                            TargetId = player.PlayerId
                        }),
                    334f,
                    4f,
                    68f,
                    28f,
                    ButtonColor,
                    9);
                invite.name = "InvitePlayer";
                ClanUiFeedback.SetTooltip(
                    invite,
                    ClanLocalization.Format(
                        "panel_tooltip_invite_player",
                        player.PlayerName));
            }

            top += 40f;
        }

        return top + 2f;
    }

    private static void BuildApplicationDecisionButtons(
        Transform parent,
        string clanId,
        string playerId,
        string playerName,
        Rect acceptBounds,
        Rect declineBounds)
    {
        Button accept = CreateButton(
            parent,
            ClanLocalization.Text("panel_action_accept"),
            () => SendRequest(
                new ClanRequest
                {
                    Type = ClanRequestType.AcceptApplication,
                    ClanId = clanId,
                    TargetId = playerId
                }),
            acceptBounds.x,
            acceptBounds.y,
            acceptBounds.width,
            acceptBounds.height,
            ButtonColor,
            9);
        accept.name = "AcceptApplication";
        ClanUiFeedback.SetTooltip(
            accept,
            ClanLocalization.Format(
                "panel_tooltip_accept_application",
                playerName));

        bool confirming = IsConfirming(ConfirmAction.RejectApplication, playerId);
        Button decline = CreateButton(
            parent,
            confirming
                ? ClanLocalization.Text("panel_action_decline_question")
                : ClanLocalization.Text("panel_action_decline"),
            () => ConfirmThen(
                ConfirmAction.RejectApplication,
                playerId,
                () => SendRequest(
                    new ClanRequest
                    {
                        Type = ClanRequestType.RejectApplication,
                        ClanId = clanId,
                        TargetId = playerId
                    })),
            declineBounds.x,
            declineBounds.y,
            declineBounds.width,
            declineBounds.height,
            confirming ? DangerColor : ButtonColor,
            9);
        decline.name = "DeclineApplication";
        ClanUiFeedback.SetTooltip(
            decline,
            confirming
                ? ClanLocalization.Format(
                    "panel_tooltip_confirm_decline_application",
                    playerName)
                : ClanLocalization.Format(
                    "panel_tooltip_decline_application",
                    playerName));
    }

    private static void BuildEditor(Transform parent)
    {
        foreach (Selectable selectable in _root!.GetComponentsInChildren<Selectable>(includeInactive: true))
        {
            selectable.interactable = false;
        }

        GameObject blocker = CreateRect(
            "ProfileEditorBlocker",
            parent,
            0f,
            0f,
            PanelWidth,
            PanelHeight,
            typeof(Image));
        blocker.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);
        blocker.GetComponent<Image>().raycastTarget = true;

        GameObject editor = CreateWoodPanelObject(
            "ProfileEditor",
            blocker.transform,
            650f,
            470f,
            draggable: false);
        Place(editor.GetComponent<RectTransform>(), 70f, 25f, 650f, 470f);
        editor.GetComponent<Image>().raycastTarget = true;

        CreateLabel(
            editor.transform,
            _editorCreating
                ? ClanLocalization.Text("panel_editor_make_clan")
                : ClanLocalization.Format(
                    "panel_editor_edit_clan",
                    _snapshot.ClanName),
            14f,
            426f,
            420f,
            32f,
            18,
            TextAnchor.MiddleLeft,
            AccentColor);
        Button saveButton = CreateButton(
            editor.transform,
            _editorSubmissionPending
                ? ClanLocalization.Text("panel_editor_sending")
                : _editorCreating
                    ? ClanLocalization.Text("panel_editor_create_clan")
                    : ClanLocalization.Text("panel_editor_save_profile"),
            SubmitEditor,
            448f,
            426f,
            138f,
            32f,
            ActiveButtonColor,
            12);
        saveButton.interactable = !_editorSubmissionPending;
        if (_editorSubmissionPending)
        {
            SetButtonLabelColor(saveButton, DisabledButtonLabelColor);
        }
        CreateButton(
            editor.transform,
            "×",
            CloseEditor,
            594f,
            426f,
            40f,
            32f,
            ButtonColor,
            19);

        CreateLabel(
            editor.transform,
            ClanLocalization.Text("panel_editor_clan_name"),
            14f,
            394f,
            180f,
            24f,
            12,
            TextAnchor.MiddleLeft,
            MutedColor);
        InputField nameInput = CreateInputField(
            editor.transform,
            "ClanName",
            _draftName,
            ClanLocalization.Text("panel_editor_name_placeholder"),
            14f,
            358f,
            620f,
            34f,
            ClanDataRules.MaxClanNameLength * 2);
        nameInput.interactable = !_editorSubmissionPending;
        nameInput.onValueChanged.AddListener(value =>
        {
            _draftName = value;
            ClearEditorError();
        });

        CreateLabel(
            editor.transform,
            ClanLocalization.Text("panel_editor_description"),
            14f,
            330f,
            180f,
            24f,
            12,
            TextAnchor.MiddleLeft,
            MutedColor);
        InputField descriptionInput = CreateInputField(
            editor.transform,
            "ClanDescription",
            _draftDescription,
            ClanLocalization.Text("panel_editor_description_placeholder"),
            14f,
            254f,
            620f,
            70f,
            ClanDataRules.MaxClanDescriptionLength);
        descriptionInput.interactable = !_editorSubmissionPending;
        descriptionInput.onValueChanged.AddListener(value =>
        {
            _draftDescription = value;
            ClearEditorError();
        });

        CreateLabel(
            editor.transform,
            ClanLocalization.Text("panel_editor_emblem"),
            14f,
            226f,
            260f,
            24f,
            12,
            TextAnchor.MiddleLeft,
            MutedColor);
        BuildEditorEmblemPicker(editor.transform);
        BuildEditorErrorBanner(editor.transform);
    }

    private static void BuildEditorEmblemPicker(Transform parent)
    {
        ScrollRect scroll = CreateScrollView(
            parent,
            "EmblemPicker",
            14f,
            14f,
            620f,
            206f,
            out RectTransform content,
            _emblemScrollPosition,
            value => _emblemScrollPosition = value);
        scroll.enabled = !_editorSubmissionPending;
        IReadOnlyList<ClanEmoji.ClanEmblemPickerItem> emblems = ClanEmoji.GetEmblemPickerItems();
        const int columns = 9;
        const float cell = 58f;
        const float gap = 6f;
        int itemCount = emblems.Count + 1;

        bool noEmblemSelected = string.IsNullOrWhiteSpace(_draftEmblemKey);
        Button none = CreateTopButton(
            content,
            ClanLocalization.Text("common_none"),
            () => SelectDraftEmblem(""),
            5f,
            5f,
            cell,
            cell,
            noEmblemSelected
                ? ActivePickerButtonColor
                : PickerButtonColor,
            10);
        RemoveButtonFrame(none);
        SetPickerButtonState(
            none,
            noEmblemSelected,
            !_editorSubmissionPending);
        ClanUiFeedback.SetTooltip(
            none,
            ClanLocalization.Text("panel_tooltip_no_emblem"));

        for (int index = 0; index < emblems.Count; index++)
        {
            ClanEmoji.ClanEmblemPickerItem emblem = emblems[index];
            int gridIndex = index + 1;
            int column = gridIndex % columns;
            int row = gridIndex / columns;
            bool selected = StringComparer.Ordinal.Equals(
                _draftEmblemKey,
                emblem.Name);
            Button button = CreateTopButton(
                content,
                "",
                () => SelectDraftEmblem(emblem.Name),
                5f + column * (cell + gap),
                5f + row * (cell + gap),
                cell,
                cell,
                selected
                    ? ActivePickerButtonColor
                    : PickerButtonColor,
                10);
            RemoveButtonFrame(button);
            AddSprite(button.transform, emblem.Sprite, 7f, 7f, 44f, 44f);
            SetPickerButtonState(
                button,
                selected,
                !_editorSubmissionPending);
        }

        int rows = Math.Max(1, (itemCount + columns - 1) / columns);
        SetScrollContentHeight(
            scroll,
            content,
            10f + rows * (cell + gap),
            206f);
        scroll.verticalNormalizedPosition = Mathf.Clamp01(_emblemScrollPosition);
    }

    private static void BuildEditorErrorBanner(Transform parent)
    {
        _editorErrorBanner = CreateRect(
            "EditorError",
            parent,
            14f,
            14f,
            620f,
            36f,
            typeof(Image));
        Image background = _editorErrorBanner.GetComponent<Image>();
        background.color = new Color(0.38f, 0.1f, 0.08f, 0.96f);
        background.raycastTarget = false;
        _editorErrorLabel = CreateLabel(
            _editorErrorBanner.transform,
            _editorError,
            10f,
            2f,
            600f,
            32f,
            11,
            TextAnchor.MiddleLeft,
            Color.white);
        _editorErrorLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
        _editorErrorLabel.verticalOverflow = VerticalWrapMode.Truncate;
        _editorErrorBanner.SetActive(!string.IsNullOrWhiteSpace(_editorError));
    }

    private static void OpenCreateEditor()
    {
        ClearConfirmation();
        _editorOpen = true;
        _editorCreating = true;
        ClearEditorSubmission();
        ClearEditorError();
        _draftName = "";
        _draftDescription = "";
        _draftEmblemKey = "";
        RebuildView();
    }

    private static void OpenEditEditor()
    {
        if (!_snapshot.IsLeader)
        {
            ClanRpc.NotifyStatus(
                ClanLocalization.Text("panel_error_leader_only_edit"));
            return;
        }

        ClearConfirmation();
        _editorOpen = true;
        _editorCreating = false;
        ClearEditorSubmission();
        ClearEditorError();
        _draftName = _snapshot.ClanName;
        _draftDescription = _snapshot.ClanDescription;
        _draftEmblemKey = _snapshot.ClanEmblemKey;
        RebuildView();
    }

    private static void CloseEditor()
    {
        _editorOpen = false;
        ClearEditorSubmission();
        ClearEditorError();
        RebuildView();
    }

    private static void SubmitEditor()
    {
        if (_editorSubmissionPending)
        {
            return;
        }

        ClearEditorError();

        try
        {
            string name = ClanDataRules.RequireClanName(_draftName);
            string description = ClanDataRules.RequireClanDescription(
                (_draftDescription ?? "").Replace('\r', ' ').Replace('\n', ' '));
            string emblemKey = ClanDataRules.RequireClanEmblemKey(_draftEmblemKey);
            if (!ClanEmoji.IsAvailableEmblemKey(emblemKey))
            {
                ShowEditorError(
                    ClanLocalization.Text("panel_error_select_available_emblem"));
                return;
            }

            _draftName = name;
            _draftDescription = description;
            _draftEmblemKey = emblemKey;

            ClanRequest request;
            if (_editorCreating)
            {
                if (_snapshot.HasAnyClan)
                {
                    ShowEditorError(
                        ClanLocalization.Text("panel_error_leave_before_create"));
                    return;
                }
                request = new ClanRequest
                {
                    Type = ClanRequestType.CreateClan,
                    ClanName = _draftName,
                    Description = _draftDescription,
                    EmblemKey = _draftEmblemKey
                };
            }
            else
            {
                if (!_snapshot.IsLeader)
                {
                    ShowEditorError(
                        ClanLocalization.Text("panel_error_leader_only_edit"));
                    return;
                }
                if (SubmittedProfileMatches(_snapshot))
                {
                    _editorOpen = false;
                    ClearEditorSubmission();
                    ClearEditorError();
                    RebuildView();
                    return;
                }
                request = new ClanRequest
                {
                    Type = ClanRequestType.UpdateClanProfile,
                    ClanId = _snapshot.ClanId,
                    ClanName = _draftName,
                    Description = _draftDescription,
                    EmblemKey = _draftEmblemKey
                };
            }

            request.RequestId = ClanRpc.NextRequestId();
            _editorSubmissionRequestId = request.RequestId;
            _editorSubmissionPending = true;
            RebuildView();
            if (!ClanRpc.Send(request))
            {
                ClearEditorSubmission();
                ShowEditorError(
                    ClanLocalization.Text("panel_error_server_not_connected"));
                RebuildView();
                return;
            }
            ScheduleDirectoryRefresh();
        }
        catch (InvalidDataException exception)
        {
            ClearEditorSubmission();
            ShowEditorError(LocalizeEditorValidationError(exception));
        }
    }

    private static void ClearEditorSubmission()
    {
        _editorSubmissionPending = false;
        _editorSubmissionRequestId = 0L;
    }

    private static string LocalizeEditorValidationError(InvalidDataException exception)
    {
        if (!ClanDataRules.TryGetValidationError(
                exception,
                out ClanValidationField field,
                out ClanValidationError error))
        {
            return ClanLocalization.Text("panel_error_invalid_profile");
        }

        return (field, error) switch
        {
            (ClanValidationField.ClanName, ClanValidationError.Required) =>
                ClanLocalization.Text("panel_error_name_required"),
            (ClanValidationField.ClanName, ClanValidationError.InvalidUnicode) =>
                ClanLocalization.Text("panel_error_name_invalid_unicode"),
            (ClanValidationField.ClanName, ClanValidationError.InvalidCharacters) =>
                ClanLocalization.Text("panel_error_name_invalid_characters"),
            (ClanValidationField.ClanName, ClanValidationError.TooLong) =>
                ClanLocalization.Format(
                    "panel_error_name_too_long",
                    ClanDataRules.MaxClanNameLength),
            (ClanValidationField.ClanName, ClanValidationError.TrailingSeparator) =>
                ClanLocalization.Text("panel_error_name_trailing_separator"),
            (ClanValidationField.ClanName, ClanValidationError.MissingLetterOrNumber) =>
                ClanLocalization.Text("panel_error_name_letter_number"),
            (ClanValidationField.ClanDescription, ClanValidationError.TooLong) =>
                ClanLocalization.Format(
                    "panel_error_description_too_long",
                    ClanDataRules.MaxClanDescriptionLength),
            (ClanValidationField.ClanDescription, ClanValidationError.RichText) =>
                ClanLocalization.Text("panel_error_description_rich_text"),
            (ClanValidationField.ClanDescription, ClanValidationError.ControlCharacters) =>
                ClanLocalization.Text("panel_error_description_control_characters"),
            (ClanValidationField.ClanEmblemKey, ClanValidationError.TooLong) =>
                ClanLocalization.Format(
                    "panel_error_emblem_too_long",
                    ClanDataRules.MaxClanEmblemKeyLength),
            (ClanValidationField.ClanEmblemKey, ClanValidationError.RichText) =>
                ClanLocalization.Text("panel_error_emblem_rich_text"),
            (ClanValidationField.ClanEmblemKey, ClanValidationError.ControlCharacters) =>
                ClanLocalization.Text("panel_error_emblem_control_characters"),
            (ClanValidationField.ClanEmblemKey, ClanValidationError.InvalidStartOrEnd) =>
                ClanLocalization.Text("panel_error_emblem_start_end"),
            (ClanValidationField.ClanEmblemKey, ClanValidationError.InvalidCharacters) =>
                ClanLocalization.Text("panel_error_emblem_invalid_characters"),
            _ => ClanLocalization.Text("panel_error_invalid_profile")
        };
    }

    private static void ShowEditorError(string message)
    {
        _editorError = ClanUiFactory.CleanSingleLine(message);
        if (_editorErrorLabel != null)
        {
            _editorErrorLabel.text = _editorError;
        }
        if (_editorErrorBanner != null)
        {
            _editorErrorBanner.SetActive(_editorError.Length > 0);
        }
    }

    private static void ClearEditorError()
    {
        _editorError = "";
        if (_editorErrorLabel != null)
        {
            _editorErrorLabel.text = "";
        }
        if (_editorErrorBanner != null)
        {
            _editorErrorBanner.SetActive(false);
        }
    }

    private static bool SubmittedProfileMatches(ClanClientSnapshot snapshot)
    {
        return snapshot.HasClan &&
               StringComparer.Ordinal.Equals(snapshot.ClanName, _draftName) &&
               StringComparer.Ordinal.Equals(snapshot.ClanDescription, _draftDescription) &&
               StringComparer.Ordinal.Equals(snapshot.ClanEmblemKey, _draftEmblemKey);
    }

    private static void SelectDraftEmblem(string emblemKey)
    {
        if (_editorSubmissionPending)
        {
            return;
        }

        _draftEmblemKey = emblemKey ?? "";
        ClearEditorError();
        RebuildView();
    }

    private static void SelectTab(PanelTab tab)
    {
        if (_tab == tab)
        {
            return;
        }

        _tab = tab;
        _rightScrollPosition = 1f;
        ClearConfirmation();
        RefreshHeaderState();
        PopulatePeopleRows();
        if (_tab == PanelTab.Players && _directory.RequestId <= 0L)
        {
            ClanRpc.RequestDirectory();
        }
    }

    private static void OnClanSearchChanged(string value)
    {
        string query = NormalizeSearchQuery(value);
        if (StringComparer.Ordinal.Equals(_clanSearchQuery, query))
        {
            return;
        }

        _clanSearchQuery = query;
        _clanScrollPosition = 1f;
        ClearConfirmation();
        _clanRowsDirty = true;
    }

    private static void OnPlayerSearchChanged(string value)
    {
        string query = NormalizeSearchQuery(value);
        if (StringComparer.Ordinal.Equals(_playerSearchQuery, query))
        {
            return;
        }

        _playerSearchQuery = query;
        _rightScrollPosition = 1f;
        ClearConfirmation();
        _peopleRowsDirty = true;
    }

    private static string NormalizeSearchQuery(string value)
    {
        return (value ?? "").Trim();
    }

    private static bool MatchesClanSearch(ClanPublicSummary clan)
    {
        return SearchMatches(clan.Name, _clanSearchQuery) ||
               SearchMatches(clan.LeaderName, _clanSearchQuery) ||
               SearchMatches(clan.Description, _clanSearchQuery);
    }

    private static bool SearchMatches(string? value, string query)
    {
        return string.IsNullOrEmpty(query) ||
               (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static void ResetSearchState()
    {
        bool changed = !string.IsNullOrEmpty(_clanSearchQuery) ||
                       !string.IsNullOrEmpty(_playerSearchQuery);
        _clanSearchQuery = "";
        _playerSearchQuery = "";
        _clanScrollPosition = 1f;
        _rightScrollPosition = 1f;
        if (_clanSearchInput != null)
        {
            _clanSearchInput.SetTextWithoutNotify("");
        }
        if (_playerSearchInput != null)
        {
            _playerSearchInput.SetTextWithoutNotify("");
        }
        if (changed)
        {
            _clanRowsDirty = true;
            _peopleRowsDirty = true;
        }
    }

    private static void ApplyToClan(string clanId)
    {
        SendRequest(
            new ClanRequest
            {
                Type = ClanRequestType.Apply,
                ClanId = clanId
            });
    }

    private static void SendRequest(ClanRequest request)
    {
        ClearConfirmation();
        if (!ClanRpc.Send(request))
        {
            return;
        }
        ScheduleDirectoryRefresh();
    }

    private static void ScheduleDirectoryRefresh()
    {
        _directoryRefreshAt = Mathf.Min(
            _directoryRefreshAt,
            Time.unscaledTime + MutationDirectoryRefreshDelay);
    }

    private static void ConfirmThen(
        ConfirmAction action,
        string target,
        Action confirmed)
    {
        if (IsConfirming(action, target))
        {
            ClearConfirmation();
            if (IsOpen)
            {
                RebuildView();
            }
            confirmed();
            return;
        }

        _confirmAction = action;
        _confirmTarget = target ?? "";
        RebuildView();
    }

    private static bool IsConfirming(ConfirmAction action, string target)
    {
        return _confirmAction == action &&
               StringComparer.Ordinal.Equals(_confirmTarget, target ?? "");
    }

    private static void ClearConfirmation()
    {
        _confirmAction = ConfirmAction.None;
        _confirmTarget = "";
    }

    private static bool CanAffectMember(ClanClientSnapshot snapshot, ClanPlayerSummary member)
    {
        return snapshot.CanModerate &&
               !member.IsSelf &&
               ClanDataRules.GetRolePower(snapshot.SelfRole) >
               ClanDataRules.GetRolePower(member.Role);
    }

    private static string DirectoryStateText(ClanDirectoryPlayerSummary player)
    {
        return player.State switch
        {
            ClanDirectoryPlayerState.Clan => string.IsNullOrWhiteSpace(player.ClanName)
                ? ClanLocalization.Text("panel_state_clan")
                : player.ClanName,
            ClanDirectoryPlayerState.Pending =>
                ClanLocalization.Text("panel_state_pending_lower"),
            ClanDirectoryPlayerState.Invited =>
                ClanLocalization.Text("panel_state_invited"),
            _ => "-"
        };
    }

    private static Color DirectoryStateColor(ClanDirectoryPlayerState state)
    {
        return state switch
        {
            ClanDirectoryPlayerState.Pending => AccentColor,
            ClanDirectoryPlayerState.Invited => new Color(0.56f, 0.78f, 1f, 1f),
            ClanDirectoryPlayerState.Clan => Color.white,
            _ => MutedColor
        };
    }

    private static string LastSeenText(ClanDirectoryPlayerSummary player)
    {
        if (player.IsOnline)
        {
            return ClanLocalization.Text("panel_last_seen_online");
        }
        if (player.LastSeenUtcTicks <= 0L)
        {
            return ClanLocalization.Text("panel_last_seen_unknown");
        }

        TimeSpan age = DateTime.UtcNow - new DateTime(player.LastSeenUtcTicks, DateTimeKind.Utc);
        if (age.TotalHours < 24d)
        {
            return ClanLocalization.Text("panel_last_seen_today");
        }
        return ClanLocalization.Format(
            "panel_last_seen_days",
            Mathf.Clamp((int)age.TotalDays, 1, 28));
    }

    private static GameObject CreateTopRow(
        RectTransform content,
        float top,
        float height,
        bool alternate)
    {
        GameObject row = ClanUiFactory.CreateObject("Row", content, typeof(Image));
        PlaceTop(row.GetComponent<RectTransform>(), 3f, top, RightContentWidth - 6f, height);
        row.GetComponent<Image>().color = alternate ? AlternateRowColor : RowColor;
        return row;
    }

    private static ScrollRect CreateScrollView(
        Transform parent,
        string name,
        float x,
        float y,
        float width,
        float height,
        out RectTransform content,
        float normalizedPosition,
        Action<float> rememberPosition,
        float scrollbarTopInset = 0f)
    {
        GameObject root = CreateRect(
            name,
            parent,
            x,
            y,
            width,
            height,
            typeof(Image),
            typeof(ScrollRect));
        Image background = root.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.1f);
        background.raycastTarget = true;

        GameObject viewportObject = CreateRect(
            "Viewport",
            root.transform,
            0f,
            0f,
            width,
            height,
            typeof(Image),
            typeof(Mask));
        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = Color.white;
        viewportImage.raycastTarget = true;
        Mask mask = viewportObject.GetComponent<Mask>();
        mask.showMaskGraphic = false;

        GameObject contentObject = ClanUiFactory.CreateObject("Content", viewportObject.transform);
        content = contentObject.GetComponent<RectTransform>();
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = new Vector2(0f, height);

        ScrollRect scroll = root.GetComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = viewportObject.GetComponent<RectTransform>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.inertia = true;
        scroll.decelerationRate = 0.12f;
        ClanUiFactory.ConfigureVerticalScroll(scroll, hasOverflow: true);
        scroll.verticalScrollbar = null;
        GameObject scrollbarObject = ClanUiFactory.CreateObject(
            "Scrollbar",
            root.transform,
            typeof(Image),
            typeof(Scrollbar));
        RectTransform scrollbarRect =
            scrollbarObject.GetComponent<RectTransform>();
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = new Vector2(1f, 1f);
        scrollbarRect.pivot = new Vector2(1f, 0.5f);
        scrollbarRect.offsetMin = new Vector2(-PanelScrollbarWidth, 0f);
        scrollbarRect.offsetMax = new Vector2(
            0f,
            -Mathf.Clamp(scrollbarTopInset, 0f, height));

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
        scrollbar.value = Mathf.Clamp01(normalizedPosition);
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility =
            ScrollRect.ScrollbarVisibility.Permanent;
        scroll.verticalScrollbarSpacing = 0f;
        scroll.verticalNormalizedPosition = Mathf.Clamp01(normalizedPosition);
        scroll.onValueChanged.AddListener(
            value => rememberPosition(Mathf.Clamp01(value.y)));
        return scroll;
    }

    private static void SetScrollContentHeight(
        ScrollRect scroll,
        RectTransform content,
        float requestedHeight,
        float viewportHeight)
    {
        content.sizeDelta = new Vector2(0f, Mathf.Max(viewportHeight, requestedHeight));
        bool hasOverflow = requestedHeight > viewportHeight + ScrollOverflowEpsilon;
        ClanUiFactory.ConfigureVerticalScroll(scroll, hasOverflow);
    }

    private static Button CreateTopButton(
        Transform parent,
        string text,
        Action action,
        float x,
        float top,
        float width,
        float height,
        Color color,
        int fontSize)
    {
        Button button = CreateButtonObject(parent, text, action, color, fontSize);
        PlaceTop(button.GetComponent<RectTransform>(), x, top, width, height);
        return button;
    }

    private static Text CreateTopLabel(
        Transform parent,
        string text,
        float x,
        float top,
        float width,
        float height,
        int fontSize,
        TextAnchor alignment,
        Color color)
    {
        Text label = CreateLabelObject(parent, text, fontSize, alignment, color);
        PlaceTop(label.GetComponent<RectTransform>(), x, top, width, height);
        return label;
    }

    private static Button CreateButton(
        Transform parent,
        string text,
        Action action,
        float x,
        float y,
        float width,
        float height,
        Color color,
        int fontSize)
    {
        Button button = CreateButtonObject(parent, text, action, color, fontSize);
        Place(button.GetComponent<RectTransform>(), x, y, width, height);
        return button;
    }

    private static Button CreateButtonObject(
        Transform parent,
        string text,
        Action action,
        Color color,
        int fontSize)
    {
        GameObject buttonObject = ClanUiFactory.CreateObject(
            "Button",
            parent,
            typeof(Image),
            typeof(Button));
        Image image = buttonObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = true;

        Button button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(new UnityAction(action));

        Text label = CreateLabelObject(
            buttonObject.transform,
            text,
            fontSize,
            TextAnchor.MiddleCenter,
            Color.white);
        RectTransform labelRect = label.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(2f, 1f);
        labelRect.offsetMax = new Vector2(-2f, -1f);
        label.raycastTarget = false;
        TryApplyButtonStyle(button, fontSize);
        SetButtonColor(button, color);
        return button;
    }

    private static Button CreateIconButton(
        Transform parent,
        ClanActionIcon icon,
        string tooltip,
        Action action,
        float x,
        float y,
        float width,
        float height,
        Color? color = null)
    {
        Button button = CreateButton(
            parent,
            "",
            action,
            x,
            y,
            width,
            height,
            color ?? ButtonColor,
            10);
        ClanUiFeedback.ApplyIcon(
            button,
            icon,
            tooltip,
            displaySize: Mathf.Min(width, height) - 8f);
        return button;
    }

    private static Text CreateLabel(
        Transform parent,
        string text,
        float x,
        float y,
        float width,
        float height,
        int fontSize,
        TextAnchor alignment,
        Color color)
    {
        Text label = CreateLabelObject(parent, text, fontSize, alignment, color);
        Place(label.GetComponent<RectTransform>(), x, y, width, height);
        return label;
    }

    private static Text CreateLabelObject(
        Transform parent,
        string text,
        int fontSize,
        TextAnchor alignment,
        Color color)
    {
        GameObject labelObject = ClanUiFactory.CreateObject("Text", parent, typeof(Text));
        Text label = labelObject.GetComponent<Text>();
        label.text = text ?? "";
        label.font = ClanUiFactory.GetBoldFont();
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = color;
        label.supportRichText = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.raycastTarget = false;
        return label;
    }

    private static InputField CreateInputField(
        Transform parent,
        string name,
        string value,
        string placeholderValue,
        float x,
        float y,
        float width,
        float height,
        int characterLimit,
        int fontSize = 13)
    {
        GameObject root = CreateRect(
            name,
            parent,
            x,
            y,
            width,
            height,
            typeof(Image),
            typeof(InputField));
        Image background = root.GetComponent<Image>();
        background.color = new Color(0.025f, 0.023f, 0.021f, 0.98f);
        background.raycastTarget = true;

        Text text = CreateLabel(
            root.transform,
            value,
            8f,
            4f,
            width - 16f,
            height - 8f,
            fontSize,
            TextAnchor.MiddleLeft,
            Color.white);
        text.name = "Text";
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;

        Text placeholder = CreateLabel(
            root.transform,
            placeholderValue,
            8f,
            4f,
            width - 16f,
            height - 8f,
            fontSize,
            TextAnchor.MiddleLeft,
            new Color(0.55f, 0.52f, 0.48f, 0.8f));
        placeholder.name = "Placeholder";
        placeholder.fontStyle = FontStyle.Italic;
        placeholder.raycastTarget = false;

        InputField input = root.GetComponent<InputField>();
        input.targetGraphic = background;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.characterLimit = characterLimit;
        input.lineType = InputField.LineType.SingleLine;
        input.text = value ?? "";
        input.caretColor = Color.white;
        input.selectionColor = new Color(0.28f, 0.62f, 0.56f, 0.55f);
        TryApplyInputFieldStyle(input, fontSize);
        if (background.sprite != null)
        {
            background.type = Image.Type.Sliced;
        }
        return input;
    }

    private static void AddEmblem(
        Transform parent,
        string emblemKey,
        float x,
        float y,
        float width,
        float height,
        string fallbackText)
    {
        Sprite? sprite = ClanEmoji.GetEmblemSprite(emblemKey);
        if (sprite != null)
        {
            AddSprite(parent, sprite, x, y, width, height);
            return;
        }

        GameObject frame = CreateRect(
            "ClanEmblemFallback",
            parent,
            x,
            y,
            width,
            height,
            typeof(Image));
        frame.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.28f);
        string initial = string.IsNullOrWhiteSpace(fallbackText)
            ? "?"
            : fallbackText.Trim().Substring(0, 1).ToUpperInvariant();
        CreateLabel(
            frame.transform,
            initial,
            0f,
            0f,
            width,
            height,
            Mathf.RoundToInt(Mathf.Min(width, height) * 0.42f),
            TextAnchor.MiddleCenter,
            AccentColor);
    }

    private static void AddSprite(
        Transform parent,
        Sprite sprite,
        float x,
        float y,
        float width,
        float height)
    {
        GameObject iconObject = CreateRect(
            "Icon",
            parent,
            x,
            y,
            width,
            height,
            typeof(Image));
        Image image = iconObject.GetComponent<Image>();
        image.sprite = sprite;
        image.color = Color.white;
        image.preserveAspect = true;
        image.raycastTarget = false;
    }

    private static GameObject CreateRect(
        string name,
        Transform parent,
        float x,
        float y,
        float width,
        float height,
        params Type[] components)
    {
        GameObject gameObject = ClanUiFactory.CreateObject(name, parent, components);
        Place(gameObject.GetComponent<RectTransform>(), x, y, width, height);
        return gameObject;
    }

    private static GameObject CreateWoodPanelObject(
        string name,
        Transform parent,
        float width,
        float height,
        bool draggable)
    {
        GameObject panel = ClanUiFactory.CreateObject(name, parent, typeof(Image));
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(width, height);
        Image image = panel.GetComponent<Image>();
        try
        {
            GUIManager.Instance.ApplyWoodpanelStyle(panel.transform);
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Could not style {name} as a Valheim wood panel; using the fallback surface: {ex.Message}");
            image.sprite = null;
            image.material = null;
            image.type = Image.Type.Simple;
            image.color = PanelColor;
        }

        if (draggable)
        {
            panel.AddComponent<Jotunn.GUI.DragWindowCntrl>();
        }
        return panel;
    }

    private static void Place(
        RectTransform rect,
        float x,
        float y,
        float width,
        float height)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
    }

    private static void PlaceTop(
        RectTransform rect,
        float x,
        float top,
        float width,
        float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -top);
        rect.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
    }

    private static void SetButtonColor(Button button, Color color)
    {
        if (button.targetGraphic is Image image)
        {
            image.color = Color.white;
        }

        ColorBlock colors = button.colors;
        colors.normalColor = color;
        colors.highlightedColor = Color.Lerp(color, AccentColor, 0.18f);
        colors.pressedColor = new Color(
            color.r * 0.72f,
            color.g * 0.72f,
            color.b * 0.72f,
            color.a);
        colors.selectedColor = Color.Lerp(color, AccentColor, 0.1f);
        colors.disabledColor = DisabledButtonColor;
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.1f;
        button.colors = colors;
    }

    private static void SetPickerButtonState(
        Button button,
        bool selected,
        bool interactable)
    {
        Color color = selected ? ActivePickerButtonColor : PickerButtonColor;
        SetButtonColor(button, color);

        ColorBlock colors = button.colors;
        colors.highlightedColor = Color.Lerp(color, Color.white, 0.28f);
        colors.pressedColor = new Color(
            color.r * 0.58f,
            color.g * 0.58f,
            color.b * 0.58f,
            color.a);
        colors.selectedColor = Color.Lerp(color, AccentColor, 0.16f);
        colors.disabledColor = DisabledPickerButtonColor;
        button.colors = colors;
        button.interactable = interactable;
        SetButtonContentColor(
            button,
            interactable ? Color.white : DisabledButtonLabelColor);
        if (interactable && !selected)
        {
            SetButtonLabelColor(button, InactiveButtonLabelColor);
        }
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

    private static void SetButtonContentColor(Button button, Color color)
    {
        SetButtonLabelColor(button, color);
        foreach (Image image in button.GetComponentsInChildren<Image>(includeInactive: true))
        {
            if (image.transform.parent == button.transform &&
                !ReferenceEquals(image, button.targetGraphic))
            {
                image.color = color;
            }
        }
    }

    private static void RemoveButtonFrame(Button button)
    {
        if (button.targetGraphic is not Image image)
        {
            return;
        }

        image.sprite = null;
        image.overrideSprite = null;
        image.material = null;
        image.type = Image.Type.Simple;
    }

    private static void TryApplyButtonStyle(Button button, int fontSize)
    {
        try
        {
            GUIManager.Instance.ApplyButtonStyle(button, fontSize);
        }
        catch (Exception)
        {
            // The Jotunn GUI can be between instances during scene transitions.
        }
    }

    private static void TryApplyInputFieldStyle(InputField input, int fontSize)
    {
        try
        {
            GUIManager.Instance.ApplyInputFieldStyle(input, fontSize);
        }
        catch (Exception)
        {
            // The Jotunn GUI can be between instances during scene transitions.
        }
    }

    private static void PositionPanel()
    {
        if (_rootRect == null || _overlayRoot == null)
        {
            return;
        }

        Rect safeArea = Screen.safeArea;
        if (safeArea.width <= 0f || safeArea.height <= 0f)
        {
            safeArea = new Rect(0f, 0f, Screen.width, Screen.height);
        }

        Camera? camera = ClanUiFactory.GetCanvasCamera(_overlayRoot);
        Rect overlayBounds = ClanUiFactory.GetScreenBounds(_overlayRoot, camera);
        if (_positionValid &&
            ClanUiFactory.RectApproximately(_positionSafeArea, safeArea) &&
            ClanUiFactory.RectApproximately(_positionOverlayBounds, overlayBounds))
        {
            ClampCurrentPanelToSafeArea(safeArea, camera);
            return;
        }

        Vector2 preferredCenter = _positionValid
            ? ClanUiFactory.GetScreenBounds(_rootRect, camera).center
            : safeArea.center;
        _positionValid = true;
        _positionSafeArea = safeArea;
        _positionOverlayBounds = overlayBounds;

        _rootRect.localScale = Vector3.one;
        Canvas.ForceUpdateCanvases();
        Rect unscaledBounds = ClanUiFactory.GetScreenBounds(_rootRect, camera);
        float unscaledWidth = Mathf.Max(1f, unscaledBounds.width);
        float unscaledHeight = Mathf.Max(1f, unscaledBounds.height);
        float availableWidth = Mathf.Max(1f, safeArea.width - SafeAreaGap * 2f);
        float availableHeight = Mathf.Max(1f, safeArea.height - SafeAreaGap * 2f);
        float scale = Mathf.Min(
            PreferredPanelScale,
            availableWidth / unscaledWidth,
            availableHeight / unscaledHeight);
        scale = Mathf.Max(0.05f, scale);
        _rootRect.localScale = Vector3.one * scale;

        float panelScreenWidth = unscaledWidth * scale;
        float panelScreenHeight = unscaledHeight * scale;
        float halfWidth = panelScreenWidth * 0.5f;
        float halfHeight = panelScreenHeight * 0.5f;
        float centerX = Mathf.Clamp(
            preferredCenter.x,
            safeArea.xMin + SafeAreaGap + halfWidth,
            safeArea.xMax - SafeAreaGap - halfWidth);
        float centerY = Mathf.Clamp(
            preferredCenter.y,
            safeArea.yMin + SafeAreaGap + halfHeight,
            safeArea.yMax - SafeAreaGap - halfHeight);

        if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
                _overlayRoot,
                new Vector2(centerX, centerY),
                camera,
                out Vector3 worldPosition))
        {
            _rootRect.position = worldPosition;
        }
    }

    private static void ClampCurrentPanelToSafeArea(Rect safeArea, Camera? camera)
    {
        if (_rootRect == null || _overlayRoot == null)
        {
            return;
        }

        Rect bounds = ClanUiFactory.GetScreenBounds(_rootRect, camera);
        float halfWidth = bounds.width * 0.5f;
        float halfHeight = bounds.height * 0.5f;
        Vector2 center = new(
            Mathf.Clamp(
                bounds.center.x,
                safeArea.xMin + SafeAreaGap + halfWidth,
                safeArea.xMax - SafeAreaGap - halfWidth),
            Mathf.Clamp(
                bounds.center.y,
                safeArea.yMin + SafeAreaGap + halfHeight,
                safeArea.yMax - SafeAreaGap - halfHeight));
        if ((center - bounds.center).sqrMagnitude <= 0.01f)
        {
            return;
        }

        if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
                _overlayRoot,
                center,
                camera,
                out Vector3 worldPosition))
        {
            _rootRect.position = worldPosition;
        }
    }

}
