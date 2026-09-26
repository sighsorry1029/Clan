# RtDQuestForge: Clan shared kill-credit integration handoff

Prepared 2026-09-26 for the RtDQuestForge author. This document describes a Clan-side compatibility implementation and proposed upstream improvements. It is not a report of completed multiplayer testing, and no changes were made to or redistributed in the original RtDQuestForge DLL.

## Player request and intended behavior

Players asked for group quest credit and XP while using Clan, RtDQuestForge and WackyEpicMMOSystem.

The implemented scope is nearby members of the same effective clan. It does not introduce temporary parties, invitations or a party leader. Guest membership takes precedence over primary clan membership; a player never receives credit through both affiliations.

- A kills a creature; B is an eligible nearby clan member.
- A retains RtDQuestForge's existing personal kill-credit path.
- Clan delivers one additional kill objective increment to B.
- B must have accepted a matching quest and satisfy its normal prerequisites.
- When B completes that quest, RtDQuestForge grants B's normal personal rewards.
- Gathering, quest acceptance, progress storage, items and completion rewards remain owned by RtDQuestForge.

Quest completion rewards are not broadcast a second time. Vanilla skill XP and Epic MMO XP are already supported by RtDQuestForge's reward code.

## Exact binaries inspected

| Mod | Plugin identity | Assembly version | SHA-256 |
| --- | --- | --- | --- |
| RtDQuestForge 0.2.13 | `soloredis.rtdquestforge` | 1.0.0.0 | `8AC1ACD0AAFEED2496AF7D570D498ABD6B8BD60396340AF4233DC5C75B7A41DE` |
| WackyEpicMMOSystem 1.9.68 | `WackyMole.EpicMMOSystem` | 1.9.68.0 | `72D75F468AB358AAFD4C3DAA21653AA9BF47BD6D562525072D081FBC46D872E0` |

The QuestForge binary reports informational commit `b7d2820b668cbbdae8707584539d2e8b6b38d29a`. These findings apply to those binaries; they are not assumptions about newer upstream source.

This integration is included in **Clan 1.1.0**, developed from version 1.0.10, commit `fd7572f459166a71cfa6eb9d333134bce4fa6768`. Runtime prerequisites remain QuestForge's own Jotunn/JSON dependencies; Clan itself does not acquire a Jotunn or direct QuestForge assembly reference.

## What Clan patches and calls

Implementation files in the Clan repository:

- `QuestForgeCompat.cs`: optional detection, owner-side death capture and the local consumer bridge.
- `ClanGroupSharing.cs`: connection-bound relay, server membership selection, receiver validation and replay protection.
- `EpicMmoCompat.cs`: separate combat XP adapter; it does not share quest rewards.
- `Plugin.cs` / `ClanRpc.cs`: soft dependencies, synchronized configuration and lifecycle wiring.

QuestForge is detected through its BepInEx GUID with a soft dependency. Version 0.2.13 and the following signatures are checked before enabling the adapter:

```text
RtDQuestForge.QuestForgePlugin.Manager
    public static field on the internal plugin type
RtDQuestForge.QuestManager.RegisterKill(string prefabName)
    public instance method returning void
```

The field and method lookup are cached. The current Manager field is read when applying credit; if the Manager instance changes, the cached delegate is replaced. The delegate is released at session teardown. Other versions disable only this adapter with a warning until reviewed.

Clan adds a prefix/postfix to `Character.OnDeath()`. It does not suppress the game's original method or modify QuestForge's existing patch.

1. Prefix captures creature ZDOID, killer character ZDOID, prefab and position before the view is reset. It requires the local creature owner, non-player victim, nonpositive local health and `m_lastHit.GetAttacker() is Player`. The protected last-hit field is read using Harmony field injection, not a publicized game reference.
2. Postfix runs after the original and after the QuestForge Harmony owner, and reports only if the original ran and capture succeeded. Normal original exceptions prevent this postfix path.
3. The owner sends immediately using the existing server ZRpc connection. No Jotunn coroutine or frame-delayed send is used.
4. The server verifies the actual connection, live victim ZDO owner, prefab, plausible position and connected killer identity. It resolves membership from Clan's server registry and excludes the killer from additional delivery.
5. Eligible recipients must be in the same effective clan and within the configured quest range. Delivery includes the intended recipient's character ZDOID.
6. Each receiver accepts only its server connection, current character, living state and local range, then invokes the normal `Manager.RegisterKill(prefab)` method once.

The inspected original Valheim 1.0.16 path calls `ZNetScene.Destroy` during OnDeath, which resets the view but queues the ZDO for later `ZDOMan.SendDestroyed`. Synchronous postfix transmission normally precedes the queued destruction message. This is why capture must happen before the original, and why the server can still validate the live ZDO. No game DLL was publicized or replaced.

## Settings and delivery contract

The following Clan settings are server-synchronized and can change live:

| Section/key | Default |
| --- | --- |
| `3 - Compatibility / Share QuestForge Kills` | On |
| `3 - Compatibility / QuestForge Share Range` | 70 metres; accepted range 1–200 |
| `3 - Compatibility / Share Epic MMO Experience` | On |

All participating clients, the host/server and clients that own nearby creatures need the compatible Clan build and relevant optional mod. QuestForge retains its own network compatibility requirements. The original killer path continues to work independently of additional sharing.

Clan uses new `Clan_GroupCreditRequest_v1` and `Clan_GroupCreditResponse_v1` messages; it does not reuse or change QuestForge's RPC payloads. New messages are at most 512 bytes. Per-connection request sequences and a 256-report/5-second budget bound replay and processing; delivery sequences prevent duplicate delivery. Quest reports additionally deduplicate by victim ZDOID on both server and receiver. Death caches retain at most 4,096 IDs for 120 seconds and reject new entries rather than evicting fresh replay protection when full. They reset at world/session teardown, including shutdown without saving.

These checks are not an anti-cheat guarantee. A distributed creature owner still reports the last hit, and the server does not replay combat to prove it. Unknown victims, mismatched ownership and reports arriving after ZDO removal are rejected. Very fast spawn/death, owner transfer and nonstandard destruction by another mod need live tests; conservative rejection can omit additional credit in those cases.

## Existing reward behavior intentionally preserved

The inspected path is:

```text
RegisterKill -> RegisterObjective -> CompleteQuest -> OnQuestCompleted
  -> QuestForgePlugin.HandleQuestCompleted -> RewardGranter.GrantRewards
```

QuestForge already grants skill XP with `Player.RaiseSkill`. Its Epic integration resolves `LevelSystem.Instance` and `AddExp` reflectively. With the supplied Epic signature `AddExp(int exp, bool noxpMulti = false)`, its two conversions of a positive ExpReward result in `AddExp(expReward, true)`.

In this Epic binary, true skips RateExp/singleRate multiplication but XP potion modifiers still apply afterward. Clan deliberately preserves this behavior. Changing to the single-argument public AddExp API would change reward amounts and should be an explicit upstream policy decision.

The separate Clan combat XP adapter retains Epic's raw-monster-XP group multiplier and per-recipient level/range calculation. It does not patch AddExp or RaiseSkill globally. The native Epic combat message lacks a unique victim/death ID: Clan can reject a repeated transport sequence, but cannot recognize independently repeated native kill reports with new sequences. Native third-party RPC weaknesses remain outside this bridge.

## Potential upstream issues found by static inspection

These are code-path findings, not multiplayer reproductions. They are intentionally not silently fixed by Clan's additional-teammate adapter.

### 1. Listen-host killer may miss native remote-owner credit

Reproduction to try:

1. Start a listen host and join with a remote client.
2. Ensure the remote client owns the creature.
3. Let the host land the last hit on that creature.
4. Compare the host's accepted kill objective before/after death.

`Patch_Character_OnDeath_KillTracking` routes the remote killer by player name. On the host, `QuestSync.OnKillServerReceive` forwards only to `ZNet.GetPeers()` and does not apply the host's local Manager. The inspected Jotunn CustomRPC dispatch uses the server handler on a server, not an additional automatic client-handler call. This suggests a missing local-host consumer in that route.

Clan excludes the original killer to avoid duplicate credit, so it does not repair this native host-killer omission.

### 2. Display-name routing and missing death identity

The native kill-credit payload contains killer display name and prefab name. `OnKillClientReceive` matches the local display name. It cannot distinguish two connected characters with the same name, and the payload contains no victim/death ID for deduplication. The inspected server handler also does not perform application-level owner/killer validation before forwarding.

Consider stable connected character identity, targeted delivery, an explicit host-local path and bounded death-ID deduplication. A completed-quest set prevents some repeated completion rewards, but it does not prevent duplicated increments of an incomplete objective.

### 3. Progress identity and crash consistency

Progress is stored in `progress_<sanitized character display name>.json`. Account/world collisions and save migration require an explicit storage policy. Completion events/rewards occur before progress is saved, so in-memory event deduplication does not provide an atomic reward/save transaction after a crash. Clan makes no storage or transactional changes.

## Suggested public integration surface

The smallest useful upstream change would be a documented public local-consumer API around RegisterKill, with explicit semantics:

- Accept a unique session/death identifier plus prefab, and return whether the event was applied.
- Deduplicate the event at the consumer before changing objective counts.
- Continue to enforce the player's accepted quests and prerequisites internally.
- State when the local Manager/progress is ready, and reject calls during character/world transitions.
- Keep completion rewards entirely inside QuestForge.

A separate optional event for a resolved kill could expose victim ID, stable killer identity, prefab and position after ownership validation. This would let Clan use an upstream event instead of maintaining its own OnDeath capture. The event must describe whether it fires on the owner, server or recipient; firing on every peer without that contract would reintroduce duplicates.

Clan can supply membership policy through its existing API v5 (`ResolveMemberships` on the server, with Guest precedence). There is no need for QuestForge to reproduce Clan's registry or implement Groups' entire API. We should coordinate who owns delivery/deduplication before enabling both an upstream shared-credit path and Clan's current adapter.

## Verification performed and remaining

Performed locally:

- Debug and Release builds against original game DLLs, including final ServerSync/YamlDotNet merge.
- Final DLL checks against installed original client and dedicated-server assemblies: public game-member access and existing Harmony contracts pass.
- 38 source-linked checks executing Clan's complete relay with controlled substitutes: membership, Guest precedence, host handling, identity/owner validation, malformed messages, range, live settings, replay/capacity/rate limits and session reset.
- Original optional DLL metadata contracts checked. Actual Epic IL passed Clan's production matcher/transpiler: 274 instructions become 279, original instructions, 23 labels and 3 exception markers preserved; ambiguous anchors rejected.
- Independent read-only review of owner/destruction ordering, Harmony field/state injection and recipient selection.

Not performed: actual Unity rendering, live Harmony detour execution in Valheim, real QuestForge reward/XP execution, host/remote/dedicated multiplayer or crash/reconnect gameplay tests. The standalone checks must not be presented as those tests.

Remaining real-game verification: owner=killer and owner≠killer; host killer and host recipient; A/B individually accepting, completing or not accepting quests; range boundaries/death/respawn; duplicate names; Guest changes; duplicate/delayed reports; fast spawn/death and owner transfer; reconnect/world changes; full inventories and reward items advancing other gather objectives; Epic XP rate/level/Mentor/potion policies. Keep quest reward XP and combat XP as distinct, nonrecursive paths.
