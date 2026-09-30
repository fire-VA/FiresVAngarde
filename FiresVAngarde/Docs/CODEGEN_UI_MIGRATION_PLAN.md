# Codegen UI Migration Plan

## Overview

Replace the current NPC panel construction pipeline (asset bundle prefab → programmatic fallback → manual wiring) with the auto-generated `completecompanionsui.generated.cs` code. The generated file creates the entire 656-element UI hierarchy from code — no asset bundle or JSON layout needed.

**Goal**: One `Instantiate(parent)` call replaces all panel construction. All screen controllers keep their existing logic but wire into the pre-built hierarchy instead of creating their own elements.

---

## Current Architecture

```
      UIInitializer.CreatePanelInstance()
      → UIPrefabFactory.GetOrCreateNpcPanelPrefab()
      → Tries: Asset bundle "NPCMainPanel"
      → Fallback: UIPrefabFactoryProgrammatic.CreateNpcPanelPrefab()
      → UIPrefabFactory.WireUpPanelReferences()    (573 lines)
      → NpcMainPanelController.RewireButtonListeners()
      → NpcMainPanelController.CreateTypeScreens()
      → InfoNpcScreenController.Create()        — builds own UI
      → QuestConfigScreenController.Create()    — builds own UI
      → DressingRoomScreenController.Create()   — builds own UI
      → DialogueEditorScreenController.Create() — builds own UI
      → QuestInteractionScreenController.Create() — builds own UI
```

## Target Architecture

```
UIInitializer.CreatePanelInstance()
  → GeneratedUI_completecompanionsui.Instantiate(parent)  ← single call
  → CodegenUIWiring.ResolveSpritesAndFonts(root)          ← new helper
  → CodegenUIWiring.WireMainPanel(root, controller)       ← new helper
  → NpcMainPanelController.RewireButtonListeners()        ← unchanged
  → Each screen controller.WireFromCodegen(root)          ← new method per screen
```

---

## Files Affected

### DELETED after migration (replaced by codegen)
| File | Lines | Purpose |
|------|-------|---------|
| `UI/PrefabFactory/UIPrefabFactoryProgrammatic.cs` | 533 | Builds panel from code (replaced by codegen) |
| `UI/PrefabFactory/UIPrefabFactoryWiring.cs` | 573 | Wires references by name search (replaced) |
| `UI/PrefabFactory/UIPrefabFactoryInputFields.cs` | 255 | Creates input fields (now in codegen) |
| `UI/PrefabFactory/UIPrefabFactoryHelpers.cs` | 83 | Helper methods (absorbed into wiring) |

### MODIFIED (keep logic, change UI construction)
| File | Lines | Changes Needed |
|------|-------|----------------|
| `UI/PrefabFactory/UIPrefabFactoryCore.cs` | 336 | `GetOrCreateNpcPanelPrefab` → call codegen |
| `UI/UIInitializer.cs` | 350 | Swap construction to codegen + new wiring |
| `UI/NpcMainPanelController.cs` | 1711 | Remove `CreateTypeScreens`, screens found in hierarchy |
| `UI/Screens/InfoNpcScreenController.cs` | 782 | Add `WireFromCodegen(root)`, remove `Create()` UI building |
| `UI/Screens/QuestConfigScreenController.cs` | 1526 | Add `WireFromCodegen(root)`, remove `Create()` UI building |
| `UI/Screens/DressingRoomScreenController.cs` | 1869 | Add `WireFromCodegen(root)`, remove `Create()` UI building |
| `UI/Screens/DressingRoomScreenController.Fashion.cs` | 283 | May need minor adjustments for new element names |
| `UI/Screens/DialogueEditorScreenController.cs` | 1467 | Add `WireFromCodegen(root)`, remove `Create()` UI building |
| `UI/Screens/QuestInteractionScreenController.cs` | 1141 | Add `WireFromCodegen(root)`, remove `Create()` UI building |

### NEW FILES
| File | Purpose |
|------|---------|
| `UI/Screens/completecompanionsui.generated.cs` | Already exists — the codegen output |
| `UI/CodegenUIWiring.cs` | Sprite resolution, font application, main panel wiring |

### UNCHANGED
| File | Why |
|------|-----|
| `UI/NpcController.cs` | No UI construction, just data |
| `UI/GUIManager.cs` | Panel open/close gating only |
| `UI/UIBuilderHelper.cs` | Still used for runtime helpers |
| `UI/UIFontConfig.cs` | Font loading — called by new wiring |
| `Modules/Companions/UI/*` | Companion screens are separate from NPC panel |

---

## Migration Phases

### Phase 1: Foundation — New Wiring Layer
**Build & test after this phase**

1. Create `UI/CodegenUIWiring.cs` with:
   - `ResolveSprites(GameObject root)` — find all Images with null sprites, match by known names
   - `ResolveFonts(GameObject root)` — apply `UIFontConfig` fonts to all TMP components
   - `WireMainPanel(GameObject root, NpcMainPanelController ctrl)` — set all public field references

2. Modify `UIPrefabFactoryCore.GetOrCreateNpcPanelPrefab()`:
   - Replace asset bundle loading + programmatic fallback with codegen `Instantiate()`
   - Call `CodegenUIWiring` to resolve sprites/fonts
   - Return the generated root

3. Modify `UIInitializer.CreatePanelInstance()`:
   - Use new codegen path
   - Call `CodegenUIWiring.WireMainPanel()` instead of `UIPrefabFactory.WireUpPanelReferences()`

### Phase 2: Screen Controller Wiring
**Build & test after EACH screen**

For each screen controller, add a `WireFromCodegen(Transform root)` method that:
- Finds its root container in the hierarchy by name (e.g. `"InfoNpcScreen"`)
- Locates child elements by `transform.Find()` path
- Assigns internal field references
- Removes the old `Create()` UI-building code

Order (simplest first):
1. `InfoNpcScreenController` — simplest screen ✅ DONE
2. `QuestInteractionScreenController` — read-only quest display ✅ DONE
3. `DialogueEditorScreenController` — list + editor ✅ DONE
4. `QuestConfigScreenController` — form with many fields ✅ DONE
5. `DressingRoomScreenController` — most complex (equipment grid, fashion dropdowns, color pickers) ✅ DONE

### Phase 3: Cleanup
**Build & test after this phase**

1. Delete `UIPrefabFactoryProgrammatic.cs`
2. Delete `UIPrefabFactoryWiring.cs`
3. Delete `UIPrefabFactoryInputFields.cs`
4. Delete `UIPrefabFactoryHelpers.cs`
5. Remove dead code paths from `UIPrefabFactoryCore.cs`
6. Remove `CreateTypeScreens()` from `NpcMainPanelController`

---

## SETUP_REQUIRED Resolution Map

These are the items flagged in the generated code that need runtime resolution:

| Pattern | Count | Resolution |
|---------|-------|------------|
| `Apply Primary font to tmp` | 143 | `UIFontConfig.ApplyPrimaryFont(tmp)` |
| `Apply Body font to btnLabel` | 37 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Apply Body font to ifTxt` | 30 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Apply Body font to ifPh` | 30 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Apply Body font to tglCheckTxt` | 14 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Apply Body font to ddLabelTmp` | 13 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Apply Body font to ddItemLabel` | 13 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Apply Body font to ddArrowTxt` | 13 | `UIFontConfig.ApplyBodyFont(tmp)` |
| `Wire up button click handler` | 37 | Done per-screen in `WireFromCodegen()` |
| `Assign sprite: "cached:NpcUiBkg"` | 1 | `VAMiscAssetManager.GetSprite("npcuibkg")` |
| `Assign sprite: "cached:item_background"` | 36 | Valheim's `InventoryGui` slot bg sprite |
| `Assign sprite: "cached:selection_frame"` | 9 | Valheim's inventory selection frame sprite |
| `Assign sprite: "cached:noteleport"` | 9 | Valheim's no-teleport icon sprite |

Font/sprite resolution can be done in a single recursive pass over the hierarchy — no need to edit the generated file.

---

## Risk Assessment

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Element names changed between codegen export and code | Low | Names match what was exported from the running UI |
| Font NullRef on TMP without font | High | `ResolveFonts()` runs immediately after `Instantiate()` |
| Sprite Images render white/missing | Medium | `ResolveSprites()` with Valheim sprite fallbacks |
| Screen controller can't find its elements | Medium | Add logging in `WireFromCodegen()`, validate paths |
| Existing screen logic breaks after rewire | Low | Logic stays identical, only construction changes |
| Compile errors from removed factory code | Low | Phase 3 cleanup is last, after everything works |

---

## Element Hierarchy Quick Reference

Key named elements in the generated hierarchy (use `transform.Find()` paths):

```
Root
└── FiresRPGmaker_NpcPanel
    ├── Header
    │   ├── TitleText
    │   ├── CloseButton
    │   └── PageControls (PrevButton, NextButton, PageText)
    ├── TabsBar
    │   ├── QuestNpcTab / QuestNpcTab_Highlight
    │   ├── InfoNpcTab / InfoNpcTab_Highlight
    │   ├── DialogueNpcTab / DialogueNpcTab_Highlight
    │   ├── TraderTab / TraderTab_Highlight
    │   ├── SizeButton
    │   └── SearchBar
    ├── TabContent
    │   ├── Content
    │   │   ├── ListRoot (ScrollRect)
    │   │   └── DetailPanel
    │   │       ├── Container_NameOverrideInput
    │   │       ├── Container_ProfileInput
    │   │       ├── Container_ModelOverrideInput
    │   │       ├── DressingRoom (button)
    │   │       └── ApplyButton
    │   ├── QuestInteract
    │   ├── InfoNpcScreen
    │   ├── DressingRoomScreen
    │   │   ├── FashionPanel_Right (dropdowns, color pickers)
    │   │   ├── FashionPanel_Left (model scale, toggles)
    │   │   └── EquipmentGrid (9 inventory slots)
    │   ├── DialogueEditorScreen
    │   ├── QuestScreenContainer
    │   │   ├── QuestListPanel
    │   │   ├── QuestDetailsPanel
    │   │   └── QuestButtonsPanel
    │   └── QuestConfigScreen
    └── (tab highlights created at runtime)
```

---

## Estimated Effort Per Phase

| Phase | Estimated Time | Complexity |
|-------|---------------|------------|
| Phase 1: Foundation | ~2 hours | Low-Medium |
| Phase 2a: InfoNpcScreen | ~30 min | Low |
| Phase 2b: QuestInteractionScreen | ~45 min | Low |
| Phase 2c: DialogueEditorScreen | ~1 hour | Medium |
| Phase 2d: QuestConfigScreen | ~1.5 hours | Medium |
| Phase 2e: DressingRoomScreen | ~2 hours | High |
| Phase 3: Cleanup | ~30 min | Low |
| **Total** | **~8 hours** | |
