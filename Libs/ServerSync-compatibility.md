# ServerSync input for Valheim 1.0.7

Clan retains its existing vendored ServerSync library and protocol. Its assembly version is 1.0.0.0; this is not a claim about an upstream release number.

The original binary is preserved in Git (for example commit `63f89c3`, `Libs/ServerSync.dll`). Original SHA-256: `166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60`.

The game changed `ZRoutedRpc.Everybody` from a static Int64 field to the Int64 constant 0. The adapted binary changes exactly three `ldsfld` instructions to `ldc.i8 0`: the AddConfigEntry callback, AddCustomValue callback, and sendZPackage iterator factory. All methods, configuration/version policies, packet formats, buffering, and access checks are retained. MVID is changed to identify this local adaptation.

Adapted SHA-256: `611D9F9C6D5C3A88021DD9A3FA74A8B6B959FCE0F859185AD392A36FAA783DBB`.

Reproduce with PowerShell 7, Mono.Cecil 0.11.6, the original input from Git, and the unmodified Valheim 1.0.7 assembly:

```powershell
pwsh -NoProfile -File Tools/PatchServerSync.ps1 -InputAssembly <original-ServerSync.dll> -OutputAssembly <adapted-ServerSync.dll> -GameAssembly <original-assembly_valheim.dll> -CecilAssembly <Mono.Cecil.dll>
```

The script rejects other input hashes or a different game field contract. It does not modify the game assembly. `ILRepack.targets` embeds this library in the final Clan.dll; no separate ServerSync installation is required. This adaptation does not fix other mods' independently embedded copies of ServerSync.
