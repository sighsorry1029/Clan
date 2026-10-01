# Clan verification

Build references follow the installed original Valheim assemblies configured in `environment.props`; there is no Jotunn dependency. The 2026-10-01 checks below used Windows x64 Valheim 1.0.16. This records the inspected build, not a change to runtime support policy or proof of in-game compatibility.

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

The media test also executes the production animation-budget methods and sprite-tag expression. It checks newest-GIF priority, zero/excess budget, reactivation of frozen GIFs, case-insensitive tags, static emoji normalization, and preservation of unknown tags and absent text/runtime. These checks do not execute TMP animation or build Unity sprite sheets.

`ClanConfigReloadSmoke.ps1` compiles the production watcher fields and methods unchanged with a controlled config/save substitute. Its 30 checks use real temporary files and Windows filesystem events, including three-second dispatcher delays, duplicate writes, atomic replacement and exclusive file locks. It also checks rapid edits, writes during reload, an external revert following an in-game save, bounded retry, save-flag restoration and cleanup. It does not load BepInEx/ServerSync or run the game. In-game validation must additionally cover client-only settings and admin/non-admin edits of locked server settings, followed by disconnect/reconnect to verify local preferences are preserved.

The compatibility script examines the actual final DLL against client and dedicated-server original assemblies. It checks game member access, declared Harmony targets, specific reflection contracts, and removal of Jotunn. Path parameters can be overridden. It requires Mono.Cecil 0.11.6 (or its explicit DLL path). It does not apply Harmony patches or execute Unity.

The ServerSync comparison reads the original library from Git commit 63f89c3 and verifies that the adapted library changes only the three reviewed static field reads, retaining method identities, other instructions, and exception boundaries. See `Libs/ServerSync-compatibility.md` for reproduction.

`ClanGroupSharingSmoke.ps1` compiles the entire production group-credit transport with controlled game/network/consumer substitutes. Its 38 checks cover effective membership, Guest precedence, host delivery, caller/character/owner validation, malformed messages, range, live settings, sequence/death replay protection, bounded caches, rate windows and session reset. It does not execute either external mod.

`ClanOptionalCompatContracts.ps1` accepts paths to the original optional DLLs with `-EpicDll` and `-QuestDll`. It verifies plugin identities and required APIs without a version allowlist, prints the inspected versions, and executes the production Epic matcher/transpiler on original IL converted through Cecil to reflection instructions. It checks that only the five intended instructions are added and original instructions, branch labels and exception markers survive. Ambiguous input and same-name anchors with incompatible parameters, return type or instance scope are rejected. Original DLL checks have covered WackyEpicMMOSystem 1.9.68/1.9.70 and RtDQuestForge 0.2.13; these are validation records, not runtime version restrictions. It does not install a live Harmony detour or execute game/XP/reward code; the conversion handles the reviewed method's single finally block, not arbitrary assemblies.

For optional sharing, run local-host and dedicated-server tests with the same Clan build on all participants (including creature owners). Cover killer/owner differences, fast spawn/death, owner transfer, delayed events, same display names, Guest changes, death/respawn and reconnect. Check Epic curves/Mentor/XP potions, bosses/stars/DoT/tamed summons/PvP, and QuestForge accepted/unaccepted/completed quests, personal rewards and full inventories. See `docs/RTDQUESTFORGE_HANDOFF.md` for the upstream boundaries and known limitations.

Before release, run the target Valheim client without Jotunn, then a local host and a dedicated server with a remote client. Verify:

- Fonts, button sounds/styles, tooltip order, HUD, PNG/GIF emoji and panel drag/resize at different screen scales.
- H/J, Enter/Escape, KeepOpen/CloseAfterSend, Ctrl+F1 and mouse/gamepad switching; build, report, inventory and split dialogs must keep their input.
- Repeated world entry/exit and reconnect: no extra event subscriptions, duplicate overlays, retained drag input or unbalanced asset references.
- Server settings and media sync, matching/mismatching builds, admin/non-admin actions, clan membership/roles, private chat recipients, hidden positions and clan-only pings.
- Split item quantities and no accidental click-through; clan persistence after save/reload. No new item storage or migration is implemented by this patch.
- The supported mod combination, including ServerManager and independently embedded ServerSync copies. Fixes to Clan do not update other mods' libraries.

UI resource acquisition uses the game's extended asset manifest and loads only seven named UI assets. It never initializes the game's loader early; required assets are held for the plugin lifetime, released on shutdown, and atlas sprite copies/Clan overlays are cleared on scene replacement. Borrowed game resources are never destroyed by Clan.

## Structural review verification — 2026-10-01

- Baseline: clean `main` at `1a7a4ba`. The baseline Debug build and eight smoke scripts (config, chat, media, panel, dock, UI resources, group sharing, ServerSync adaptation) passed. Each code change was then built with `-c Debug -p:DeployToGame=true`, checked with the relevant scripts and reviewed before its own commit.
- `375f532` moves `CommitClanProfile` unchanged into the existing persistence partial; the chat smoke retains its save-before-swap-and-notify order check. `16093c1` removes the redundant chat rebuild state parameter while retaining independent panel lifetime and exception cleanup. `c00d201` inlines animation rewriting into its sole budget caller; all 39 media checks passed both before and after the code change, including 11 new animation cases.
- Installed original `assembly_valheim.dll` hashes matched the prepared 1.0.16 client (Steam 25527674) and dedicated-server (25527701) snapshots. The final DLL passed 841 public game member references, 36 declared Harmony targets and the selected dynamic contracts against each role. No publicized references were introduced.
- A Cecil comparison of baseline and final DLLs found 130 public Clan contract entries unchanged, identical top-level method IL in the plugin, API, RPC, group-sharing and optional adapter types, unchanged save format 6/RPC v13, and embedded ServerSync/YamlDotNet without hard optional-mod references. This is static evidence, not Harmony installation or runtime access testing.
- The optional contract check **failed before any edits** against the installed Epic MMO **1.9.71**. Its new effective-level calculation and moved boss local do not match the existing `TryFindInjection` patterns. Clan disables that XP adapter on this mismatch. Expanding compatibility requires a separate patch preserving the effective-level calculation; this review did not relax the guard or mark the combined optional-mod script as passed.
- Final Debug DLL and the Steam `BepInEx/plugins/Clan.dll` copy matched SHA-256 `58BBA7C3C839C4746EDB956CB1519AAB17247B1BD98A0B0C98F402063A2D9320`. No Release build, version change or push was performed.
- No actual client, local-host, remote/dedicated multiplayer, UI rendering or item/reward execution was performed. Generated files, third-party library implementation and every media asset were not exhaustively reviewed; library merge/access contracts and the selected optional DLL paths were inspected. No measured performance improvement is claimed.
