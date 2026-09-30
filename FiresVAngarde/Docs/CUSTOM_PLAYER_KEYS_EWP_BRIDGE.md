# Custom Player Keys + EWP Data Bridge

## Overview

Custom Player Keys are per-player keys stored in the EWP data file and optionally
on the player's ZDO. They can be set via dialogue commands, console commands (F5),
and checked via dialogue conditions.

Every write to a Custom Player Key mirrors to
`BepInEx/config/expand_world/ewp_data.yaml` so Expand World Pro (and any
other mod reading that file) can see the data.

## EWP Data Format

Keys are written as `{steamID}/{keyName}` (presence-only) or
`{steamID}/{keyName}: {value}` (with integer value).

```yaml
76561198012345678/Tank
76561198012345678/Healer: 3
76561198099999999/Tank
```

- The `expand_world` folder and `ewp_data.yaml` file are created automatically
  if they don't exist.
- The steamID prefix is what makes each key per-player.
- Presence-only keys (no colon/value) are treated as value = 1 when read.
- Existing keys are updated in-place; new keys are appended.

## Console Commands (F5)

Steam ID is auto-resolved from the local player. Optional `[steamId]` at the
end lets an admin target another player.

| Command | Example | Description |
|---|---|---|
| `va_addcustomkey <key>` | `va_addcustomkey TANK` | Adds key to yourself |
| `va_addcustomkey <key> <steamId>` | `va_addcustomkey TANK 76561198...` | Admin: adds key to another player |
| `va_removecustomkey <key>` | `va_removecustomkey TANK` | Removes key from yourself |
| `va_removecustomkey <key> <steamId>` | `va_removecustomkey TANK 76561198...` | Admin: removes from another player |
| `va_listcustomkeys` | `va_listcustomkeys` | Lists your own keys |
| `va_listcustomkeys <steamId>` | `va_listcustomkeys 76561198...` | Lists another player's keys |

## Dialogue Commands

| Command | Format | Description |
|---|---|---|
| **AddCustomPlayerKey** | `AddCustomPlayerKey,TANK` | Presence key — writes `steamId/TANK` to EWP, sets ZDO to 1 |
| **AddCustomPlayerKey** | `AddCustomPlayerKey,TANK/++` | Increments TANK by 1 |
| **AddCustomPlayerKey** | `AddCustomPlayerKey,TANK/++,5` | Increments TANK by 5 |
| **AddCustomPlayerKey** | `AddCustomPlayerKey,TANK/--` | Decrements TANK by 1 |
| **AddCustomPlayerKey** | `AddCustomPlayerKey,TANK/--,3` | Decrements TANK by 3 |
| **RemoveCustomPlayerKey** | `RemoveCustomPlayerKey,TANK` | Removes TANK from ZDO + EWP |

## Dialogue Conditions

| Condition | Format | Description |
|---|---|---|
| **CustomPlayerKeyMore** | `CustomPlayerKeyMore,Key,Threshold` | Pass if player's value for `Key` >= `Threshold` |
| **CustomPlayerKeyLess** | `CustomPlayerKeyLess,Key,Threshold` | Pass if player's value for `Key` < `Threshold` |

Legacy names `CustomValueMore` and `CustomValueLess` still work as aliases.

## Example Dialogue Config

```yaml
# Give the player the "Tank" role key when they pick this option
Option: I'll be the Tank! | Command: AddCustomPlayerKey,Tank,1

# Only show this option if they already have Tank >= 1
Option: Tank training | Condition: CustomPlayerKeyMore,Tank,1
```

This writes `76561198012345678/Tank: 1` to `ewp_data.yaml` while also
storing the value on the player's ZDO for in-game condition checks.

## Files Changed

- `DialogueCommandEngine.cs` — Renamed commands, added EWP bridge writes
- `DialogueConditionEngine.cs` — Renamed conditions, kept legacy aliases
- `DialogueCodex.cs` — Updated known prefixes + reference comments
- `DialogueEditorScreenController.cs` — Updated dropdown names + hints
- `EwpDataBridge.cs` — **New** — reads/writes `expand_world/ewp_data.yaml`
