using System;
using HarmonyLib;
using UnityEngine;

namespace Clan;

internal static class ClanFriendlyFire
{
    private const short FriendlyAoeMarker = -23749;

    [ThreadStatic]
    private static int _friendlyAoeDepth;

    private static bool ShouldBlockDamage(Character target, HitData hit)
    {
        if (ClanPlugin.ClanFriendlyFire.Value.IsOn() ||
            target != Player.m_localPlayer ||
            hit.m_weakSpot == FriendlyAoeMarker)
        {
            return false;
        }

        Character attacker = hit.GetAttacker();
        if (attacker is not Player attackerPlayer || attackerPlayer == Player.m_localPlayer)
        {
            return false;
        }

        ClanPlayerRef attackerRef = ClanIdentity.FromPlayer(attackerPlayer);
        return attackerRef.IsValid && ClanRpc.CurrentSnapshot.ContainsClanPlayer(attackerRef);
    }

    [HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
    private static class DamagePatch
    {
        private static bool Prefix(Character __instance, HitData hit)
        {
            return !ShouldBlockDamage(__instance, hit);
        }
    }

    [HarmonyPatch(typeof(Aoe), nameof(Aoe.OnHit))]
    private static class FriendlyAoeScopePatch
    {
        private static void Prefix(Aoe __instance, Collider collider, out bool __state)
        {
            GameObject hitObject = Projectile.FindHitObject(collider);
            __state =
                __instance.m_hitFriendly &&
                hitObject != null &&
                hitObject.GetComponent<Player>() != null;
            if (__state)
            {
                _friendlyAoeDepth++;
            }
        }

        private static Exception? Finalizer(Exception? __exception, bool __state)
        {
            if (__state)
            {
                _friendlyAoeDepth--;
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.FindWeakSpotIndex))]
    private static class MarkFriendlyAoePatch
    {
        private static void Postfix(ref short __result)
        {
            if (_friendlyAoeDepth > 0)
            {
                __result = FriendlyAoeMarker;
            }
        }
    }
}
