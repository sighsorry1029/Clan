using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using SoftReferenceableAssets;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

using UnityEngine;
using UnityEngine.UI;

namespace Clan;

internal static class ClanUiFactory
{
    internal static event Action? ResourcesAvailable;

    private static bool _initialized;
    private static bool _resourcesReady;
    private static float _nextResourceAttempt;
    private static Font? _regularFont;
    private static Font? _boldFont;
    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Material> Materials = new(StringComparer.Ordinal);
    private static readonly List<Sprite> OwnedSprites = new();
    private static GameObject? _buttonSound;
    private static RectTransform? _overlay;
    private static readonly FieldInfo GameAssetLoader = typeof(Runtime).GetField(
        "s_assetLoader", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo GameAssetLoaderReady = typeof(Runtime).Assembly
        .GetType("SoftReferenceableAssets.AssetBundleLoader")!
        .GetProperty("Initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly HashSet<string> RequiredAssetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AveriaSerifLibre-Regular", "AveriaSerifLibre-Bold", "UIAtlas",
        "litpanel", "lithud", "sfx_gui_button"
    };
    private static readonly List<AssetID> HeldAssets = new();
    private static IAssetLoader? _assetOwner;

    private static void AcquireGameAssets()
    {
        if (_assetOwner != null) return;
        // Do not access Runtime.Loader: that property would initialize it before
        // the game's own startup has installed its manifests.
        if (GameAssetLoader.GetValue(null) is not IAssetLoader loader ||
            !GameAssetLoaderReady.DeclaringType!.IsInstanceOfType(loader) ||
            GameAssetLoaderReady.GetValue(loader) is not true) return;
        Dictionary<string, AssetID> candidates = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, AssetID> entry in loader.GetAllAssetPathsMappedToAssetID())
        {
            string name = Path.GetFileNameWithoutExtension(entry.Key);
            if (!RequiredAssetNames.Contains(name)) continue;
            if (candidates.TryGetValue(name, out AssetID previous) && !previous.Equals(entry.Value))
                ambiguous.Add(name);
            else candidates[name] = entry.Value;
        }
        _assetOwner = loader;
        foreach (KeyValuePair<string, AssetID> entry in candidates)
        {
            if (ambiguous.Contains(entry.Key) || !loader.IsAvailable(entry.Value)) continue;
            // Load acquires one reference even on a failed load. Retain every
            // attempted reference so Dispose releases exactly what we acquired.
            HeldAssets.Add(entry.Value);
            loader.Load(entry.Value);
        }
    }

    internal static bool IsHeadless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    internal static bool ResourcesReady =>
        _resourcesReady && _regularFont != null && _boldFont != null;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        // Register the game's extended asset manifest before its loader starts.
        // This makes UI assets discoverable; only RequiredAssetNames are loaded.
        if (!IsHeadless && GameAssetLoader.GetValue(null) == null)
            Runtime.MakeAllAssetsLoadable();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    internal static void Dispose()
    {
        if (_initialized) SceneManager.sceneLoaded -= OnSceneLoaded;
        _initialized = false;
        ReleaseResources();
        if (_assetOwner != null)
        {
            foreach (AssetID asset in HeldAssets) _assetOwner.Release(asset);
            HeldAssets.Clear();
            _assetOwner = null;
        }
    }

    internal static void Tick()
    {
        if (!_initialized || IsHeadless || ResourcesReady)
        {
            return;
        }

        PrepareResources();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode == LoadSceneMode.Single) ReleaseResources();
    }

    private static void ReleaseResources()
    {
        if (_overlay != null)
        {
            _overlay.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_overlay.gameObject);
        }
        _overlay = null;
        foreach (Sprite sprite in OwnedSprites) UnityEngine.Object.Destroy(sprite);
        OwnedSprites.Clear();
        Sprites.Clear();
        Materials.Clear();
        _regularFont = _boldFont = null;
        _buttonSound = null;
        _resourcesReady = false;
        _nextResourceAttempt = 0f;
    }

    // Lookup during UI construction, never per-frame layout. Only atlas sprite copies are owned.
    internal static bool PrepareResources()
    {
        if (IsHeadless) return false;
        if (ResourcesReady) return true;
        if (Time.unscaledTime < _nextResourceAttempt) return false;
        _nextResourceAttempt = Time.unscaledTime + 1f;
        AcquireGameAssets();
        Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
        _regularFont = fonts.FirstOrDefault(font => font.name == "AveriaSerifLibre-Regular");
        _boldFont = fonts.FirstOrDefault(font => font.name == "AveriaSerifLibre-Bold");
        if (_regularFont == null || _boldFont == null) return false;
        SpriteAtlas? atlas = Resources.FindObjectsOfTypeAll<SpriteAtlas>()
            .FirstOrDefault(value => value.name == "UIAtlas");
        Sprite[] loadedSprites = Resources.FindObjectsOfTypeAll<Sprite>();
        foreach (string name in new[] { "woodpanel_trophys", "button", "text_field", "bar_gradient" })
        {
            Sprite? sprite = null;
            if (atlas != null)
            {
                sprite = atlas.GetSprite(name);
                if (sprite != null) OwnedSprites.Add(sprite);
            }
            sprite ??= loadedSprites.FirstOrDefault(value => value.name == name);
            if (sprite != null) Sprites[name] = sprite;
        }
        foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (material.name is "litpanel" or "lithud") Materials[material.name] = material;
        }
        foreach (ButtonSfx sfx in Resources.FindObjectsOfTypeAll<ButtonSfx>())
        {
            if (sfx.m_sfxPrefab != null && sfx.m_sfxPrefab.name == "sfx_gui_button") _buttonSound = sfx.m_sfxPrefab;
        }
        _resourcesReady = true;
        ResourcesAvailable?.Invoke();
        return true;
    }

    internal static Font GetFont(bool bold)
    {
        if (!PrepareResources()) throw new InvalidOperationException("Valheim UI fonts are not loaded yet.");
        return bold ? _boldFont! : _regularFont!;
    }

    internal static Sprite? GetSprite(string name) => Sprites.TryGetValue(name, out Sprite sprite) ? sprite : null;
    internal static Material? GetMaterial(string name) => Materials.TryGetValue(name, out Material material) ? material : null;

    internal static RectTransform GetOverlayRoot(Component context)
    {
        Canvas canvas = context.GetComponentInParent<Canvas>(true).rootCanvas;
        if (_overlay != null && _overlay.parent == canvas.transform) return _overlay;
        if (_overlay != null) UnityEngine.Object.Destroy(_overlay.gameObject);
        GameObject root = CreateObject("ClanOverlay", canvas.transform, typeof(Canvas), typeof(GraphicRaycaster));
        _overlay = root.GetComponent<RectTransform>();
        _overlay.anchorMin = Vector2.zero;
        _overlay.anchorMax = Vector2.one;
        _overlay.offsetMin = _overlay.offsetMax = Vector2.zero;
        Canvas overlayCanvas = root.GetComponent<Canvas>();
        overlayCanvas.overrideSorting = true;
        overlayCanvas.sortingLayerID = canvas.sortingLayerID;
        overlayCanvas.sortingOrder = canvas.sortingOrder + 1;
        return _overlay;
    }

    internal static void ApplyWoodpanelStyle(Transform panel)
    {
        Image image = panel.GetComponent<Image>();
        image.sprite = GetSprite("woodpanel_trophys");
        image.material = GetMaterial("litpanel");
        image.type = image.sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        image.color = image.sprite != null ? Color.white : new Color(0.12f, 0.10f, 0.08f, 0.98f);
    }

    internal static void ApplyButtonStyle(Button button, int fontSize)
    {
        Image image = button.GetComponent<Image>();
        image.sprite = GetSprite("button");
        image.type = image.sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.824f, 0.824f, 0.824f, 1f);
        colors.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f);
        colors.pressedColor = new Color(0.537f, 0.556f, 0.556f, 1f);
        colors.selectedColor = colors.normalColor;
        colors.disabledColor = new Color(0.566f, 0.566f, 0.566f, 0.502f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.1f;
        button.colors = colors;
        Text? text = button.GetComponentInChildren<Text>(true);
        if (text != null) ApplyTextStyle(text, fontSize);
        if (_buttonSound != null)
        {
            ButtonSfx sfx = button.GetComponent<ButtonSfx>() ?? button.gameObject.AddComponent<ButtonSfx>();
            sfx.m_sfxPrefab = _buttonSound;
            // Valheim 1.0.7 plays selection SFX on pointer selection. A mouse
            // click selects on press and invokes onClick on release, so assigning
            // both sounds makes one physical click audible twice.
            sfx.m_selectSfxPrefab = null;
        }
    }

    internal static void ApplyInputFieldStyle(InputField field, int fontSize)
    {
        if (field.targetGraphic is Image image)
        {
            image.color = Color.white;
            image.sprite = GetSprite("text_field");
            image.type = image.sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.pixelsPerUnitMultiplier =
                SceneManager.GetActiveScene().name == "start" ? 2f : 1f;
        }
        if (field.textComponent != null) ApplyTextStyle(field.textComponent, fontSize);
        if (field.placeholder is Text placeholder)
        {
            placeholder.font = GetFont(true);
            placeholder.fontSize = fontSize;
            placeholder.color = Color.grey;
        }
    }

    private static void ApplyTextStyle(Text text, int fontSize)
    {
        text.font = GetFont(true);
        text.fontSize = fontSize;
        Outline outline = text.GetComponent<Outline>() ?? text.gameObject.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = true;
    }

    private const float ScrollSensitivity = 196f;
    private const float UnderfilledScrollSensitivity = 35f;
    private const float UnderfilledScrollElasticity = 0.07f;
    private const float RectComparisonTolerance = 0.25f;

    private static readonly Color FixedClanColor = new(0.48f, 0.92f, 0.78f, 1f);
    private static readonly Vector3[] WorldCorners = new Vector3[4];

    public static Color GetClanColor() => FixedClanColor;

    public static GameObject CreateObject(string name, Transform parent, params Type[] components)
    {
        Type[] allComponents = new Type[components.Length + 1];
        allComponents[0] = typeof(RectTransform);
        Array.Copy(components, 0, allComponents, 1, components.Length);
        GameObject gameObject = new(name, allComponents);
        gameObject.transform.SetParent(parent, false);
        gameObject.layer = 5;
        return gameObject;
    }

    public static void ClearChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            GameObject child = parent.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
    }

    public static Font GetBoldFont() => GetFont(bold: true);

    public static Camera? GetCanvasCamera(Component? component)
    {
        Canvas? canvas = component == null
            ? null
            : component.GetComponentInParent<Canvas>();
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : canvas.worldCamera;
    }

    public static Rect GetScreenBounds(RectTransform rect, Camera? camera)
    {
        rect.GetWorldCorners(WorldCorners);
        Vector2 first = RectTransformUtility.WorldToScreenPoint(camera, WorldCorners[0]);
        float left = first.x;
        float right = first.x;
        float bottom = first.y;
        float top = first.y;
        for (int index = 1; index < WorldCorners.Length; index++)
        {
            Vector2 point =
                RectTransformUtility.WorldToScreenPoint(camera, WorldCorners[index]);
            left = Mathf.Min(left, point.x);
            right = Mathf.Max(right, point.x);
            bottom = Mathf.Min(bottom, point.y);
            top = Mathf.Max(top, point.y);
        }
        return Rect.MinMaxRect(left, bottom, right, top);
    }

    public static bool RectApproximately(Rect left, Rect right)
    {
        return Mathf.Abs(left.xMin - right.xMin) <= RectComparisonTolerance &&
               Mathf.Abs(left.yMin - right.yMin) <= RectComparisonTolerance &&
               Mathf.Abs(left.xMax - right.xMax) <= RectComparisonTolerance &&
               Mathf.Abs(left.yMax - right.yMax) <= RectComparisonTolerance;
    }

    public static string CleanSingleLine(string? value)
    {
        return (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('<', ' ')
            .Replace('>', ' ')
            .Trim();
    }

    public static void ConfigureVerticalScroll(
        ScrollRect scroll,
        bool hasOverflow,
        RectTransform? resetContentOnTransition = null)
    {
        ScrollRect.MovementType movementType = hasOverflow
            ? ScrollRect.MovementType.Clamped
            : ScrollRect.MovementType.Elastic;
        bool movementTypeChanged = scroll.movementType != movementType;
        scroll.movementType = movementType;
        scroll.scrollSensitivity = hasOverflow
            ? ScrollSensitivity
            : UnderfilledScrollSensitivity;
        scroll.elasticity = UnderfilledScrollElasticity;

        if (!movementTypeChanged || resetContentOnTransition == null)
        {
            return;
        }

        scroll.StopMovement();
        if (!hasOverflow)
        {
            resetContentOnTransition.anchoredPosition = Vector2.zero;
        }
    }
}
