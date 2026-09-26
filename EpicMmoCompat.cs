using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Clan;

internal static class EpicMmoCompat
{
    internal const string PluginGuid = "WackyMole.EpicMMOSystem";
    private static ConfigEntry<float>? _groupMultiplier;
    private static Action<long, int, Vector3, int>? _receive;
    internal static bool IsReady { get; private set; }

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        MethodInfo? target = null;
        MethodInfo transpiler = typeof(EpicMmoCompat).GetMethod(nameof(Transpile), BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            if (plugin.Metadata.Version != new System.Version(1, 9, 68))
                throw new NotSupportedException($"Unreviewed version {plugin.Metadata.Version}.");
            Assembly assembly = plugin.Instance.GetType().Assembly;
            Type type = assembly.GetType("EpicMMOSystem.MonsterDeath_Path", true)!;
            target = type.GetMethod("RPC_DeadMonster", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(long), typeof(ZPackage) }, null);
            MethodInfo? receive = type.GetMethod("RPC_AddGroupExp", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(long), typeof(int), typeof(Vector3), typeof(int) }, null);
            _groupMultiplier = assembly.GetType("EpicMMOSystem.EpicMMOSystem", true)!
                .GetField("groupExp", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as ConfigEntry<float>;
            if (target?.ReturnType != typeof(void) || receive?.ReturnType != typeof(void) || _groupMultiplier == null)
                throw new MissingMethodException("Combat XP contract changed.");
            _receive = (Action<long, int, Vector3, int>)Delegate.CreateDelegate(
                typeof(Action<long, int, Vector3, int>), receive);
            if (!TryFindInjection(PatchProcessor.GetOriginalInstructions(target), target, out _, out _))
                throw new NotSupportedException("Combat XP instruction pattern changed.");
            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
            if (!IsReady) throw new NotSupportedException("Another patch changed the combat XP instruction pattern.");
            ClanPlugin.ClanLogger.LogInfo("WackyEpicMMOSystem 1.9.68 clan combat XP compatibility ready.");
        }
        catch (Exception exception)
        {
            if (target != null) harmony.Unpatch(target, transpiler);
            Dispose();
            ClanPlugin.ClanLogger.LogWarning($"Epic MMO sharing disabled: {exception.Message}");
        }
    }

    internal static void Dispose()
    {
        IsReady = false;
        _receive = null;
        _groupMultiplier = null;
    }

    internal static void ReceiveExperience(long killer, int experience, Vector3 position, int monsterLevel)
    {
        if (IsReady && Player.m_localPlayer != null) _receive?.Invoke(killer, experience, position, monsterLevel);
    }

    private static bool ShareOrKeepNative(bool nativeGroups, int baseXp, Vector3 position, int level, bool boss)
    {
        if (nativeGroups || !IsReady || !ClanPlugin.ShareEpicMmoExperience.Value.IsOn() || baseXp <= 0)
            return nativeGroups;
        try
        {
            // Read the live config, retaining Epic's float multiplication and integer truncation.
            int experience = (int)(baseXp * _groupMultiplier!.Value);
            ClanGroupSharing.ShareEpicExperience(experience, position, boss && level != 0 ? -level : level);
        }
        catch (Exception exception)
        {
            ClanPlugin.ClanLogger.LogWarning($"Could not share Epic MMO combat XP: {exception.Message}");
        }
        return nativeGroups;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> code = instructions.ToList();
        IsReady = TryFindInjection(code, __originalMethod, out int index, out int[] locals);
        if (!IsReady)
        {
            ClanPlugin.ClanLogger.LogWarning("Epic MMO sharing disabled: combat XP patch no longer matches.");
            return code;
        }
        // Keep all original labels/EH markers, including the PvP jump to IsLoaded.
        code.InsertRange(index + 1, new[]
        {
            LoadLocal(code, locals[0]),
            LoadLocal(code, locals[1]),
            LoadLocal(code, locals[2]),
            LoadLocal(code, locals[3]),
            new CodeInstruction(OpCodes.Call, typeof(EpicMmoCompat).GetMethod(nameof(ShareOrKeepNative), BindingFlags.NonPublic | BindingFlags.Static))
        });
        return code;
    }

    // Infer locals from their producers; numbered locals are not an external API.
    internal static bool TryFindInjection(IList<CodeInstruction> code, MethodBase method, out int index, out int[] locals)
    {
        index = -1;
        locals = Array.Empty<int>();
        var slots = method.GetMethodBody()?.LocalVariables;
        if (slots == null) return false;
        var calls = Enumerable.Range(0, code.Count)
            .Where(i => Calls(code[i], "Groups.API", "IsLoaded")).ToArray();
        if (calls.Length != 1) return false;
        index = calls[0];
        if (index + 2 >= code.Count ||
            (code[index + 1].opcode != OpCodes.Brtrue && code[index + 1].opcode != OpCodes.Brtrue_S) ||
            code[index + 2].opcode != OpCodes.Ret || code[index + 1].labels.Count != 0 ||
            code[index + 1].blocks.Count != 0) return false;

        List<int> raw = new(), levels = new(), positions = new(), bosses = new();
        for (int i = 0; i < index - 4; i++)
        {
            if (Calls(code[i], "System.Convert", "ToInt32") &&
                code[i].operand is MethodInfo convert && convert.GetParameters().Length == 1 &&
                convert.GetParameters()[0].ParameterType == typeof(float))
            {
                int slot = Local(code[i + 1], true);
                if (slot >= 0 && Local(code[i + 2], false) == slot &&
                    Local(code[i + 3], true) >= 0 && Local(code[i + 3], true) != slot) raw.Add(slot);
            }
            if (Calls(code[i], "EpicMMOSystem.DataMonsters", "getLevel") && Local(code[i + 1], true) >= 0)
                levels.Add(Local(code[i + 1], true));
            if (Calls(code[i], "ZPackage", "ReadBool") && code[i + 1].opcode == OpCodes.Ldarg_1 &&
                Calls(code[i + 2], "ZPackage", "ReadVector3"))
            {
                positions.Add(Local(code[i + 3], true));
                bosses.Add(Local(code[i + 4], true));
            }
        }
        if (raw.Count != 1 || levels.Count != 1 || positions.Count != 1 || bosses.Count != 1) return false;
        locals = new[] { raw[0], positions[0], levels[0], bosses[0] };
        Type[] types = { typeof(int), typeof(Vector3), typeof(int), typeof(bool) };
        if (locals.Distinct().Count() != 4) return false;
        for (int i = 0; i < locals.Length; i++)
            if (locals[i] < 0 || locals[i] >= slots.Count || slots[locals[i]].LocalType != types[i]) return false;
        return true;
    }

    private static bool Calls(CodeInstruction code, string type, string name) =>
        (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) &&
        code.operand is MethodInfo method && method.DeclaringType?.FullName == type && method.Name == name;

    private static CodeInstruction LoadLocal(IList<CodeInstruction> code, int index)
    {
        CodeInstruction store = code.First(instruction => Local(instruction, true) == index);
        OpCode opcode = store.opcode == OpCodes.Stloc_0 ? OpCodes.Ldloc_0 :
            store.opcode == OpCodes.Stloc_1 ? OpCodes.Ldloc_1 :
            store.opcode == OpCodes.Stloc_2 ? OpCodes.Ldloc_2 :
            store.opcode == OpCodes.Stloc_3 ? OpCodes.Ldloc_3 :
            store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S : OpCodes.Ldloc;
        return new CodeInstruction(opcode, store.operand);
    }

    private static int Local(CodeInstruction code, bool store)
    {
        if (code.opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
        if (code.opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
        if (code.opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
        if (code.opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
        if (code.opcode != (store ? OpCodes.Stloc : OpCodes.Ldloc) &&
            code.opcode != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
        return code.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(code.operand);
    }
}
