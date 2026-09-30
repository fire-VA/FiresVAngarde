# Wild Companion Spawn � Gameplan

**Status:** v1 landed, **re-architected to Path B** after the runtime-cloning
approach corrupted `ZNetScene.m_namedPrefabs` with Unity fake-null entries.
Single shared `CompanionNpc` prefab, biome-driven faction roll, builds clean.
**Spawn injection added** � wild companions now spawn out-of-the-box via a
Harmony patch on `SpawnSystem.Awake` that appends `SpawnData` entries to every
zone (no EW YAML required; EW can still override per-entry).

> **Correction (2026-06): gear roll path.** This plan describes a static
> `CompanionGearTables.Roll(archetype, tier, rng)` lookup table as the gear
> mechanism. That table was written but **never wired into the runtime path**,
> and `CompanionGearTables.cs` has since been removed as dead code. Wild-companion
> gear is actually rolled at runtime by **`CompanionRandomLoadout`** (a
> `MonoBehaviour` on the spawned companion): it builds per-slot item pools and
> filters them by biome tier via `GetTierFilteredItems` / `GetBiomeTier` /
> `GetItemTier`, then equips each slot directly on the live `Humanoid`. Treat the
> `CompanionGearTables.*` references below as historical design intent, not
> shipped code.

## 0. Path B architecture (the one we shipped)

The earlier design (PR 1) tried to clone `CompanionNpc` three times at
runtime into `CompanionNpc_Wild_Neutral / _Bandit / _Cultist` prefabs and
register each with `ZNetScene`. That fought Unity's object lifecycle � the
`CompanionNpc` prefab has a dense component stack (`Character`,
`CompanionController`, `MonsterAI`, `CompanionAI`, `VisEquipment`,
`ZSyncTransform`, `ZSyncAnimation`, etc.) and one or more of those `Awake`
paths called `Destroy(gameObject)` when they woke without a valid ZDO. The
clones ended up fake-null in `m_namedPrefabs` within minutes, NREing every
consumer that walked the dictionary (`ServerDevcommands` autocomplete,
`InfinityHammer.SpriteHelper.FindSprite`, etc.).

**Path B (current)** follows the existing codebase pattern
(`CompanionPrefabManager`, `VAPieceManager`, `StaticNpcInitializer`):

1. **No runtime cloning.** The `CompanionNpc` prefab loaded from the asset
   bundle is used as-is.
2. **One name alias** � `CompanionNpc_Wild` is added to
   `ZNetScene.m_namedPrefabs` via reflection, pointing at the existing
   `CompanionNpc` GameObject. ExpandWorld YAML references this single name.
3. **Components attached to the base prefab** � `WildCompanionSeed`,
   `WildCompanionDresser`, `WildCompanionSquadFollower`,
   `WildCompanionLootOnDeath` all attached directly to `CompanionNpc` via
   `AddComponent` during registration. Because the base prefab is inactive
   while sitting in `m_prefabs`, `AddComponent` queues � not fires � each
   component's `Awake`. When `SpawnSystem.Spawn` / `Object.Instantiate`
   creates a live copy, all the `Awake`s run at the correct lifecycle
   moment alongside vanilla components.
4. **Guards on every component** so the wild-companion logic is a no-op on
   non-wild `CompanionNpc` spawns (tamed recruits, admin spawns, placed
   static NPCs). Guards read the instance's ZDO:
   - Server-only
   - `companion_wild_dressed` not already set
   - `companion_id` empty
   - `companion_tamed` false
5. **Faction is rolled per-instance** from the biome at spawn position,
   then written to `Character.m_faction` on the instance (not the prefab).
   This is the key realisation: because the prefab is shared, faction
   cannot be a prefab field � it must be an instance-level mutation.

## 1. Goals (unchanged)

- Wild companions appear in the world through **ExpandWorld**'s spawn YAML.
  Admins reference `CompanionNpc_Wild` in spawn tables exactly like any
  vanilla creature.
- Each wild companion is **persistent per ZDO**: same archetype, gear,
  stats, stars, and name across zone reloads / server restarts.
- Groups of 1�4 spawn with **cohesion** (hard ceiling of 10 in dresser).
- **Faction-aware** � biome-driven mix of Neutral (Dverger) / Bandit
  (ForestMonsters) / Cultist (Demon).
- **Levels / stars** mirror vanilla via `Character.SetLevel`.
- **Recruitment** uses the existing dialogue + currency flow.
- **Gear drops on death** via `WildCompanionLootOnDeath`.

## 2. Biome ? faction table (per user directive 2026-04)

All weights are integer, unnormalised (the dresser does its own weighted
pick).

| Biome        | Neutral | Bandit | Cultist | Gear Tier | Star 0 / 1 / 2 |
|--------------|---------|--------|---------|-----------|----------------|
| Meadows      | 100     | 0      | 0       | 0         | 92 / 7 / 1     |
| BlackForest  | 70      | 30     | 0       | 1         | 85 / 12 / 3    |
| Swamp        | 50      | 50     | 0       | 2         | 80 / 15 / 5    |
| Mountain     | 30      | 50     | 20      | 3         | 75 / 18 / 7    |
| Plains       | 20      | 60     | 20      | 4         | 70 / 22 / 8    |
| Mistlands    | 10      | 40     | 50      | 5         | 65 / 25 / 10   |
| **AshLands** | **5**   | 40     | 55      | 6         | 55 / 30 / 15   |
| **DeepNorth**| **5**   | 45     | 50      | 6 (fallback) | 55 / 30 / 15 |

The 5 % Neutral slice in AshLands + DeepNorth is the user-requested
"very small chance of recruitable veterans in the end-game biomes". Those
spawns still roll from the full neutral archetype pool, wear tier-6 gear,
and recruit through the normal dialogue / currency flow.

Any other biome (Ocean, River, None) falls through to the Meadows-equivalent
profile.

## 3. File inventory

## 1. Goals

- Wild companions appear in the world through **ExpandWorld**'s spawn YAML, NOT
  through a FiresRPGmaker-owned config file. Admins reference our companion
  prefab(s) by stable prefab name in EW's spawn tables exactly like any vanilla
  creature.
- Each wild companion is **persistent per ZDO**: once spawned, they keep the
  same archetype, gear, stats, stars, and name until killed or despawned by
  vanilla zone timeout. Walking away and coming back (even hours later, even
  after server restart) produces the *same individual* � same sword, same
  helmet, same archetype, same level � until the player recruits them or kills
  them.
- Groups of 1�4 spawn with **cohesion** (hard ceiling of 10 in the dresser, 4 by
  default via EW YAML): shared squad id, shared faction, rough AI coordination
  (at minimum: "stay near squad leader", "if leader is engaged, engage").
- **Faction-aware** from day one so bandit / hostile-companion archetypes can
  drop in later without refactoring. A wild Meadows Ranger is Neutral (Dverger
  faction); a Blackforest Bandit squad is Hostile (ForestMonsters faction).
- **Levels / stars** mirror vanilla � 0-star, 1-star, 2-star with the normal
  vanilla HP + damage multipliers, visual ring effect, and loot scaling.
- **Recruitment** uses the existing dialogue + currency flow on
  `CompanionController`. Interacting with a neutral wild companion opens a
  dialogue that gates on gold/coins/whatever; paying flips `isTamed=true` and
  binds `ownerPlayerId`.
- **Gear drops on death** via `CharacterDrop` � killing a Bandit Berserker has
  a real chance of dropping their sword, chest, etc. Killing a neutral
  companion is murder; loot reflects that (drop table still defined, but
  populated only with generic forageables).

## 2. Non-goals (v1)

- No custom `.cfg` files. ExpandWorld owns the spawn table. We expose prefabs.
- No custom zone-tile walker / density logic. Vanilla `SpawnSystem` handles it.
- No PvP/PvE toggles, no territory-faction reputation, no bounty system. Those
  stack on top of the faction primitive once it exists.
- No per-biome tier bakes in code (difficulty tuning lives in EW YAML).
- No squad "captain with better gear" variant � deferred until cohesion v1 is
  stable. Every squad member rolls independently for v1.

## 3. What's ours vs what's ExpandWorld's

| Concern | Owner |
|---|---|
| Where and when wild companions spawn | **ExpandWorld** (YAML) |
| Biome / time-of-day / group size / density caps | **ExpandWorld** |
| Prefab definition (Character, ZNetView, ZSyncAnimation, MonsterAI) | **FiresRPGmaker** (`CompanionPrefabManager`) |
| Archetype / gear / stats / stars assigned at spawn | **FiresRPGmaker** (new `WildCompanionDresser`) |
| Faction assignment & hostility | **FiresRPGmaker** (dresser reads a prefab-level faction tag) |
| Group cohesion (squad id + leader follow) | **FiresRPGmaker** (dresser + small addition to `CompanionAI`) |
| Persistence of dresser output across reloads | **FiresRPGmaker** � ZDO-stored rolled values + deterministic RNG seeded from ZDO UID |
| Recruitment flow | **FiresRPGmaker** existing dialogue system, with one guard added |

## 4. Prefab strategy � **decided**

All variants **clone from the single `CompanionNpc` asset-bundle prefab** at
runtime. We never ship multiple prefabs in the bundle; instead
`WildCompanionPrefabs.Register()` does `Object.Instantiate(basePrefab)` three
times (once per faction), renames each clone, attaches a `WildCompanionSeed`
MonoBehaviour tuned for that faction, and registers each with `ZNetScene`.

Stable prefab names exposed to ExpandWorld YAML:

- `CompanionNpc_Wild_Neutral` � neutral roamer, recruitable, any archetype
- `CompanionNpc_Wild_Bandit` � hostile-to-players, humanoid bandits
- `CompanionNpc_Wild_Cultist` � reserved for future biome-specific faction

Each clone carries a `WildCompanionSeed` component holding:

```
[faction] CompanionFaction     � Neutral | Bandit | Cultist
[allowedArchetypes] int bitmask over ArchetypeClass values
[allowedGearTiers]  int[]      � e.g. {0,1} for Meadows-appropriate tiers
[starWeights]       int[3]     � relative weights for 0 / 1 / 2 stars
[enableGroupCohesion] bool     � squad-leader follow behaviour
```

All the per-spawn randomness (archetype pick, gear roll, star roll) is read
from the seed + ZDO UID at spawn time. Because ExpandWorld places these prefabs
by name, switching out which one spawns in which biome is purely a YAML edit.

## 5. Per-spawn "dresser" flow

One static class, `WildCompanionDresser`, runs server-side on `ZNetView.Awake`
for any prefab that carries a `WildCompanionSeed` AND has `companion_id == 0`
on its ZDO (meaning "never dressed before").

```
if (zdo.GetLong("companion_id", 0L) != 0L) return;   // already dressed
if (!ZNet.instance.IsServer()) return;               // server authoritative

var seed = prefab.GetComponent<WildCompanionSeed>();
var rng  = new System.Random(HashCombine(zdo.m_uid));

// 1. Pick archetype from seed.allowedArchetypes
var archetype = WeightedPick(seed.allowedArchetypes, rng);

// 2. Pick stars from seed.starChanceWeights
var stars = WeightedPick(seed.starChanceWeights, rng);

// 3. Gear is rolled per slot at runtime by the instance's CompanionRandomLoadout
//    component: it builds biome-tier-filtered item pools
//    (GetTierFilteredItems / GetBiomeTier / GetItemTier) and equips each slot
//    directly on the live Humanoid. There is no static (archetype, tier) table.

// 4. Roll display name from biome-appropriate name pool
var name = CompanionNamePool.Roll(seed.faction, rng);

// 5. Persist everything to ZDO so the next load is deterministic
zdo.Set("companion_id", NextPersistedId());
zdo.Set("companion_archetype", (int)archetype);
zdo.Set("companion_stars", stars);
zdo.Set("companion_name", name);
zdo.Set("companion_faction", (int)seed.faction);
zdo.Set("companion_wild", true);          // flag for recruitment gate
zdo.Set("companion_squad_id", squadId);   // see �7 (group cohesion)
// equipped gear is persisted by CompanionRandomLoadout via its own ZDO keys

// 6. Apply live (calls existing CompanionController.ApplyFromZDO + level setter)
controller.LoadFromZDO();
character.SetLevel(1 + stars);           // vanilla star mechanic
character.m_faction = seed.faction.ToValheim();
```

A non-server peer seeing the companion just reads the already-written ZDO keys
via `CompanionController.LoadFromZDO`. No RPC traffic � the ZDO IS the
broadcast.

## 6. Gear roll (runtime, via CompanionRandomLoadout)

Wild gear is rolled at runtime by `CompanionRandomLoadout` (a `MonoBehaviour`
on the spawned companion), **not** by a static table. The planned
`CompanionGearTables` was never wired into the runtime path and has been removed.

In the shipped code there is **no** `(ArchetypeClass, tier)` constant table: tier is resolved at runtime, where `GetBiomeTier(biome)` caps the allowed tier (Meadows 1 ... AshLands/DeepNorth 7) and `GetItemTier(prefab)` infers each item's tier from its material/name keywords.
No external config. Changing tables requires a DLL rebuild, which is fine for
v1 � EW doesn't care what gear they wear, just that they spawn.

```
Tier 0 (Meadows): wood, flint, leather
Tier 1 (BlackForest): bronze, troll hide
Tier 2 (Swamp): iron, root harness
Tier 3 (Mountain): silver, wolf / fenris
Tier 4 (Plains): padded, blackmetal, needle
Tier 5 (Mistlands): carapace, eitr-weave
Tier 6 (AshLands): flametal, ashwood, askvin
Tier 7 (DeepNorth): reserved
```

Ranger/Mage/Healer get tier-appropriate ranged or staves; Tank/Paladin get
shields; Berserker gets two-handers. Re-uses existing archetype weapon-type
requirements from `ArchetypeDefinition.RequiredItemTypes`.

## 7. Group cohesion � **decided**

Hard ceiling of **10** group members enforced in the dresser (defence against
a mistuned EW YAML). Default group size stays at **4** � ExpandWorld YAML sets
the true cap within that ceiling.

ExpandWorld spawns a group via vanilla `SpawnSystem.m_groupSizeMin/Max` � all
group members arrive in the same frame, within `m_groupRadius`. The dresser
detects group membership by:

1. On its own `Awake`, scan a small radius for other freshly-spawned (i.e.
   `companion_squad_id == 0`) seed prefabs.
2. If any are found, elect the lowest-ZDO-UID member as **squad leader**,
   assign everyone a shared `companion_squad_id = leaderZdoUid`, and store
   each member's role on their ZDO.
3. The leader's archetype biases the squad composition: e.g. if leader rolled
   Tank, the dresser re-rolls any Mage/Healer to a re-weighted table favouring
   front-line types. Gear stays independent per-member.

`CompanionAI` gets a small extension: if `companion_squad_id != 0` AND
`companion_wild == true` AND not recruited, the AI targets the squad leader as
its follow anchor instead of `null`. Leader's own follow anchor is `null` (it
roams as vanilla creatures do).

## 8. Faction model � **decided (vanilla factions, Dverger analogue)**

Valheim's `Character.Faction` enum (confirmed from decompile):

```
Players, AnimalsVeg, ForestMonsters, Undead, Demon, MountainMonsters,
SeaMonsters, PlainsMonsters, Boss, MistlandsMonsters, Dverger,
PlayerSpawned, TrainingDummy
```

Our `CompanionFaction` enum maps directly:

```
CompanionFaction.Neutral  ? Character.Faction.Dverger
                            (non-aggressive, same-faction with player allies
                            once recruited; Dverger is the closest vanilla
                            analogue)

CompanionFaction.Bandit   ? Character.Faction.ForestMonsters
                            (hostile to Players and Dverger; same-faction
                            with themselves so squads don't infight)

CompanionFaction.Cultist  ? Character.Faction.Demon
                            (reserved � Mistlands / Ashlands later)
```

On recruitment (currency paid, `isTamed=true`, `ownerPlayerId=X`):

```
character.m_faction = Character.Faction.Players;
monsterAI.m_alertRange = 0;
monsterAI.m_attackPlayerObjects = false;
zdo.Set("companion_wild", false);
```

Bandit squads never gate on recruitment dialogue � they attack on sight and
drop loot on death via vanilla `CharacterDrop` (we define a `CharacterDrop`
config per faction prefab in the prefab setup, NOT in ExpandWorld). Bandits
inherit vanilla zone-timeout despawn (no custom logic needed).

## 9. Levels / stars

Pure vanilla piggyback:

- `character.SetLevel(1 + stars)` � vanilla handles HP/damage multipliers
  (x1.5 per level by default) and the visual ring shader
- Star roll happens in the dresser, is persisted, is restored on load
- Loot drops scale automatically via vanilla's `CharacterDrop.m_oneOfEach`

No new stat math. The `ArchetypeDefinition` multipliers stack *on top of*
vanilla stars, so a 2-star Berserker Meadows companion is legitimately
terrifying. Dresser prints a server-log line when spawning 2-stars so admins
can tune via EW YAML density.

## 10. Recruitment gate

`CompanionController.TryRecruit(playerId, currencyAmount)` already exists (per
grep of our codebase). One new check in the recruitment dialogue precondition:

```
if (zdo.GetBool("companion_wild", false) &&
    zdo.GetInt("companion_faction", 0) != (int)CompanionFaction.Neutral)
    return RecruitmentResult.HostileFactionRefused;
```

Bandits/Cultists reject recruitment outright. Neutrals flow through the
existing dialogue ? payment ? tame pipeline.

## 11. Design decisions � **confirmed**

1. **Prefab strategy:** separate per-faction registrations (`CompanionNpc_Wild_Neutral`,
   `CompanionNpc_Wild_Bandit`, `CompanionNpc_Wild_Cultist`), **all cloned at
   runtime from the single `CompanionNpc` asset-bundle prefab**. Differences
   live in the `WildCompanionSeed` component attached to each clone.
2. **Gear drops on death:** yes, via `CharacterDrop` on the clone's Humanoid.
3. **Name pools:** hand-authored, unique per faction, 50-100 names. C# arrays,
   no config file. ZDO-seed picks deterministically.
4. **Group size:** hard ceiling of 10 in the dresser; default 4 via EW YAML.
5. **Bandit despawn:** inherits vanilla zone-timeout behaviour. No custom
   logic.

## 12. Decompiled Valheim references used

All dropped into `FiresNPCs/Debug/DebugRefs/`:

- `SpawnSystemRef.md` � `SpawnData`, `m_spawners`, group-spawn logic, `Spawn`
  flow that sets level / hunt-player / despawn-in-day.
- `CharacterRefs.md` � `m_faction`, `SetLevel`, `SetTamed`, full `Faction`
  enum (Players, AnimalsVeg, ForestMonsters, Undead, Demon, MountainMonsters,
  SeaMonsters, PlainsMonsters, Boss, MistlandsMonsters, Dverger,
  PlayerSpawned, TrainingDummy).
- `MonsterAIRef.md` � `m_alertRange`, `m_follow`, `m_attackPlayerObjects`,
  follow target plumbing.
- `HumaniodRef.md` � `EquipItem`, `m_defaultItems`, `m_randomWeapons`.
- `CharacterDropRef.md` � drop table structure for gear-on-death.
- `ZoneSystemRef.md` � zone-load spawn interaction.
- `ZDORef.md` � `GetLong`/`GetBool`/`Set` signatures for the dresser.
- `ZnetSceneRef.md` � `m_prefabs` + `m_namedPrefabs` for registering our three
  clones.

## 13. Implementation sequencing

Three reviewable PRs; each one builds clean before proceeding.

- **PR 1 � Prefab skeletons.** ? **Landed, builds clean.**
  - `Modules/Companions/WildSpawn/CompanionFaction.cs` � logical faction enum
    + `ToValheim()` mapping to `Character.Faction.{Dverger,ForestMonsters,Demon}`.
  - `Modules/Companions/WildSpawn/WildCompanionSeed.cs` � `MonoBehaviour` component
    with `Faction`, `AllowedArchetypesMask`, `AllowedGearTiers`, `StarWeights`,
    `EnableGroupCohesion`, `HardMaxGroupSize`.
  - `Modules/Companions/WildSpawn/WildCompanionPrefabs.cs` � clones the
    asset-bundle `CompanionNpc` into three named variants
    (`CompanionNpc_Wild_Neutral`, `_Bandit`, `_Cultist`), attaches a
    faction-configured `WildCompanionSeed` to each, and registers them in
    `ZNetScene.m_prefabs` + reflection into `m_namedPrefabs` (same path
    `CompanionPrefabManager.RegisterWithZNetScene` uses for the base prefab).
    Idempotent; safe on reconnects.
  - Hooked in `Ascend.cs` immediately after `CompanionPrefabManager.RegisterWithZNetScene()`
    so the base is resolvable before we clone.
  - **At this point:** ExpandWorld YAML can list any of the three variant names
    in a spawn table. They'll spawn as undecorated companions (no archetype,
    no gear, no stars) because the dresser isn't wired yet � that's PR 2's job.
    A spawned instance IS a live `CompanionController` though; interactions /
    dialogue still work.

- **PR 2 � Dresser.** ? **Landed, builds clean.**
  - `Modules/Companions/WildSpawn/CompanionGearTables.cs` (since removed - see correction note) � `(ArchetypeClass, tier)`
    keyed hard-coded pool of slot ? prefab-name candidates; `Roll(archetype,
    allowedTiers, rng)` returns a deterministic loadout. Tier 0 populated for
    all 8 archetypes; higher tiers intentionally empty until content ships.
  - `Modules/Companions/WildSpawn/CompanionNamePool.cs` � three hand-authored
    ~50-name arrays, one per faction (Norse-pastoral / guttural / occult).
    `Roll(faction, rng)` picks deterministically.
  - `Modules/Companions/WildSpawn/WildCompanionDresser.cs` � `MonoBehaviour`
    that runs **server-only** in a one-frame-delayed coroutine from `Start`.
    Checks `ZDO_DRESSED_FLAG`; if unset, rolls archetype / gear / stars / name
    with `System.Random(zdo.m_uid.GetHashCode() ^ 0x7F51_1D23)`, applies to
    `CompanionController` / `ArchetypeController` / `CompanionInventory` /
    `Character.SetLevel`, then calls `controller.SaveToZDO()` to flush.
    Subsequent spawns of the same ZDO no-op (`CompanionController.LoadFromZDO`
    handles replay from ZDO state).
  - `WildCompanionPrefabs.RegisterVariant` now also:
    - Attaches a `WildCompanionDresser` to each clone.
    - Sets `Character.m_faction` on the clone prefab so every spawned instance
      starts with the correct vanilla faction (Dverger / ForestMonsters / Demon)
      without needing per-instance faction writes on reload.
  - **At this point:** a `CompanionNpc_Wild_Neutral` spawned by EW arrives
    with an archetype, gear, optional stars, a Norse-pastoral name, and the
    Dverger faction � non-aggressive, recruitable through existing dialogue.
    Walking away and coming back produces the same individual (same gear,
    same name) because all choices are persisted in ZDO.

- **PR 3 � Cohesion + Bandits.** ? **Landed, builds clean.**
  - `Modules/Companions/WildSpawn/WildCompanionSquad.cs` � two pieces:
    1. Static `ElectAndAssign(dresser, seed)` that every dresser calls once
       at the end of its dress flow. Scans `OverlapSphereNonAlloc` within
       `CohesionRadius = 15f` for same-faction `WildCompanionDresser`s that
       have already written `ZDO_DRESSED_FLAG`, and picks the lowest hashed
       ZDO UID as leader. Writes `companion_squad_id` (leader's hash) and
       `companion_squad_role` (1 = leader, 0 = member) to each participant's
       ZDO. Honours `seed.HardMaxGroupSize` (capped at 10 per PLAN �7).
    2. `WildCompanionSquadFollower` MonoBehaviour (server-only) that ticks
       every 1.25s: reads `companion_squad_id`, skips if solo or leader,
       otherwise resolves the leader GameObject (cached) and sets
       `CompanionIdleBehavior.SetHomePosition(leaderPos)` so the existing
       idle pathfinder walks the member back toward the leader when it
       strays. Same mechanism tamed "stay here" companions use � keeps
       motion consistent with the rest of the companion system.
  - `Modules/Companions/WildSpawn/WildCompanionLootOnDeath.cs` � new
    MonoBehaviour attached to every wild clone. Subscribes to
    `Character.m_onDeath`. Reads each equipment slot via
    `CompanionInventory.GetEquipmentPrefabNamePublic(slot)`; for Bandit /
    Cultist (never Neutral) rolls an independent drop per slot:
    - Weapons 40%, armor 25% base
    - Multiplied by `1 + stars` (1-star = 2x, 2-star = 3x)
    - Deterministic RNG seeded from `zdo.m_uid.GetHashCode() ^ 0x49E457A3`
      so save-scum reloads produce the same loot (no kill-reload cheese).
    Owner-only to avoid double-drops across peers.
  - `WildCompanionPrefabs.RegisterVariant` now also attaches
    `WildCompanionSquadFollower` and `WildCompanionLootOnDeath` to each
    clone alongside the existing dresser.
  - `CompanionController.TameCompanion(Player)` now has a faction gate at
    the top: reads `WildCompanionDresser.ZDO_FACTION` from the instance ZDO
    and refuses recruitment if the value is non-zero (`Bandit` or `Cultist`
    from `CompanionFaction`). Neutrals (0) fall through unchanged; non-wild
    companions (no ZDO key at all) also fall through unchanged, so admin
    spawns and placed NPCs are unaffected.

## 14. v1 done � possible follow-ups

Not scoped into any of the three PRs; captured here so they don't get lost:

- **Higher-tier gear tables.** Tier 1�7 entries in `CompanionGearTables` (removed; runtime gear is biome-tier-filtered by `CompanionRandomLoadout`)
  are intentionally empty. Fill as content ships.
- **Custom `CharacterDrop`** instead of our `WildCompanionLootOnDeath`
  component, once we're confident about drop rates. Would let vanilla
  handle world-save integration if we ever care about that.
- **Squad AI cohesion** beyond just "follow the leader" � shared target
  focus so a 4-bandit squad converges on the player instead of spreading
  fire.
- **Faction reputation** � killing N bandits biases future Meadows
  spawns, etc. Needs a new persistence layer.
- **ArchetypeDefinition stat multipliers** applied on top of vanilla
  stars so a 2-star Berserker Meadows companion hits the intended
  terrifying threshold (currently the dresser sets `SetLevel(1+stars)`
  but doesn't stack archetype multipliers on top).

Doc ends.


## 2. Non-goals (v1)

- No custom `.cfg` files. ExpandWorld owns the spawn table. We expose prefabs.
- No custom zone-tile walker / density logic. Vanilla `SpawnSystem` handles it.
- No PvP/PvE toggles, no territory-faction reputation, no bounty system. Those
  stack on top of the faction primitive once it exists.
- No per-biome tier bakes in code (difficulty tuning lives in EW YAML).
- No squad "captain with better gear" variant � deferred until cohesion v1 is
  stable. Every squad member rolls independently for v1.

## 3. What's ours vs what's ExpandWorld's

| Concern | Owner |
|---|---|
| Where and when wild companions spawn | **ExpandWorld** (YAML) |
| Biome / time-of-day / group size / density caps | **ExpandWorld** |
| Prefab definition (Character, ZNetView, ZSyncAnimation, MonsterAI) | **FiresRPGmaker** (`CompanionPrefabManager`) |
| Archetype / gear / stats / stars assigned at spawn | **FiresRPGmaker** (new `WildCompanionDresser`) |
| Faction assignment & hostility | **FiresRPGmaker** (dresser reads a prefab-level faction tag) |
| Group cohesion (squad id + leader follow) | **FiresRPGmaker** (dresser + small addition to `CompanionAI`) |
| Persistence of dresser output across reloads | **FiresRPGmaker** � ZDO-stored rolled values + deterministic RNG seeded from ZDO UID |
| Recruitment flow | **FiresRPGmaker** existing dialogue system, with one guard added |

## 4. Prefab strategy

We ship **one registered prefab per faction archetype family** so ExpandWorld
admins have clear handles:

- `CompanionNpc_Wild_Neutral` � neutral roamer, recruitable, any archetype
- `CompanionNpc_Wild_Bandit` � hostile-to-players, humanoid bandits
- `CompanionNpc_Wild_Cultist` � reserved for future biome-specific faction

Each is a ZNetScene-registered clone of the existing `CompanionNpc` prefab with
the only prefab-level difference being a serialized **`WildCompanionSeed`**
component holding:

```
[faction] Neutral | Bandit | Cultist
[allowedArchetypes] flags enum � ArchetypeClass bitmask
[allowedGearTiers] int[] � e.g. {0,1} for Meadows-appropriate tiers
[starChanceWeights] int[3] � relative weights for 0/1/2 stars
[groupCohesion] bool � should squad-leader logic kick in
```

All the per-spawn randomness (archetype pick, gear roll, star roll) is read
from the seed + ZDO UID at spawn time. Because ExpandWorld places these prefabs
by name, switching out which one spawns in which biome is purely a YAML edit.

## 5. Per-spawn "dresser" flow

One static class, `WildCompanionDresser`, runs server-side on `ZNetView.Awake`
for any prefab that carries a `WildCompanionSeed` AND has `companion_id == 0`
on its ZDO (meaning "never dressed before").

```
if (zdo.GetLong("companion_id", 0L) != 0L) return;   // already dressed
if (!ZNet.instance.IsServer()) return;               // server authoritative

var seed = prefab.GetComponent<WildCompanionSeed>();
var rng  = new System.Random(HashCombine(zdo.m_uid));

// 1. Pick archetype from seed.allowedArchetypes
var archetype = WeightedPick(seed.allowedArchetypes, rng);

// 2. Pick stars from seed.starChanceWeights
var stars = WeightedPick(seed.starChanceWeights, rng);

// 3. Gear is rolled per slot at runtime by the instance's CompanionRandomLoadout
//    component: it builds biome-tier-filtered item pools
//    (GetTierFilteredItems / GetBiomeTier / GetItemTier) and equips each slot
//    directly on the live Humanoid. There is no static (archetype, tier) table.

// 4. Roll display name from biome-appropriate name pool
var name = CompanionNamePool.Roll(seed.faction, rng);

// 5. Persist everything to ZDO so the next load is deterministic
zdo.Set("companion_id", NextPersistedId());
zdo.Set("companion_archetype", (int)archetype);
zdo.Set("companion_stars", stars);
zdo.Set("companion_name", name);
zdo.Set("companion_faction", (int)seed.faction);
zdo.Set("companion_wild", true);          // flag for recruitment gate
zdo.Set("companion_squad_id", squadId);   // see �7 (group cohesion)
// equipped gear is persisted by CompanionRandomLoadout via its own ZDO keys

// 6. Apply live (calls existing CompanionController.ApplyFromZDO + level setter)
controller.LoadFromZDO();
character.SetLevel(1 + stars);           // vanilla star mechanic
character.m_faction = seed.faction.ToValheim();
```

A non-server peer seeing the companion just reads the already-written ZDO keys
via `CompanionController.LoadFromZDO`. No RPC traffic � the ZDO IS the
broadcast.

## 6. Gear roll (runtime, via CompanionRandomLoadout)

Wild gear is rolled at runtime by `CompanionRandomLoadout` (a `MonoBehaviour`
on the spawned companion), **not** by a static table. The planned
`CompanionGearTables` was never wired into the runtime path and has been removed.

In the shipped code there is **no** `(ArchetypeClass, tier)` constant table: tier is resolved at runtime, where `GetBiomeTier(biome)` caps the allowed tier (Meadows 1 ... AshLands/DeepNorth 7) and `GetItemTier(prefab)` infers each item's tier from its material/name keywords.
No external config. Changing tables requires a DLL rebuild, which is fine for
v1 � EW doesn't care what gear they wear, just that they spawn.

```
Tier 0 (Meadows): wood, flint, leather
Tier 1 (BlackForest): bronze, troll hide
Tier 2 (Swamp): iron, root harness
Tier 3 (Mountain): silver, wolf / fenris
Tier 4 (Plains): padded, blackmetal, needle
Tier 5 (Mistlands): carapace, eitr-weave
Tier 6 (AshLands): flametal, ashwood, askvin
Tier 7 (DeepNorth): reserved
```

Ranger/Mage/Healer get tier-appropriate ranged or staves; Tank/Paladin get
shields; Berserker gets two-handers. Re-uses existing archetype weapon-type
requirements from `ArchetypeDefinition.RequiredItemTypes`.

## 7. Group cohesion

ExpandWorld spawns a group via vanilla `SpawnSystem.m_groupSizeMin/Max` � all
group members arrive in the same frame, within `m_groupRadius`. The dresser
detects group membership by:

1. On its own `Awake`, scan a small radius for other freshly-spawned (i.e.
   `companion_squad_id == 0`) seed prefabs.
2. If any are found, elect the lowest-ZDO-UID member as **squad leader**,
   assign everyone a shared `companion_squad_id = leaderZdoUid`, and store
   each member's role on their ZDO.
3. The leader's archetype biases the squad composition: e.g. if leader rolled
   Tank, the dresser re-rolls any Mage/Healer to a re-weighted table favoring
   front-line types. Gear stays independent per-member.

`CompanionAI` gets a small extension: if `companion_squad_id != 0` AND
`companion_wild == true` AND not recruited, the AI targets the squad leader as
its follow anchor instead of `null`. Leader's own follow anchor is `null` (it
roams as vanilla creatures do).

## 8. Faction model

Valheim has `Character.m_faction` (enum: Players, AnimalsVeg, ForestMonsters,
Undead, Demon, MountainMonsters, SeaMonsters, PlainsMonsters,
MistlandsMonsters, Boss, Dverger). Same-faction members don't aggro each
other.

We introduce our own `CompanionFaction` enum and a tiny mapping layer:

```
CompanionFaction.Neutral  ? Character.Faction.Dverger
                            (non-aggressive, same-faction with player allies
                            once recruited; Dverger is the closest vanilla
                            analogue)

CompanionFaction.Bandit   ? Character.Faction.ForestMonsters
                            (hostile to Players, hostile to Dverger, same-
                            faction with themselves so squads don't infight)

CompanionFaction.Cultist  ? Character.Faction.Demon
                            (reserved � Mistlands / Ashlands later)
```

On recruitment (currency paid, `isTamed=true`, `ownerPlayerId=X`):

```
character.m_faction = Character.Faction.Players;
monsterAI.m_alertRange = 0;
monsterAI.m_attackPlayerObjects = false;
zdo.Set("companion_wild", false);
```

Bandit squads never gate on recruitment dialogue � they attack on sight and
drop loot on death via vanilla `CharacterDrop` (we define a `CharacterDrop`
config per faction prefab in the prefab setup, NOT in ExpandWorld).

## 9. Levels / stars

Pure vanilla piggyback:

- `character.SetLevel(1 + stars)` � vanilla handles HP/damage multipliers
  (x1.5 per level by default) and the visual ring shader
- Star roll happens in the dresser, is persisted, is restored on load
- Loot drops scale automatically via vanilla's `CharacterDrop.m_oneOfEach`

No new stat math. The `ArchetypeDefinition` multipliers stack *on top of*
vanilla stars, so a 2-star Berserker Meadows companion is legitimately
terrifying. Dresser prints a server-log line when spawning 2-stars so admins
can tune via EW YAML density.

## 10. Recruitment gate

`CompanionController.TryRecruit(playerId, currencyAmount)` already exists (per
grep of our codebase). One new check in the recruitment dialogue precondition:

```
if (zdo.GetBool("companion_wild", false) &&
    zdo.GetInt("companion_faction", 0) != (int)CompanionFaction.Neutral)
    return RecruitmentResult.HostileFactionRefused;
```

Bandits/Cultists reject recruitment outright. Neutrals flow through the
existing dialogue ? payment ? tame pipeline.

## 11. Open design questions for user

Before writing code, please confirm:

1. **One prefab per faction OR one prefab for all with a faction ZDO field
   set by ExpandWorld?** The per-faction prefab approach (�4) is simpler and
   matches how vanilla handles e.g. `Goblin` vs `GoblinBrute` vs `GoblinShaman`
   as separate prefabs. I'd recommend it.

2. **Should wild companions drop their equipped gear on death?** Vanilla
   humanoid NPCs don't � only their `CharacterDrop` list drops. Recommend:
   *yes, via `CharacterDrop`*, so killing a wild Bandit Berserker yields a
   small chance of their actual weapon. Killing a neutral Ranger yields
   nothing (they're non-hostile � this is murder, not a kill).

3. **Name pools � procedural (`"Ragnar" + "the Bold"`) or hand-authored
   lists per faction?** I'd recommend a short hand-authored list per faction
   (50-100 names) with the ZDO seed picking deterministically. No config file;
   just a C# array.

4. **Group size cap enforcement** � ExpandWorld YAML controls it, but do you
   want a hard ceiling of 4 enforced in the dresser regardless? (Defense
   against a mistuned YAML spawning 20-man squads.) Recommend yes.

5. **Bandits despawning on zone unload** � vanilla creatures despawn on zone
   timeout if no player is nearby. Bandits inherit that. OK?

## 12. Decompiled Valheim code I need before implementing

To make this bulletproof rather than guessing at signatures, paste the class
bodies of:

1. **`SpawnSystem.cs`** � specifically `SpawnData`, `m_spawners`, the
   `Spawn(...)` inner helper, and the group-spawn logic. ExpandWorld hooks
   here; we want to know exactly what fields we'd be piggybacking on.

2. **`Character.cs`** � `m_faction`, `m_level`, `SetLevel`, `SetTamed`, the
   `Faction` enum with all values. Need to see which faction values exist in
   the player's Valheim build (mods sometimes add).

3. **`MonsterAI.cs`** � `m_faction`, `m_alertRange`, `m_attackPlayerObjects`,
   `SetFollowTarget`, `m_follow` and how it interacts with `m_target`. The
   squad-leader follow (�7) sits on top of this.

4. **`Humanoid.cs`** � `EquipItem`, `GetCurrentWeapon`, `m_defaultItems`,
   `m_randomWeapons`. We want to understand how vanilla humanoid creatures
   (goblins, fulings) get their starting loadout so we can hook the dresser
   into the same codepath rather than fighting it.

5. **`CharacterDrop.cs`** � full class. For the "bandit drops their sword"
   design (�open-q-2).

6. **`ZoneSystem.cs`** � only the portion that shows how `SpawnMode` /
   `SpawnLocation` interacts with `SpawnSystem` at zone-load. Mostly for sanity
   checking that ExpandWorld's injection points don't clash with anything we'd
   subscribe to.

7. **`ZDO.cs`** � signature of `GetLong` / `GetBool` / `Set` variants we'll
   use heavily in the dresser. Need to confirm that `long` keys work (the
   existing codebase mostly uses `int`-hash keys with string form; the dresser
   should match whichever convention the rest of the companion system uses �
   please also point me at one existing example so I match style).

8. **`ZNetScene.cs`** � just `m_prefabs` registration. To confirm the right
   path for registering our new per-faction prefab clones.

If any of those are easier to share as decompiled file drops in a `Refs/`
folder (same pattern as `HEIGHTMAP_OVERRIDE_REFERENCE.md`, `WAP_DECOMPILED_REFERENCE.md`),
that works too � just let me know where they land and I'll read from there.

## 13. Implementation sequencing (once design is locked)

Three reviewable PRs, same staging as always:

- **PR 1** � Prefab skeletons + `WildCompanionSeed` component + ZNetScene
  registration + faction enum + ZDO plumbing. No dresser yet; prefabs spawn
  as undecorated companions. Confirms ExpandWorld can reference them.

- **PR 2** � Dresser: archetype/gear/stars/name rolls, ZDO persistence,
  deterministic seed, live apply via existing `CompanionController.LoadFromZDO`.
  Single faction (Neutral) only so we can verify without hostility confusing
  test signals.

- **PR 3** � Group cohesion (squad id + leader follow) + Bandit faction +
  `CharacterDrop` loot + recruitment gate. Full v1.

Doc ends.
