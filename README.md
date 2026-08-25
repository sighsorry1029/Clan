# Clan

Clan is a server-authoritative clan system for Valheim. It requires Jotunn and attaches its only clan interface to the vanilla chat UI.

## Version compatibility

Clan 1.0.0 supports only its current registry and RPC formats. Run the same Clan build on the server and every client; test data from a pre-release schema must be recreated rather than migrated.

Clan is incompatible with the Groups and Guilds plugins. Remove either plugin before enabling Clan on the server and clients.

Only the vanilla chat UI is supported. Chatter may remain installed only with
`_Global.isModEnabled=false`. Marketplace may remain installed, but the server's
`BepInEx/config/Marketplace/MarketPlace.cfg` must set `EnableKGChat=false` before
clients enter the world. If KGChat has already replaced the chat UI, restart the
client after changing the setting. When an active replacement chat UI is
detected, Clan leaves its chat dock and inline emoji renderer disabled and
prints one warning instead of attempting partial compatibility.

## Interface

Open Valheim chat to use the attached Clan controls:

- Choose Say, Shout, Whisper, or Clan before sending a message. Slash commands continue through the vanilla chat path. The client-local `Chat After Send` setting defaults to closing chat and can instead refocus it for consecutive messages.
- With mouse/keyboard input, opening chat releases the cursor so the attached Clan controls can be clicked; closing chat returns cursor handling to Valheim.
- Drag the handle outside the chat window's upper-left corner to uniformly resize the vanilla chat window. Text, inline PNG/GIF emojis, and the attached Clan controls scale together, and the client-local scale is remembered.
- The left rail is ordered `Clans`, `Clan`, `Shout`, `Whisper`, `Say`, `Emoji`. `Clans` opens the full clan browser; the other channel buttons route the existing chat input.
- After the server emoji catalog is synchronized and cached, `Emoji` opens a seven-column, two-row-high scrolling tray. Static PNG and animated GIF tokens can be inserted into Say, Shout, Whisper, or Clan.
- The clan browser shows scrollable clan cards, profile details, the current roster and applications, and all players seen on the world during the last 28 days. The local player is pinned first and each row shows its clan, `pending`, `invited`, or `-` state.
- Leaders and officers receive compact Approve, Deny, and Invite icon actions with delayed hover tooltips. Apply and Invite always add a player as Guest; roles are changed from the roster afterward. Leaders can assign Officer, Member, or Guest; officers can assign Member or Guest to lower-role players. Leadership transfer remains leader-only.
- `Make a clan` opens the profile editor. Leaders can later click the clan title or edit icon to change the name, description, and a static PNG image from the server-approved emblem catalogue.

## Behavior

- A character can keep one primary clan role (`Leader`, `Officer`, or `Member`) and one persistent `Guest` connection to a different clan. A duplicate connection to the same clan and a second Guest connection are rejected by the server.
- While a Guest connection exists, that Guest clan is the character's effective clan for the clan panel, chat, pings, map sharing, friendly-fire checks, and HUD. The primary membership remains stored but inactive until the character leaves or is removed from the Guest clan.
- Invitations, applications, kicks, role changes, and leadership transfers target stable player IDs rather than display names. Applications and invitations do not carry a requested role; every accepted player starts as Guest.
- Leaders can atomically update a clan name, description, and static PNG image while the immutable clan ID keeps memberships, invitations, and applications stable.
- One outgoing application is tracked per player and can be cancelled. Clan recruitment actions are available to leaders and officers and are revalidated by the server.
- The server persists a bounded recent-player directory per world and exposes only the state and actions appropriate for the requesting player.
- Clan requests, chat, map pings, and position updates are validated and handled by the server.
- The server persists its registry as readable YAML under `BepInEx/config/Clan/clans.yml`.
- Members and Guests in the effective clan can share minimap positions and clan-only pings.
- The synchronized maximum-clan-player setting limits new joins without invalidating an existing clan that is already above a newly lowered limit.
- The clan HUD contains only effective-clan players who are currently online. The server selects the nearest ten first, then orders those rows by `Leader`, `Officer`, `Member`, and `Guest`. Each row shows current/maximum health with a logarithmically scaled maximum-health bar; positions are used for selection but are not sent in the HUD response.
- Same-clan friendly fire is controlled by a synchronized server setting.

## Public API

`ClanApi.ApiVersion` is `4` in Clan 1.0.0. Server-side integrations can query
independent primary and Guest memberships:

```csharp
ClanMembershipResolution resolution = ClanApi.ResolveMemberships(
    platformId,
    characterPlayerId,
    out string primaryClanId,
    out string primaryClanName,
    out string guestClanId,
    out string guestClanName);
```

`Resolved` means the authoritative registry answered the query. Either membership can be
empty, including both for a player with no Clan membership. Primary membership accepts only
`Leader`, `Officer`, or `Member`; Guest membership accepts only `Guest`. An inconsistent index
or role returns `Unavailable` with every output empty.

The existing ward-specific API remains available:

```csharp
ClanWardAuthorizationResolution resolution = ClanApi.ResolveWardAuthorization(
    platformId,
    characterPlayerId,
    out string clanId,
    out string clanName);
```

Both queries are server-authoritative and use the canonical platform ID plus character player ID. A bare numeric Steam64 account ID is normalized to the same identity as `Steam_<id>`. The ward query authorizes only a primary `Leader`, `Officer`, or `Member`; Guest-only connections return `ResolvedNoAuthorization`. On the authoritative server the queries load the registry on demand, so they are safe during an early integration scan. `Unavailable` means the call was not made in an authoritative server context, the identity or registry could not be resolved, or registry indexes were inconsistent. Consumers must fail closed rather than use client state.

`ClanApi.RegistryRevision` increases exactly once after each successfully persisted membership or profile change. `ClanApi.RegistryChanged` and the retained `ClanApi.WardAuthorizationChanged` are payload-free invalidation events raised for that same revision after the commit; subscriber exceptions are isolated. Consumers should discard cached membership or authorization and query again instead of treating the revision as persisted Clan data. API results contain copied strings and do not expose mutable registry state.

## Server-synchronized PNG and GIF emojis

The current emoji format is intentionally not compatible with the old 5x5
`spritesheet.png`. Each image file represents one emoji. Place source files only
on the server under:

`BepInEx/config/Clan/emoji`

Clan emblem PNG files use a separate server folder:

`BepInEx/config/Clan/emblems`

Examples:

```text
wave.png       -> :clan_wave:
pink_math.gif  -> :clan_pink_math:
```

The server supports up to 100 emoji files total in any PNG/GIF mix, including
up to 50 GIF files, plus up to 50 PNG emblem files. Basenames must be unique
lowercase ASCII names using only letters, digits, `_` or `-`. PNG emoji and
emblem files must be no larger than 512 KiB; GIF emoji files must be no larger
than 2 MiB. Neither image dimension may exceed 512 pixels. PNG files must use
8-bit color, standard compression/filtering, and no interlace. Animated GIF
files are bounded to 180 source frames, 8,388,608 cumulative decoded pixels,
ten seconds, and an infinite loop. The resolved catalog is additionally
bounded to 3,000 GIF frames in total. A new file outside that shared budget is
skipped; an over-budget replacement keeps its previous valid version.

ServerSync distributes a canonical file type, basename, length, and SHA-256
manifest. Clients compare it with their content-addressed cache under
`BepInEx/cache/Clan` and request only missing PNG/GIF bytes through a bounded
file RPC. Reconnecting to an unchanged catalog transfers no image files, and
replacing one server file downloads only its new SHA-256 content. A completely
full source set can be about 150 MiB. The cache may grow to 384 MiB and removes
old, inactive hashes toward 320 MiB without evicting either active or incoming
catalog files. Cold-cache transfer is deliberately
throttled to roughly three minutes per client under an uncongested connection;
world entry is not blocked, game traffic takes priority, and several simultaneous
cold-cache clients can take longer. A one-file update normally completes in a
few seconds.

Clients pack the individual PNG files into one in-memory TextMeshPro atlas.
All rendered emojis are normalized so their longest side is 96 pixels. Every
accepted GIF source frame is decoded and composited locally, then packed into a
per-animation frame atlas. TextMeshPro uses one average integer FPS, so GIFs
with different per-frame delays preserve every frame but approximate their
original timing. Runtime sheets are prepared across multiple game frames and
become visible only after the complete new catalog is ready.

**Full-frame GIF playback is use at your own risk.** At the 3,000 total GIF
frame maximum, resized RGBA frame arrays can use about 105 MiB of system memory
and uncompressed TextMeshPro atlases can use about 122 MiB of graphics memory,
before Unity object overhead. One source can also briefly use about 32 MiB while
being decoded at the cumulative-pixel limit. A catalog replacement can
temporarily retain both the old and new runtime while the atomic update is
prepared.

GIF decoding runs away from Unity's main thread, and a hot-reload reuses
unchanged rendered sources by content hash instead of decoding the full catalog
again. Unity texture and TextMeshPro asset creation remains on the main thread.
Actual playback cadence is quantized by TextMeshPro and the client frame rate.
Cached source files remain separate and are never merged. The picker
shows the first GIF frame. The vanilla chat output converts matching tokens only
while they are being displayed. Each submitted message can contain up to five
recognized PNG/GIF emoji tokens in any mix. To bound UI work, only the newest
15 GIF occurrences in the current rendered vanilla chat buffer animate; older
GIFs remain visible on their first frame. Server file changes are debounced and
validated independently.
Valid files are published even when another file is invalid; an invalid new file
is skipped, an invalid replacement keeps its previous valid server version, and
a deleted file is removed. The server reserves existing valid files before
admitting replacements, so one enlarged GIF cannot evict an unrelated file.
Clients build the accepted runtime off-screen and still activate it atomically;
a defensive client-side budget check skips only GIFs beyond 3,000 frames. If a
client download or other runtime build step fails, the previous runtime remains
active.
