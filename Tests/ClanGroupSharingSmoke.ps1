#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$production = Get-Content -LiteralPath (Join-Path $root 'ClanGroupSharing.cs') -Raw
$support = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ClanGroupSharingTestSupport.cs') -Raw
# Execute the entire production transport/eligibility implementation, with controlled
# connections, game state and consumers. No external mod or Unity execution is claimed.
$production = $production.Replace('namespace Clan;', 'namespace Clan {') + "`n}"
Add-Type -TypeDefinition ("#nullable enable`n" + $production + "`n#nullable disable`n" + $support) -WarningAction SilentlyContinue
[Clan.GroupSharingTests]::Run()
