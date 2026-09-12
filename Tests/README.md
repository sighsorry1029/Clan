# Clan verification

Development target: Valheim 1.0.12, original game assemblies, no Jotunn dependency.

```powershell
dotnet build Clan.csproj -c Debug -p:DeployToGame=true
pwsh -NoProfile -File Tests/ClanChatIntegrationSmoke.ps1
pwsh -NoProfile -File Tests/ClanMediaStateSmoke.ps1
pwsh -NoProfile -File Tests/ClanPanelInteractionSmoke.ps1
pwsh -NoProfile -File Tests/ClanChatDockLayoutSmoke.ps1
pwsh -NoProfile -File Tests/ClanUiResourceSmoke.ps1
pwsh -NoProfile -File Tests/ClanValheim1Compatibility.ps1
pwsh -NoProfile -File Tests/ServerSyncAdaptationSmoke.ps1
```

The first four scripts execute selected production method bodies with controlled game/Unity substitutes. The resource test covers delayed discovery, caching, headless execution, atlas copy ownership, scene changes, event cleanup, and balanced game asset references. It does not load real game assets or render the UI.

The compatibility script examines the actual final DLL against client and dedicated-server original assemblies. It checks game member access, declared Harmony targets, specific reflection contracts, and removal of Jotunn. Path parameters can be overridden. It requires Mono.Cecil 0.11.6 (or its explicit DLL path). It does not apply Harmony patches or execute Unity.

The ServerSync comparison reads the original library from Git commit 63f89c3 and verifies that the adapted library changes only the three reviewed static field reads, retaining method identities, other instructions, and exception boundaries. See `Libs/ServerSync-compatibility.md` for reproduction.

Before release, run a real 1.0.12 client without Jotunn, then a local host and a dedicated server with a remote client. Verify:

- Fonts, button sounds/styles, tooltip order, HUD, PNG/GIF emoji and panel drag/resize at different screen scales.
- H/J, Enter/Escape, KeepOpen/CloseAfterSend, Ctrl+F1 and mouse/gamepad switching; build, report, inventory and split dialogs must keep their input.
- Repeated world entry/exit and reconnect: no extra event subscriptions, duplicate overlays, retained drag input or unbalanced asset references.
- Server settings and media sync, matching/mismatching builds, admin/non-admin actions, clan membership/roles, private chat recipients, hidden positions and clan-only pings.
- Split item quantities and no accidental click-through; clan persistence after save/reload. No new item storage or migration is implemented by this patch.
- The supported mod combination, including ServerManager and independently embedded ServerSync copies. Fixes to Clan do not update other mods' libraries.

UI resource acquisition uses the game's extended asset manifest and loads only seven named UI assets. It never initializes the game's loader early; required assets are held for the plugin lifetime, released on shutdown, and atlas sprite copies/Clan overlays are cleared on scene replacement. Borrowed game resources are never destroyed by Clan.
