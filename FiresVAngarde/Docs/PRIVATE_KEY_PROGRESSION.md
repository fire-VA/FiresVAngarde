# Private Key Progression System

## Overview

Replaces World Advancement Progression (WAP) with an integrated, simpler system
that uses per-player private keys for boss progression and raid events.

**Philosophy**: Everything is UNLOCKED by default. Players should never be
surprised that they can't equip, craft, or eat something. Only cheat items
(SwordCheat, HammerCheat, StaffCheat) are blocked.

## Files

| File | Purpose |
|---|---|
| `Modules/Progression/PrivateKeyManager.cs` | Core key storage, RPC sync, EWP bridge, persistence |
| `Modules/Progression/ProgressionConfig.cs` | Runtime config (defaults: private keys ON, locks OFF) |
| `Modules/Progression/ProgressionPatches.cs` | Harmony patches for key interception, raids, save/load |
| `Modules/Progression/ProgressionConsoleCommands.cs` | Console commands: va_setprivatekey, etc. |
| `Docs/WAP_DECOMPILED_REFERENCE.md` | Full decompiled WAP code analysis (do not compile) |

## Default Behaviour

| Feature | Default | WAP Default | Notes |
|---|---|---|---|
| Private boss keys | ? ON | ? ON | Per-player boss kill credit, 100m range |
| Block global boss keys | ? ON | ? ON | Boss keys go to private, not global |
| Private raids | ? ON | ? ON | Each player gets raids for their progression |
| Block equipment | ? OFF | ? ON | Only cheat items blocked by default. Set LockEquipmentKey to require a specific key. |
| Block crafting | ? OFF | ? ON | Set LockCraftingKey to require a specific key. |
| Block building | ? OFF | ? ON | Set LockBuildingKey to require a specific key. |
| Block cooking | ? OFF | ? ON | Set LockCookingKey to require a specific key. |
| Block eating | ? OFF | ? ON | Set LockEatingKey to require a specific key. |
| Block taming | ? OFF | ? OFF | Set LockTamingKey to require a specific key. |
| Block guardian power | ? OFF | ? ON | Set LockGuardianPowerKey to require a specific key. |
| Block boss summons | ? OFF | ? ON | Set LockBossSummonsKey to require a specific key. |
| Admin bypass | ? ON | ? OFF | Admins skip all locks |
| Blocked effect (fire) | ? OFF | ? ON | No punishment for trying |

## How It Works

### Boss Kill Flow
1. Player kills Eikthyr ? Valheim calls `ZoneSystem.SetGlobalKey("defeated_eikthyr")`
2. Our prefix blocks the global key (returns false)
3. Our postfix sends `VA_SetPrivateKey` RPC to all players within 100m
4. Each nearby player adds `defeated_eikthyr` to their `_privateKeys` set
5. Keys are saved to `Player.m_customData["VA_PrivateKeys"]`
6. Keys are mirrored to `expand_world/ewp_data.yaml` as `{steamID}/defeated_eikthyr: 1`

### Key Check Flow
1. Game calls `ZoneSystem.GetGlobalKey("defeated_eikthyr")`
2. Our prefix checks `PrivateKeyManager.HasPrivateKey("defeated_eikthyr")`
3. If player has it ? return true (skip vanilla check)
4. If not ? fall through to vanilla global key check

### Raid Flow
1. `RandEventSystem.RefreshPlayerEventData` is replaced
2. For each player (host + peers), we build event data using their private keys
3. Events are filtered: only show raids whose `m_requiredGlobalKeys` match the player's private set
4. Result: Player A gets Swamp raids, Player B still gets Meadows raids

## Console Commands

| Command | Description | Cheat |
|---|---|---|
| `va_setprivatekey <key>` | Add a private key | Yes |
| `va_removeprivatekey <key>` | Remove a private key | Yes |
| `va_listprivatekeys` | List all private keys | No |
| `va_resetprivatekeys` | Clear all private keys | Yes |
| `va_bosscount` | Show boss count (private vs global) | No |

## Player Save Format

```
Player.m_customData["VA_PrivateKeys"] = "defeated_eikthyr,defeated_gdking,defeated_bonemass"
```

Comma-separated, lowercase. Loaded on `Player.EquipInventoryItems`, saved on `Player.Save`.

## EWP Integration

Every private key write also calls `EwpDataBridge.WriteKey(steamID, keyName, 1)`, so
Expand World Pro sees entries like:
```yaml
76561198012345678/defeated_eikthyr: 1
76561198012345678/defeated_gdking: 1
```

## RPC Names

| RPC | Direction | Purpose |
|---|---|---|
| `VA_ServerSetPrivateKeys` | Client ? Server | Bulk sync all keys on login |
| `VA_ServerAddPrivateKey` | Client ? Server | Single key add |
| `VA_ServerRemovePrivateKey` | Client ? Server | Single key remove |
| `VA_SetPrivateKey` | Server ? Client | Grant a key to specific player |
| `VA_RemovePrivateKey` | Server ? Client | Revoke a key from player |
| `VA_ResetPrivateKeys` | Server ? Client | Clear all keys |

## Blocked Items (Default)

Only these three prefabs are blocked from equipping:
- `SwordCheat` (cheatsword)
- `HammerCheat` (cheathammer)
- `StaffCheat` (trollstaff)

Server admins can enable key-based locking via config if they want the WAP-style experience.
Each lock has a checkbox (enable/disable) and a key name input field. The key is checked
against ALL key sources: global keys, private keys, custom player keys (ZDO), and EWP keys.

### Lock Config Example
```cfg
[Progression.Locking]
LockEquipment = true
LockEquipmentKey = defeated_eikthyr
# Players must have defeated_eikthyr (as a global, private, custom, or EWP key) to equip items.

LockCrafting = true
LockCraftingKey = BLACKSMITH
# Players must have the custom key "BLACKSMITH" to craft. This can be given via:
#   - Dialogue: AddCustomPlayerKey,BLACKSMITH
#   - Console: va_addcustomkey BLACKSMITH
#   - EWP: steamId/BLACKSMITH in ewp_data.yaml
```

## Future Work

- [ ] YAML-driven material?key mappings (instead of hardcoded dictionaries)
- [ ] Portal teleport restrictions (per-material unlock keys)
- [ ] Skill cap/floor tied to boss count
- [ ] Trader item gating
- [ ] Taming/summoning locks (opt-in)
- [ ] Integration with dialogue system CustomPlayerKeys for quest-driven progression
