using System;
using System.Collections.Generic;
using Jotunn.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Clan;

internal enum ClanActionIcon
{
    Edit,
    Folder,
    Resize,
    Collapse,
    Expand,
    Accept,
    Decline
}

internal enum ClanTooltipPlacement
{
    Default,
    LeftOfTarget
}

/// <summary>
/// Shared icon and delayed-tooltip feedback for the clan UGUI.
/// All state is lazily recreated so a destroyed or replaced GUI canvas is harmless.
/// </summary>
internal static class ClanUiFeedback
{
    private const int IconPixels = 32;
    private const float TooltipDelaySeconds = 0.2f;
    private const float TooltipScreenGap = 6f;
    private const float TooltipPointerOffset = 14f;
    private const float TooltipHorizontalPadding = 9f;
    private const float TooltipVerticalPadding = 5f;
    private const float TooltipMaximumTextWidth = 320f;
    private const float TooltipMaximumHeight = 160f;
    private const float NotificationPulsePeriodSeconds = 1f;
    private const string IconObjectName = "ClanActionIcon";

    private static readonly Dictionary<ClanActionIcon, IconAsset> IconAssets = new();
    private static readonly Vector3[] TooltipWorldCorners = new Vector3[4];

    private static GameObject? _tooltipRoot;
    private static RectTransform? _tooltipRect;
    private static Text? _tooltipText;
    private static Transform? _tooltipHost;
    private static Canvas? _tooltipCanvas;
    private static TooltipTrigger? _tooltipOwner;

    internal static Color GetNotificationPulseColor()
    {
        float phase = Mathf.Repeat(
            Time.unscaledTime,
            NotificationPulsePeriodSeconds) / NotificationPulsePeriodSeconds;
        float blend = 0.5f - 0.5f * Mathf.Cos(phase * Mathf.PI * 2f);
        return Color.Lerp(Color.white, ClanUiFactory.GetClanColor(), blend);
    }

    /// <summary>
    /// Applies a generated icon to a button without replacing its target graphic.
    /// Button text is cleared because generated icons are used only on icon buttons.
    /// </summary>
    internal static Image ApplyIcon(
        Button button,
        ClanActionIcon icon,
        string tooltip,
        float displaySize = IconPixels)
    {
        if (button == null)
        {
            throw new ArgumentNullException(nameof(button));
        }

        foreach (Text label in button.GetComponentsInChildren<Text>(includeInactive: true))
        {
            label.text = "";
        }
        foreach (TMP_Text label in button.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            label.text = "";
        }

        Transform? existing = button.transform.Find(IconObjectName);
        GameObject iconObject;
        if (existing != null)
        {
            iconObject = existing.gameObject;
        }
        else
        {
            iconObject = new GameObject(
                IconObjectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            iconObject.transform.SetParent(button.transform, worldPositionStays: false);
        }

        iconObject.layer = button.gameObject.layer;
        iconObject.SetActive(true);
        iconObject.transform.SetAsLastSibling();

        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        float safeDisplaySize = Mathf.Max(1f, displaySize);
        iconRect.sizeDelta = new Vector2(safeDisplaySize, safeDisplaySize);

        Image image = iconObject.GetComponent<Image>();
        image.sprite = GetIcon(icon);
        image.color = Color.white;
        image.preserveAspect = true;
        image.raycastTarget = false;

        SetTooltip(button, tooltip);
        return image;
    }

    /// <summary>
    /// Adds or updates delayed hover and selection feedback for a UGUI selectable.
    /// An empty tooltip disables feedback on that selectable.
    /// </summary>
    internal static void SetTooltip(
        Selectable selectable,
        string tooltip,
        bool richText = false,
        ClanTooltipPlacement placement = ClanTooltipPlacement.Default)
    {
        if (selectable == null)
        {
            throw new ArgumentNullException(nameof(selectable));
        }

        TooltipTrigger trigger = selectable.GetComponent<TooltipTrigger>() ??
                                 selectable.gameObject.AddComponent<TooltipTrigger>();
        trigger.Configure(selectable, tooltip, richText, placement);
    }

    internal static Sprite GetIcon(ClanActionIcon icon)
    {
        if (IconAssets.TryGetValue(icon, out IconAsset existing) &&
            existing.Texture != null &&
            existing.Sprite != null)
        {
            return existing.Sprite;
        }

        if (existing != null)
        {
            DestroyObject(existing.Sprite);
            DestroyObject(existing.Texture);
            IconAssets.Remove(icon);
        }

        IconAsset created = CreateIcon(icon);
        IconAssets.Add(icon, created);
        return created.Sprite;
    }

    internal static void HideTooltip()
    {
        _tooltipOwner?.NotifyTooltipHidden();
        _tooltipOwner = null;
        if (_tooltipRoot != null)
        {
            _tooltipRoot.SetActive(false);
        }
    }

    /// <summary>
    /// Releases generated textures, sprites, and the shared tooltip object.
    /// Calling this more than once is safe.
    /// </summary>
    internal static void Dispose()
    {
        HideTooltip();

        if (_tooltipRoot != null)
        {
            GameObject root = _tooltipRoot;
            ClearTooltipReferences();
            DestroyObject(root);
        }
        else
        {
            ClearTooltipReferences();
        }

        foreach (IconAsset asset in IconAssets.Values)
        {
            DestroyObject(asset.Sprite);
            DestroyObject(asset.Texture);
        }
        IconAssets.Clear();
    }

    private static bool ShowTooltip(
        TooltipTrigger owner,
        Selectable target,
        string value,
        bool usePointerPosition,
        Vector2 pointerPosition,
        bool richText,
        ClanTooltipPlacement placement)
    {
        if (target == null ||
            string.IsNullOrWhiteSpace(value) ||
            !EnsureTooltip(target))
        {
            return false;
        }

        if (_tooltipOwner != owner)
        {
            _tooltipOwner?.NotifyTooltipHidden();
            _tooltipOwner = owner;
        }

        _tooltipText!.supportRichText = richText;
        _tooltipText.text = value.Trim();
        SizeTooltip();
        _tooltipRoot!.SetActive(true);
        _tooltipRoot.transform.SetAsLastSibling();

        if (placement == ClanTooltipPlacement.LeftOfTarget &&
            TryPlaceTooltipBesideTarget(target))
        {
            return true;
        }

        _tooltipRect!.pivot = new Vector2(0f, 1f);
        Vector2 anchor = usePointerPosition
            ? pointerPosition + new Vector2(TooltipPointerOffset, -TooltipPointerOffset)
            : GetSelectionAnchor(target);
        PlaceAndClampTooltip(anchor);
        return true;
    }

    private static bool TryPlaceTooltipBesideTarget(Selectable target)
    {
        if (target.transform is not RectTransform targetRect ||
            _tooltipRect == null)
        {
            return false;
        }

        targetRect.GetWorldCorners(TooltipWorldCorners);
        Canvas? targetCanvas = target.GetComponentInParent<Canvas>();
        Camera? targetCamera = ClanUiFactory.GetCanvasCamera(targetCanvas);
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(
            targetCamera,
            TooltipWorldCorners[0]);
        Vector2 topLeft = RectTransformUtility.WorldToScreenPoint(
            targetCamera,
            TooltipWorldCorners[1]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(
            targetCamera,
            TooltipWorldCorners[2]);
        Vector2 bottomRight = RectTransformUtility.WorldToScreenPoint(
            targetCamera,
            TooltipWorldCorners[3]);
        Vector2 leftCenter = (bottomLeft + topLeft) * 0.5f;
        Vector2 rightCenter = (bottomRight + topRight) * 0.5f;

        _tooltipRect.pivot = new Vector2(1f, 0.5f);
        Vector2 leftAnchor = leftCenter + Vector2.left * TooltipPointerOffset;
        SetTooltipScreenPosition(leftAnchor);
        Canvas.ForceUpdateCanvases();
        if (IsTooltipInsideHorizontalSafeArea())
        {
            PlaceAndClampTooltip(leftAnchor);
            return true;
        }

        _tooltipRect.pivot = new Vector2(0f, 0.5f);
        PlaceAndClampTooltip(
            rightCenter + Vector2.right * TooltipPointerOffset);
        return true;
    }

    private static bool IsTooltipInsideHorizontalSafeArea()
    {
        _tooltipRect!.GetWorldCorners(TooltipWorldCorners);
        Camera? camera = ClanUiFactory.GetCanvasCamera(_tooltipCanvas);
        Vector2 lowerLeft = RectTransformUtility.WorldToScreenPoint(
            camera,
            TooltipWorldCorners[0]);
        Vector2 upperRight = RectTransformUtility.WorldToScreenPoint(
            camera,
            TooltipWorldCorners[2]);
        float minimumX = Mathf.Min(lowerLeft.x, upperRight.x);
        float maximumX = Mathf.Max(lowerLeft.x, upperRight.x);
        Rect safeArea = GetSafeArea();
        return minimumX >= safeArea.xMin + TooltipScreenGap &&
               maximumX <= safeArea.xMax - TooltipScreenGap;
    }

    private static void HideTooltip(TooltipTrigger owner)
    {
        if (_tooltipOwner != owner)
        {
            return;
        }

        _tooltipOwner = null;
        if (_tooltipRoot != null)
        {
            _tooltipRoot.SetActive(false);
        }
    }

    private static bool IsTooltipRootMissingFor(TooltipTrigger owner)
    {
        return _tooltipOwner == owner &&
               (_tooltipRoot == null || _tooltipRect == null || _tooltipText == null);
    }

    private static bool EnsureTooltip(Selectable target)
    {
        if (!TryResolveTooltipHost(
                target,
                out Transform host,
                out Canvas canvas))
        {
            return false;
        }

        if (_tooltipRoot != null &&
            _tooltipRect != null &&
            _tooltipText != null &&
            _tooltipHost == host &&
            _tooltipCanvas == canvas)
        {
            return true;
        }

        if (_tooltipRoot != null)
        {
            GameObject staleRoot = _tooltipRoot;
            ClearTooltipReferences();
            staleRoot.SetActive(false);
            DestroyObject(staleRoot);
        }

        GameObject root = new(
            "ClanTooltip",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(CanvasGroup),
            typeof(Outline));
        root.transform.SetParent(host, worldPositionStays: false);
        root.layer = host.gameObject.layer;

        RectTransform rect = root.GetComponent<RectTransform>();
        RectTransform hostRect = (RectTransform)host;
        rect.anchorMin = hostRect.pivot;
        rect.anchorMax = hostRect.pivot;
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(80f, 28f);

        Image background = root.GetComponent<Image>();
        background.color = new Color(0.045f, 0.04f, 0.035f, 0.96f);
        background.raycastTarget = false;

        Outline border = root.GetComponent<Outline>();
        border.effectColor = new Color(0.72f, 0.58f, 0.34f, 0.9f);
        border.effectDistance = new Vector2(1f, -1f);
        border.useGraphicAlpha = true;

        CanvasGroup group = root.GetComponent<CanvasGroup>();
        group.alpha = 1f;
        group.interactable = false;
        group.blocksRaycasts = false;
        group.ignoreParentGroups = false;

        GameObject textObject = new(
            "Text",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Text),
            typeof(Shadow));
        textObject.transform.SetParent(root.transform, worldPositionStays: false);
        textObject.layer = root.layer;

        Text text = textObject.GetComponent<Text>();
        text.font = ClanUiFactory.GetBoldFont();
        text.fontSize = 14;
        text.fontStyle = FontStyle.Normal;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = new Color(0.96f, 0.92f, 0.82f, 1f);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.supportRichText = false;
        text.raycastTarget = false;

        Shadow shadow = textObject.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.75f);
        shadow.effectDistance = new Vector2(1f, -1f);
        shadow.useGraphicAlpha = true;

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(TooltipHorizontalPadding, TooltipVerticalPadding);
        textRect.offsetMax = new Vector2(-TooltipHorizontalPadding, -TooltipVerticalPadding);

        root.SetActive(false);
        _tooltipRoot = root;
        _tooltipRect = rect;
        _tooltipText = text;
        _tooltipHost = host;
        _tooltipCanvas = canvas;
        return true;
    }

    private static bool TryResolveTooltipHost(
        Selectable target,
        out Transform host,
        out Canvas canvas)
    {
        try
        {
            GameObject customFront = GUIManager.CustomGUIFront;
            Canvas? customCanvas = customFront != null
                ? customFront.GetComponentInParent<Canvas>()
                : null;
            if (customFront != null &&
                customFront.activeInHierarchy &&
                customFront.transform is RectTransform &&
                customCanvas != null &&
                customCanvas.isActiveAndEnabled)
            {
                host = customFront.transform;
                canvas = customCanvas;
                return true;
            }
        }
        catch (Exception)
        {
            // Jotunn can be between GUI instances during a scene transition.
        }

        Canvas? targetCanvas = target.GetComponentInParent<Canvas>();
        if (targetCanvas != null && targetCanvas.transform is RectTransform)
        {
            host = targetCanvas.transform;
            canvas = targetCanvas;
            return true;
        }

        host = null!;
        canvas = null!;
        return false;
    }

    private static void ClearTooltipReferences()
    {
        _tooltipRoot = null;
        _tooltipRect = null;
        _tooltipText = null;
        _tooltipHost = null;
        _tooltipCanvas = null;
    }

    private static void SizeTooltip()
    {
        Rect safeArea = GetSafeArea();
        float canvasScale = Mathf.Max(0.01f, _tooltipCanvas!.scaleFactor);
        float availableWidth = Mathf.Max(
            24f,
            (safeArea.width - TooltipScreenGap * 2f) / canvasScale -
            TooltipHorizontalPadding * 2f);
        float maximumTextWidth = Mathf.Min(TooltipMaximumTextWidth, availableWidth);

        TextGenerationSettings widthSettings =
            _tooltipText!.GetGenerationSettings(new Vector2(maximumTextWidth, 0f));
        float pixelsPerUnit = Mathf.Max(0.01f, _tooltipText.pixelsPerUnit);
        float preferredWidth = _tooltipText.cachedTextGeneratorForLayout.GetPreferredWidth(
                                   _tooltipText.text,
                                   widthSettings) /
                               pixelsPerUnit;
        if (!IsFinitePositive(preferredWidth))
        {
            preferredWidth = 80f;
        }

        float textWidth = Mathf.Clamp(preferredWidth, 24f, maximumTextWidth);
        TextGenerationSettings heightSettings =
            _tooltipText.GetGenerationSettings(new Vector2(textWidth, 0f));
        float preferredHeight = _tooltipText.cachedTextGeneratorForLayout.GetPreferredHeight(
                                    _tooltipText.text,
                                    heightSettings) /
                                pixelsPerUnit;
        if (!IsFinitePositive(preferredHeight))
        {
            preferredHeight = _tooltipText.fontSize * 1.4f;
        }

        float availableHeight = Mathf.Max(
            20f,
            (safeArea.height - TooltipScreenGap * 2f) / canvasScale -
            TooltipVerticalPadding * 2f);
        float textHeight = Mathf.Clamp(
            preferredHeight,
            _tooltipText.fontSize * 1.2f,
            Mathf.Min(TooltipMaximumHeight, availableHeight));

        _tooltipRect!.sizeDelta = new Vector2(
            textWidth + TooltipHorizontalPadding * 2f,
            textHeight + TooltipVerticalPadding * 2f);
    }

    private static Vector2 GetSelectionAnchor(Selectable target)
    {
        RectTransform? targetRect = target.transform as RectTransform;
        if (targetRect == null)
        {
            return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        Vector3[] corners = new Vector3[4];
        targetRect.GetWorldCorners(corners);
        Canvas? targetCanvas = target.GetComponentInParent<Canvas>();
        Camera? camera = ClanUiFactory.GetCanvasCamera(targetCanvas);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return topRight + new Vector2(TooltipPointerOffset, -4f);
    }

    private static void PlaceAndClampTooltip(Vector2 screenPosition)
    {
        SetTooltipScreenPosition(screenPosition);
        Canvas.ForceUpdateCanvases();

        _tooltipRect!.GetWorldCorners(TooltipWorldCorners);
        Camera? camera = ClanUiFactory.GetCanvasCamera(_tooltipCanvas);
        Vector2 lowerLeft = RectTransformUtility.WorldToScreenPoint(camera, TooltipWorldCorners[0]);
        Vector2 upperRight = RectTransformUtility.WorldToScreenPoint(camera, TooltipWorldCorners[2]);
        float minimumX = Mathf.Min(lowerLeft.x, upperRight.x);
        float maximumX = Mathf.Max(lowerLeft.x, upperRight.x);
        float minimumY = Mathf.Min(lowerLeft.y, upperRight.y);
        float maximumY = Mathf.Max(lowerLeft.y, upperRight.y);

        Rect safeArea = GetSafeArea();
        float left = safeArea.xMin + TooltipScreenGap;
        float right = safeArea.xMax - TooltipScreenGap;
        float bottom = safeArea.yMin + TooltipScreenGap;
        float top = safeArea.yMax - TooltipScreenGap;
        Vector2 adjustment = Vector2.zero;

        if (minimumX < left)
        {
            adjustment.x += left - minimumX;
        }
        if (maximumX + adjustment.x > right)
        {
            adjustment.x += right - (maximumX + adjustment.x);
        }
        if (minimumY < bottom)
        {
            adjustment.y += bottom - minimumY;
        }
        if (maximumY + adjustment.y > top)
        {
            adjustment.y += top - (maximumY + adjustment.y);
        }

        if (adjustment.sqrMagnitude > 0.01f)
        {
            SetTooltipScreenPosition(screenPosition + adjustment);
        }
    }

    private static void SetTooltipScreenPosition(Vector2 screenPosition)
    {
        if (_tooltipHost is not RectTransform hostRect)
        {
            return;
        }

        Camera? camera = ClanUiFactory.GetCanvasCamera(_tooltipCanvas);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                hostRect,
                screenPosition,
                camera,
                out Vector2 localPosition))
        {
            _tooltipRect!.anchoredPosition = localPosition;
        }
    }

    private static Rect GetSafeArea()
    {
        Rect safeArea = Screen.safeArea;
        return safeArea.width > 0f && safeArea.height > 0f
            ? safeArea
            : new Rect(0f, 0f, Screen.width, Screen.height);
    }

    private static bool IsFinitePositive(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static IconAsset CreateIcon(ClanActionIcon icon)
    {
        Color32[] pixels = new Color32[IconPixels * IconPixels];
        Color32 outline = new(18, 16, 14, 235);
        Color32 foreground = icon switch
        {
            ClanActionIcon.Edit => new Color32(255, 207, 96, 255),
            ClanActionIcon.Folder => new Color32(255, 207, 96, 255),
            ClanActionIcon.Resize => new Color32(248, 248, 244, 255),
            ClanActionIcon.Collapse => new Color32(255, 255, 255, 255),
            ClanActionIcon.Expand => new Color32(255, 255, 255, 255),
            ClanActionIcon.Accept => new Color32(104, 235, 150, 255),
            ClanActionIcon.Decline => new Color32(255, 112, 108, 255),
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, null)
        };

        switch (icon)
        {
            case ClanActionIcon.Edit:
                DrawLine(pixels, new Vector2(7.5f, 7.5f), new Vector2(23f, 23f), 8f, outline);
                DrawLine(pixels, new Vector2(7.5f, 7.5f), new Vector2(23f, 23f), 4.8f, foreground);
                DrawLine(pixels, new Vector2(20.5f, 25.5f), new Vector2(25.5f, 20.5f), 4.5f, outline);
                DrawLine(pixels, new Vector2(20.5f, 25.5f), new Vector2(25.5f, 20.5f), 2f, foreground);
                DrawLine(pixels, new Vector2(5.5f, 5.5f), new Vector2(9f, 6.5f), 3f, outline);
                break;

            case ClanActionIcon.Folder:
                DrawFolderIcon(pixels, outline, foreground);
                break;

            case ClanActionIcon.Resize:
                DrawResizeIcon(pixels, 6f, outline);
                DrawResizeIcon(pixels, 3f, foreground);
                break;

            case ClanActionIcon.Collapse:
                DrawTriangle(pixels, pointsUp: true, foreground);
                break;

            case ClanActionIcon.Expand:
                DrawTriangle(pixels, pointsUp: false, foreground);
                break;

            case ClanActionIcon.Accept:
                DrawCheckIcon(pixels, outline, foreground);
                break;

            case ClanActionIcon.Decline:
                DrawCrossIcon(pixels, outline, foreground);
                break;
        }

        Texture2D texture = new(IconPixels, IconPixels, TextureFormat.RGBA32, mipChain: false)
        {
            name = $"ClanActionIcon.{icon}.Texture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, IconPixels, IconPixels),
            new Vector2(0.5f, 0.5f),
            IconPixels,
            0,
            SpriteMeshType.FullRect);
        sprite.name = $"ClanActionIcon.{icon}";
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return new IconAsset(texture, sprite);
    }

    private static void DrawFolderIcon(
        Color32[] pixels,
        Color32 outline,
        Color32 foreground)
    {
        Vector2[] outlinePoints =
        {
            new(6f, 7f),
            new(26f, 7f),
            new(26f, 21f),
            new(16f, 21f),
            new(13f, 25f),
            new(6f, 25f),
            new(6f, 7f)
        };
        for (int index = 1; index < outlinePoints.Length; index++)
        {
            DrawLine(
                pixels,
                outlinePoints[index - 1],
                outlinePoints[index],
                6f,
                outline);
        }
        for (int index = 1; index < outlinePoints.Length; index++)
        {
            DrawLine(
                pixels,
                outlinePoints[index - 1],
                outlinePoints[index],
                3f,
                foreground);
        }
    }

    private static void DrawResizeIcon(Color32[] pixels, float width, Color32 color)
    {
        Vector2 upperLeft = new(7.5f, 24.5f);
        Vector2 lowerRight = new(24.5f, 7.5f);
        DrawLine(pixels, upperLeft, lowerRight, width, color);
        DrawLine(pixels, upperLeft, new Vector2(7.5f, 16.5f), width, color);
        DrawLine(pixels, upperLeft, new Vector2(15.5f, 24.5f), width, color);
        DrawLine(pixels, lowerRight, new Vector2(24.5f, 15.5f), width, color);
        DrawLine(pixels, lowerRight, new Vector2(16.5f, 7.5f), width, color);
    }

    private static void DrawCheckIcon(
        Color32[] pixels,
        Color32 outline,
        Color32 foreground)
    {
        Vector2 start = new(6.5f, 16f);
        Vector2 middle = new(13f, 9.5f);
        Vector2 end = new(26f, 23f);
        DrawLine(pixels, start, middle, 7f, outline);
        DrawLine(pixels, middle, end, 7f, outline);
        DrawLine(pixels, start, middle, 4f, foreground);
        DrawLine(pixels, middle, end, 4f, foreground);
    }

    private static void DrawCrossIcon(
        Color32[] pixels,
        Color32 outline,
        Color32 foreground)
    {
        Vector2 lowerLeft = new(8f, 8f);
        Vector2 upperLeft = new(8f, 24f);
        Vector2 lowerRight = new(24f, 8f);
        Vector2 upperRight = new(24f, 24f);
        DrawLine(pixels, lowerLeft, upperRight, 7f, outline);
        DrawLine(pixels, upperLeft, lowerRight, 7f, outline);
        DrawLine(pixels, lowerLeft, upperRight, 4f, foreground);
        DrawLine(pixels, upperLeft, lowerRight, 4f, foreground);
    }

    private static void DrawTriangle(
        Color32[] pixels,
        bool pointsUp,
        Color32 color)
    {
        const int minimumY = 9;
        const int maximumY = 23;
        const int centerX = 16;
        const int maximumHalfWidth = 9;
        int height = maximumY - minimumY;
        for (int y = minimumY; y <= maximumY; y++)
        {
            float progress = (y - minimumY) / (float)height;
            float widthProgress = pointsUp ? 1f - progress : progress;
            int halfWidth = Mathf.Max(
                1,
                Mathf.RoundToInt(maximumHalfWidth * widthProgress));
            for (int x = centerX - halfWidth; x <= centerX + halfWidth; x++)
            {
                BlendPixel(pixels, x, y, color, 1f);
            }
        }
    }

    private static void DrawLine(
        Color32[] pixels,
        Vector2 start,
        Vector2 end,
        float width,
        Color32 color)
    {
        float radius = width * 0.5f;
        float minimumX = Mathf.Min(start.x, end.x) - radius - 1f;
        float maximumX = Mathf.Max(start.x, end.x) + radius + 1f;
        float minimumY = Mathf.Min(start.y, end.y) - radius - 1f;
        float maximumY = Mathf.Max(start.y, end.y) + radius + 1f;
        Vector2 segment = end - start;
        float segmentLengthSquared = segment.sqrMagnitude;

        ForEachPixel(minimumX, maximumX, minimumY, maximumY, (x, y) =>
        {
            Vector2 point = new(x + 0.5f, y + 0.5f);
            float t = segmentLengthSquared > 0.0001f
                ? Mathf.Clamp01(Vector2.Dot(point - start, segment) / segmentLengthSquared)
                : 0f;
            float distance = Vector2.Distance(point, start + segment * t);
            BlendPixel(pixels, x, y, color, Mathf.Clamp01(radius + 0.5f - distance));
        });
    }

    private static void ForEachPixel(
        float minimumX,
        float maximumX,
        float minimumY,
        float maximumY,
        Action<int, int> action)
    {
        int left = Mathf.Clamp(Mathf.FloorToInt(minimumX), 0, IconPixels - 1);
        int right = Mathf.Clamp(Mathf.CeilToInt(maximumX), 0, IconPixels - 1);
        int bottom = Mathf.Clamp(Mathf.FloorToInt(minimumY), 0, IconPixels - 1);
        int top = Mathf.Clamp(Mathf.CeilToInt(maximumY), 0, IconPixels - 1);
        for (int y = bottom; y <= top; y++)
        {
            for (int x = left; x <= right; x++)
            {
                action(x, y);
            }
        }
    }

    private static void BlendPixel(
        Color32[] pixels,
        int x,
        int y,
        Color32 source,
        float coverage)
    {
        if (coverage <= 0f)
        {
            return;
        }

        int index = y * IconPixels + x;
        Color32 destination = pixels[index];
        float sourceAlpha = source.a / 255f * coverage;
        float destinationAlpha = destination.a / 255f;
        float outputAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);
        if (outputAlpha <= 0f)
        {
            return;
        }

        float sourceWeight = sourceAlpha / outputAlpha;
        float destinationWeight = destinationAlpha * (1f - sourceAlpha) / outputAlpha;
        pixels[index] = new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(source.r * sourceWeight + destination.r * destinationWeight), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(source.g * sourceWeight + destination.g * destinationWeight), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(source.b * sourceWeight + destination.b * destinationWeight), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(outputAlpha * 255f), 0, 255));
    }

    private static void DestroyObject(UnityEngine.Object? value)
    {
        if (value == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(value);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(value);
        }
    }

    private sealed class IconAsset
    {
        internal IconAsset(Texture2D texture, Sprite sprite)
        {
            Texture = texture;
            Sprite = sprite;
        }

        internal Texture2D Texture { get; }
        internal Sprite Sprite { get; }
    }

    private sealed class TooltipTrigger :
        MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        ISelectHandler,
        IDeselectHandler
    {
        private Selectable? _target;
        private string _value = "";
        private bool _scheduled;
        private bool _shown;
        private bool _pointerInside;
        private bool _usePointerPosition;
        private bool _richText;
        private ClanTooltipPlacement _placement;
        private Vector2 _pointerPosition;
        private float _showAt;

        internal void Configure(
            Selectable target,
            string value,
            bool richText,
            ClanTooltipPlacement placement)
        {
            string normalizedValue = value?.Trim() ?? "";
            if (_target == target &&
                _value == normalizedValue &&
                _richText == richText &&
                _placement == placement)
            {
                enabled = normalizedValue.Length > 0;
                return;
            }

            bool refreshHoveredTooltip =
                _target == target &&
                _pointerInside &&
                normalizedValue.Length > 0;
            Cancel();
            _target = target;
            _value = normalizedValue;
            _richText = richText;
            _placement = placement;
            enabled = _value.Length > 0;
            if (refreshHoveredTooltip)
            {
                _usePointerPosition = true;
                _pointerPosition = Input.mousePosition;
                _shown = ShowTooltip(
                    this,
                    target,
                    _value,
                    true,
                    _pointerPosition,
                    _richText,
                    _placement);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _pointerInside = true;
            Schedule(usePointerPosition: true, eventData.position);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pointerInside = false;
            Cancel();
        }

        public void OnSelect(BaseEventData eventData)
        {
            Schedule(usePointerPosition: false, Vector2.zero);
        }

        public void OnDeselect(BaseEventData eventData)
        {
            Cancel();
        }

        private void Update()
        {
            if (_scheduled && Time.unscaledTime >= _showAt)
            {
                _scheduled = false;
                _shown = _target != null && ShowTooltip(
                    this,
                    _target,
                    _value,
                    _usePointerPosition,
                    _pointerPosition,
                    _richText,
                    _placement);
            }
            else if (_shown && IsTooltipRootMissingFor(this) && _target != null)
            {
                // The GUI canvas can be destroyed independently during a scene rebuild.
                _shown = ShowTooltip(
                    this,
                    _target,
                    _value,
                    _usePointerPosition,
                    _pointerPosition,
                    _richText,
                    _placement);
            }
        }

        private void OnDisable()
        {
            _pointerInside = false;
            Cancel();
        }

        private void OnDestroy()
        {
            _pointerInside = false;
            Cancel();
        }

        internal void NotifyTooltipHidden()
        {
            _shown = false;
        }

        private void Schedule(bool usePointerPosition, Vector2 pointerPosition)
        {
            if (!enabled || _target == null || string.IsNullOrWhiteSpace(_value))
            {
                return;
            }

            HideTooltip(this);
            _usePointerPosition = usePointerPosition;
            _pointerPosition = pointerPosition;
            _showAt = Time.unscaledTime + TooltipDelaySeconds;
            _scheduled = true;
            _shown = false;
        }

        private void Cancel()
        {
            _scheduled = false;
            _shown = false;
            HideTooltip(this);
        }
    }
}
