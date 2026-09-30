# Party (Friend Group) System — v1

## Goal

Stepping-stone toward a full guild system. v1 gives players a lightweight
friend-group with a shared HUD, persisted on the character file so it
survives logout, with an in-game `/invite` flow gated by the vanilla
`YesNoPopup`.

## User-facing surface

| Command                  | Who                | Effect                                                               |
| ------------------------ | ------------------ | -------------------------------------------------------------------- |
| `/invite <playername>`   | anyone             | Sends a YesNo popup invite to the named online player.               |
| `/disband`               | leader only        | Tears the party down for every member.                              |
| `/leave`                 | any member         | Removes self; promotes next-oldest member if leaver was leader.      |

Group HUD now also renders one row per other party member:

- ? prefix on the leader's name.
- `(offline)` suffix and dimmed name colour when the member is not in
  `ZNet.GetPlayerList()`.
- Live HP / Stamina / Eitr bars + distance whenever the remote player's
  `Player` instance is loaded locally (i.e. they're in our active zone).
  Out-of-zone but online ? name only, bars hidden.

## Storage

Per-player blob on `Player.m_customData["fires.va.party.v1"]`, JSON, mirrors
the companion-roster pattern (`PlayerCompanionStorage`):

```
PartyData {
  int  SchemaVersion           // 1
  long PartyId                 // generated on first accept; stable for life of party
  long ServerWorldUid          // 0 = unknown / permissive
  long LeaderPlayerId
  long LastUpdatedUtcTicks
  PartyMember[] Members        // { PlayerId, PlayerName, JoinedUtcTicks }
}
```

Server-binding rule mirrors the companion roster: 0 on either side is
permissive; non-zero mismatches mean the party belongs to a different
world and is treated as absent.

## Networking

Two routed RPCs, server-routed because clients can't enumerate each
other's `ZNetPeer.m_uid` directly.

| RPC                              | Direction          | Purpose                                                                                           |
| -------------------------------- | ------------------ | ------------------------------------------------------------------------------------------------- |
| `FiresRPGmaker_PartyRoute`       | client ? server   | Carries `(targetPlayerName, innerZPackage)`. Server resolves name ? peer and forwards inner.      |
| `FiresRPGmaker_PartyMsg`         | server ? target   | Carries `(MsgType, payload)`. Recipient acts on it directly.                                      |

`MsgType` enum: `Invite`, `InviteAccept`, `InviteDecline`, `Sync`,
`Disband`, `Leave`.

## Lifecycle

### Invite

1. Inviter types `/invite Bob` ? resolves Bob's player-id via `ZNet.GetPlayerList()`.
2. If inviter has no party, a fresh one is created locally with `LeaderPlayerId = self`.
3. `Invite` payload (carries inviter id/name, partyId, leaderId, current member list with tenure ticks) is routed to Bob's peer via the server.
4. Bob's client pushes a `YesNoPopup`. Self-targeted re-invites while a popup is up are silently dropped.
5. **Yes** ? Bob writes the (remoteMembers + self) party locally and sends `InviteAccept` back to the inviter.
6. Inviter receives accept ? adds Bob to local party ? broadcasts `Sync` to every other current member (excluding Bob and self, who already have it).

### Disband

1. Leader types `/disband` ? broadcast `Disband(partyId)` to every member except self ? each clears their local party.
2. Non-leader gets a "use `/leave` instead" message and the command is a no-op.

### Leave

1. Any member types `/leave` ? broadcast `Leave(selfId)` to every member except self ? recipients remove the leaver.
2. If leaver was leader, recipients promote the longest-tenured remaining member (lowest `JoinedUtcTicks`).
3. If a recipient ends up alone in the party (size ? 1), they clear local state instead of saving a one-member "party of self".

### Sync semantics

Receiving a `Sync` whose member list does **not** include self is treated
as an external kick ? local party is cleared with a "you were removed"
notify. This handles the future "leader kicks member" UX without needing
a dedicated Kick message.

## Login / logout behaviour

- Logout: party blob stays on the .fch via `m_customData`. Other members'
  HUDs flip the leaver's row to `(offline)` because `IsPlayerOnline` falls
  back to checking `ZNet.GetPlayerList()`.
- Login: each player loads their own copy from `m_customData`. Online
  members appear immediately because `ZNet.GetPlayerList()` is already
  populated by the time `Player.m_localPlayer` is set. No active sync
  ping is needed.

### Drift tolerance

If the leader is offline when a member leaves, the offline copy of the
party held by the leader is *out of date* until they reconnect and
receive the next event. Since v1 has no central authority, this is
accepted: the offline leader's copy will simply have the leaver still
in it. Any action they take post-login that targets the leaver routes
through the server, fails to resolve, and is dropped — no corruption.

## Security guards

- Sender's reported `playerId` is sanity-checked against connected
  peers on the server side (the router only forwards if the named target
  resolves; it does not vouch for the sender's claimed identity, which
  is acceptable for a friend-group system but should be tightened when
  guilds add real ranks / permissions).
- Self-invite is blocked at the entry point.
- "Already in a different party" auto-declines on the receiver side so
  the inviter sees a clear "they're busy" message rather than a popup
  the recipient could never see.

## Files

| Path                                                  | Role                                                                            |
| ----------------------------------------------------- | ------------------------------------------------------------------------------- |
| `FiresNPCs/Modules/Party/PartyData.cs`                | POCO model + `PartyStorage` read/write helpers (mirrors `PlayerCompanionStorage`). |
| `FiresNPCs/Modules/Party/PartyManager.cs`             | RPC registration, invite/disband/leave flows, console-command Harmony postfix.  |
| `FiresNPCs/UI/GroupHudController.cs`                  | Renders party-member rows alongside companions; reuses `MemberUI` template.     |
| `FiresNPCs/Ascend.cs`                                 | Initializes `PartyManager` once `ZRoutedRpc.instance` is available.             |

## Out of scope (v1)

- Group chat channel (already stubbed in `ChatPatches.cs` — `Channel.Group` says "coming soon"). Wiring is deferred until party UX has been tested.
- Kick action (current Sync-without-self semantics already implements it; UI button can be added later).
- Leader transfer command (`/promote <name>` etc.).
- Persistent guild ranks, banners, MOTDs.
- Cross-world party membership.
- Anti-spam rate limit on `/invite`.