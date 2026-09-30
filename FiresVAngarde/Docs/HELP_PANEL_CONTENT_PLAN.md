# Verdant's Ascent — Help Panel Content Plan

This document outlines every section of the in-game help panel and the specific content
that needs to be written for each. The panel serves as a complete user manual accessible
without ever leaving the game.

---

## Sidebar Sections

```
1.  Getting Started
2.  NPC Types & Setup
3.  Quest System
4.  Quest Config Format
5.  Quest Journal
6.  Dialogue System
7.  Dialogue Config Format
8.  Companions Overview
9.  Companion Commands
10. Companion Archetypes
11. Companion Idle Behaviors
12. Companion Combat & Roles
13. Territories
14. Territory Config Format
15. Globe HUD Overlay
16. Vault & Bank System
17. Underwater Bubble
18. Custom Terrain & Water
19. UI Customization
20. BepInEx Config Reference
21. File & Folder Structure
22. Admin Guide
23. Keyboard Shortcuts
24. Troubleshooting
```

---

## 1. Getting Started

**Purpose**: First-time user onboarding. What this mod does, what it adds, how to begin.

**Content needed**:
- What Verdant's Ascent is (RPG framework for Valheim — NPCs, quests, companions, territories)
- First-time setup: install via r2modman, mod creates default config files on first run
- Folder structure overview: `BepInEx/config/FiresRPGmaker/` with subfolders
- Quick-start checklist:
  1. Place an NPC using the companion system or spawn command
  2. Assign an NPC type (Quest, Info, Dialogue, Trader) via admin panel
  3. Create a quest profile and assign it to the NPC
  4. Interact with the NPC to see the quest screen
- Mention the admin panel (shift+click or admin access on NPC)
- Link to other sections for deep dives

---

## 2. NPC Types & Setup

**Purpose**: Explain the four NPC types and how to configure each one.

**Content needed**:
- **NPC Types**:
  - `QuestNpc` — Offers quests from a quest profile. Player-facing quest interaction screen.
  - `InfoNpc` — Displays an info panel with header, optional image, and rich text body.
  - `DialogueNpc` — Runs branching dialogue trees with conditions, commands, and icons.
  - `Trader` — (Future) Trading interface with buy/sell inventories.
- **NPC Controller fields** (what admins set via the admin panel):
  - `npcProfileName` — Display name above the NPC
  - `displayNameOverride` — Optional override for the floating name
  - `dialogueProfile` — Name of the dialogue .cfg profile
  - `questProfile` — Name of the quest profile
  - `infoProfile` — Name of the info profile
  - `traderProfile` — Name of the trader profile
  - `modelOverride` — Swap the NPC's visual model
- **Admin Panel**: How to open it, what each tab does, how to assign profiles
- **Spawning NPCs**: Console commands, placement, persistence

---

## 3. Quest System

**Purpose**: How the quest system works from the player's perspective.

**Content needed**:
- Overview: NPC-based quest givers, quest journal tracking, auto-complete support
- Quest lifecycle: Available ? Accepted ? In Progress ? Completed ? Cooldown
- Quest types: Kill (track creature kills), Collect (future), Explore (future)
- Quest objectives: Target prefab + amount, optional min level filter
- Quest rewards: Items (prefab + amount), Skill Experience (skill + amount)
- Quest requirements: `HasCompletedQuest`, `HasItem`, skill checks
- Cooldowns: Per-quest cooldown in days, repeatable quests
- Time limits: Optional countdown timer
- Auto-complete: Quest completes automatically when objectives are met
- The quest interaction screen: list on left, details on right, Accept/Cancel buttons
- Quest status indicators in the list (Active, Completed, On Cooldown, Missing Requirements)

---

## 4. Quest Config Format

**Purpose**: Complete reference for writing quest .cfg files.

**Content needed**:
- File location: `BepInEx/config/FiresRPGmaker/Quests/`
- File naming: Any `*.cfg` file, can have multiple quests per file
- **Bracketed ID format** (primary):
  ```
  [QuestId]
  QuestType = Kill
  Name = Display Name
  Description = Quest description text
  QuestTarget = Boar, 10
  QuestReward = Coins, 50 | SkillToken, 1
  CooldownDays = 7
  TimeLimitMinutes = 0
  Requirement = HasCompletedQuest, OtherQuestId
  Autocomplete = true
  ```
- **Legacy line-based format** (also supported):
  ```
  [QuestId=autocomplete]
  Kill
  Display Name
  Description text
  Boar, 10
  Skill_Exp: Clubs, 15 | Item: Coins, 50
  7
  None
  ```
- Quest target format: `PrefabName, Amount` or `PrefabName, Amount, MinLevel`
- Quest target name filter: `QuestTargetName = Greydwarf Brute` (display name override)
- Reward format: `Item: PrefabName, Amount` or `Skill_Exp: SkillName, Amount`
- Pipe-separated multiple rewards: `Coins, 50 | Iron, 10 | Skill_Exp: Swords, 15`
- Requirement types: `HasCompletedQuest, QuestId` | `HasItem, PrefabName, Amount`
- **Quest Profiles** (assigns quests to NPCs):
  - File location: `BepInEx/config/FiresRPGmaker/QuestProfiles/`
  - Format:
    ```
    [ProfileName]
    Quests = QuestId1, QuestId2, QuestId3
    ```
- Common mistakes and how to fix them

---

## 5. Quest Journal

**Purpose**: How the quest journal UI works.

**Content needed**:
- Default hotkey: `J` (configurable in BepInEx config)
- Layout: Active quests list, quest details, progress tracking
- Quest progress: Shows current/target for each objective
- Status colors: Yellow = In Progress, Green = Complete, Red = Failed/Expired
- Abandon quest option
- How quest progress is tracked (kill tracking via Harmony patches)

---

## 6. Dialogue System

**Purpose**: How dialogue trees work from the player's perspective.

**Content needed**:
- Overview: Branching conversations with NPC, condition-gated options
- Dialogue UI: NPC name, dialogue text, numbered option buttons
- Option features: Icons, conditions (greyed-out vs hidden), keyboard shortcuts (1-9)
- Commands: Dialogues can execute commands (give items, start quests, teleport, etc.)
- Navigation: Options can transition to other dialogue nodes
- Back button: Navigate history with Backspace
- ESC to close
- Icon display: Item/creature icons on option buttons

---

## 7. Dialogue Config Format

**Purpose**: Complete reference for writing dialogue .cfg files.

**Content needed**:
- File location: `BepInEx/config/FiresRPGmaker/Dialogues/`
- Marketplace-compatible format
- **Node format**:
  ```
  [unique_dialogue_id]
  Dialogue text shown to the player
  Text: Option label | Transition: next_node_id
  Text: Another option | Command: GiveItem,SwordIron,1 | Condition: HasItem,Gold,10
  ```
- **Option fields** (pipe-separated key:value pairs):
  - `Text:` — Display text for the option button
  - `Transition:` — UID of the next dialogue node
  - `RandomTransition:` — Comma-separated UIDs, picks one randomly
  - `Command:` — Command(s) to execute when clicked
  - `RandomCommand:` — `weight,Command` for weighted random commands
  - `Condition:` — Condition(s) that must pass for option to be available
  - `Icon:` — Prefab name to show as icon on the option button
  - `AlwaysVisible:` — Show greyed-out when condition fails (true/false)
  - `OverrideError:` — Custom error message when condition fails
  - `Color:` — R,G,B byte values for option text color
- **Conditions reference**:
  - `HasItem,PrefabName,Amount` | `NotHasItem,PrefabName`
  - `HasQuest,QuestId` | `NotHasQuest,QuestId`
  - `HasCompletedQuest,QuestId`
  - `SkillMore,SkillName,Level` | `SkillLess,SkillName,Level`
  - `GlobalKey,KeyName` | `NotGlobalKey,KeyName`
  - `IsAdmin` | `IsNotAdmin`
  - `Biome,BiomeName`
  - Multiple conditions: separate with commas (AND logic)
  - OR logic: separate groups with `||`
- **Commands reference**:
  - `GiveItem,PrefabName,Amount` | `RemoveItem,PrefabName,Amount`
  - `GiveQuest,QuestId` | `CompleteQuest,QuestId`
  - `Teleport,X,Y,Z` | `TeleportToPlayer,PlayerName`
  - `Spawn,PrefabName,Amount`
  - `SetGlobalKey,KeyName` | `RemoveGlobalKey,KeyName`
  - `Heal` | `Damage,Amount`
  - `AddPin,Name,X,Y,Z`
  - `PlaySFX,ClipName` | `ShowMessage,Text`
  - `@command` prefix for auto-execute (no button shown)
- **Dialogue profiles** (assign to NPCs):
  - Set `dialogueProfile` on the NPC to the UID of the root dialogue node

---

## 8. Companions Overview

**Purpose**: What companions are and how they work at a high level.

**Content needed**:
- Overview: Tameable creatures that follow you, fight alongside you, and perform tasks
- How to get a companion: Tame a creature using vanilla taming, then command it
- Companion modes: Follow, Stay, Patrol (future)
- Health/stamina tracking: Billboard bars above head, group HUD on screen
- Companion persistence: Saved via ZDO, survives logout/restart
- Companion limit: Configurable, performance considerations
- The Group HUD: Right-side panel showing all companion status bars
- Billboard health bar: Floating name + HP bar above companion's head

---

## 9. Companion Commands

**Purpose**: How to give commands to companions.

**Content needed**:
- Command system: Chat-based commands or radial menu (future)
- Available commands and how they work
- Follow/Stay toggle
- Idle wander radius (configurable per-NPC and globally)
- Return home behavior and radius
- How companions respond to player distance

---

## 10. Companion Archetypes

**Purpose**: The RPG class system for companions.

**Content needed**:
- Overview: Companions have RPG archetypes (Tank, Healer, Mage, Ranger, Rogue, Berserker, Monk, Paladin)
- Hybrid archetypes: Combinations like Crusader (Tank+Paladin), Juggernaut (Tank+Berserker), etc.
- Archetype abilities: Each class has unique skills (Novice ? Apprentice ? Expert ? Master ? Ultimate)
- Ability unlock system: Leveling and progression
- Skill decision system: AI chooses abilities based on situation
- Group roles: Tank, DPS, Healer, Support
- Group synergy system: Bonuses for balanced party composition
- Status effects: Buffs, debuffs, HoTs, DoTs, shields, taunts
- FX system: Visual effects for abilities (particles, sounds)

---

## 11. Companion Idle Behaviors

**Purpose**: What companions do when not in combat or following.

**Content needed**:
- **Work behaviors** (companions automate tasks when in Stay mode):
  - `ChestDeposit` — Picks up nearby items and deposits them in chests
  - `LootPickup` — Collects dropped loot within radius
  - `FireTending` — Keeps campfires, torches, and hearths fueled
  - `FarmingBehavior` — Plants, waters, and harvests crops (multi-phase)
  - `WoodGathering` — Collects wood from fallen trees
  - `CraftingUpgrade` — Operates workbenches and forges
  - `WorkstationInteraction` — Feeds smelters, kilns, and other stations
  - `BowTraining` — Practices archery at targets
  - `SmartStorageOrganizer` — Auto-sorts items across nearby chests
- Idle wander: Companions wander within configured radius when not working
- Occupancy manager: Prevents multiple companions from using the same workstation
- Chest search radius: How far companions look for storage chests
- Resource data helpers: How companions identify what items go where

---

## 12. Companion Combat & Roles

**Purpose**: How companions fight and their role in group combat.

**Content needed**:
- Combat AI: Aggression, target selection, flee behavior
- Formation system: How companions position relative to the player
- Role-based behavior: Tanks hold aggro, healers stay back, DPS focus targets
- Damage and health scaling: Level-up multipliers
- Group synergy bonuses: Party composition effects
- Taunt system: Tank taunts to pull enemies
- Healing priorities: Healers target lowest-HP allies
- Ability cooldowns and mana/resource management

---

## 13. Territories

**Purpose**: What territories are and what they do.

**Content needed**:
- Overview: Defined zones in the world with special rules and effects
- Territory shapes: Circle, Rectangle (future: polygon)
- Territory effects/flags:
  - `NoBuild` — Building disabled
  - `NoPickaxe` — Pickaxe disabled
  - `NoPvP` — PvP disabled
  - `Pushback` — Players pushed out of zone
  - `MoveSpeedMultiplier` — Speed boost/slow
  - `IncreasedPlayerDamage` — Damage multiplier
  - `ForceEnvironment` — Override weather/environment
  - `CustomSpawn` — Spawn specific creatures in zone
  - `OverridenHeight` — Force terrain height
  - `AddMonsterLevel` — Add star levels to spawned creatures
  - `PaintType` — Terrain paint override
  - `Wind` — Wind strength modifier
  - `DropMultiplier` — Loot multiplier
  - `MapReveal` — Reveal map area on entry
- Map overlay: Territory visualization on the minimap
- HUD overlay: Territory name/status shown on screen
- Entry reporter: Notification when entering/leaving territories
- Admin bypass: Admins can ignore territory restrictions

---

## 14. Territory Config Format

**Purpose**: Complete reference for territory .cfg files.

**Content needed**:
- File location: `BepInEx/config/FiresRPGmaker/Territories/`
- Format (line-based):
  ```
  [territory_name]
  Circle                          <- Shape
  X, Z, Radius                    <- Position and size
  R, G, B, ShowOnMap              <- Color and visibility
  Flag1, Flag2, Key=Value         <- Effects/flags
  ALL                             <- Apply to: ALL or specific players
  ```
- Custom spawn format: `CustomSpawn=PrefabName,Amount,Level,X,Y,Z,RespawnSeconds`
- Map reveal: `MapReveal=Radius` or `MapReveal=X,Z,Radius`
- All available flags listed with descriptions
- Multiple territories per file
- Examples for common setups (safe zone, arena, dungeon entrance)

---

## 15. Globe HUD Overlay

**Purpose**: The custom health/stamina/eitr globe HUD.

**Content needed**:
- Overview: Replaces vanilla health bars with globe-style orbs
- Displays: Health (red), Stamina (olive), Eitr (purple), Adrenaline (bar)
- Configurable: Toggle between custom HUD and vanilla HUD (BepInEx config)
- Positioning: Drag to reposition when ESC menu is open
- Resizing: Double-click for resize mode with corner/edge handles
- Position saved to PlayerPrefs (persists across sessions)
- Reset position: Double-click again or close ESC menu
- Low-resource pulse: Globes pulse when below 25%
- Hidden during map view and piece selection

---

## 16. Vault & Bank System

**Purpose**: Server-side persistent storage for player data.

**Content needed**:
- Overview: The Vault of Knowledge stores player progression data server-side
- What's stored: Quest progress, completed quests, bank items, skill data
- Bank system: Server-side item storage separate from inventory
- Treasury: Server-wide economy tracking
- Player Chronicle: Achievement and milestone tracking
- Realm Ledger: Server-wide shared state
- Data sync: How client-server synchronization works (RPC-based)
- Data persistence: Saved to server filesystem

---

## 17. Underwater Bubble

**Purpose**: The buildable underwater air bubble system.

**Content needed**:
- Overview: A placeable structure that creates a breathable air pocket underwater
- How to build: Craft recipe, placement requirements
- Fuel system: Uses Surtling Cores / Eitr as fuel
- Bubble radius: Scales with fuel level (configurable min/max)
- Fuel drain: Base rate + depth-based drain multiplier
- Configurable values:
  - `MinRadius` (default 5m), `MaxRadius` (default 25m)
  - `MaxFuel` (default 20), `FuelPerItem` (default 1)
  - `BaseDrainRate` (default 0.005/sec), `DepthDrainMultiplier` (default 0.02/m)

---

## 18. Custom Terrain & Water

**Purpose**: Auto-generated rivers, lakes, and terrain modifications.

**Content needed**:
- Auto Water system: Procedural river and stream generation
- Height cache: Pre-computed terrain height grid for fast lookups
- Configurable resolution: 1024 to 20480 (tradeoff: speed vs precision)
- Custom terrain tools: Painting, biome overrides, dirt floors
- Environment boxes: Custom atmosphere zones
- Ice system: Frozen water surfaces (seasonal/biome-based)

---

## 19. UI Customization

**Purpose**: Font and UI appearance options.

**Content needed**:
- Font options (BepInEx config):
  - `PrimaryFont` — Headers/titles (default: Valheim_Prstartk)
  - `DecorativeFont` — Quest titles, Viking-themed (default: Valheim_Norse)
  - `BodyFont` — Descriptions, buttons (default: Valheim_AveriaSerif)
- Available fonts: Prstartk, AveriaSerif, Norse, AveriaSans, Rune
- How font changes are applied (runtime hot-reload)
- Globe HUD customization (position, scale)
- Group HUD toggle

---

## 20. BepInEx Config Reference

**Purpose**: Complete list of all config options.

**Content needed**:
- **General**: VerboseLogging, ServerAuthority
- **Hotkeys**: QuestJournalKey (default: J)
- **Territories**: AdminBypassTerritoryRestrictions, EnableWorldVisualizers, EnableMapOverlay
- **Companions**: ShowBillboardHealthBar, ShowGroupHud
- **Companions.Behavior**: IdleWanderRadius, ReturnHomeRadius, ChestSearchRadius, ChestAutoSortRadius
- **UnderwaterBubble**: MinRadius, MaxRadius, MaxFuel, BaseDrainRate, DepthDrainMultiplier, FuelPerItem
- **AutoWater**: MaxPaths, HeightCacheResolution, HeightCacheSamplesPerFrame
- **UI.Fonts**: PrimaryFont, DecorativeFont, BodyFont
- **Pieces.{name}**: Per-piece Recipe and Enabled toggles
- Default values, valid ranges, and descriptions for each

---

## 21. File & Folder Structure

**Purpose**: Where everything lives on disk.

**Content needed**:
- Root: `BepInEx/config/FiresRPGmaker/`
- Subfolders:
  ```
  Quests/              <- Quest definition .cfg files
  QuestProfiles/       <- Quest profile assignment .cfg files
  Dialogues/           <- Dialogue tree .cfg files
  Territories/         <- Territory definition .cfg files
  ServerInfos/         <- Info NPC content files
  VaultData/           <- Server-side player data (auto-generated)
  RuntimeSprites/      <- Custom quest preview images (.png/.jpg)
  ```
- Config file: `BepInEx/config/com.Fire.FiresRPGmaker.cfg`
- Asset bundle: `vaditems` (ships with the mod)
- Log location: `BepInEx/LogOutput.log`
- PlayerPrefs: HUD positions, UI state

---

## 22. Admin Guide

**Purpose**: Server admin operations and tools.

**Content needed**:
- Admin detection: Valheim's admin list or server owner
- Admin panel: Shift+click NPC (or configurable) to open admin config
- Admin panel tabs: Quest Config, Info Config, Dialogue Editor, Dressing Room
- Dressing Room: Change NPC model/appearance
- Server Authority mode: Lock configs to admin-only changes
- Config sync: How server pushes config to clients
- File watchers: Hot-reload on .cfg file changes (server-side)
- Territory admin bypass
- Console commands for admins

---

## 23. Keyboard Shortcuts

**Purpose**: Quick-reference for all keybindings.

**Content needed**:
- `J` — Open Quest Journal (configurable)
- `E` — Interact with NPC
- `Escape` — Close any open UI panel
- `1-9, 0` — Select dialogue option by number
- `Backspace` — Go back in dialogue history
- Quest screen: Click quest in list, Accept/Cancel buttons
- Globe HUD: Drag to reposition (ESC menu), double-click for resize
- Admin panel: Shift+click NPC

---

## 24. Troubleshooting

**Purpose**: Common problems and solutions.

**Content needed**:
- **"NPC has no quests available"**: Check quest profile assignment, verify .cfg syntax
- **Quest not appearing in NPC's list**: Verify quest ID is in the profile's `Quests=` line
- **Quest progress not tracking**: Check prefab name matches exactly (case-sensitive)
- **Dialogue conditions not working**: Verify condition format, check skill/item names
- **Companions not following**: Check if in Stay mode, verify taming status
- **Territory not working**: Verify coordinates, check shape definition
- **Globe HUD missing**: Check if asset bundle loaded, verify config toggle
- **Config changes not applying**: Server authority may be blocking client changes
- **Quest screen stays visible**: Should be fixed — if still happens, press ESC
- **Icons not showing on quest details**: Prefab name must match an ObjectDB/ZNetScene entry
- **Log checking**: Where to find `BepInEx/LogOutput.log`, what to search for
- **Performance**: Disable verbose logging, reduce territory visualizers, close unused UIs

---

## Implementation Priority

### Phase 1 — Core (ship first)
1. Getting Started
2. Quest System
3. Quest Config Format
4. Dialogue System
5. Dialogue Config Format
6. BepInEx Config Reference
7. File & Folder Structure
8. Keyboard Shortcuts
9. Troubleshooting

### Phase 2 — Companions & Territories
10. NPC Types & Setup
11. Companions Overview
12. Companion Commands
13. Companion Idle Behaviors
14. Territories
15. Territory Config Format

### Phase 3 — Advanced
16. Companion Archetypes
17. Companion Combat & Roles
18. Vault & Bank System
19. Quest Journal
20. Globe HUD Overlay
21. Underwater Bubble
22. Custom Terrain & Water
23. UI Customization
24. Admin Guide

---

## Admin-Gated Content Strategy

Each help section should have an optional **Admin** subsection that is only rendered when the
viewing player is a server admin. This keeps the player-facing help clean while giving admins
the config/setup details they need.

### Detection
```csharp
bool isAdmin = ZNet.instance != null && ZNet.instance.IsAdmin(localPlayer.GetPlayerID());
// fallback: check SteamGameServer / ZNet.instance.IsLocalInstance()
```

### Implementation Pattern
```csharp
private void BuildQuestSystem()
{
    AddHeader("Quest System");
    AddParagraph("...");  // player-facing content

    if (IsCurrentPlayerAdmin())
    {
        AddDivider();
        AddSubHeader("Admin: Quest Configuration");
        AddParagraph("...");  // admin-only config format, file paths, etc.
    }
}
```

### What goes in admin sections
- Config file paths and formats
- Console commands for managing features
- Server authority / sync behavior
- File watcher hot-reload details
- Vault data management
- Territory creation/editing
- NPC profile assignment
- Troubleshooting server-side issues

### What stays in player sections
- How to interact with the feature
- UI controls and keybindings
- Quest/dialogue flow from the player perspective
- Companion commands and behavior
- Visual customization (HUD, fonts)

---

## Help Button HUD Element

The help panel needs a persistent HUD button that appears only on the ESC/pause menu screen.

### Behavior
- **Hidden** during normal gameplay (not on the HUD layer)
- **Visible** only when the ESC menu is open (same check as Globe HUD reposition mode: `Menu.IsVisible()`)
- Positioned in a non-intrusive corner (bottom-left or top-right of ESC menu)
- Click opens the full help panel overlay
- When help panel is open:
  - `GUIManager.BlockInput(true)` — blocks player movement/camera
  - ESC closes the help panel (not the game menu behind it)
  - Clicking the backdrop also closes
- When help panel closes:
  - `GUIManager.BlockInput(false)` only if the ESC menu is also closed
  - If ESC menu is still open, leave input blocked (ESC menu handles its own blocking)

### Implementation
- Create during `UIInitializer.Initialize()` alongside Globe HUD / Group HUD
- Parent to `Hud.instance.transform` (same as Globe HUD)
- Use a small `?` icon button with the mod's gold/brown theme
- Track visibility via `Menu.IsVisible()` in `Update()` — same pattern as `GlobeHudController._isRepositionMode`
- The help panel itself is a separate full-screen overlay (same as PrefabEditorHelpPanel pattern)
- Position/size NOT saved to PlayerPrefs (fixed position, small button)
