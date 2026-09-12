using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Clan;

internal static class ClanHud
{
    private static readonly AccessTools.FieldRef<SplitDialog, RectTransform> SplitPanel =
        AccessTools.FieldRefAccess<SplitDialog, RectTransform>("m_panel");
    private const int MaxDisplayedMembers = 10;
    private const float RequestIntervalSeconds = 0.5f;
    private const float HeaderHeight = 42f;
    private const float HeaderToggleSize = 26f;
    private const float HeaderToggleGap = 7f;
    private const float HeaderRowGap = 5f;
    private const float RowHeight = 26f;
    private const float RowGap = 3f;
    private const float ColumnGap = 7f;
    private const float HudRightGap = 7f;
    private const float MaximumLabelWidth = 190f;
    private const float MinimumBarWidth = 100f;
    private const float MaximumBarWidth = 220f;
    private const float BarHeight = 20f;
    private const float MaximumHudWidth = 450f;
    private const float HealthLogBaseline = 100f;
    private const float HealthLogReference = 1000f;
    private const float SlowHealthDelaySeconds = 0.35f;
    private const float SlowHealthDrainPerSecond = 0.6f;
    private const float SafeAreaGap = 6f;
    private const float HeaderDragThresholdPixels = 5f;

    private static readonly Vector2 DefaultNormalizedPosition =
        new(0.015f, 0.32f);
    private static readonly Color HeaderToggleColor =
        new(1f, 0.52f, 0.16f, 0.72f);
    private static readonly Color HeaderToggleHoverColor =
        new(1f, 0.68f, 0.28f, 0.96f);
    private static readonly Color HeaderTogglePressedColor =
        new(0.78f, 0.32f, 0.08f, 0.92f);
    private static readonly Color HeaderNameColor =
        ClanUiFactory.GetClanColor();
    private static readonly Color HeaderNameHoverColor =
        Color.Lerp(HeaderNameColor, Color.white, 0.35f);
    private static readonly Color HealthBackgroundColor =
        new(0f, 0f, 0f, 0.484f);
    private static readonly Color HealthSlowColor =
        new(1f, 0.8482759f, 0f, 1f);
    private static readonly Color HealthFastColor =
        new(1f, 0.333f, 0.333f, 1f);
    private static readonly Color RowTextColor =
        new(0.96f, 0.93f, 0.84f, 1f);
    private static readonly List<RaycastResult> UiRaycastResults = new();

    private static GameObject? _root;
    private static RectTransform? _rootRect;
    private static RectTransform? _headerRect;
    private static Text? _headerClanName;
    private static Image? _headerDragSurface;
    private static RectTransform? _collapseButtonRect;
    private static Image? _collapseIcon;
    private static HudRowView[] _rows = Array.Empty<HudRowView>();
    private static float _nextRequestTime;
    private static string _displayedClanId = "";
    private static int _lastScreenWidth;
    private static int _lastScreenHeight;
    private static Rect _lastSafeArea;
    private static Vector2 _lastHudScale;
    private static bool _isWritingPosition;
    private static HudPointerMode _pointerMode;
    private static bool _restoreChatFocusAfterPointer;
    private static Vector2 _headerPressPosition;
    private static Vector2 _dragPointerOffset;
    private static Camera? _dragCamera;
    private static Transform? _trackedHudParent;
    private static bool _lastHudParentActive;
    private static Vector2 _lastHudParentSize;
    private static EventSystem? _uiRaycastEventSystem;
    private static PointerEventData? _uiRaycastPointerData;

    private enum HudPointerMode
    {
        None,
        Collapse,
        HeaderPending,
        Drag
    }

    private sealed class HudRowView
    {
        public GameObject Root = null!;
        public RectTransform Rect = null!;
        public Text Label = null!;
        public RectTransform Bar = null!;
        public RectTransform SlowFill = null!;
        public RectTransform FastFill = null!;
        public Text Health = null!;
        public string PlayerId = "";
        public float BarWidth = MinimumBarWidth;
        public float FastRatio;
        public float SlowRatio;
        public float SlowTargetRatio;
        public float SlowDelayUntil;
    }

    public static void Init()
    {
        ClanRpc.SnapshotChanged += OnSnapshotChanged;
        ClanRpc.HudSnapshotChanged += OnHudSnapshotChanged;
        ClanPlugin.ClanHudPosition.SettingChanged += OnHudPositionChanged;
        ClanPlugin.ClanHudPlayerListCollapsed.SettingChanged +=
            OnPlayerListCollapsedChanged;
        ClanPlugin.ShowClanHud.SettingChanged += OnShowHudChanged;
        ClanLocalization.LanguageChanged += OnLanguageChanged;
        Rebuild();
    }

    public static void Dispose()
    {
        ClanRpc.SnapshotChanged -= OnSnapshotChanged;
        ClanRpc.HudSnapshotChanged -= OnHudSnapshotChanged;
        ClanPlugin.ClanHudPosition.SettingChanged -= OnHudPositionChanged;
        ClanPlugin.ClanHudPlayerListCollapsed.SettingChanged -=
            OnPlayerListCollapsedChanged;
        ClanPlugin.ShowClanHud.SettingChanged -= OnShowHudChanged;
        ClanLocalization.LanguageChanged -= OnLanguageChanged;
        _pointerMode = HudPointerMode.None;
        _restoreChatFocusAfterPointer = false;
        _dragCamera = null;
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        _rootRect = null;
        _headerRect = null;
        _headerClanName = null;
        _headerDragSurface = null;
        _collapseButtonRect = null;
        _collapseIcon = null;
        _rows = Array.Empty<HudRowView>();
        _nextRequestTime = 0f;
        _displayedClanId = "";
        _lastScreenWidth = 0;
        _lastScreenHeight = 0;
        _lastSafeArea = default;
        _lastHudScale = Vector2.zero;
        _trackedHudParent = null;
        _lastHudParentActive = false;
        _lastHudParentSize = Vector2.zero;
        _uiRaycastEventSystem = null;
        _uiRaycastPointerData = null;
        UiRaycastResults.Clear();
    }

    public static void Tick()
    {
        Transform? hudParent = GetHudParent();
        if (hudParent == null)
        {
            CancelPointerInteraction(saveDrag: false, restoreChatFocus: false);
            if (_root != null)
            {
                _root.SetActive(false);
            }
            _trackedHudParent = null;
            _lastHudParentActive = false;
            _lastHudParentSize = Vector2.zero;
            return;
        }

        if (_root == null || _root.transform.parent != hudParent)
        {
            Rebuild();
            if (_root == null || _root.transform.parent != hudParent)
            {
                return;
            }
        }

        if (HudParentEnvironmentChanged(hudParent))
        {
            _nextRequestTime = 0f;
            Refresh();
            if (hudParent.gameObject.activeInHierarchy)
            {
                ApplyConfiguredPosition();
            }
        }

        if (ScreenEnvironmentChanged() && _pointerMode != HudPointerMode.Drag)
        {
            ApplyConfiguredPosition();
        }

        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        bool visible = ReconcileVisibility();

        UpdateHeaderRaycastState();
        HandleHudPointerInput();

        if (!visible)
        {
            _nextRequestTime = 0f;
            return;
        }

        if (IsPlayerListCollapsed())
        {
            _nextRequestTime = 0f;
            return;
        }

        AnimateSlowHealth();
        float now = Time.realtimeSinceStartup;
        if (now < _nextRequestTime)
        {
            return;
        }

        _nextRequestTime = now + RequestIntervalSeconds;
        ClanRpc.RequestHudSnapshot(snapshot.ClanId);
    }

    private static bool ShouldShow(ClanClientSnapshot snapshot)
    {
        return ShouldShow(snapshot, GetHudParent());
    }

    private static bool ShouldShow(
        ClanClientSnapshot snapshot,
        Transform? hudParent)
    {
        return ZNet.instance != null &&
               Player.m_localPlayer != null &&
               hudParent != null &&
               hudParent.gameObject.activeInHierarchy &&
               ClanPlugin.ShowClanHud.Value.IsOn() &&
               snapshot.HasClan;
    }

    private static bool ReconcileVisibility()
    {
        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        Transform? hudParent = GetHudParent();
        bool visible = ShouldShow(snapshot, hudParent);
        if (_root == null)
        {
            return visible;
        }

        bool stateChanged = _root.activeSelf != visible;
        bool clanChanged =
            visible &&
            !StringComparer.Ordinal.Equals(_displayedClanId, snapshot.ClanId);
        if (!stateChanged && !clanChanged)
        {
            return visible;
        }

        _nextRequestTime = 0f;
        Refresh();
        if (visible)
        {
            ApplyConfiguredPosition();
        }
        return visible;
    }

    internal static bool CapturesChatPointer(Vector2 pointerPosition)
    {
        if (_pointerMode != HudPointerMode.None)
        {
            return true;
        }

        bool pointerInsideHeader =
            HasReleasedCursor() &&
            _headerRect != null &&
            _headerRect.gameObject.activeInHierarchy &&
            RectTransformUtility.RectangleContainsScreenPoint(
                _headerRect,
                pointerPosition,
                ClanUiFactory.GetCanvasCamera(_headerRect));
        if (!pointerInsideHeader)
        {
            return false;
        }

        return InventoryGui.IsVisible()
            ? !IsPointerBlockedByVisibleInventoryPanel(pointerPosition)
            : IsHudTopmostAtPointer(pointerPosition);
    }

    private static void Refresh()
    {
        if (_root == null)
        {
            return;
        }

        ClanClientSnapshot snapshot = ClanRpc.CurrentSnapshot;
        bool visible = ShouldShow(snapshot);
        _root.SetActive(visible);
        _displayedClanId = visible ? snapshot.ClanId : "";
        if (!visible)
        {
            HideRows();
            LayoutVisibleRows(0);
            return;
        }

        if (_headerClanName != null)
        {
            _headerClanName.text = ClanLocalization.Format(
                "clan_hud_header",
                ClanUiFactory.CleanSingleLine(snapshot.ClanName));
        }

        if (IsPlayerListCollapsed())
        {
            HideRows();
            LayoutVisibleRows(0);
            return;
        }

        ClanHudSnapshot hudSnapshot = ClanRpc.CurrentHudSnapshot;
        if (!StringComparer.Ordinal.Equals(hudSnapshot.ClanId, snapshot.ClanId))
        {
            HideRows();
            LayoutVisibleRows(0);
            return;
        }

        int rowIndex = 0;
        foreach (ClanHudPlayerSummary hudPlayer in hudSnapshot.Players)
        {
            if (rowIndex == _rows.Length)
            {
                break;
            }
            if (!TryFindOnlineRosterPlayer(
                    snapshot,
                    hudPlayer.PlayerId,
                    out ClanPlayerSummary rosterPlayer))
            {
                continue;
            }

            UpdateRow(_rows[rowIndex], hudPlayer, rosterPlayer);
            rowIndex++;
        }

        for (int index = rowIndex; index < _rows.Length; index++)
        {
            _rows[index].Root.SetActive(false);
        }
        LayoutVisibleRows(rowIndex);
    }

    private static void OnLanguageChanged()
    {
        Refresh();
    }

    private static void HideRows()
    {
        foreach (HudRowView row in _rows)
        {
            row.Root.SetActive(false);
        }
    }

    private static bool TryFindOnlineRosterPlayer(
        ClanClientSnapshot snapshot,
        string playerId,
        out ClanPlayerSummary player)
    {
        foreach (ClanPlayerSummary rosterPlayer in snapshot.Roster)
        {
            if (rosterPlayer.IsOnline &&
                StringComparer.Ordinal.Equals(rosterPlayer.Id, playerId))
            {
                player = rosterPlayer;
                return true;
            }
        }

        player = null!;
        return false;
    }

    private static void UpdateRow(
        HudRowView row,
        ClanHudPlayerSummary player,
        ClanPlayerSummary rosterPlayer)
    {
        bool initializeFill =
            !row.Root.activeSelf ||
            !StringComparer.Ordinal.Equals(row.PlayerId, player.PlayerId);
        row.Root.SetActive(true);
        row.PlayerId = player.PlayerId;
        row.Label.text = ClanLocalization.Format(
            "clan_hud_player",
            ClanLocalization.Role(rosterPlayer.Role),
            ClanUiFactory.CleanSingleLine(rosterPlayer.Name));

        bool hasHealth =
            player.HasHealth &&
            IsFinite(player.CurrentHealth) &&
            IsFinite(player.MaxHealth) &&
            player.MaxHealth > 0f;
        if (!hasHealth)
        {
            row.BarWidth = MinimumBarWidth;
            SetHealthRatio(row, 0f, initialize: true);
            row.Health.text = "\u2014 / \u2014";
            return;
        }

        float maximumHealth = player.MaxHealth;
        float currentHealth = Mathf.Clamp(player.CurrentHealth, 0f, maximumHealth);
        row.BarWidth = GetLogScaledBarWidth(maximumHealth);
        SetHealthRatio(
            row,
            Mathf.Clamp01(currentHealth / maximumHealth),
            initializeFill);
        row.Health.text =
            $"{FormatHealth(currentHealth)}/{FormatHealth(maximumHealth)}";
    }

    private static float GetLogScaledBarWidth(float maximumHealth)
    {
        float referenceLog =
            Mathf.Log(1f + HealthLogReference / HealthLogBaseline);
        float normalized = referenceLog <= 0f
            ? 0f
            : Mathf.Log(1f + maximumHealth / HealthLogBaseline) / referenceLog;
        return Mathf.Ceil(Mathf.Lerp(
            MinimumBarWidth,
            MaximumBarWidth,
            Mathf.Clamp01(normalized)));
    }

    private static void SetHealthRatio(
        HudRowView row,
        float ratio,
        bool initialize)
    {
        ratio = Mathf.Clamp01(ratio);
        float previousFastRatio = row.FastRatio;
        row.FastRatio = ratio;
        SetFill(row.FastFill, ratio);

        if (initialize)
        {
            row.SlowRatio = ratio;
            row.SlowTargetRatio = ratio;
            row.SlowDelayUntil = 0f;
            SetFill(row.SlowFill, ratio);
            return;
        }

        if (ratio > previousFastRatio + 0.0001f)
        {
            row.SlowRatio = ratio;
            row.SlowTargetRatio = ratio;
            row.SlowDelayUntil = 0f;
            SetFill(row.SlowFill, ratio);
            return;
        }

        if (ratio < previousFastRatio - 0.0001f)
        {
            row.SlowRatio = Mathf.Max(row.SlowRatio, previousFastRatio);
            row.SlowDelayUntil = Time.unscaledTime + SlowHealthDelaySeconds;
        }
        row.SlowTargetRatio = ratio;
    }

    private static void AnimateSlowHealth()
    {
        float now = Time.unscaledTime;
        float maximumStep = SlowHealthDrainPerSecond * Time.unscaledDeltaTime;
        foreach (HudRowView row in _rows)
        {
            if (!row.Root.activeSelf ||
                now < row.SlowDelayUntil ||
                row.SlowRatio <= row.SlowTargetRatio)
            {
                continue;
            }

            row.SlowRatio = Mathf.MoveTowards(
                row.SlowRatio,
                row.SlowTargetRatio,
                maximumStep);
            SetFill(row.SlowFill, row.SlowRatio);
        }
    }

    private static void SetFill(RectTransform fill, float ratio)
    {
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(Mathf.Clamp01(ratio), 1f);
        fill.offsetMin = Vector2.zero;
        fill.offsetMax = Vector2.zero;
    }

    private static string FormatHealth(float health)
    {
        return Mathf.Ceil(health).ToString("0", CultureInfo.InvariantCulture);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void Rebuild()
    {
        Rebuild(GetHudParent());
    }

    private static void Rebuild(Transform? hudParent)
    {
        if (ClanUiFactory.IsHeadless ||
            hudParent == null || !ClanUiFactory.PrepareResources())
        {
            return;
        }

        CancelPointerInteraction(saveDrag: false, restoreChatFocus: false);
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }

        Sprite? healthSprite = null;
        Material? healthMaterial = null;
        try
        {
            healthSprite = ClanUiFactory.GetSprite("bar_gradient");
        }
        catch (Exception)
        {
            // The flat-color fallback remains usable while the GUI is rebuilding.
        }
        try
        {
            healthMaterial =
                ClanUiFactory.GetMaterial("lithud");
        }
        catch (Exception)
        {
            // The default UI material is a safe fallback.
        }

        _root = ClanUiFactory.CreateObject(
            "ClanMemberHud",
            hudParent);
        _root.SetActive(false);
        _rootRect = _root.GetComponent<RectTransform>();
        _rootRect.anchorMin = new Vector2(0f, 1f);
        _rootRect.anchorMax = new Vector2(0f, 1f);
        _rootRect.pivot = new Vector2(0f, 1f);
        _rootRect.anchoredPosition = new Vector2(18f, -220f);
        _rootRect.sizeDelta = new Vector2(0f, HeaderHeight);

        CreateHeader(_root.transform);
        _rows = new HudRowView[MaxDisplayedMembers];
        for (int index = 0; index < _rows.Length; index++)
        {
            _rows[index] = CreateMemberRow(
                _root.transform,
                index,
                healthSprite,
                healthMaterial);
            _rows[index].Root.SetActive(false);
        }

        _lastScreenWidth = 0;
        _lastScreenHeight = 0;
        _lastSafeArea = default;
        _lastHudScale = Vector2.zero;
        _displayedClanId = "";
        _nextRequestTime = 0f;
        _trackedHudParent = hudParent;
        _lastHudParentActive = false;
        _lastHudParentSize = Vector2.zero;
        Refresh();
        ApplyConfiguredPosition();
    }

    private static void CreateHeader(Transform parent)
    {
        GameObject header = ClanUiFactory.CreateObject(
            "HudHeader",
            parent,
            typeof(Image));
        _headerRect = header.GetComponent<RectTransform>();
        _headerDragSurface = header.GetComponent<Image>();
        _headerDragSurface.color = Color.clear;
        _headerDragSurface.raycastTarget = HasReleasedCursor();

        _headerClanName = CreateText(
            header.transform,
            ClanLocalization.Text("clan_hud_title"),
            24,
            TextAnchor.MiddleLeft,
            HeaderNameColor,
            bold: true);
        _headerClanName.resizeTextForBestFit = true;
        _headerClanName.resizeTextMinSize = 18;
        _headerClanName.resizeTextMaxSize = 24;
        SetTopLeftRect(
            _headerClanName.rectTransform,
            0f,
            0f,
            0f,
            HeaderHeight);
        AddTextOutline(_headerClanName);

        GameObject buttonObject = ClanUiFactory.CreateObject(
            "HudPlayerListToggle",
            header.transform);
        _collapseButtonRect = buttonObject.GetComponent<RectTransform>();
        SetTopLeftRect(
            _collapseButtonRect,
            0f,
            (HeaderHeight - HeaderToggleSize) * 0.5f,
            HeaderToggleSize,
            HeaderToggleSize);

        GameObject iconObject = ClanUiFactory.CreateObject(
            "Icon",
            buttonObject.transform,
            typeof(Image));
        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(14f, 14f);
        _collapseIcon = iconObject.GetComponent<Image>();
        _collapseIcon.sprite = ClanUiFeedback.GetIcon(ClanActionIcon.Collapse);
        _collapseIcon.preserveAspect = true;
        _collapseIcon.raycastTarget = false;
        _collapseIcon.color = HeaderToggleColor;
        RefreshCollapseButton();
    }

    private static HudRowView CreateMemberRow(
        Transform parent,
        int index,
        Sprite? healthSprite,
        Material? healthMaterial)
    {
        GameObject row = ClanUiFactory.CreateObject(
            $"HudMemberRow{index}",
            parent);
        RectTransform rowRect = row.GetComponent<RectTransform>();

        Text label = CreateText(
            row.transform,
            "",
            13,
            TextAnchor.MiddleLeft,
            RowTextColor,
            bold: true);
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 10;
        label.resizeTextMaxSize = 13;
        AddTextOutline(label);

        GameObject barObject = ClanUiFactory.CreateObject(
            "HealthBar",
            row.transform,
            typeof(Image));
        RectTransform barRect = barObject.GetComponent<RectTransform>();
        Image barBackground = barObject.GetComponent<Image>();
        barBackground.color = HealthBackgroundColor;
        barBackground.raycastTarget = false;

        GameObject fillAreaObject = ClanUiFactory.CreateObject(
            "FillArea",
            barObject.transform);
        RectTransform fillArea = fillAreaObject.GetComponent<RectTransform>();
        Stretch(fillArea, 2f, 2f, 2f, 2f);

        GameObject slowFillObject = ClanUiFactory.CreateObject(
            "HealthSlow",
            fillArea,
            typeof(Image));
        Image slowFillImage = slowFillObject.GetComponent<Image>();
        slowFillImage.color = HealthSlowColor;
        slowFillImage.raycastTarget = false;
        ApplyVanillaHealthStyle(slowFillImage, healthSprite, healthMaterial);
        RectTransform slowFill = slowFillObject.GetComponent<RectTransform>();
        SetFill(slowFill, 0f);

        GameObject fastFillObject = ClanUiFactory.CreateObject(
            "HealthFast",
            fillArea,
            typeof(Image));
        Image fastFillImage = fastFillObject.GetComponent<Image>();
        fastFillImage.color = HealthFastColor;
        fastFillImage.raycastTarget = false;
        ApplyVanillaHealthStyle(fastFillImage, healthSprite, healthMaterial);
        RectTransform fastFill = fastFillObject.GetComponent<RectTransform>();
        SetFill(fastFill, 0f);

        Text health = CreateText(
            barObject.transform,
            "\u2014 / \u2014",
            12,
            TextAnchor.MiddleCenter,
            Color.white,
            bold: true);
        health.resizeTextForBestFit = true;
        health.resizeTextMinSize = 9;
        health.resizeTextMaxSize = 12;
        Stretch(health.rectTransform, 3f, 3f, 0f, 0f);
        AddTextOutline(health);

        return new HudRowView
        {
            Root = row,
            Rect = rowRect,
            Label = label,
            Bar = barRect,
            SlowFill = slowFill,
            FastFill = fastFill,
            Health = health
        };
    }

    private static void ApplyVanillaHealthStyle(
        Image image,
        Sprite? sprite,
        Material? material)
    {
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Tiled;
            image.pixelsPerUnitMultiplier = 2f;
        }
        if (material != null)
        {
            image.material = material;
        }
    }

    private static void LayoutVisibleRows(int visibleCount)
    {
        if (_rootRect == null || _headerRect == null)
        {
            return;
        }

        visibleCount = Mathf.Clamp(visibleCount, 0, _rows.Length);
        float sharedLabelWidth = 0f;
        for (int index = 0; index < visibleCount; index++)
        {
            HudRowView row = _rows[index];
            sharedLabelWidth = Mathf.Max(
                sharedLabelWidth,
                Mathf.Min(
                    MaximumLabelWidth,
                    Mathf.Ceil(MeasureNaturalWidth(row.Label))));
        }

        float maximumHeaderNameWidth = Mathf.Max(
            0f,
            MaximumHudWidth -
            HeaderToggleGap -
            HeaderToggleSize -
            HudRightGap);
        float headerNameWidth = _headerClanName == null
            ? 0f
            : Mathf.Clamp(
                Mathf.Ceil(MeasureNaturalWidth(_headerClanName)),
                0f,
                maximumHeaderNameWidth);
        float headerWidth =
            headerNameWidth +
            HeaderToggleGap +
            HeaderToggleSize +
            HudRightGap;
        float hudWidth = headerWidth;
        for (int index = 0; index < visibleCount; index++)
        {
            float rowWidth =
                sharedLabelWidth +
                ColumnGap +
                _rows[index].BarWidth +
                HudRightGap;
            hudWidth = Mathf.Max(hudWidth, rowWidth);
        }
        hudWidth = Mathf.Min(hudWidth, MaximumHudWidth);

        float rowsHeight = visibleCount == 0
            ? 0f
            : HeaderRowGap +
              visibleCount * RowHeight +
              (visibleCount - 1) * RowGap;
        float hudHeight = HeaderHeight + rowsHeight;
        bool sizeChanged =
            Mathf.Abs(_rootRect.rect.width - hudWidth) > 0.1f ||
            Mathf.Abs(_rootRect.rect.height - hudHeight) > 0.1f;
        _rootRect.sizeDelta = new Vector2(hudWidth, hudHeight);

        SetTopLeftRect(
            _headerRect,
            0f,
            0f,
            headerWidth,
            HeaderHeight);
        if (_headerClanName != null)
        {
            SetTopLeftRect(
                _headerClanName.rectTransform,
                0f,
                0f,
                headerNameWidth,
                HeaderHeight);
        }
        if (_collapseButtonRect != null)
        {
            SetTopLeftRect(
                _collapseButtonRect,
                headerNameWidth + HeaderToggleGap,
                (HeaderHeight - HeaderToggleSize) * 0.5f,
                HeaderToggleSize,
                HeaderToggleSize);
        }

        float firstRowTop = HeaderHeight + HeaderRowGap;
        for (int index = 0; index < visibleCount; index++)
        {
            HudRowView row = _rows[index];
            float rowTop = firstRowTop + index * (RowHeight + RowGap);
            float rowWidth =
                sharedLabelWidth +
                ColumnGap +
                row.BarWidth +
                HudRightGap;
            SetTopLeftRect(
                row.Rect,
                0f,
                rowTop,
                rowWidth,
                RowHeight);
            SetTopLeftRect(
                row.Label.rectTransform,
                0f,
                0f,
                sharedLabelWidth,
                RowHeight);
            SetTopLeftRect(
                row.Bar,
                sharedLabelWidth + ColumnGap,
                (RowHeight - BarHeight) * 0.5f,
                row.BarWidth,
                BarHeight);
        }

        if (sizeChanged && _pointerMode != HudPointerMode.Drag)
        {
            ApplyConfiguredPosition();
        }
    }

    private static void SetTopLeftRect(
        RectTransform rect,
        float left,
        float top,
        float width,
        float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(left, -top);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static float MeasureNaturalWidth(Text text)
    {
        TextGenerationSettings settings =
            text.GetGenerationSettings(Vector2.zero);
        settings.resizeTextForBestFit = false;
        settings.horizontalOverflow = HorizontalWrapMode.Overflow;
        return text.cachedTextGeneratorForLayout.GetPreferredWidth(
                   text.text,
                   settings) /
               Mathf.Max(0.0001f, text.pixelsPerUnit);
    }

    private static void Stretch(
        RectTransform rect,
        float left,
        float right,
        float bottom,
        float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static void OnSnapshotChanged(ClanClientSnapshot snapshot)
    {
        if (!StringComparer.Ordinal.Equals(_displayedClanId, snapshot.ClanId))
        {
            _nextRequestTime = 0f;
        }
        Refresh();
        if (ShouldShow(snapshot))
        {
            ApplyConfiguredPosition();
        }
    }

    private static void OnHudSnapshotChanged(ClanHudSnapshot snapshot)
    {
        _ = snapshot;
        Refresh();
    }

    private static void OnHudPositionChanged(object sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (_pointerMode != HudPointerMode.Drag && !_isWritingPosition)
        {
            ApplyConfiguredPosition();
        }
    }

    private static void OnPlayerListCollapsedChanged(object sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        _nextRequestTime = 0f;
        RefreshCollapseButton();
        Refresh();
    }

    private static void OnShowHudChanged(object sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        _nextRequestTime = 0f;
        Refresh();
        ApplyConfiguredPosition();
    }

    private static bool IsPlayerListCollapsed()
    {
        return ClanPlugin.ClanHudPlayerListCollapsed.Value.IsOn();
    }

    private static void TogglePlayerListCollapsed()
    {
        ClanPlugin.ClanHudPlayerListCollapsed.Value =
            IsPlayerListCollapsed()
                ? ClanPlugin.Toggle.Off
                : ClanPlugin.Toggle.On;
    }

    private static void RefreshCollapseButton()
    {
        bool collapsed = IsPlayerListCollapsed();
        if (_collapseIcon != null)
        {
            _collapseIcon.sprite = ClanUiFeedback.GetIcon(
                collapsed ? ClanActionIcon.Expand : ClanActionIcon.Collapse);
            _collapseIcon.color = HeaderToggleColor;
        }
    }

    private static bool HudParentEnvironmentChanged(Transform hudParent)
    {
        bool parentChanged = _trackedHudParent != hudParent;
        bool parentActive = hudParent.gameObject.activeInHierarchy;
        Vector2 parentSize = hudParent is RectTransform parentRect
            ? parentRect.rect.size
            : Vector2.zero;
        bool becameActive =
            parentActive && (parentChanged || !_lastHudParentActive);
        bool layoutChanged =
            parentActive &&
            (parentChanged ||
             (parentSize - _lastHudParentSize).sqrMagnitude > 0.25f);

        _trackedHudParent = hudParent;
        _lastHudParentActive = parentActive;
        _lastHudParentSize = parentSize;
        return becameActive || layoutChanged;
    }

    private static bool ScreenEnvironmentChanged()
    {
        Rect safeArea = GetSafeArea();
        Vector3 lossyScale = _rootRect == null
            ? Vector3.one
            : _rootRect.lossyScale;
        Vector2 hudScale = new(
            Mathf.Abs(lossyScale.x),
            Mathf.Abs(lossyScale.y));
        bool changed =
            _lastScreenWidth != Screen.width ||
            _lastScreenHeight != Screen.height ||
            !ClanUiFactory.RectApproximately(_lastSafeArea, safeArea) ||
            (_lastHudScale - hudScale).sqrMagnitude > 0.000001f;
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;
        _lastSafeArea = safeArea;
        _lastHudScale = hudScale;
        return changed;
    }

    private static void ApplyConfiguredPosition()
    {
        if (_rootRect == null ||
            _rootRect.parent is not RectTransform parentRect)
        {
            return;
        }

        Vector2 normalized = SanitizeNormalizedPosition(
            ClanPlugin.ClanHudPosition.Value);
        Canvas.ForceUpdateCanvases();
        Camera? camera = ClanUiFactory.GetCanvasCamera(_rootRect);
        Rect safeArea = GetSafeArea();
        Vector2 targetTopLeft = new(
            safeArea.xMin + normalized.x * safeArea.width,
            safeArea.yMax - normalized.y * safeArea.height);
        SetRootPivotScreenPosition(parentRect, targetTopLeft, camera);
        ClampToSafeArea(parentRect, safeArea, camera);
    }

    private static Vector2 SanitizeNormalizedPosition(Vector2 value)
    {
        if (!IsFinite(value.x) || !IsFinite(value.y))
        {
            return DefaultNormalizedPosition;
        }
        return new Vector2(Mathf.Clamp01(value.x), Mathf.Clamp01(value.y));
    }

    private static bool CanStartDrag()
    {
        return _root != null &&
               _root.activeInHierarchy &&
               (_pointerMode == HudPointerMode.HeaderPending ||
                _pointerMode == HudPointerMode.Drag);
    }

    private static bool HasReleasedCursor()
    {
        return ZCursor.IsVisible && ZCursor.LockState != CursorLockMode.Locked;
    }

    private static void UpdateHeaderRaycastState()
    {
        bool capturePointer =
            HasReleasedCursor() ||
            _pointerMode != HudPointerMode.None;
        if (_headerDragSurface != null)
        {
            _headerDragSurface.raycastTarget = capturePointer;
        }
    }

    private static void HandleHudPointerInput()
    {
        if (_root == null ||
            !_root.activeInHierarchy ||
            _headerRect == null ||
            !_headerRect.gameObject.activeInHierarchy)
        {
            CancelPointerInteraction(saveDrag: false, restoreChatFocus: false);
            return;
        }

        Vector2 pointerPosition = Input.mousePosition;
        Camera? canvasCamera = ClanUiFactory.GetCanvasCamera(_headerRect);
        bool pointerOverHeader =
            RectTransformUtility.RectangleContainsScreenPoint(
                _headerRect,
                pointerPosition,
                canvasCamera);
        bool pointerOverButton =
            _collapseButtonRect != null &&
            _collapseButtonRect.gameObject.activeInHierarchy &&
            RectTransformUtility.RectangleContainsScreenPoint(
                _collapseButtonRect,
                pointerPosition,
                ClanUiFactory.GetCanvasCamera(_collapseButtonRect));
        bool inventoryOpen = InventoryGui.IsVisible();
        bool pointerAvailable =
            HasReleasedCursor() &&
            (!inventoryOpen ||
             !IsPointerBlockedByVisibleInventoryPanel(pointerPosition)) &&
            (inventoryOpen || IsHudTopmostAtPointer(pointerPosition));
        bool pointerOverClanName =
            _headerClanName != null &&
            RectTransformUtility.RectangleContainsScreenPoint(
                _headerClanName.rectTransform,
                pointerPosition,
                ClanUiFactory.GetCanvasCamera(_headerClanName.rectTransform));

        if (_headerClanName != null)
        {
            _headerClanName.color = pointerAvailable && pointerOverClanName
                ? HeaderNameHoverColor
                : HeaderNameColor;
        }

        if (_collapseIcon != null)
        {
            _collapseIcon.color = _pointerMode == HudPointerMode.Collapse
                ? HeaderTogglePressedColor
                : _pointerMode == HudPointerMode.None &&
                  pointerAvailable &&
                  pointerOverButton
                    ? HeaderToggleHoverColor
                    : HeaderToggleColor;
        }

        if (_pointerMode == HudPointerMode.None)
        {
            if (!Input.GetMouseButtonDown(0) ||
                !pointerAvailable ||
                !pointerOverHeader)
            {
                return;
            }

            _pointerMode = pointerOverButton
                ? HudPointerMode.Collapse
                : HudPointerMode.HeaderPending;
            _restoreChatFocusAfterPointer =
                !inventoryOpen &&
                ClanVanillaChatDock.HasFocusedChatInputForHud();
            _headerPressPosition = pointerPosition;
            if (_collapseIcon != null && pointerOverButton)
            {
                _collapseIcon.color = HeaderTogglePressedColor;
            }
            UpdateHeaderRaycastState();
            return;
        }

        if (Input.GetMouseButton(0))
        {
            if (_pointerMode == HudPointerMode.HeaderPending &&
                (pointerPosition - _headerPressPosition).sqrMagnitude >=
                HeaderDragThresholdPixels * HeaderDragThresholdPixels &&
                BeginDrag(pointerPosition, canvasCamera))
            {
                _pointerMode = HudPointerMode.Drag;
            }
            if (_pointerMode == HudPointerMode.Drag)
            {
                Drag(pointerPosition);
            }
            return;
        }

        bool activateCollapse =
            _pointerMode == HudPointerMode.Collapse &&
            Input.GetMouseButtonUp(0) &&
            pointerOverButton &&
            (!InventoryGui.IsVisible() ||
             !IsPointerBlockedByVisibleInventoryPanel(pointerPosition));
        bool saveDrag = _pointerMode == HudPointerMode.Drag;
        bool restoreChatFocus = _restoreChatFocusAfterPointer;
        CancelPointerInteraction(
            saveDrag,
            restoreChatFocus: false);
        if (activateCollapse)
        {
            TogglePlayerListCollapsed();
        }
        if (restoreChatFocus && !InventoryGui.IsVisible())
        {
            ClanVanillaChatDock.RestoreChatInputAfterHudInteraction();
        }
    }

    private static void CancelPointerInteraction(
        bool saveDrag,
        bool restoreChatFocus)
    {
        bool shouldRestoreChatFocus =
            restoreChatFocus && _restoreChatFocusAfterPointer;
        if (_pointerMode == HudPointerMode.Drag)
        {
            EndDrag(saveDrag);
        }
        _pointerMode = HudPointerMode.None;
        _restoreChatFocusAfterPointer = false;
        _dragCamera = null;
        if (_collapseIcon != null)
        {
            _collapseIcon.color = HeaderToggleColor;
        }
        UpdateHeaderRaycastState();
        if (shouldRestoreChatFocus && !InventoryGui.IsVisible())
        {
            ClanVanillaChatDock.RestoreChatInputAfterHudInteraction();
        }
    }

    private static bool IsPointerBlockedByVisibleInventoryPanel(
        Vector2 pointerPosition)
    {
        if (!InventoryGui.IsVisible())
        {
            return false;
        }

        InventoryGui? inventory = InventoryGui.instance;
        if (inventory == null)
        {
            return true;
        }

        return ContainsPointerInActivePanel(inventory.m_player, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_crafting, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_info, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_container, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_variantDialog, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_skillsDialog, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_textsDialog, pointerPosition) ||
               ContainsPointerInActivePanel(
                   inventory.m_splitDialog != null && inventory.m_splitDialog.IsActive
                       ? SplitPanel(inventory.m_splitDialog) : null, pointerPosition) ||
               ContainsPointerInActivePanel(inventory.m_trophiesPanel, pointerPosition);
    }

    private static bool ContainsPointerInActivePanel(
        Component? panel,
        Vector2 pointerPosition)
    {
        if (panel == null)
        {
            return false;
        }

        return ContainsPointerInActivePanel(panel.gameObject, pointerPosition);
    }

    private static bool ContainsPointerInActivePanel(
        GameObject? panel,
        Vector2 pointerPosition)
    {
        if (panel == null || !panel.activeInHierarchy ||
            panel.transform is not RectTransform rect)
        {
            return false;
        }

        return RectTransformUtility.RectangleContainsScreenPoint(
            rect,
            pointerPosition,
            ClanUiFactory.GetCanvasCamera(rect));
    }

    private static bool IsHudTopmostAtPointer(Vector2 pointerPosition)
    {
        if (_root == null || !_root.activeInHierarchy)
        {
            return false;
        }

        EventSystem? eventSystem = EventSystem.current;
        if (eventSystem == null || !eventSystem.isActiveAndEnabled)
        {
            return false;
        }

        if (_uiRaycastEventSystem != eventSystem ||
            _uiRaycastPointerData == null)
        {
            _uiRaycastEventSystem = eventSystem;
            _uiRaycastPointerData = new PointerEventData(eventSystem);
        }
        else
        {
            _uiRaycastPointerData.Reset();
        }

        _uiRaycastPointerData.position = pointerPosition;
        _uiRaycastPointerData.button = PointerEventData.InputButton.Left;
        UiRaycastResults.Clear();
        eventSystem.RaycastAll(_uiRaycastPointerData, UiRaycastResults);

        bool hudIsTopmost = false;
        foreach (RaycastResult result in UiRaycastResults)
        {
            if (result.module is not GraphicRaycaster ||
                result.gameObject == null)
            {
                continue;
            }

            Transform hit = result.gameObject.transform;
            hudIsTopmost =
                hit == _root.transform || hit.IsChildOf(_root.transform);
            break;
        }

        UiRaycastResults.Clear();
        return hudIsTopmost;
    }

    private static bool BeginDrag(Vector2 pointerPosition, Camera? eventCamera)
    {
        if (!CanStartDrag() || _rootRect == null)
        {
            return false;
        }

        _dragCamera = eventCamera ?? ClanUiFactory.GetCanvasCamera(_rootRect);
        Vector2 rootScreenPosition = RectTransformUtility.WorldToScreenPoint(
            _dragCamera,
            _rootRect.position);
        _dragPointerOffset = pointerPosition - rootScreenPosition;
        return true;
    }

    private static void Drag(Vector2 pointerPosition)
    {
        if (_pointerMode != HudPointerMode.Drag ||
            _rootRect == null ||
            _rootRect.parent is not RectTransform parentRect)
        {
            return;
        }

        Vector2 targetTopLeft = pointerPosition - _dragPointerOffset;
        SetRootPivotScreenPosition(parentRect, targetTopLeft, _dragCamera);
        ClampToSafeArea(parentRect, GetSafeArea(), _dragCamera);
    }

    private static void EndDrag(bool save)
    {
        if (_pointerMode != HudPointerMode.Drag)
        {
            return;
        }

        if (_rootRect != null &&
            _rootRect.parent is RectTransform parentRect)
        {
            ClampToSafeArea(parentRect, GetSafeArea(), _dragCamera);
        }

        _dragCamera = null;
        if (save)
        {
            SaveCurrentPosition();
        }
    }

    private static void SaveCurrentPosition()
    {
        if (_rootRect == null)
        {
            return;
        }

        Canvas.ForceUpdateCanvases();
        Camera? camera = ClanUiFactory.GetCanvasCamera(_rootRect);
        Rect safeArea = GetSafeArea();
        Rect bounds = ClanUiFactory.GetScreenBounds(_rootRect, camera);
        Vector2 normalized = new(
            safeArea.width <= 0f
                ? 0f
                : (bounds.xMin - safeArea.xMin) / safeArea.width,
            safeArea.height <= 0f
                ? 0f
                : (safeArea.yMax - bounds.yMax) / safeArea.height);
        normalized = SanitizeNormalizedPosition(normalized);

        if ((ClanPlugin.ClanHudPosition.Value - normalized).sqrMagnitude <=
            0.000001f)
        {
            return;
        }

        _isWritingPosition = true;
        try
        {
            ClanPlugin.ClanHudPosition.Value = normalized;
        }
        finally
        {
            _isWritingPosition = false;
        }
    }

    private static void ClampToSafeArea(
        RectTransform parentRect,
        Rect safeArea,
        Camera? camera)
    {
        if (_rootRect == null)
        {
            return;
        }

        Rect bounds = ClanUiFactory.GetScreenBounds(_rootRect, camera);
        float deltaX = bounds.width > safeArea.width
            ? safeArea.xMin - bounds.xMin
            : bounds.xMin < safeArea.xMin
                ? safeArea.xMin - bounds.xMin
                : bounds.xMax > safeArea.xMax
                    ? safeArea.xMax - bounds.xMax
                    : 0f;
        float deltaY = bounds.height > safeArea.height
            ? safeArea.yMax - bounds.yMax
            : bounds.yMin < safeArea.yMin
                ? safeArea.yMin - bounds.yMin
                : bounds.yMax > safeArea.yMax
                    ? safeArea.yMax - bounds.yMax
                    : 0f;
        if (Mathf.Abs(deltaX) <= 0.01f && Mathf.Abs(deltaY) <= 0.01f)
        {
            return;
        }

        Vector2 pivotScreenPosition = RectTransformUtility.WorldToScreenPoint(
            camera,
            _rootRect.position);
        SetRootPivotScreenPosition(
            parentRect,
            pivotScreenPosition + new Vector2(deltaX, deltaY),
            camera);
    }

    private static void SetRootPivotScreenPosition(
        RectTransform parentRect,
        Vector2 screenPosition,
        Camera? camera)
    {
        if (_rootRect != null &&
            RectTransformUtility.ScreenPointToWorldPointInRectangle(
                parentRect,
                screenPosition,
                camera,
                out Vector3 worldPosition))
        {
            _rootRect.position = worldPosition;
        }
    }

    private static Rect GetSafeArea()
    {
        Rect safeArea = Screen.safeArea;
        if (safeArea.width <= 0f || safeArea.height <= 0f)
        {
            safeArea = new Rect(0f, 0f, Screen.width, Screen.height);
        }

        if (safeArea.width > SafeAreaGap * 2f &&
            safeArea.height > SafeAreaGap * 2f)
        {
            safeArea = Rect.MinMaxRect(
                safeArea.xMin + SafeAreaGap,
                safeArea.yMin + SafeAreaGap,
                safeArea.xMax - SafeAreaGap,
                safeArea.yMax - SafeAreaGap);
        }
        return safeArea;
    }

    private static Transform? GetHudParent()
    {
        Hud? hud = Hud.instance;
        return hud != null && hud.m_rootObject != null
            ? hud.m_rootObject.transform
            : null;
    }

    private static Text CreateText(
        Transform parent,
        string value,
        int size,
        TextAnchor anchor,
        Color color,
        bool bold)
    {
        GameObject textObject = ClanUiFactory.CreateObject("Text", parent, typeof(Text));
        Text text = textObject.GetComponent<Text>();
        text.text = value;
        text.font = bold
            ? ClanUiFactory.GetFont(bold: true)
            : ClanUiFactory.GetFont(bold: false);
        text.fontSize = size;
        text.alignment = anchor;
        text.color = color;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private static void AddTextOutline(Text text)
    {
        Outline outline = text.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.78f);
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = true;
    }

}
