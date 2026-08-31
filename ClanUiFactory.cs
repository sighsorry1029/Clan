using System;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace Clan;

internal static class ClanUiFactory
{
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
        gameObject.layer = GUIManager.UILayer;
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

    public static Font GetBoldFont()
    {
        try
        {
            return GUIManager.Instance.AveriaSerifBold;
        }
        catch (Exception)
        {
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }

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
