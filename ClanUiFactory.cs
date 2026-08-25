using System;
using Jotunn.Managers;
using UnityEngine;

namespace Clan;

internal static class ClanUiFactory
{
    private static readonly Color FixedClanColor = new(0.48f, 0.92f, 0.78f, 1f);

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
}
