# Reproducible, narrow binary adaptation of the existing vendored library.
# No protocol, authentication, buffering, configuration or version policy changes.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputAssembly,
    [Parameter(Mandatory)][string]$OutputAssembly,
    [Parameter(Mandatory)][string]$GameAssembly,
    [string]$CecilAssembly = "$env:USERPROFILE\.nuget\packages\mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll"
)
$ErrorActionPreference = 'Stop'
if ((Get-FileHash -LiteralPath $InputAssembly -Algorithm SHA256).Hash -ne '166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60') {
    throw 'Unexpected ServerSync input. Review a different library before adapting it.'
}
Add-Type -Path $CecilAssembly
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $GameAssembly).Path)
$field = ($game.MainModule.Types | Where-Object Name -eq ZRoutedRpc).Fields | Where-Object Name -eq Everybody
if (!$field.IsLiteral -or $field.FieldType.FullName -ne 'System.Int64' -or $field.Constant -ne 0L) {
    throw 'The target game does not expose the reviewed Everybody constant.'
}
$game.Dispose()
function Get-AllTypes($types) { foreach ($type in $types) { $type; Get-AllTypes $type.NestedTypes } }
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $InputAssembly).Path)
$sites = @(foreach ($type in (Get-AllTypes $assembly.MainModule.Types)) {
    foreach ($method in $type.Methods) {
        if (!$method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.Operand -is [Mono.Cecil.FieldReference] -and
                $instruction.Operand.FullName -eq 'System.Int64 ZRoutedRpc::Everybody') {
                if ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldsfld) { throw 'Unexpected field operation.' }
                [pscustomobject]@{Method=$method.FullName;Instruction=$instruction}
            }
        }
    }
})
if ($sites.Count -ne 3) { throw "Expected 3 field reads, found $($sites.Count)." }
foreach ($site in $sites) {
    $site.Instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I8
    $site.Instruction.Operand = [long]0
}
$assembly.MainModule.Mvid = [guid]'4c7919a1-f74b-4c59-b197-8119979a0060'
$assembly.Write([IO.Path]::GetFullPath($OutputAssembly))
$assembly.Dispose()
$sites.Method
Get-FileHash -LiteralPath $OutputAssembly -Algorithm SHA256
