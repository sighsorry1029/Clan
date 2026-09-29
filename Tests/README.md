# Clan verification

Development target: Valheim 1.0.12, original game assemblies, no Jotunn dependency.

```powershell
dotnet build Clan.csproj -c Debug -p:DeployToGame=true
pwsh -NoProfile -File Tests/ClanConfigReloadSmoke.ps1
pwsh -NoProfile -File Tests/ClanChatIntegrationSmoke.ps1
pwsh -NoProfile -File Tests/ClanMediaStateSmoke.ps1
pwsh -NoProfile -File Tests/ClanPanelInteractionSmoke.ps1
pwsh -NoProfile -File Tests/ClanChatDockLayoutSmoke.ps1
pwsh -NoProfile -File Tests/ClanUiResourceSmoke.ps1
pwsh -NoProfile -File Tests/ClanValheim1Compatibility.ps1
pwsh -NoProfile -File Tests/ServerSyncAdaptationSmoke.ps1
pwsh -NoProfile -File Tests/ClanGroupSharingSmoke.ps1
pwsh -NoProfile -File Tests/ClanOptionalCompatContracts.ps1
```

The chat, media, panel and dock layout scripts execute selected production method bodies with controlled game/Unity substitutes. The resource test covers delayed discovery, caching, headless execution, atlas copy ownership, scene changes, event cleanup, and balanced game asset references. It does not load real game assets or render the UI.

`ClanConfigReloadSmoke.ps1` compiles the production watcher fields and methods unchanged with a controlled config/save substitute. Its 30 checks use real temporary files and Windows filesystem events, including three-second dispatcher delays, duplicate writes, atomic replacement and exclusive file locks. It also checks rapid edits, writes during reload, an external revert following an in-game save, bounded retry, save-flag restoration and cleanup. It does not load BepInEx/ServerSync or run the game. In-game validation must additionally cover client-only settings and admin/non-admin edits of locked server settings, followed by disconnect/reconnect to verify local preferences are preserved.

The compatibility script examines the actual final DLL against client and dedicated-server original assemblies. It checks game member access, declared Harmony targets, specific reflection contracts, and removal of Jotunn. Path parameters can be overridden. It requires Mono.Cecil 0.11.6 (or its explicit DLL path). It does not apply Harmony patches or execute Unity.

The ServerSync comparison reads the original library from Git commit 63f89c3 and verifies that the adapted library changes only the three reviewed static field reads, retaining method identities, other instructions, and exception boundaries. See `Libs/ServerSync-compatibility.md` for reproduction.

`ClanGroupSharingSmoke.ps1` compiles the entire production group-credit transport with controlled game/network/consumer substitutes. Its 38 checks cover effective membership, Guest precedence, host delivery, caller/character/owner validation, malformed messages, range, live settings, sequence/death replay protection, bounded caches, rate windows and session reset. It does not execute either external mod.

`ClanOptionalCompatContracts.ps1` accepts paths to the original optional DLLs with `-EpicDll` and `-QuestDll`. It verifies plugin identities and required APIs without a version allowlist, prints the inspected versions, and executes the production Epic matcher/transpiler on original IL converted through Cecil to reflection instructions. It checks that only the five intended instructions are added and original instructions, branch labels and exception markers survive. Ambiguous input and same-name anchors with incompatible parameters, return type or instance scope are rejected. Original DLL checks have covered WackyEpicMMOSystem 1.9.68/1.9.70 and RtDQuestForge 0.2.13; these are validation records, not runtime version restrictions. It does not install a live Harmony detour or execute game/XP/reward code; the conversion handles the reviewed method's single finally block, not arbitrary assemblies.

For optional sharing, run local-host and dedicated-server tests with the same Clan build on all participants (including creature owners). Cover killer/owner differences, fast spawn/death, owner transfer, delayed events, same display names, Guest changes, death/respawn and reconnect. Check Epic curves/Mentor/XP potions, bosses/stars/DoT/tamed summons/PvP, and QuestForge accepted/unaccepted/completed quests, personal rewards and full inventories. See `docs/RTDQUESTFORGE_HANDOFF.md` for the upstream boundaries and known limitations.

Before release, run a real 1.0.12 client without Jotunn, then a local host and a dedicated server with a remote client. Verify:

- Fonts, button sounds/styles, tooltip order, HUD, PNG/GIF emoji and panel drag/resize at different screen scales.
- H/J, Enter/Escape, KeepOpen/CloseAfterSend, Ctrl+F1 and mouse/gamepad switching; build, report, inventory and split dialogs must keep their input.
- Repeated world entry/exit and reconnect: no extra event subscriptions, duplicate overlays, retained drag input or unbalanced asset references.
- Server settings and media sync, matching/mismatching builds, admin/non-admin actions, clan membership/roles, private chat recipients, hidden positions and clan-only pings.
- Split item quantities and no accidental click-through; clan persistence after save/reload. No new item storage or migration is implemented by this patch.
- The supported mod combination, including ServerManager and independently embedded ServerSync copies. Fixes to Clan do not update other mods' libraries.

UI resource acquisition uses the game's extended asset manifest and loads only seven named UI assets. It never initializes the game's loader early; required assets are held for the plugin lifetime, released on shutdown, and atlas sprite copies/Clan overlays are cleared on scene replacement. Borrowed game resources are never destroyed by Clan.
