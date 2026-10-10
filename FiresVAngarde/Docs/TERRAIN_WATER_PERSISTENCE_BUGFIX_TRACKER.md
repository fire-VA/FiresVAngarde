# Custom Terrain & Water — Persistence & Multiplayer Sync Bug Tracker

## Bug Summary
Custom terrain (DirtFloor), custom water, and clutter do not visually restore in
two key scenarios:
1. **Logout ? Main Menu ? Re-login to same server:** Water and terrain are invisible
   until the player walks into the zone and triggers anchor ZDO loading.
2. **Second client joins while first client owns the zone:** The non-owner client
   never sees custom terrain or water, even after a full game restart.

## Current Architecture Overview

### How Vanilla Valheim Handles Terrain Sync
Vanilla `TerrainComp` uses a simple model:
- **ZDOs are the source of truth.** Each terrain zone has a `TerrainComp` ZDO that
  stores height modifications and paint data.
- **Zone loading triggers restoration.** When a zone loads on any client (because a
  player is nearby), the ZDO data spawns the `TerrainComp` prefab, which rebuilds
  the heightmap from ZDO data.
- **Modifications use `ClaimOwnership`.** Any client can modify terrain — the client
  claims ZDO ownership of the `TerrainComp`, applies the change, and the ZDO sync
  propagates to all peers automatically.
- **No client-to-client RPC needed for terrain state.** ZDO replication handles it.
  Operations are applied locally by the editor, and ZDO changes replicate to peers.

### How Our Custom Systems Work (Current)

#### Custom Terrain (`DirtFloorMeshCombiner` + `TerrainCombinerAnchor`)
- **Dual persistence:** File cache (.bin) + ZDO anchor (piece_dirtfloor_vad prefab)
- **Restoration path:**
  1. Server start ? `TerrainPersistence.LoadTerrainData()` restores from file cache
  2. Zone load ? `TerrainCombinerAnchor.Start()` restores from cache or ZDO
- **Real-time sync:** `TerrainOperationRPC` broadcasts operations to ALL clients
  (client-to-client model, NOT server-authority like water)
- **Logout cleanup:** `DirtFloorPatches.ZNet_Shutdown_Prefix()` saves cache, clears
  all state, resets `_initialized` flags

#### Custom Water (`WaterMeshCombiner` + `WaterMeshCombinerAnchor`)
- **Dual persistence:** File cache (.bin) + ZDO anchor (CustomRiver prefab)
- **Restoration path:**
  1. Server start ? `WaterPersistence.LoadWaterData()` restores from file cache
  2. Zone load ? `WaterMeshCombinerAnchor` spawns and restores combiner
- **Real-time sync:** `WaterOperationRPC` uses server-authority model
  (Client ? Server ? Broadcast to all)
- **Logout cleanup:** `CustomWaterPatches.Game_Logout_Postfix()` saves cache,
  clears combiners, resets `_initialized` flags

#### Clutter (`ClutterPersistence`)
- Stored in ZDO anchor alongside terrain data
- Restored when terrain anchor loads
- Same lifecycle as terrain

## Identified Problems

### Problem 1: Logout ? Re-login — Water/Terrain Invisible
**Root Cause:** On logout, `Game_Logout_Postfix` and `ZNet_Shutdown_Prefix` correctly
clear all combiners and caches. On re-login, the file cache is available but:
- File cache is only loaded by `LoadWaterData()` / `LoadTerrainData()` which check
  `ZNet.instance.IsServer()` — **clients skip this entirely**
- Clients depend on **zone loading** to trigger `WaterMeshCombinerAnchor.Start()` /
  `TerrainCombinerAnchor.Start()`, but if the zone was already loaded by another
  peer, the anchor ZDOs may already exist and not re-spawn
- The `_initialized` flags are reset on logout but the re-initialization path
  doesn't proactively request missing data from the server

**Why it works on first login but not re-login:**
On first login, zones load fresh and anchors spawn normally. On re-login from main
menu, the `_initialized = false` reset happens correctly, but the combiners that were
`Destroy()`'d on logout are gone and nothing re-triggers their creation until the
zone reloads (which may not happen if the player is still in the same spot).

### Problem 2: Non-Owner Client Never Sees Water/Terrain
**Root Cause:** When Client A owns a zone and Client B enters:
- The anchor ZDOs exist in Client A's zone
- Client B receives the ZDOs via Valheim's ZDO replication
- BUT the anchor prefab (`piece_dirtfloor_vad` / `CustomRiver`) only spawns when
  the zone first loads — if Client A already loaded the zone, the prefabs are
  already instantiated on Client A but Client B gets the ZDO data without the
  MonoBehaviour `Start()` being called on their side
- Client B's `TerrainCombinerAnchor.Start()` / `WaterMeshCombinerAnchor.Start()`
  never fires because the prefab was already instantiated by zone loading before
  Client B connected

**This is the ZDO vs prefab spawn disconnect:** ZDOs replicate to all clients, but
the prefab instantiation only happens when the zone loads on the peer that owns it.
Other peers receive the ZDO but don't necessarily instantiate the prefab locally
unless they also process the zone load.

### Problem 3: Clutter Changes Not Propagated Between Clients
**Root Cause:** Clutter is stored alongside terrain in the ZDO anchor. When one
client paints clutter, the `TerrainOperationRPC` broadcasts the operation. But:
- The RPC uses client-to-client broadcast (no server authority)
- If Client B doesn't own the zone, their local apply works but the ZDO save
  doesn't persist (they can't write to the anchor ZDO they don't own)
- Client B's changes are visually applied locally but lost on relog

## How Vanilla Valheim Solves This

Vanilla terrain uses **ZDO ownership transfer**:
1. Client wants to modify terrain ? calls `nview.ClaimOwnership()` on the
   `TerrainComp` ZDO
2. Valheim's ZDO system transfers ownership to that client
3. Client modifies the ZDO data directly
4. ZDO replication pushes the changes to all peers automatically
5. On peers, the `TerrainComp` detects ZDO data changed and rebuilds heightmap

**Key insight:** Vanilla does NOT use RPCs for terrain sync. It uses ZDO replication.
The ZDO IS the sync mechanism. Any client can claim ownership, modify, and the
changes propagate automatically.

**For zone loading:** When any client enters a zone, `ZoneSystem` instantiates ALL
ZDO prefabs in that zone on that client. So if Client B enters a zone that Client A
already loaded, Client B still instantiates the prefabs locally from ZDO data.

## Fix Plan

### Phase 1: Fix Re-login Restoration
**Goal:** Water and terrain visually restore after logout ? main menu ? re-login.

1. **Client-side cache restoration:** On re-login (player spawn), clients should
   proactively restore combiners from their local file cache — don't wait for zone
   load. Add a `Game.Start` or player spawn hook that calls a client-side restore.
2. **Re-request sync from server:** After restoring from local cache, request a
   delta sync from the server to pick up any changes made while disconnected.
3. **Ensure `_initialized` reset chain is complete:** Verify that ALL static state
   (caches, registries, zone tracking) is properly reset on logout so re-init
   works cleanly.

### Phase 2: Fix Non-Owner Client Visibility
**Goal:** Client B sees water/terrain when entering zones owned by Client A.

1. **Ensure prefab instantiation on all peers:** When Client B enters a zone,
   Valheim's `ZoneSystem` should instantiate anchor prefabs from ZDO data. Verify
   that our anchor prefabs are registered correctly so this happens.
2. **Add zone-enter hook for clients:** If prefab instantiation doesn't trigger
   naturally, add a `ZoneSystem` patch that detects when a client enters a zone
   containing our anchor ZDOs and triggers local combiner creation.
3. **Consider ZDO-only restoration:** Like vanilla `TerrainComp`, detect when our
   anchor ZDO data changes on a client and rebuild the combiner mesh. This makes
   the system reactive to ZDO replication rather than depending on prefab spawns.

### Phase 3: Fix Cross-Client Modifications
**Goal:** Client B can modify terrain/water/clutter and changes persist.

1. **Use `ClaimOwnership` pattern:** Before modifying an anchor's data, call
   `nview.ClaimOwnership()` on the anchor ZDO. This ensures the modifying client
   can write to the ZDO.
2. **For terrain:** Already uses client-to-client RPC broadcast. Add
   `ClaimOwnership` before ZDO writes so the modifying client can persist.
3. **For water:** Already uses server-authority RPC. Server should call
   `ClaimOwnership` on the anchor before saving. Or have the server update ZDO
   data on the anchor it owns.
4. **For clutter:** Same as terrain — ensure `ClaimOwnership` before ZDO writes.

### Phase 4: Verify Clutter Sync
**Goal:** Clutter changes from any client propagate and persist.

1. Clutter data is part of terrain anchor ZDO — fix terrain ZDO write ownership
   (Phase 3) and clutter follows automatically.
2. Verify clutter RPC broadcast reaches all peers.
3. Verify clutter restoration from anchor ZDO on zone load.

## Questions to Investigate
- [x] Does the file cache exist on clients (not just server)? **YES** — saved on
  logout via `WaterCombinerCache.OnWorldUnload()` / `DirtFloorCombinerCache.SaveCache()`.
  Clients have local cache files for any world they've previously loaded.
- [ ] Does `ZoneSystem` instantiate anchor prefabs on Client B when they enter a
  zone already loaded by Client A? (Check `ZoneSystem.SpawnZone` flow)
- [ ] Are our anchor prefabs (`piece_dirtfloor_vad`, `CustomRiver`) registered in
  `ZNetScene` so they can be instantiated from ZDO data on all clients?
- [ ] Is `WaterOperationRPC.RPC_REQUEST_SYNC` / `RPC_SYNC_CACHE` ever called on
  client join? The code registers the handlers but unclear if sync is requested.

## Changes Made — Phase 1: Client-Side Cache Restoration

### File: `FiresNPCs/Modules/CustomWater/CustomWaterPatches.cs`

**1. `LoadWaterDataDelayed()` — removed server-only guard**
- Previously: `if (!ZNet.instance.IsServer()) yield break;` — clients skipped entirely
- Now: Both server and clients restore from their local file cache. Server uses
  `WaterPersistence.LoadWaterData()` (authoritative). Clients use new
  `RestoreWaterFromClientCache()` method.

**2. Added `RestoreWaterFromClientCache()` method**
- Reads client's local file cache (`WaterCombinerCache.GetCurrentCache()`)
- For each cached group with valid data, checks if a combiner already exists at
  that position (dedup). If not, calls `WaterCombinerCache.RestoreFromCache()`.
- When ZDO anchors eventually spawn (zone load), `WaterMeshCombinerAnchor.Start()`
  detects existing cache-restored combiners and links to them (no duplicates).

**3. `Player_OnSpawned_Postfix()` — added fallback cache restore**
- After ensuring water cache is initialized, checks if any combiners exist.
- If no combiners and we're a client, calls `RestoreWaterFromClientCache()`.
- Handles the re-login case where `LoadWaterDataDelayed` may not have fired yet.

### File: `FiresNPCs/Modules/CustomTerrain/DirtFloorPatches.cs`

**4. `LoadTerrainDataDelayed()` — removed server-only guard**
- Previously: `if (!ZNet.instance.IsServer()) yield break;` — clients skipped entirely
- Now: Both server and clients restore from their local file cache. Server uses
  `TerrainPersistence.LoadTerrainData()` + `ClutterPersistence` + `BiomeFilePersistence`.
  Clients use new `RestoreTerrainFromClientCache()` method.

**5. Added `RestoreTerrainFromClientCache()` method**
- Reads client's local file cache (`DirtFloorCombinerCache.GetCurrentCache()`)
- For each cached group with valid mesh data, checks if a combiner already exists
  for that group (dedup). If not, calls `DirtFloorCombinerCache.RestoreFromCacheStatic()`.
- When ZDO anchors eventually spawn (zone load), `TerrainCombinerAnchor.Start()`
  detects existing cache-restored combiners and links to them (no duplicates).

### File: `FiresNPCs/Modules/CustomTerrain/Core/DirtFloorCombinerCache.cs`

**6. Added `RestoreFromCacheStatic()` public method**
- Public wrapper around the private `InstantiateCombinerFromCache()`.
- Enables the client-side restoration code in `DirtFloorPatches` to create
  combiners from cached data.

**7. Added `GetCurrentCache()` public method**
- Returns the current loaded cache data (or null if not loaded).
- Mirrors `WaterCombinerCache.GetCurrentCache()` pattern.

### Remaining Work (Phases 2–4) — COMPLETED

## Changes Made — Phase 2: Terrain ZDO Retry for Non-Owner Clients

### File: `FiresNPCs/Modules/CustomTerrain/Core/TerrainCombinerAnchor.cs`

**8. `RestoreFromZDO()` — added retry logic for non-owner clients**
- Previously: if ZDO version == 0, silently returned with no retry. Brand-new clients
  whose ZDO byte arrays hadn't synced yet would never see terrain.
- Now: Non-owner clients schedule a `RetryRestoreFromZDO` coroutine that retries at
  3s, 6s, and 12s intervals (mirrors water anchor's existing retry mechanism).
- Owner clients still treat version=0 as genuinely empty (no retry needed).

**9. Added `RetryRestoreFromZDO()` coroutine**
- Retries ZDO version check at 3s, 6s, 12s intervals.
- On success (version > 0), starts `RestoreFromZDOAsync()`.
- On all retries exhausted, logs warning (ZDO data never synced).

## Changes Made — Phase 3: ClaimOwnership for Cross-Client ZDO Writes

### File: `FiresNPCs/Modules/CustomTerrain/Core/TerrainCombinerAnchor.cs`

**10. `SaveToZDO()` — replaced `IsOwner()` skip with `ClaimOwnership()`**
- Previously: `if (!_nview.IsOwner()) return;` — non-owner clients silently skipped
  saving, losing their terrain changes on relog.
- Now: `if (!_nview.IsOwner()) _nview.ClaimOwnership();` — claims ZDO ownership first,
  then saves. This mirrors vanilla `TerrainComp` behavior where any client that
  modifies terrain claims ownership, writes data, and ZDO replication propagates.

**11. `SaveClutterData()` — replaced `IsOwner()` skip with `ClaimOwnership()`**
- Same pattern as SaveToZDO. Non-owner clients now claim ownership before writing
  clutter paint data to the anchor ZDO.

**12. `SaveBiomeData()` — replaced `IsOwner()` skip with `ClaimOwnership()`**
- Same pattern. Non-owner clients now claim ownership before writing biome paint
  data to the anchor ZDO.

**13. `FlushZonePaintBundle()` — replaced `IsOwner()` skip with `ClaimOwnership()`**
- Same pattern. Non-owner clients now claim ownership before writing zone paint
  bundle data to the anchor ZDO.

**Note:** Water anchors do NOT need ClaimOwnership — water uses server-authority
model (clients send RPC ? server saves). The server already owns anchor ZDOs.

## All Phases Summary

| Phase | Description | Status |
|-------|-------------|--------|
| 1 | Client-side cache restoration on login | ? Done |
| 2 | Terrain ZDO retry for non-owner clients | ? Done |
| 3 | ClaimOwnership for cross-client terrain saves | ? Done |
| 4 | Clutter sync — inherits from Phase 3 | ? Done |

## Test Scenarios
1. **Solo re-login:** Place water + terrain ? logout to main menu ? login ? verify
   visible immediately
2. **Two-player zone owner:** P1 places water + terrain ? P2 joins ? P2 should see
   it immediately
3. **Two-player non-owner edit:** P1 owns zone ? P2 paints clutter/terrain ? P1
   should see changes ? both relog ? changes persist
4. **Server restart:** Place water + terrain ? restart server ? login ? verify
   restored from file cache + ZDO anchors
5. **Same-zone water:** Place water at spawn point ? logout ? login ? water visible
   immediately (no need to walk away and back)
6. **Static NPCs re-login:** Place static NPC ? logout ? login ? NPC appears with
   correct appearance and dialogue

## Changes Made — Phase 5: Stale _initialized Flags on Re-Login

### Root Cause
When player logs out to main menu and back in, BepInEx plugins do NOT re-Awake.
`ZRoutedRpc.instance` is destroyed during scene transition and recreated on the
new session. All `static bool _initialized` guards in RPC init methods stay `true`
from the previous session, so `Initialize()` calls silently skip — leaving all RPC
registrations dead.

Water already had `Game.Logout` cleanup (resetting `_initialized` flags). Terrain
and static NPCs were missing it entirely.

### Fix: Terrain _initialized Reset

**File: `DirtFloorPatches.cs`** — Added `Game.Logout` postfix:
- Saves and unloads `DirtFloorCombinerCache`
- Resets `_initialized` on: `TerrainOperationRPC`, `TerrainPaintRPC`,
  `TerrainPersistence`, `BiomeFilePersistence`, `ClutterPersistence`
- Clears all persistence caches and undo history

**Files: `TerrainOperationRPC.cs`, `TerrainPaintRPC.cs`, `TerrainPersistence.cs`,
`BiomeFilePersistence.cs`, `ClutterPersistence.cs`** — Added `ResetInitialized()`:
- Simple `_initialized = false` method, mirroring `WaterOperationRPC.ClearCaches()`
  and `WaterPersistence.ResetInitialized()` patterns.

### Fix: Water Same-Zone Visibility

**File: `CustomWaterPatches.cs`** — `ZoneSystem_Start_Postfix_Water()` now calls
`WaterOperationRPC.Initialize()` and `WaterPersistence.Initialize()` as fallback:
- Primary init in `ZNet.Start` postfix returns early because `Game.instance == null`
  at that point (ZNet starts before Game in Valheim's boot order)
- `ZoneSystem.Start` fires after `Game.Start`, so `Game.instance` is valid
- On re-login, `_initialized` was reset by `Game.Logout` handler, so this call
  actually registers the RPCs with the new `ZRoutedRpc.instance`

### Fix: Static NPC RPC Re-Registration

**File: `SavedNpcManager.cs`** — Added `ResetRPCState()`:
- Resets `_rpcsRegistered = false` so `RegisterRPCs()` will run again

**File: `CompanionPatches.cs`** — `Game_Logout_Prefix()` calls `SavedNpcManager.ResetRPCState()`:
- Ensures NPC data sync RPCs are dead-flagged on logout

**File: `CompanionPatches.cs`** — `Player_OnSpawned_Postfix()` calls `SavedNpcManager.RegisterRPCs()`:
- On every player spawn (including re-login), ensures NPC RPCs are registered
  with the current `ZRoutedRpc.instance`
- Safe to call multiple times — guarded by `_rpcsRegistered` flag

### Summary of _initialized Lifecycle (After Fix)

```
First Login:
  Awake() ? FinishInit() ? harmony.PatchAll()
  ZNet.Start ? [Game.instance null, some inits skipped]
  Game.Start ? Game.instance set
  ZoneSystem.Start ? terrain init + water init (fallback RPC init here)
  WaitForZNetReady ? more inits (one-time only)
  Player.OnSpawned ? NPC RPC re-register

Logout:
  Game.Logout Prefix ? companions destroyed, SavedNpcManager.ResetRPCState()
  ZNet.Shutdown Prefix ? terrain cache saved, caches cleared
  Game.Logout Postfix ? terrain _initialized flags reset
  Game.Logout Postfix (water) ? water _initialized flags reset, cache saved

Re-Login:
  ZNet.Start ? [Game.instance null, skipped]
  Game.Start ? Game.instance set
  ZoneSystem.Start ? terrain reinit (cache reloaded, RPCs re-registered)
                   ? water reinit (RPCs re-registered, cache reloaded)
  Player.OnSpawned ? NPC RPCs re-registered, companions restored
```
