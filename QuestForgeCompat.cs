using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Clan;

internal static class QuestForgeCompat
{
    internal const string PluginGuid = "soloredis.rtdquestforge";
    private static FieldInfo? _managerField;
    private static MethodInfo? _registerMethod;
    private static object? _lastManager;
    private static Action<string>? _registerKill;
    internal static bool IsReady => _managerField != null;

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        MethodInfo? target = null;
        MethodInfo prefix = AccessTools.Method(typeof(QuestForgeCompat), nameof(CaptureDeath));
        MethodInfo postfix = AccessTools.Method(typeof(QuestForgeCompat), nameof(ShareDeath));
        try
        {
            target = AccessTools.Method(typeof(Character), nameof(Character.OnDeath), Type.EmptyTypes);
            if (target == null) throw new MissingMethodException("Character.OnDeath is unavailable.");
            if (plugin.Metadata.Version != new System.Version(0, 2, 13))
                throw new NotSupportedException($"Unreviewed version {plugin.Metadata.Version}.");
            Type pluginType = plugin.Instance.GetType().Assembly.GetType("RtDQuestForge.QuestForgePlugin", true)!;
            _managerField = pluginType.GetField("Manager", BindingFlags.Public | BindingFlags.Static);
            _registerMethod = _managerField?.FieldType.GetMethod("RegisterKill", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string) }, null);
            if (_registerMethod?.ReturnType != typeof(void) ||
                AccessTools.Field(typeof(Character), "m_lastHit")?.FieldType != typeof(HitData))
                throw new MissingMethodException("Quest kill or last-attacker contract changed.");
            harmony.Patch(target,
                prefix: new HarmonyMethod(prefix) { priority = Priority.Last },
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last, after = new[] { PluginGuid } });
            ClanPlugin.ClanLogger.LogInfo("RtDQuestForge 0.2.13 clan kill-credit compatibility ready.");
        }
        catch (Exception exception)
        {
            if (target != null)
            {
                harmony.Unpatch(target, prefix);
                harmony.Unpatch(target, postfix);
            }
            Dispose();
            ClanPlugin.ClanLogger.LogWarning($"QuestForge sharing disabled: {exception.Message}");
        }
    }

    internal static void Dispose()
    {
        _managerField = null;
        _registerMethod = null;
        ResetSession();
    }

    internal static void ResetSession()
    {
        _lastManager = null;
        _registerKill = null;
    }

    private static void CaptureDeath(Character __instance, HitData ___m_lastHit, bool __runOriginal, out Death? __state)
    {
        __state = null;
        if (!__runOriginal || !IsReady || !ClanPlugin.ShareQuestForgeKills.Value.IsOn()) return;
        try
        {
            if (__instance is Player || __instance.GetHealth() > 0f ||
                ___m_lastHit?.GetAttacker() is not Player killer) return;
            ZDOID victim = __instance.GetZDOID();
            ZDO? zdo = ZDOMan.instance?.GetZDO(victim);
            if (zdo == null || !zdo.IsOwner()) return;
            __state = new Death(victim, killer.GetZDOID(), Utils.GetPrefabName(__instance.gameObject),
                __instance.transform.position);
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning($"Could not capture quest kill credit: {exception.Message}");
        }
    }

    private static void ShareDeath(bool __runOriginal, Death? __state)
    {
        if (!__runOriginal || __state == null) return;
        try
        {
            // OnDeath has reset the view. The captured ID is still in ZDOMan until
            // SendDestroyed runs later; send immediately over the existing connection.
            ClanGroupSharing.ShareQuestKill(__state.Victim, __state.Killer, __state.Prefab, __state.Position);
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning($"Could not share quest kill credit: {exception.Message}");
        }
    }

    internal static void ReceiveKill(string prefab)
    {
        object? manager = _managerField?.GetValue(null);
        if (manager == null || _registerMethod == null) return;
        if (!ReferenceEquals(manager, _lastManager))
        {
            _registerKill = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), manager, _registerMethod);
            _lastManager = manager;
        }
        _registerKill!(prefab);
    }

    private sealed class Death
    {
        internal readonly ZDOID Victim;
        internal readonly ZDOID Killer;
        internal readonly string Prefab;
        internal readonly Vector3 Position;

        internal Death(ZDOID victim, ZDOID killer, string prefab, Vector3 position)
        {
            Victim = victim;
            Killer = killer;
            Prefab = prefab;
            Position = position;
        }
    }
}
