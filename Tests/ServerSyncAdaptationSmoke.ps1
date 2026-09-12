#requires -Version 7.0
# Compares the adapted library with its exact Git baseline; no game execution.
[CmdletBinding()]
param([string]$CecilAssembly = "$env:USERPROFILE\.nuget\packages\mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll")
$ErrorActionPreference='Stop'
Add-Type -Path $CecilAssembly
$root=Split-Path $PSScriptRoot
$start=[Diagnostics.ProcessStartInfo]::new('git')
$start.WorkingDirectory=$root;$start.UseShellExecute=$false;$start.RedirectStandardOutput=$true
$start.ArgumentList.Add('show');$start.ArgumentList.Add('63f89c3:Libs/ServerSync.dll')
$process=[Diagnostics.Process]::Start($start)
$stream=[IO.MemoryStream]::new()
$process.StandardOutput.BaseStream.CopyTo($stream);$process.WaitForExit()
if($process.ExitCode -ne 0){throw 'Unable to read original ServerSync from Git'}
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray()))
if($hash -ne '166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60'){throw 'Wrong baseline'}
$stream.Position=0
$old=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($stream)
$new=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Libs/ServerSync.dll'))
function AllTypes($types){foreach($type in $types){$type;AllTypes $type.NestedTypes}}
function Operand($instruction,$body){
    $value=$instruction.Operand
    if($value -is [Mono.Cecil.Cil.Instruction]){return "target:$($body.Instructions.IndexOf($value))"}
    if($value -is [Mono.Cecil.Cil.Instruction[]]){return 'targets:'+(($value | ForEach-Object {$body.Instructions.IndexOf($_)}) -join ',')}
    if($value -is [Mono.Cecil.MemberReference]){return $value.FullName}
    return [string]$value
}
if($old.Name.FullName -ne $new.Name.FullName){throw 'Library identity changed'}
if(($old.MainModule.AssemblyReferences.FullName -join '|') -ne ($new.MainModule.AssemblyReferences.FullName -join '|')){throw 'Dependencies changed'}
$oldTypes=@(AllTypes $old.MainModule.Types);$newTypes=@(AllTypes $new.MainModule.Types)
if(($oldTypes.FullName -join '|') -ne ($newTypes.FullName -join '|')){throw 'Types changed'}
$changes=0;$methods=0;$instructions=0
for($t=0;$t -lt $oldTypes.Count;$t++){
    $a=$oldTypes[$t];$b=$newTypes[$t]
    if(($a.Fields | ForEach-Object {"$($_.FullName):$($_.Attributes):$($_.Constant)"}) -join '|' -cne
       (($b.Fields | ForEach-Object {"$($_.FullName):$($_.Attributes):$($_.Constant)"}) -join '|')){throw "Fields changed: $a"}
    if(($a.Methods.FullName -join '|') -ne ($b.Methods.FullName -join '|')){throw "Methods changed: $a"}
    for($m=0;$m -lt $a.Methods.Count;$m++){
        $before=$a.Methods[$m];$after=$b.Methods[$m];$methods++
        if($before.Attributes -ne $after.Attributes -or $before.HasBody -ne $after.HasBody){throw "Method contract changed: $before"}
        if(!$before.HasBody){continue}
        if($before.Body.Instructions.Count -ne $after.Body.Instructions.Count -or $before.Body.ExceptionHandlers.Count -ne $after.Body.ExceptionHandlers.Count){throw "Control flow size changed: $before"}
        for($i=0;$i -lt $before.Body.Instructions.Count;$i++){
            $x=$before.Body.Instructions[$i];$y=$after.Body.Instructions[$i];$instructions++
            if($x.OpCode.Name -eq 'ldsfld' -and $x.Operand.FullName -eq 'System.Int64 ZRoutedRpc::Everybody'){
                if($y.OpCode.Name -ne 'ldc.i8' -or $y.Operand -ne 0L){throw 'Incorrect constant replacement'}
                $changes++;continue
            }
            if($x.OpCode.Name -ne $y.OpCode.Name -or (Operand $x $before.Body) -cne (Operand $y $after.Body)){throw "Unexpected IL change: $before instruction $i"}
        }
        for($h=0;$h -lt $before.Body.ExceptionHandlers.Count;$h++){
            $x=$before.Body.ExceptionHandlers[$h];$y=$after.Body.ExceptionHandlers[$h]
            if($x.HandlerType -ne $y.HandlerType -or $x.CatchType.FullName -ne $y.CatchType.FullName){throw 'Exception policy changed'}
            foreach($edge in @('TryStart','TryEnd','HandlerStart','HandlerEnd','FilterStart')){
                if($before.Body.Instructions.IndexOf($x.$edge) -ne $after.Body.Instructions.IndexOf($y.$edge)){throw 'Exception boundary changed'}
            }
        }
    }
}
if($changes -ne 3){throw "Unexpected change count: $changes"}
"PASS: $methods methods / $instructions IL instructions compared; only 3 Everybody reads changed, exception boundaries preserved."
$old.Dispose();$new.Dispose();$stream.Dispose();$process.Dispose()
