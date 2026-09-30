# Companion AI Behavior Bugfix Tracker

Source: in-game observations + log inspection (run dated 04/28/2026 03:16:xx,
local listen-server, full Balrond + Seasons mod stack, two human players).

Issues are listed in the order the player asked them to be addressed
("most annoying first").

---

## #1 — Move command ignored / overridden by flee  ✅ FIXED (partial)

### Symptom
> "When a companion is scared and wants to flee they will ignore move commands.
> I don't give a shit how scared they are, they do what they're told even if it
> will lead to direct death."

### Root cause
Three transition points dropped the companion into `AIState.Fleeing` without
checking whether the player had an active command (`HasCommandPriority`
on `CompanionCombatMovement`):

1. `CompanionAI.OnDamaged` — any incoming damage that returned `ShouldFlee()`
   forced flee, overwriting any in-flight move or attack command.
2. `CompanionAI.UpdateCombatState` — the "critical stamina recovery" branch
   force-fled non-tanks unconditionally.
3. `CompanionAI.UpdateCombatState` — the `ShouldFlee()` branch transitioned to
   Fleeing without consulting the command flag.

In addition, once in `UpdateFleeingState` the only exits were
"no threats / health recovered / stamina recovered / time-out". A player
issuing a fresh command had no effect until one of those fired, which
in practice never happened mid-fight.

### Fix
Files:
- `FiresNPCs/Modules/Companions/AI/CompanionAI/CompanionAI.cs`
- `FiresNPCs/Modules/Companions/AI/CompanionAI/CompanionAI.Combat.cs`

Each transition site computes
`bool underPlayerCommand = _combatMovement?.HasCommandPriority == true`
and short-circuits the flee branch when set. `UpdateFleeingState` now
exits flee on the very next tick if a command appears, returning to
`AIState.Following` (or `Idle`) so the existing command-priority block
in `Tick()` can resume the order.

### Verification
- Build clean (no compile errors in touched files).
- In-game: pending player confirmation.

### Side-effect noted but **not** fixed in this pass
`ShouldFlee()` returns true even when the range scan in `UpdateFleeingState`
reports zero threats nearby (Gefjon log, ~0.5 s oscillation). Tracked as
issue #4 below.

### Known scenarios this pass does **not** cover yet
If a sub-behavior (cooking / fire-tending / smelter / wood-gather) is
active, `Tick()` short-circuits at the sub-behavior gate before reaching
the player-command block. So a new move ping while a sub-behavior is
running will still be ignored until the behavior ends. Belongs in #1
follow-up — flagged for a second pass.

---

## #2 — Companions push the player around  ✅ FIXED

### Symptom
Companions physically displace the local player when they walk into them,
shoving the player around terrain. Persisted after the recent flee changes.

### Root cause
`CompanionPersonalSpaceEnforcer` applied a soft repulsive acceleration
to the companion's own rigidbody when it entered a 1.6 m bubble around
a player — but did **not** disable the underlying physics collision.
Unity's rigidbody-vs-rigidbody contact response transfers momentum in
both directions on contact, so by the time the bubble force dominates,
the impulse has already shoved the player. The bubble was correct in
intent; it just wasn't enough on its own.

### Fix
File: `FiresNPCs/Modules/Companions/Movement/CompanionPersonalSpaceEnforcer.cs`

Added a periodic (1 s cadence) sweep that calls
`Physics.IgnoreCollision(companionCollider, playerCollider, true)` for
every (own-collider, player-collider) pair across every active player.
The bubble force still handles visual spacing; the physics engine
never transfers momentum between companion and player in either
direction.

The cadence picks up new players (joins, respawns) and any newly-added
child colliders (equipped weapons etc.) without explicit hooks. Calls
are idempotent so repeated invocations are free.

### Verification
- Build clean.
- In-game: pending player confirmation.

---

## #3 — Stay / Follow state breaking; auto-flips to Follow  ⚠️ ROUND-1 ATTEMPTED

### Symptom
> "They no longer properly save their stay state/follow state and now every
> time we run away from where they are set to stay they just automatically
> return to following."

### Round 1 — death/respawn override (applied)
File: `Modules/Companions/CompanionRespawnManager.cs`.

The respawn path had a hard-coded "all non-stationed companions follow
after respawn" branch that ignored `savedData.IsFollowing`. Replaced
with a branch that reads the persistent intent and restores Stay
(at the saved `HomePosition`) when the companion was Staying before
death, and Follow otherwise.

This covers the *one* concrete trigger we could verify from logs:
owner sets Stay → companion dies to enemy → respawns and returns to
player. Player ran far enough away from the original Stay anchor that
the death scenario can't be ruled out, so we're treating Round 1 as
the most likely candidate and moving on.

### Round-2 candidates (only if symptom recurs without an intervening
death)
- Distance-based auto-flip in `CheckFollowTeleport` or sub-behaviour
  exit logic re-acquiring follow target without consulting persistent
  intent.
- Vault → ZDO mirror writing the wrong `companion_wasfollowing` value
  on a save tick.

Round 2 only investigated if a future log shows the flip without a
death cycle.

### Verification
- Build clean.
- In-game: pending player confirmation. Test scenario: command Stay,
  kill the companion, wait for respawn, confirm respawned companion
  returns to its stay anchor (not to the player).

---

## #4 — Flee oscillation (`ShouldFlee=true` while no threats)  ⏳ PENDING

### Symptom (log)
```
[CompanionAI] Gefjon ShouldFlee=true but re-entry cooldown active (0.4s left) — staying in Combat
[CompanionAI] EXITING FLEE: Gefjon no active threats nearby (stamina=100%) - returning to normal
[CompanionAI] Gefjon ShouldFlee=true but re-entry cooldown active (1.4s left) — staying in Combat
```
Repeats every ~0.5 s indefinitely. Stamina at 100 %, no threats in range.

### Root cause
`ShouldFlee()` and `UpdateFleeingState`'s exit-condition use *different*
sources of truth. `UpdateFleeingState` does a range-limited scan
(`Character.GetCharactersInRange(... FLEE_THREAT_SCAN_RANGE)`) and
correctly reports zero enemies; `ShouldFlee()` keys off `_threatAnalyzer`
and the boss / multi-dangerous heuristics, which can return retreat-
recommendation even when no enemy is actually in range.

### Planned fix
Have `ShouldFlee()` short-circuit to `false` when the same range scan
reports zero hostiles. The threat analyzer remains an input but the
range scan is the authoritative ground truth.

### Also addresses
The Gefjon-style aggression complaint:
> "still chase after EVERYTHING in sight"
Likely a target-eligibility rather than flee bug. Tracked separately if
the range-scan fix doesn't quiet target acquisition.

---

## #5 — Ability spam (Warcry) + over-aggression (chase everything)  ✅ FIXED

### Symptom
> "every berserker was spamming as soon as it was available as soon as
> they even became alerted without us even being in combat. They may have
> even been ignoring the cooldown time all together."
>
> "the super agression they now have since we stopped them from running
> away all the time now they just chase after everything in the forest
> regardless of if their owner is actually engaging in a fight or not."

The two complaints share a root cause and a single fix.

### Root cause
`CompanionAI.IsInCombat` is just `_currentState == AIState.Combat`,
and the targeting scan inside `CompanionAI.Targeting.cs` would put a
companion into `Combat` state for ANY non-passive enemy inside
`aggroRange` (default 20 m). So a companion in Follow mode that
spotted a greyling 18 m away would:

1. Acquire that greyling as `_targetCreature` and SetState(Combat).
2. Run after it (the over-aggression complaint).
3. `ArchetypeAbilitySystem.Update` sees `_inCombat = true`, stamps
   `_combatEntryTime`, and fires Warcry inside the 5 s combat-entry
   window.

Warcry's actual cooldown (30 s, `groupAbilityCooldown`) was always
being respected; the perceived spam was Warcry firing on every fresh
"alerted at a wandering greyling" cycle.

### Fix
Files:
- `Modules/Companions/AI/CompanionAI/CompanionAI.cs` — reduced default
  `aggroRange` from 20 → 15 (perf-only; the gate below filters
  engagement) and added `OWNER_DEFENSE_RADIUS = 8f` constant with
  doc-comment explaining the policy.
- `Modules/Companions/AI/CompanionAI/CompanionAI.Targeting.cs` — added
  `IsThreatToOwnerOrSelf(target, ownerPos, ownerCharacter)` helper and
  applied it as a gate in the threat-scan loop.

For an aggressive (non-passive) enemy in Follow mode, the companion
now only picks it as a target when AT LEAST ONE of:

1. The enemy is within `OWNER_DEFENSE_RADIUS` (8 m) of the owner
   (defending the owner's personal space).
2. The enemy's `BaseAI.GetTargetCreature()` is the owner, any other
   player, or this companion (the enemy already engaged us — fight
   back).
3. This companion was directly damaged in the last
   `DIRECT_DAMAGE_ALERT_DURATION` (15 s) — retaliate even if the
   attacker has since broken aggro.

If none hold, the enemy is left alone even though it's inside
`aggroRange`. Companions in Follow mode now defend the owner instead
of roaming attack-dogs.

Unchanged paths (deliberately not affected):

- Stay mode keeps its existing `STAY_MODE_AGGRO_RANGE = 5 m` bubble
  and ignores the new gate so a staying companion still defends its
  post.
- Passive wildlife (boar / deer / neck / ...) is still governed by
  the per-companion Hunting toggle in `CompanionBehaviorToggles`.
- Owner-issued explicit attack commands (`ForceTarget`) bypass the
  detection scan entirely, so they're never blocked by the gate.

### Knock-on for Warcry / other group abilities
No change to `ArchetypeAbilitySystem` was required. With the targeting
gate in place, `AIState.Combat` only fires for genuine threats, so
`_combatEntryTime` only stamps on real engagements and Warcry / other
group buffs only fire when the companion is actually fighting
something that's threatening the player or them.

### Verification
- Build clean.
- In-game: pending player confirmation. Test scenarios:
  1. Walk through forest with Berserker companion, ignore distant
     greylings. Companion should NOT engage and should NOT log Warcry.
  2. Let a greyling close to within 8 m of the player. Companion
     should engage (defending owner) and Warcry may fire (real
     combat).
  3. Let a greyling hit the companion directly. Companion should
     retaliate even if the player runs away (15 s retaliation
     window).
  4. Right-click an enemy to issue an attack command. Companion
     should engage that target unconditionally (ForceTarget bypass).

---

## #6 — Auto-pickup re-request loop  ⏳ PENDING

### Symptom (log)
```
Player 1468977070 wants to pickup SpearFlint(Clone)   im: 171759845
  but they are already the owner
```
Companion fires the ownership-grab RPC every tick on the same item even
after winning ownership. Spam, not a hang.

### Suspected files
- `Modules/Companions/CompanionAutoPickup.cs`
- `Modules/Companions/IdleBehaviors/LootPickupBehaviorV2.cs`

### Planned fix
Track per-ZDO "I already own this, stop re-requesting" set; clear when
the item enters inventory or despawns.

### Fix not yet attempted.

---

## #7 — Party invite popup soft-locks invitee  ⏳ PENDING (last)

### Symptom
Invitee's screen freezes; Yes / No clicks do nothing; only Alt-F4 escapes.

### Workaround in use
Don't send party invites.

### Suspected
`UnifiedPopup.Push(new YesNoPopup(...))` callback throws synchronously,
popup never dismisses, modal blocks all input. Confirmed pending code
inspection of `OnInviteYes` / `OnInviteNo`.

### Fix not yet attempted.

---

## #8 — Long-form emotes never reset  ✅ FIXED

### Symptom
Persistent/looping emotes (`emote_dance`, `emote_vibe`, `emote_sit`,
`emote_rest`, `emote_headbang`, etc.) keep playing indefinitely even
after their scheduled duration expires. The companion slides or stays
frozen in the emote pose long after it should have returned to idle.

### Root cause
Two related problems in `CompanionIdleBehavior.StuckPrevention.cs`:

1. **Animator never re-evaluated after bool clear.**  
   `ForceAnimationStateReset()` sets all emote booleans to `false` via
   both `ZSyncAnimator` and the local `Animator`, but never calls
   `Animator.Update()` afterwards. Valheim's Mecanim state machine only
   evaluates exit transitions when the animator is updated; without an
   explicit update tick the transition from the emote state to Locomotion
   never fires, so the animation keeps looping.

2. **No movement kick to satisfy velocity-based exit conditions.**  
   Even after the bool is cleared, some Valheim emote states require the
   character's velocity to be non-zero to trigger the Locomotion blend
   transition. Vanilla players exit emotes through input (`StopEmote()`
   is called by movement/attack input, which naturally implies non-zero
   velocity). Companion NPCs have no such input-driven interrupt, so the
   exit condition is never met without an explicit one-frame movement kick.

### Fix
File: `FiresNPCs/Modules/Companions/CompanionIdleBehavior/CompanionIdleBehavior.StuckPrevention.cs`

- Added `_animator.Update(0.001f)` at the end of `ForceAnimationStateReset()`
  so the animator re-evaluates all transition conditions immediately after
  all emote booleans are cleared.
- Added a one-frame forward movement kick in `ForceEndEmote()` via
  `_character.SetMoveDir(transform.forward * 0.01f)` immediately before
  calling `ForceAnimationStateReset()`. This satisfies any velocity-based
  exit conditions in the Mecanim state machine. The movement is zeroed
  out on the very next Update pass by the normal idle/standing state.

---

## #9 — Wild companions spawn bald (no hair or beard)  ✅ FIXED

### Symptom
Wild (world-spawned) companions consistently appear with no hair and no
beard. Hammer-placed companions from the build menu generate hair/beard
correctly. Taming a bald wild companion does not give them hair retroactively.

### Root cause
`CompanionRandomLoadout.ShouldGenerateRandomLoadout()` guards against
re-running generation on an already-initialized companion. The guard
checks three things:

```csharp
bool hasAppearance =
    !string.IsNullOrEmpty(zdo.GetString("companion_hair", "")) ||
    zdo.GetBool("companion_isfemale", false) ||
    (!string.IsNullOrEmpty(savedName)
        && savedName != "Companion"
        && savedName != "CompanionNpc");  // ← BUG
```

`WildCompanionDresser.Dress()` assigns a name from the name pool (e.g.
`"Olaf the Bold"`) to the companion's ZDO **before**
`CompanionRandomLoadout.Start()` runs. When `Start()` calls
`ShouldGenerateRandomLoadout()`, `savedName` is already `"Olaf the Bold"`,
which is neither `"Companion"` nor `"CompanionNpc"`, so `hasAppearance`
evaluates to `true` and the method returns `false` — generation is
skipped entirely. The companion ships with an empty `companion_hair` key
and remains bald forever.

The name-based check was intended as an optimization ("if we have a real
name the companion was already fully set up"), but it fires a false
positive whenever a wild companion receives its name before the loadout
runs, which is always.

### Fix
File: `FiresNPCs/Modules/Companions/CompanionRandomLoadout.cs`
Method: `ShouldGenerateRandomLoadout()`

Removed the name-based predicate from `hasAppearance`. The check now
only considers actual appearance ZDO data (`companion_hair` non-empty or
`companion_isfemale` true) to decide whether generation already ran.
A companion with a real name but no hair ZDO key will now correctly
proceed through generation.

### Verification
- Build clean.
- In-game: newly spawned wild companions should have randomised hair,
  beard (for male companions), and hair colour matching their skin tone.

---

## Out-of-scope but noted

- Vali repeatedly logs `cannot gather wood - no axe available`. The wood-
  gathering behavior should detect the missing tool *once* per attempt
  and idle, not log spam. Cosmetic; defer.

---

## #10 — SmelterOperator pull-failure spam loop  ✅ FIXED

### Symptom (log)
```
[SmelterOperator] Orm Bloodaxe failed pull attempt #1/3
[SmelterOperator] Orm Bloodaxe failed pull attempt #1/3
[SmelterOperator] Orm Bloodaxe failed pull attempt #1/3
...  (repeated every few seconds indefinitely)
```

### Root cause
`SmelterOperatorBehavior.Lifecycle.cs` resets `_consecutiveFailedPulls = 0`
in `OnStart()` every time the behavior is selected. The circuit breaker
requires 3 consecutive failures within a single run to set the
`_materialUnavailableCooldowns` entry. But after each single failure
`CheckResourceAvailability()` returns false and calls `SetPhase(Complete)`,
ending the behavior cleanly — so the counter never reaches 3. On the next
idle tick the behavior is selected again, the counter resets, the same
single failure occurs, and the cycle repeats forever with no cooldown
ever being applied.

### Fix
`SmelterOperatorBehavior.StationPhases.cs` — `UpdatePullingFromChests()`.

Added: when `_lastPullCount == 0` (pull failed) AND
`CheckResourceAvailability()` also returns false (confirms no resources
anywhere), set `_materialUnavailableCooldowns` immediately before calling
`SetPhase(Complete)`. This gates re-selection for 60 seconds, breaking
the loop without requiring the counter to accumulate across restarts.

---

## #11 — FarmingBehavior harvests 0 honey  ✅ FIXED

### Symptom (log)
```
[FarmingBehavior] Urd the Swift CanStart: Found 1 beehives with honey
[FarmingBehavior] Urd the Swift harvested 0 honey
```

### Root cause
`FarmingBehavior.Phases.cs` — `UpdateHarvestingBeehive()`.
`FarmingDataHelper.GetHoneyLevel(_targetBeehive)` was called **after**
`_targetBeehive.Interact(...)`. Valheim's `Beehive.Interact()` drops
the honey items and resets the internal `m_honeyLevel` to 0 immediately.
So `GetHoneyLevel()` always returned 0 post-interaction, and
`_honeyHarvested` was never incremented despite the honey actually
being dispensed.

### Fix
`FarmingBehavior.Phases.cs` — moved `GetHoneyLevel()` call to **before**
`Interact()` so the pre-harvest level is captured.

---

## #12 — IdleWander DENIED log floods console  ✅ FIXED

### Symptom (log)
```
[MovementAuthority] Alfhild Forkbeard CompanionAI (IdleWander) DENIED
  - IdleWander already holds same-priority IdleWander authority
  (no equal-priority preemption)
```
Appears roughly once every 5 s per idle companion. With 10+ base
companions this generates 100+ lines per minute.

### Root cause
`CompanionAI.Pathfinding.cs` — when `_currentState == AIState.Idle`,
`GetAIMovementSource()` returns `MovementSource.IdleWander` and the AI
calls `TryAcquireAuthority(IdleWander, "CompanionAI", 2f)` on every tick.
`CompanionIdleBehavior.Wandering.cs` acquires the same source with owner
`"IdleWander"`.  Because the two owner strings are different
(`"CompanionAI"` vs `"IdleWander"`), the equal-priority incumbency guard
fires and logs every 5 s (throttle interval). This is an expected
architectural overlap — CompanionAI correctly defers to CompanionIdleBehavior
for wandering — but it should be entirely silent.

### Fix
`UnifiedMovementAuthority.cs` — equal-priority denial block.

Added a targeted suppression: when the requesting source is `IdleWander`
AND the current authority owner is `"IdleWander"`, the denial is
returned silently without logging. This is the one benign pattern
(CompanionAI-in-idle vs CompanionIdleBehavior) that produces the flood;
all other same-priority denials continue to log normally.

---

## #13 — SmelterOperator `Unexpected state in UpdateFillingStation` loop  ⏳ PENDING

### Symptom (log)
```
[SmelterOperator] Thorsten Forkbeard -> Unexpected state in UpdateFillingStation, waiting...
  (×10 in rapid succession)
```

### Observed pattern
Thorsten pulls 49 items successfully, transitions to `FillingStation`,
then hits the `Unexpected state` catch-all 10+ times before the behavior
finishes. The anti-spam guard (`Time.time - _phaseStartTime < 0.1f`)
fires on each phase re-entry, meaning `FillingStation` is being exited
and re-entered in a tight loop.

### Suspected cause
`UpdateFillingStation()` has 8 decision steps (STEP 1–8). When none of the
steps match the current smelter state, the fall-through catch-all fires.
The smelter may be in a transitional state (just started processing ore)
that none of the STEP conditions cover. The behavior needs an additional
state-detection step for "smelter is running and does not need anything
right now — wait silently without re-entering PullingFromChests".

### Files
- `SmelterOperatorBehavior.StationPhases.cs` → `UpdateFillingStation()`

### Fix not yet attempted.

---

## #14 — WorkstationInteraction MovingToWorkstation timeout (occasional)  ⏳ PENDING

### Symptom (log)
```
[Warning] [WorkstationInteraction] Gunnar the Silent Phase MovingToWorkstation timed out
```

### Observed pattern
Single occurrence. Gunnar times out walking to a workstation (30 s
limit). The per-companion cooldown (`_lastWorkstationTime`) is set in
`Start()` so the same companion does not retry for 5 minutes, which is
correct. However, no per-workstation cooldown exists — a different
companion could immediately target the same unreachable station.

### Suspected cause
Navmesh gap between the companion's home area and the workstation. Likely
only a problem for specific base layouts.

### Planned fix
Add a per-station blacklist (`static Dictionary<int, float>` keyed on
the station `GetInstanceID()`) with a short cooldown (e.g. 120 s) set
on `MovingToWorkstation` timeout. `FindNearbyWorkstation()` should skip
blacklisted entries. The existing per-companion cooldown covers the
same-companion case; this addition covers the cross-companion case.

### Fix not yet attempted.

---

