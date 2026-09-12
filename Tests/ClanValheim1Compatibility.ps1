#requires -Version 7.0
# Checks actual compiled IL against original game DLLs. This is not a Unity run.
[CmdletBinding()]
param(
    [string]$ModAssembly = (Join-Path (Split-Path $PSScriptRoot) 'bin\Debug\Clan.dll'),
    [string]$ClientManaged = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed',
    [string]$ServerManaged = 'D:\SteamLibrary\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed',
    [string]$BepInExCore = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core',
    [string]$CecilAssembly = "$env:USERPROFILE\.nuget\packages\mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll"
)
$ErrorActionPreference='Stop'
Add-Type -Path $CecilAssembly
function AllTypes($types){foreach($type in $types){$type;AllTypes $type.NestedTypes}}
foreach($managed in @($ClientManaged,$ServerManaged)){
    $resolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
    $resolver.AddSearchDirectory($managed)
    $resolver.AddSearchDirectory($BepInExCore)
    $parameters=[Mono.Cecil.ReaderParameters]::new();$parameters.AssemblyResolver=$resolver
    $mod=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path $ModAssembly).Path,$parameters)
    if(@($mod.MainModule.AssemblyReferences | Where-Object Name -eq Jotunn).Count){throw 'Jotunn reference remains'}
    $calls=0;$patches=0
    foreach($type in (AllTypes $mod.MainModule.Types)){
        foreach($method in $type.Methods){
            if(!$method.HasBody){continue}
            foreach($instruction in $method.Body.Instructions){
                $member=$instruction.Operand
                if($member -isnot [Mono.Cecil.MethodReference] -and $member -isnot [Mono.Cecil.FieldReference]){continue}
                if($member.DeclaringType.Scope.Name -notin @('assembly_valheim','assembly_utils','assembly_guiutils','gui_framework','Splatform','SoftReferenceableAssets')){continue}
                $resolved=$member.Resolve()
                if(!$resolved){throw "Unresolved game member: $member in $method"}
                if(!$resolved.IsPublic){throw "Direct nonpublic access: $member in $method"}
                if($resolved -is [Mono.Cecil.FieldDefinition] -and $resolved.IsLiteral){throw "Runtime literal field read: $member"}
                $calls++
            }
        }
        foreach($owner in @($type)+@($type.Methods)){
            foreach($attribute in $owner.CustomAttributes){
                if($attribute.AttributeType.FullName -ne 'HarmonyLib.HarmonyPatch'){continue}
                $targetType=$null;$name='';$argumentTypes=$null
                foreach($argument in $attribute.ConstructorArguments){
                    switch($argument.Type.FullName){
                        'System.Type' {$targetType=$argument.Value}
                        'System.String' {$name=[string]$argument.Value}
                        'System.Type[]' {$argumentTypes=@($argument.Value | ForEach-Object {$_.Value.FullName})}
                        'HarmonyLib.MethodType' {if([int]$argument.Value -eq 2){$name="set_$name"};if([int]$argument.Value -eq 1){$name="get_$name"}}
                    }
                }
                if(!$targetType -or !$name){continue} # Dynamic targets checked separately below.
                $definition=$targetType.Resolve()
                if(!$definition){throw "Missing patch type $targetType"}
                $candidates=@($definition.Methods | Where-Object Name -eq $name)
                if($null -ne $argumentTypes){$candidates=@($candidates | Where-Object {($_.Parameters.ParameterType.FullName -join '|') -eq ($argumentTypes -join '|')})}
                if($candidates.Count -ne 1){throw "Patch target $targetType.$name has $($candidates.Count) matches"}
                $patches++
            }
        }
    }
    $game=$resolver.Resolve([Mono.Cecil.AssemblyNameReference]::new('assembly_valheim',[version]'0.0.0.0'))
    $split=($game.MainModule.Types | Where-Object Name -eq SplitDialog).Fields | Where-Object Name -eq m_panel
    if(!$split -or $split.FieldType.FullName -ne 'UnityEngine.RectTransform'){throw 'SplitDialog field contract changed'}
    $terminal=$game.MainModule.Types | Where-Object Name -eq Terminal
    foreach($signature in @('Splatform.PlatformUserID|System.String|Talker/Type|System.Boolean','System.String|System.String|Talker/Type|System.Boolean')){
        if(@($terminal.Methods | Where-Object {$_.Name -eq 'AddString' -and ($_.Parameters.ParameterType.FullName -join '|') -eq $signature}).Count -ne 1){throw "Missing dynamic chat target $signature"}
    }
    $gui=$resolver.Resolve([Mono.Cecil.AssemblyNameReference]::new('UnityEngine.ImageConversionModule',[version]'0.0.0.0'))
    $imageType=$gui.MainModule.Types | Where-Object FullName -eq UnityEngine.ImageConversion
    if(@($imageType.Methods | Where-Object {$_.Name -eq 'LoadImage' -and ($_.Parameters.ParameterType.FullName -join '|') -eq 'UnityEngine.Texture2D|System.Byte[]'}).Count -ne 1){throw 'Missing byte[] image loader'}
    $soft=$resolver.Resolve([Mono.Cecil.AssemblyNameReference]::new('SoftReferenceableAssets',[version]'0.0.0.0'))
    $runtime=$soft.MainModule.Types | Where-Object FullName -eq SoftReferenceableAssets.Runtime
    $loaderField=$runtime.Fields | Where-Object Name -eq s_assetLoader
    $loaderType=$soft.MainModule.Types | Where-Object FullName -eq SoftReferenceableAssets.AssetBundleLoader
    $ready=$loaderType.Properties | Where-Object Name -eq Initialized
    if(!$loaderField -or !$loaderField.IsStatic -or $loaderField.FieldType.FullName -ne 'SoftReferenceableAssets.IAssetLoader' -or !$ready -or $ready.PropertyType.FullName -ne 'System.Boolean'){throw 'Game asset loader reflection contract changed'}
    "PASS: $managed — $calls public game IL references, $patches declared Harmony targets, dynamic chat/image/split contracts; no Jotunn."
    $mod.Dispose();$resolver.Dispose()
}
