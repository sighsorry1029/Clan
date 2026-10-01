#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EpicDll = "$env:APPDATA\com.kesomannen.gale\valheim\profiles\wackyepicmmoy\BepInEx\plugins\WackyMole-WackyEpicMMOSystem\EpicMMOSystem.dll",
    [string]$QuestDll = "$env:APPDATA\com.kesomannen.gale\valheim\profiles\wackyepicmmoy\BepInEx\plugins\Soloredis-RtDQuestForge\RtDQuestForge.dll",
    [string]$ModAssembly = (Join-Path (Split-Path $PSScriptRoot) 'bin\Debug\Clan.dll'),
    # Optional independent oracle from the reviewed original IL: raw XP, position, level, boss.
    [string]$ExpectedLocalSlots = '',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$CecilAssembly = "$env:USERPROFILE\.nuget\packages\mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll"
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilAssembly
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
public static class ClanCompatTestResolver {
    public static string[] Directories;
    private static readonly HashSet<string> Loading = new HashSet<string>();
    public static Assembly Resolve(object sender, ResolveEventArgs e) {
        string name = new AssemblyName(e.Name).Name;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == name) return a;
        if (!Loading.Add(name)) return null;
        try {
            foreach (string dir in Directories) {
                string path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return Assembly.UnsafeLoadFrom(path);
            }
            return null;
        } finally { Loading.Remove(name); }
    }
}
'@
[ClanCompatTestResolver]::Directories = @((Join-Path $GamePath 'BepInEx\core'), (Join-Path $GamePath 'valheim_Data\Managed'), (Split-Path $ModAssembly))
$resolver = [ResolveEventHandler][ClanCompatTestResolver]::Resolve
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
$original = $quest = $modMetadata = $null
$cecilResolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($directory in [ClanCompatTestResolver]::Directories) { $cecilResolver.AddSearchDirectory($directory) }
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $cecilResolver
try {
    $null = [Reflection.Assembly]::UnsafeLoadFrom((Join-Path $GamePath 'BepInEx\core\0Harmony.dll'))
    $epic = [Reflection.Assembly]::UnsafeLoadFrom((Resolve-Path $EpicDll))
    $clan = [Reflection.Assembly]::UnsafeLoadFrom((Resolve-Path $ModAssembly))
    $target = $epic.GetType('EpicMMOSystem.MonsterDeath_Path').GetMethod('RPC_DeadMonster')
    $original = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path $EpicDll), $reader)
    $quest = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path $QuestDll), $reader)
    $modMetadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path $ModAssembly), $reader)
    $cm = ($original.MainModule.Types | Where-Object FullName -eq 'EpicMMOSystem.MonsterDeath_Path').Methods | Where-Object Name -eq 'RPC_DeadMonster'
    $epicPlugin = $original.MainModule.Types | Where-Object FullName -eq 'EpicMMOSystem.EpicMMOSystem'
    $epicMetadata = $epicPlugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' }
    if ($epicMetadata.ConstructorArguments[0].Value -ne 'WackyMole.EpicMMOSystem') { throw 'Unexpected Epic plugin identity.' }
    $combatType = $original.MainModule.Types | Where-Object FullName -eq 'EpicMMOSystem.MonsterDeath_Path'
    $receiver = @($combatType.Methods | Where-Object { $_.Name -eq 'RPC_AddGroupExp' -and $_.IsPublic -and $_.IsStatic -and !$_.HasGenericParameters -and $_.ReturnType.FullName -eq 'System.Void' -and ($_.Parameters.ParameterType.FullName -join '|') -eq 'System.Int64|System.Int32|UnityEngine.Vector3|System.Int32' })
    if (!$cm.IsPublic -or !$cm.IsStatic -or $cm.HasGenericParameters -or $cm.ReturnType.FullName -ne 'System.Void' -or
        ($cm.Parameters.ParameterType.FullName -join '|') -ne 'System.Int64|ZPackage' -or $receiver.Count -ne 1 -or
        !($epicPlugin.Fields | Where-Object { $_.Name -eq 'groupExp' -and $_.IsPublic -and $_.IsStatic -and $_.FieldType.FullName -eq 'BepInEx.Configuration.ConfigEntry`1<System.Single>' })) { throw 'Epic combat API contract changed.' }
    if ($cm.Body.ExceptionHandlers.Count -ne 1 -or $cm.Body.ExceptionHandlers[0].HandlerType -ne 'Finally') { throw 'Epic EH shape is not supported by this standalone test converter.' }
    $plugin = $quest.MainModule.Types | Where-Object FullName -eq 'RtDQuestForge.QuestForgePlugin'
    $metadata = $plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' }
    if ($metadata.ConstructorArguments[0].Value -ne 'soloredis.rtdquestforge') { throw 'Unexpected QuestForge plugin identity.' }
    $managerField = @($plugin.Fields | Where-Object { $_.Name -eq 'Manager' -and $_.IsPublic -and $_.IsStatic })
    if ($managerField.Count -ne 1) { throw 'QuestForge Manager field changed.' }
    $manager = $managerField[0].FieldType.Resolve()
    $register = @($manager.Methods | Where-Object { $_.Name -eq 'RegisterKill' -and $_.IsPublic -and !$_.IsStatic -and !$_.HasGenericParameters -and $_.ReturnType.FullName -eq 'System.Void' -and ($_.Parameters.ParameterType.FullName -join '|') -eq 'System.String' })
    if ($register.Count -ne 1) { throw 'QuestForge RegisterKill contract changed.' }
    if (@($modMetadata.MainModule.AssemblyReferences | Where-Object { $_.Name -in @('EpicMMOSystem','RtDQuestForge','Jotunn') }).Count) { throw 'Optional bridge gained a hard assembly reference.' }
    $clanPlugin = $modMetadata.MainModule.Types | Where-Object FullName -eq 'Clan.ClanPlugin'
    foreach ($guid in @('WackyMole.EpicMMOSystem','soloredis.rtdquestforge')) {
        $dependency = @($clanPlugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments[0].Value -eq $guid })
        if ($dependency.Count -ne 1 -or [int]$dependency[0].ConstructorArguments[1].Value -ne 2) { throw "Not a soft dependency: $guid" }
    }

    # The installed Harmony reader cannot run on the standalone CLR with every
    # Unity assembly. Convert this reviewed method's original IL without editing
    # assemblies, then execute the actual production matcher/transpiler.
    $ops = @{}
    [Reflection.Emit.OpCodes].GetFields() | ForEach-Object { $v = $_.GetValue($null); if ($v -is [Reflection.Emit.OpCode]) { $ops[$v.Name] = $v } }
    $dm = [Reflection.Emit.DynamicMethod]::new('ClanCompatProbe', [void], [Type[]]@())
    $g = $dm.GetILGenerator()
    $locals = @($target.GetMethodBody().LocalVariables | ForEach-Object { $g.DeclareLocal($_.LocalType, $_.IsPinned) })
    $labels = @{}
    foreach ($ci in $cm.Body.Instructions) {
        if ($ci.Operand -is [Mono.Cecil.Cil.Instruction]) { $labels[$ci.Operand.Offset] = $g.DefineLabel() }
        elseif ($ci.Operand -is [Mono.Cecil.Cil.Instruction[]]) { foreach ($dest in $ci.Operand) { $labels[$dest.Offset] = $g.DefineLabel() } }
    }
    $code = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    $lookup = @{}
    foreach ($ci in $cm.Body.Instructions) {
        $o = $ci.Operand
        if ($o -is [Mono.Cecil.MethodReference]) { $o = $target.Module.ResolveMethod($o.MetadataToken.ToInt32()) }
        elseif ($o -is [Mono.Cecil.FieldReference]) { $o = $target.Module.ResolveField($o.MetadataToken.ToInt32()) }
        elseif ($o -is [Mono.Cecil.TypeReference]) { $o = $target.Module.ResolveType($o.MetadataToken.ToInt32()) }
        elseif ($o -is [Mono.Cecil.Cil.VariableDefinition]) { $o = $locals[$o.Index] }
        elseif ($o -is [Mono.Cecil.Cil.Instruction]) { $o = $labels[$o.Offset] }
        elseif ($o -is [Mono.Cecil.Cil.Instruction[]]) { $o = [Reflection.Emit.Label[]]@($o | ForEach-Object { $labels[$_.Offset] }) }
        $entry = [HarmonyLib.CodeInstruction]::new($ops[$ci.OpCode.Name], $o)
        if ($labels.ContainsKey($ci.Offset)) { $entry.labels.Add($labels[$ci.Offset]) }
        $code.Add($entry); $lookup[$ci.Offset] = $entry
    }
    foreach ($eh in $cm.Body.ExceptionHandlers) {
        $lookup[$eh.TryStart.Offset].blocks.Add([HarmonyLib.ExceptionBlock]::new([HarmonyLib.ExceptionBlockType]::BeginExceptionBlock, $null))
        $lookup[$eh.HandlerStart.Offset].blocks.Add([HarmonyLib.ExceptionBlock]::new([HarmonyLib.ExceptionBlockType]::BeginFinallyBlock, $null))
        $lookup[$eh.HandlerEnd.Offset].blocks.Add([HarmonyLib.ExceptionBlock]::new([HarmonyLib.ExceptionBlockType]::EndExceptionBlock, $null))
    }
    $compat = $clan.GetType('Clan.EpicMmoCompat')
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $matcher = $compat.GetMethod('TryFindInjection', $flags)
    $argsForMatch = [object[]]@($code, $target, -1, $null)
    if (!$matcher.Invoke($null, $argsForMatch)) { throw 'Production matcher rejected the reviewed original Epic DLL.' }
    $insertion = [int]$argsForMatch[2]
    $selectedLocals = [int[]]$argsForMatch[3]
    if ($ExpectedLocalSlots -and ($selectedLocals -join ',') -ne $ExpectedLocalSlots) {
        throw "Wrong semantic locals: $($selectedLocals -join ','); expected $ExpectedLocalSlots."
    }
    $originalCount = $code.Count
    $originalObjects = @($code)
    function Instruction-State($instruction) {
        $operand = if ($instruction.operand -is [Reflection.Emit.Label]) {
            'label:' + $instruction.operand.GetHashCode()
        } elseif ($instruction.operand -is [Reflection.MemberInfo]) {
            "$($instruction.operand.Module.ModuleVersionId):$($instruction.operand.MetadataToken):$($instruction.operand.DeclaringType.AssemblyQualifiedName):$($instruction.operand)"
        } else { [string]$instruction.operand }
        $labelState = ($instruction.labels | ForEach-Object { $_.GetHashCode() }) -join ','
        $blockState = ($instruction.blocks | ForEach-Object { "$($_.blockType):$($_.catchType)" }) -join ','
        "$($instruction.opcode)|$operand|$labelState|$blockState"
    }
    $originalStates = @($code | ForEach-Object { Instruction-State $_ })
    $labelCount = ($code | ForEach-Object { $_.labels.Count } | Measure-Object -Sum).Sum
    $blockCount = ($code | ForEach-Object { $_.blocks.Count } | Measure-Object -Sum).Sum
    $rewritten = @($compat.GetMethod('Transpile', $flags).Invoke($null, @($code, $target)))
    if ($rewritten.Count -ne $originalCount + 5) { throw 'Unexpected instruction count.' }
    for ($i = 0; $i -lt $originalObjects.Count; $i++) {
        $newIndex = $i; if ($i -gt $insertion) { $newIndex += 5 }
        if (![object]::ReferenceEquals($originalObjects[$i], $rewritten[$newIndex])) { throw 'Original instruction replaced/reordered.' }
        if ((Instruction-State $rewritten[$newIndex]) -cne $originalStates[$i]) { throw 'Original instruction or label/EH placement mutated.' }
    }
    if (($rewritten | ForEach-Object { $_.labels.Count } | Measure-Object -Sum).Sum -ne $labelCount -or
        ($rewritten | ForEach-Object { $_.blocks.Count } | Measure-Object -Sum).Sum -ne $blockCount) { throw 'Branch or EH marker changed.' }
    if ($rewritten[$insertion + 5].operand.Name -ne 'ShareOrKeepNative') { throw 'Wrong injected callback.' }
    $readLocal = $compat.GetMethod('Local', $flags)
    for ($i = 0; $i -lt 4; $i++) {
        if ($readLocal.Invoke($null, @($rewritten[$insertion + 1 + $i], $false)) -ne $selectedLocals[$i]) {
            throw 'Injected XP/position/level/boss argument order changed.'
        }
    }
    foreach ($instruction in $rewritten[($insertion + 1)..($insertion + 5)]) {
        if ($instruction.labels.Count -or $instruction.blocks.Count) { throw 'Inserted instruction captured a branch/EH boundary.' }
    }

    # Mutate the original code, never the production assembly or the game DLLs.
    # These fixtures exercise wrong values of the same type and broken control flow.
    function Copy-OriginalCode {
        $copy = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
        foreach ($instruction in $originalObjects) { $copy.Add([HarmonyLib.CodeInstruction]::new($instruction)) }
        return ,$copy
    }
    function Assert-Rejected($fixtureCode, [string]$reason) {
        $probe = [object[]]@($fixtureCode, $target, -1, $null)
        if ($matcher.Invoke($null, $probe)) { throw "Unsafe pattern accepted: $reason" }
    }
    $groupField = @((0..($originalCount - 1)) | Where-Object {
        $originalObjects[$_].opcode -eq [Reflection.Emit.OpCodes]::Ldsfld -and
        $originalObjects[$_].operand.Name -eq 'groupExp'
    })
    if ($groupField.Count -ne 1) { throw 'Expected one native group multiplier load in the fixture.' }
    $bossStart = $groupField[0] - 8
    $changed = Copy-OriginalCode
    $changed[$bossStart + 2] = [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldloc, $locals[$selectedLocals[0]])
    Assert-Rejected $changed 'raw XP used as the native level'
    $changed = Copy-OriginalCode
    $changed[$bossStart] = [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldloc, $locals[$selectedLocals[2]])
    Assert-Rejected $changed 'integer level used as the boss flag'
    $changed = Copy-OriginalCode
    $changed[$bossStart + 1].operand = $g.DefineLabel()
    Assert-Rejected $changed 'boss branch misses the multiplier join'
    $changed = Copy-OriginalCode
    $changed.Insert($insertion + 3, [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Stloc, $locals[$selectedLocals[3]]))
    $changed.Insert($insertion + 3, [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Ldc_I4_0))
    Assert-Rejected $changed 'boss flag overwritten after the injected callback'

    $effectiveCall = @((0..($insertion - 1)) | Where-Object {
        $originalObjects[$_].operand -is [Reflection.MethodInfo] -and
        $originalObjects[$_].operand.Name -eq 'GetEffectiveLevel'
    })
    if ($effectiveCall.Count) {
        if ($effectiveCall.Count -ne 1) { throw 'Expected one effective-level producer in the fixture.' }
        $producer = $effectiveCall[0]
        $changed = Copy-OriginalCode
        $changed[$producer + 1] = [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Stloc, $locals[$selectedLocals[0]])
        Assert-Rejected $changed 'effective level stored to the wrong integer local'
        $changed = Copy-OriginalCode
        $changed.Insert($insertion, [HarmonyLib.CodeInstruction]::new($originalObjects[$producer + 1]))
        $changed.Insert($insertion, [HarmonyLib.CodeInstruction]::new($originalObjects[$producer]))
        Assert-Rejected $changed 'ambiguous effective-level producers'
        $changed = Copy-OriginalCode
        $changed[$producer].operand = [Math].GetMethod('Max', [Type[]]@([int], [int]))
        Assert-Rejected $changed 'unrecognized level calculation'
    }
    $duplicate = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    foreach ($instruction in $originalObjects) { $duplicate.Add($instruction) }
    $duplicate.Add($originalObjects[$insertion])
    $negative = [object[]]@($duplicate, $target, -1, $null)
    if ($matcher.Invoke($null, $negative)) { throw 'Ambiguous anchor was accepted.' }
    # Same full type/method name, but changed stack contracts must not be patched.
    foreach ($variant in @('argument', 'return', 'instance')) {
        $fixture = [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly([Reflection.AssemblyName]::new("ClanAnchorProbe_$variant"), [Reflection.Emit.AssemblyBuilderAccess]::Run)
        $fixtureModule = $fixture.DefineDynamicModule('Main')
        $typeBuilder = $fixtureModule.DefineType('Groups.API', [Reflection.TypeAttributes]::Public)
        $attributes = [Reflection.MethodAttributes]::Public
        if ($variant -ne 'instance') { $attributes = $attributes -bor [Reflection.MethodAttributes]::Static }
        $returnType = if ($variant -eq 'return') { [int] } else { [bool] }
        $parameters = if ($variant -eq 'argument') { [Type[]]@([int]) } else { [Type[]]@() }
        $methodBuilder = $typeBuilder.DefineMethod('IsLoaded', $attributes, $returnType, [Type[]]$parameters)
        $il = $methodBuilder.GetILGenerator(); $il.Emit([Reflection.Emit.OpCodes]::Ldc_I4_0); $il.Emit([Reflection.Emit.OpCodes]::Ret)
        $badAnchor = $typeBuilder.CreateType().GetMethod('IsLoaded')
        $changed = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
        foreach ($instruction in $originalObjects) { $changed.Add($instruction) }
        $changed[$insertion] = [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Call, $badAnchor)
        $negative = [object[]]@($changed, $target, -1, $null)
        if ($matcher.Invoke($null, $negative)) { throw "Incompatible $variant anchor was accepted." }

        if ($effectiveCall.Count) {
            $levelType = $fixtureModule.DefineType('EpicMMOSystem.DataMonsters', [Reflection.TypeAttributes]::Public)
            $levelReturn = if ($variant -eq 'return') { [float] } else { [int] }
            $levelParameters = if ($variant -eq 'argument') { [Type[]]@([int], [int]) } else { [Type[]]@([int], [int], [int]) }
            $levelMethod = $levelType.DefineMethod('GetEffectiveLevel', $attributes, $levelReturn, $levelParameters)
            $levelIL = $levelMethod.GetILGenerator()
            $levelIL.Emit([Reflection.Emit.OpCodes]::Ldc_I4_0)
            if ($variant -eq 'return') { $levelIL.Emit([Reflection.Emit.OpCodes]::Conv_R4) }
            $levelIL.Emit([Reflection.Emit.OpCodes]::Ret)
            $changed = Copy-OriginalCode
            $changed[$effectiveCall[0]].operand = $levelType.CreateType().GetMethod('GetEffectiveLevel')
            Assert-Rejected $changed "incompatible $variant effective-level signature"
        }
    }
    Write-Output "Inspected: Epic plugin $($epicMetadata.ConstructorArguments[2].Value) / assembly $($original.Name.Version); QuestForge plugin $($metadata.ConstructorArguments[2].Value) / assembly $($quest.Name.Version)."
    Write-Output "PASS: original Epic IL $originalCount -> $($rewritten.Count) instructions; $labelCount branch labels and $blockCount EH markers preserved; ambiguous pattern rejected."
    Write-Output "PASS: selected raw-XP/position/level/boss locals [$($selectedLocals -join ',')], injected argument order and per-instruction state; incorrect level/boss/branch/producer fixtures rejected."
    Write-Output 'PASS: same-name anchors and present effective-level calls with incompatible parameters, return type or instance scope rejected.'
    Write-Output 'PASS: original QuestForge manager contract, optional loader dependencies and absence of direct external assembly references. No game/XP/reward execution.'
}
finally {
    if ($original) { $original.Dispose() }; if ($quest) { $quest.Dispose() }; if ($modMetadata) { $modMetadata.Dispose() }
    $cecilResolver.Dispose()
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
