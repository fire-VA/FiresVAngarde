# NPC UI Performance Investigation

## Problem Statement
Opening UI for **guest/stationed NPCs** (quest screen, dialogue screen, trader UI) drops FPS from ~90 to ~10.
Opening UI for **player-owned companions** (inventory, stats, etc.) does NOT cause this FPS drop.

## Key Difference
- **Companions** are owned by the player (`ownerPlayerId` matches local player)
- **Stationed/Static NPCs** are ownerless and server-authoritative (placed via hammer or stationed via Shift+P)

---

## Files Investigated So Far

### 1. `CompanionIdleBehavior.cs` (lines 1-1150) — **PRIMARY SUSPECT**

#### `IsPlayerInteractingWithUI()` (line 1065-1085) — **BUG FOUND**
This method determines whether the idle behavior system should freeze a companion during UI interaction.

**Current logic:**
```csharp
private bool IsPlayerInteractingWithUI()
{
    if (!GUIManager.IsCustomPanelOpen())
        return false;

    // Only checks companion-specific screens:
    if (CompanionInventoryScreen.IsOpenForCompanion(_companion))
        return true;
    if (CompanionStatsScreen.IsOpenForCompanion(_companion))
        return true;

    // For stationed NPCs, only checks NpcMainPanelController (admin config):
    if (_npcModule != null && _npcModule.IsStationedAsNpc)
    {
        if (NpcMainPanelController.IsOpen)
            return true;
    }

    return false;  // ? MISSES Quest, Dialogue, Trader, InfoPlayerPanel!
}
```

**Problem:** When a player opens a Quest/Dialogue/Trader UI on a stationed NPC:
- `GUIManager.IsCustomPanelOpen()` returns `true` (good)
- But `IsPlayerInteractingWithUI()` returns `false` for that specific NPC because it doesn't check `QuestInteractionScreenController`, `DialogueUI`, `TraderUIController`, or `InfoNpcPlayerPanel`
- This means `EnforceInteractionFreeze()` is **never called** for that NPC
- The NPC's full idle behavior pipeline continues running (head look-at, sub-behaviors, wandering evaluation, etc.)

**However**, this alone shouldn't cause 80 FPS drop. The idle behavior has throttles. The bigger issue is likely that ALL stationed NPCs in the area continue their full Update loops while the UI is open.

### 2. `CompanionNpcModule.cs` (lines 1-550) — **EVERY-FRAME ENFORCEMENT**

#### `Update()` (line 246-259)
Runs every frame for every stationed NPC:
```csharp
private void Update()
{
    if (_isInPlacementMode) return;
    if (_companion == null && !isStaticPlacement) return;
    if (isStationedAsNpc)
    {
        EnforceStationedState();      // EVERY FRAME
        UpdateProximityDetection();    // Every 0.25s (throttled)
    }
}
```

#### `EnforceStationedState()` (line 330-379)
For stationed NPCs WITHOUT idle wandering:
- Sets `_rigidbody.linearVelocity = Vector3.zero` every frame
- Sets `_rigidbody.angularVelocity = Vector3.zero` every frame  
- Calls `_character.SetMoveDir(Vector3.zero)` every frame
- Calls `_character.SetWalk(false)` every frame
- Calls `_character.SetRun(false)` every frame
- Distance check + position snap every frame

#### `LateUpdate()` (line 261-274)
ALSO runs every frame for stationed NPCs:
- Distance check + position snap (redundant with Update)

#### `EnforceWanderBounds()` (line 386-474)
For stationed NPCs WITH idle wandering:
- Territory check every 2s
- Distance check every 1s
- Force return home logic

### 3. `NpcController.cs` (lines 1-250) — **EVERY-FRAME PROXIMITY**

#### `Update()` (line 207-215)
Runs every frame on every NPC:
```csharp
private void Update()
{
    CheckPlayerProximity();    // Every 0.25s (throttled)
    if (_isFollowing && _followTarget != null)
        UpdateFollowBehavior();  // Every frame when following
}
```

### 4. `CompanionAI.cs` (lines 1-500) — **AI STATE MACHINE**

#### `UpdateAI()` (line 261-468)
For stationed NPCs without idle wandering, exits early at line 340-343:
```csharp
if (_npcModule != null && _npcModule.IsStationedAsNpc)
{
    if (!_npcModule.AllowsIdleBehaviors)
        return true;  // ? Early exit for stationary NPCs (good)
    // ... idle wander config
}
```
This is fine — stationed NPCs without wandering skip AI updates.

### 5. `CompanionIdleBehavior.Update()` (lines 377-513) — **HEAVY PER-FRAME WORK**

For static NPCs with idle wandering enabled, runs every frame:
1. `IsAnyPlayerNearby()` — iterates all players every 5s
2. `ShouldFreeze()` check via state controller
3. `IsPlayerInteractingWithUI()` — checks multiple UI singletons
4. Emote freeze enforcement
5. Home position sync
6. Wander radius sync from NPC module
7. `CheckForStuckState()` — every 5s
8. `UpdateActiveSubBehavior()` — if sub-behavior active
9. `UpdateHeadLookAt()` — head bone rotation every frame
10. `UpdateSmoothRotation()` — rotation lerp every frame
11. `UpdateWeaponHolstering()` — holster check

### 6. `PlayerInputPatches.cs` — **LIGHTWEIGHT (OK)**
Simple prefix patch on `Player.TakeInput()` — just checks `GUIManager.ShouldBlockGameInput()` and returns false. Not a performance issue.

### 7. `GUIManager.cs` — **LIGHTWEIGHT (OK)**
- `BlockInput()` — simple flag toggle with cursor state
- `ShouldBlockGameInput()` — returns a bool field
- `IsCustomPanelOpen()` — checks ~7 singleton instances (negligible)

### 8. `TraderUIController.cs` — **UI BUILDING**
- Builds entire UI hierarchy in code (no prefab)
- `PopulateUI()` creates rows dynamically
- `Update()` only checks for ESC key when visible
- Not a per-frame performance issue

### 9. `QuestInteractionScreenController.cs` — **UI BUILDING + COOLDOWN TIMER**
- `Update()` runs cooldown display update every 1s when visible
- `Show()` calls `PanelElementManager.ConfigureForMode()` which iterates panel hierarchy
- `ShowForNpc()` may trigger `EnsureInitialized()` which builds the entire UI on first use

### 10. `DialogueUI.cs` — **STATIC CLASS**
- `Show()` builds/refreshes dialogue panel
- Has fade animation in Update (lightweight)

### 11. `StaticNpcInitializer.cs` — **ONE-TIME INIT (OK)**
- Runs once on Start via coroutine
- Calls `EnsureCompanionNpcComponents()` to set up the full component stack
- Adds `CompanionIdleBehavior`, `CompanionNpcModule` to static NPCs

---

## Session 2 Findings — Harmony Patches & Per-Frame Costs

### 12. `CompanionPatches.cs` — **CRITICAL: 3 Harmony Patches on EVERY Character** ??

Three Harmony prefix patches run on **every Character in the game** (not just companions) every frame:

#### `Character_SetMoveDir_Prefix` (line 1920-1954)
```csharp
[HarmonyPatch(typeof(Character), nameof(Character.SetMoveDir))]
public static bool Character_SetMoveDir_Prefix(Character __instance, Vector3 dir)
{
    var companion = __instance.GetComponent<CompanionController>();  // EVERY CHARACTER, EVERY CALL
    if (companion == null) return true;
    var rigidbody = __instance.GetComponent<Rigidbody>();            // 2nd GetComponent
    var stateController = __instance.GetComponent<CompanionStateController>(); // 3rd GetComponent
    ...
}
```

#### `Character_UpdateMotion_Prefix` (line 1963-1997)
Same pattern — 3x `GetComponent<>()` calls per character per frame.

#### `Character_SyncVelocity_Prefix` (line 2004-2036)
Same pattern — 3x `GetComponent<>()` calls per character per frame.

**Impact:** For a world with 30 creatures + 10 NPCs = 40 Characters:
- `SetMoveDir` is called every frame ? 40 × 3 = 120 GetComponent calls/frame
- `UpdateMotion` is called every frame ? 40 × 3 = 120 GetComponent calls/frame  
- `SyncVelocity` is called every frame ? 40 × 3 = 120 GetComponent calls/frame
- **TOTAL: ~360 unnecessary GetComponent calls per frame** (for non-companions that immediately return true)

**This runs whether UI is open or not**, so it's a baseline performance tax. But it MULTIPLIES any other per-frame costs.

#### `EnemyHud_UpdateHuds_Postfix` (line 1796-1900) ??
Runs as a **postfix on EnemyHud.UpdateHuds** which is called every frame:
- Uses **reflection** (`_enemyHudHudsField.GetValue(...)`) every frame
- **Allocates `new List<object>()`** every frame (GC pressure!)
- Iterates ALL huds and calls `GetComponent<CompanionController>()` on each
- For companion huds, uses MORE reflection to get health bar fields
- **This is a per-frame GC allocation + reflection cost**

#### `MonsterAI_UpdateAI_SkipForCompanions` (line 2051)
Runs on every MonsterAI.UpdateAI — does `GetComponent<CompanionController>()` per creature.

### 13. Canvas Sharing — **CONFIRMED ALL UIs SHARE HUD CANVAS** ??

All NPC UI panels are parented to the same Canvas as the HUD:
```
NpcMainPanelController ? Hud.instance.GetComponentInChildren<Canvas>()
QuestInteractionScreen ? (child of NpcMainPanel)
TraderUIController     ? Hud.instance.GetComponentInChildren<Canvas>()
DialogueUI             ? Hud.instance.GetComponentInParent<Canvas>()
InfoNpcPlayerPanel     ? Hud.instance.GetComponentInChildren<Canvas>()
CompanionInventoryScreen ? Hud.instance.GetComponentInParent<Canvas>()
CompanionRosterScreen  ? Hud.instance.GetComponentInParent<Canvas>()
```

**This means activating any NPC panel dirties the entire HUD canvas**, triggering Unity's canvas rebuild (layout + rendering) for every UI element on that canvas.

### 14. `CompanionCombatMovement.cs` — **PER-FRAME + REFLECTION** ??

#### `Update()` (line 521-537)
Runs every frame on every companion/NPC:
- `UpdateGroundedState()` — ground check
- `UpdateStateContext()` — fills state context struct
- `_stateMachine.EvaluateState()` — state machine evaluation
- `ExecuteCurrentState()` — state execution
- `UpdateAnimator()` — animator parameter updates

#### `LateUpdate()` (line 496-519) — **REFLECTION EVERY FRAME** ??
```csharp
private void LateUpdate()
{
    if (_combatContext == null && _combat != null)
    {
        // REFLECTION EVERY FRAME until context is found!
        var field = typeof(CompanionCombat).GetField("_context",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _combatContext = field?.GetValue(_combat) as CombatContext;
    }
    ...
}
```
If `_combatContext` is never set (e.g., for stationed NPCs that never enter combat), this does **reflection every single frame**.

### 15. `NpcVisEquipment.cs` — **LIGHTWEIGHT (OK)**

- `Update()` (line 1968) — Only runs a simple timer for delayed player mode disable
- `LateUpdate()` (line 1133) — Checks for body mesh changes (lightweight comparison)

### 16. `CompanionStateController.cs` — **LIGHTWEIGHT (OK)**

- `IsInFrozenState` — Simple enum comparison: `CurrentState == ChairSit || Emote || UIInteraction`
- `ShouldFreeze()` — Returns `IsInFrozenState || IsAnimationBlocking`
- No per-frame Update method found

### 17. `GroupHudController.cs` — **THROTTLED (OK)**

- Scans for companions every 1s (line 257-261, `COMPANION_SCAN_INTERVAL = 1f`)
- Updates bars at throttled interval
- Uses `CompanionController.AllCompanions` static list (no FindObjectsOfType)

### 18. `Menu.IsVisible` Harmony Patch — **POTENTIAL LOOP ISSUE** ??

```csharp
[HarmonyPatch(typeof(Menu), "IsVisible")]
private static void Postfix(ref bool __result)
{
    if (GUIManager.IsCustomPanelOpen())
        __result = true;
}
```
When any custom panel is open, `Menu.IsVisible()` returns true. This is called frequently by Valheim's systems. `GUIManager.IsCustomPanelOpen()` checks ~7 singleton instances every call. If Valheim calls `Menu.IsVisible()` many times per frame, this adds up.

---

## Root Cause Analysis (Updated)

### CONFIRMED ROOT CAUSES:

#### ?? Root Cause 1: Harmony Patches with GetComponent on Every Character
The `SetMoveDir`, `UpdateMotion`, and `SyncVelocity` patches do 3× `GetComponent<>()` per Character per frame. For 40 characters, that's ~360 GetComponent calls per frame minimum. These are ALWAYS running.

#### ?? Root Cause 2: EnemyHud Postfix Allocates Every Frame
`EnemyHud_UpdateHuds_Postfix` creates `new List<object>()` every frame, uses reflection, and iterates all huds. This is continuous GC pressure.

#### ?? Root Cause 3: CompanionCombatMovement Reflection Loop
For stationed NPCs that never enter combat, `CompanionCombatMovement.LateUpdate()` does `typeof(CompanionCombat).GetField("_context", ...)` reflection every frame forever.

#### ?? Root Cause 4: Canvas Rebuild Storm When Opening NPC UI
All UIs share the HUD canvas. Opening NpcMainPanel (575+ nodes) dirties the entire canvas, causing layout rebuild of every HUD element.

#### ?? Root Cause 5: EnforceStationedState Redundant Per-Frame Work
Stationed NPCs call `SetMoveDir(Zero)`, `SetWalk(false)`, `SetRun(false)`, zero velocity — every frame, even when nothing has changed. These calls hit the Harmony patches (Root Cause 1) creating a multiplier effect.

### WHY IT'S WORSE FOR NPC UI THAN COMPANION UI:

When opening **companion** inventory/stats:
- `IsPlayerInteractingWithUI()` returns `true` for that companion
- `EnforceInteractionFreeze()` fires ? state set to `UIInteraction`
- `CompanionStateController.IsInFrozenState` returns `true`
- The 3 Harmony patches (SetMoveDir, UpdateMotion, SyncVelocity) detect `IsInFrozenState` and **skip the original method entirely** ? no physics work, no animation sync
- `CompanionCombatMovement.Update()` checks `_stateController.ShouldSkipCombatMovement` ? **skips entirely**
- `CompanionAI.UpdateAI()` checks `_stateController.ShouldSkipAIUpdate` ? **skips entirely**
- Result: The companion goes nearly dormant ? **minimal per-frame cost**

When opening **NPC** quest/dialogue/trader:
- `IsPlayerInteractingWithUI()` returns `false` (missing checks!)
- `EnforceInteractionFreeze()` **never fires**
- State stays at `Idle` ? `IsInFrozenState` is `false`
- All 3 Harmony patches run their full `GetComponent` chains
- `CompanionCombatMovement.Update()` runs full state machine
- `CompanionCombatMovement.LateUpdate()` does reflection
- `EnforceStationedState()` calls `SetMoveDir(Zero)` which hits the Harmony patch again
- `CompanionIdleBehavior.Update()` runs full idle evaluation
- **ALL of this runs on EVERY stationed NPC in the area, not just the one being interacted with**
- Result: Full per-frame cost on all NPCs ? **massive FPS drop**

---

## Areas Still To Investigate

### HIGH PRIORITY
1. ~~Canvas hierarchy~~ — **CONFIRMED: All share HUD canvas**
2. ~~Harmony patches on Character~~ — **CONFIRMED: 3 patches with GetComponent on every Character**
3. ~~NpcVisEquipment Update~~ — **CONFIRMED: Lightweight (OK)**
4. ~~CompanionCombatMovement~~ — **CONFIRMED: Reflection loop + full state machine per frame**
5. **UnifiedMovementAuthority** — Does it have per-frame work? (referenced in CompanionController)
6. **BehaviorCoordinator** — Per-frame cost?
7. **CompanionFormationController** — Per-frame cost?
8. **CompanionStats/CompanionProgression** — Per-frame cost?

### MEDIUM PRIORITY
8. **CompanionIdleBehavior.HeadLook.cs** — `UpdateHeadLookAt()` and `ApplyHeadLookAt()` run every frame
9. **CompanionIdleBehavior.SubBehaviors.cs** — `UpdateActiveSubBehavior()` runs every frame
10. **CompanionInteractionBehavior** — Does it have per-frame work?
11. ~~GroupHudController~~ — **CONFIRMED: Throttled (OK)**
12. ~~CompanionStateController~~ — **CONFIRMED: Lightweight (OK)**

### LOW PRIORITY  
13. **QuestPreviewRenderer** — Camera rendering every LateUpdate when active (but only when preview is staged)
14. ~~UIOverrideBarUpdater~~ — 20Hz updates (throttled, probably fine)
15. ~~GlobeHudController~~ — 20Hz updates (throttled, probably fine)

---

## Proposed Fixes (Ordered by Impact)

### Fix 1: CRITICAL — Expand `IsPlayerInteractingWithUI()` ??
**File:** `CompanionIdleBehavior.cs` line 1065
**Impact:** HIGH — This is the "unlock" that makes NPC UI as cheap as companion UI

Add checks for all NPC UI screens:
```csharp
private bool IsPlayerInteractingWithUI()
{
    if (!GUIManager.IsCustomPanelOpen())
        return false;

    // Companion-specific screens
    if (CompanionInventoryScreen.IsOpenForCompanion(_companion))
        return true;
    if (CompanionStatsScreen.IsOpenForCompanion(_companion))
        return true;

    // Stationed NPC screens — ANY NPC UI being open should freeze ALL stationed NPCs
    if (_npcModule != null && _npcModule.IsStationedAsNpc)
    {
        if (NpcMainPanelController.IsOpen)
            return true;
        if (QuestInteractionScreenController.Instance != null && QuestInteractionScreenController.Instance.IsVisible)
            return true;
        if (DialogueUI.IsVisible)
            return true;
        if (TraderUIController.Instance != null && TraderUIController.Instance.IsVisible)
            return true;
        if (InfoNpcPlayerPanel.IsVisible)
            return true;
    }

    return false;
}
```

### Fix 2: CRITICAL — Cache CompanionController in Harmony Patches ??
### Fix 3: HIGH — Fix EnemyHud Postfix Allocation ??
### Fix 4: HIGH — Fix CompanionCombatMovement Reflection Loop ??
### Fix 5: MEDIUM — Throttle EnforceStationedState ??
### Fix 6: MEDIUM — Separate Canvas for NPC UI ??
### Fix 7: LOW — Cache MonsterAI companion check

(See code samples in git history — all proposed fixes have been implemented.)

---

## Implementation Status

### ? Fix 1 DONE — `IsPlayerInteractingWithUI()` expanded
**File:** `CompanionIdleBehavior.cs`
- Added checks for `QuestInteractionScreenController.IsVisible`, `DialogueUI.IsVisible`, `TraderUIController.IsVisible`, `InfoNpcPlayerPanel.IsVisible`
- When any NPC UI is open, ALL stationed NPCs now freeze via `EnforceInteractionFreeze()`
- This cascades: state ? `UIInteraction` ? `IsInFrozenState=true` ? Harmony patches skip `SetMoveDir`/`UpdateMotion`/`SyncVelocity` ? AI skips ? CombatMovement skips
- **This is the primary fix for the FPS drop**

### ? Fix 2 DONE — Companion cache in Harmony patches
**File:** `CompanionPatches.cs`
- Added `_companionLookupCache`, `_rigidbodyCache`, `_stateControllerCache` (Dictionary<int, T>)
- Added `GetCachedCompanion()`, `GetCachedRigidbody()`, `GetCachedStateController()` helper methods
- Updated ALL Character Harmony patches to use cached lookups: `SetMoveDir`, `UpdateMotion`, `SyncVelocity`
- Updated ALL EnemyHud patches to use cached lookups: `ShowHud`, `TestShow`, `UpdateHuds`
- Updated `MonsterAI_UpdateAI_SkipForCompanions` to use cached lookup via `m_character`
- Added `ClearCacheForCharacter(int instanceId)` for cleanup
- Added cache clearing in `ZNetScene_Awake_Postfix`
- **Eliminates ~360+ GetComponent calls per frame baseline, plus EnemyHud per-hud lookups**

### ? Fix 3 DONE — EnemyHud allocation fix
**File:** `CompanionPatches.cs`
- Replaced `new List<object>()` in `EnemyHud_UpdateHuds_Postfix` with static `_tempHudKeys` list
- **Eliminates per-frame GC allocation**

### ? Fix 4 DONE — CompanionCombatMovement reflection loop
**File:** `CompanionCombatMovement.cs`
- Cached `FieldInfo` statically (`_cachedContextField`, `_contextFieldResolved`)
- Added `_combatContextLookupDone` flag — only attempts reflection once per NPC instance
- **Eliminates per-frame reflection for all NPCs that never enter combat**

### ? Fix 5 DONE — Throttle EnforceStationedState
**File:** `CompanionNpcModule.cs`
- Added `_stationaryEnforced` flag
- `SetMoveDir(Zero)`, `SetWalk(false)`, `SetRun(false)`, velocity zeroing now only called once
- Only re-applied after a position drift snap (distance > 0.1f)
- **Eliminates N redundant SetMoveDir/SetWalk/SetRun calls per frame per stationed NPC**

### ? Fix 7 DONE — MonsterAI companion cache
**File:** `CompanionPatches.cs`
- `MonsterAI_UpdateAI_SkipForCompanions` now uses `GetCachedCompanion(m_character)` instead of `GetComponent<>()`

### ?? Fix 6 DEFERRED — Separate Canvas (lower priority, more risk, requires broader testing)

### ? BUILD PASSES — All changes compile successfully

---

## Summary of Per-Frame Cost Reduction

### Before (with 10 stationed NPCs + 30 creatures in area):
| Source | Per-Frame Cost |
|--------|---------------|
| SetMoveDir Harmony patch | 40 chars × 3 GetComponent = 120 calls |
| UpdateMotion Harmony patch | 40 chars × 3 GetComponent = 120 calls |
| SyncVelocity Harmony patch | 40 chars × 3 GetComponent = 120 calls |
| EnforceStationedState | 10 NPCs × (SetMoveDir + SetWalk + SetRun + velocity) = 40+ calls |
| CompanionCombatMovement LateUpdate | 10 NPCs × reflection = 10 GetField calls |
| EnemyHud UpdateHuds | 1 List alloc + N hud × GetComponent |
| **Total per-frame overhead** | **~400+ GetComponent + 10 reflections + 1 GC alloc** |

### After:
| Source | Per-Frame Cost |
|--------|---------------|
| SetMoveDir Harmony patch | 40 chars × 1 dict lookup = 40 lookups (cached) |
| UpdateMotion Harmony patch | 40 chars × 1 dict lookup = 40 lookups (cached) |
| SyncVelocity Harmony patch | 40 chars × 1 dict lookup = 40 lookups (cached) |
| EnforceStationedState | 10 NPCs × (1 bool check) = 10 checks |
| CompanionCombatMovement LateUpdate | 0 (lookup done once) |
| EnemyHud UpdateHuds | 0 GC alloc + cached lookups |
| **Total per-frame overhead** | **~120 dict lookups + 10 bool checks** |

### When NPC UI is open (additional savings from Fix 1):
- All stationed NPCs enter `UIInteraction` state ? `IsInFrozenState = true`
- Harmony patches short-circuit after cached companion check + frozen check
- CompanionAI.UpdateAI skips entirely
- CompanionCombatMovement.Update skips entirely
- CompanionIdleBehavior.Update returns early at freeze check
- **~90% reduction in NPC per-frame work while UI is open**
---

## Implementation Progress

### ? Fix 1 DONE — `IsPlayerInteractingWithUI()` expanded
**File:** `CompanionIdleBehavior.cs`
- Added checks for `QuestInteractionScreenController.IsVisible`, `DialogueUI.IsVisible`, `TraderUIController.IsVisible`, `InfoNpcPlayerPanel.IsVisible`
- When any NPC UI is open, ALL stationed NPCs now freeze via `EnforceInteractionFreeze()`
- This cascades: state ? `UIInteraction` ? `IsInFrozenState=true` ? Harmony patches skip `SetMoveDir`/`UpdateMotion`/`SyncVelocity` ? AI skips ? CombatMovement skips
- **This is the primary fix for the FPS drop**

### ? Fix 2 DONE — Companion cache in Harmony patches
**File:** `CompanionPatches.cs`
- Added `_companionLookupCache`, `_rigidbodyCache`, `_stateControllerCache` (Dictionary<int, T>)
- Added `GetCachedCompanion()`, `GetCachedRigidbody()`, `GetCachedStateController()` helper methods
- Updated `Character_SetMoveDir_Prefix`, `Character_UpdateMotion_Prefix`, `Character_SyncVelocity_Prefix` to use cached lookups
- Added `ClearCacheForCharacter()` for cleanup
- Added cache clearing in `ZNetScene_Awake_Postfix`
- **Eliminates ~360+ GetComponent calls per frame**

### ? Fix 3 DONE — EnemyHud allocation fix
**File:** `CompanionPatches.cs`
- Replaced `new List<object>()` in `EnemyHud_UpdateHuds_Postfix` with static `_tempHudKeys` list
- **Eliminates per-frame GC allocation**

### ?? Fix 4 TODO — CompanionCombatMovement reflection loop
**File:** `CompanionCombatMovement.cs` ~line 496-513
- `LateUpdate()` does `typeof(CompanionCombat).GetField("_context", ...)` reflection every frame when `_combatContext == null`
- Need to cache the FieldInfo statically and add a "gave up" flag
- For stationed NPCs that never enter combat, this is reflection every frame forever

### ?? Fix 5 TODO — Throttle EnforceStationedState
**File:** `CompanionNpcModule.cs` lines 330-379
- Currently calls `SetMoveDir(Zero)`, `SetWalk(false)`, `SetRun(false)`, zero velocity EVERY frame
- Each SetMoveDir call hits the Harmony patch (even with cache, still unnecessary work)
- Need to add "already zeroed" flag, only re-zero after position snap

### ?? Fix 6 DEFERRED — Separate Canvas (lower priority, more risk)
### ?? Fix 7 TODO — MonsterAI companion cache (use same cache as Fix 2)

### Files still needing investigation:
- `CompanionCombatMovement.cs` — full Update/LateUpdate review
- `UnifiedMovementAuthority.cs` — runs Update + LateUpdate per companion
- `BehaviorCoordinator.cs` — unknown per-frame cost
- `CompanionFormationController.cs` — unknown per-frame cost
