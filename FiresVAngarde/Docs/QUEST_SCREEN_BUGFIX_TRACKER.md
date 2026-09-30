# Quest Screen Icon & Preview Bug Tracker

## Issue Summary
Three persistent bugs on the quest interaction screen:
1. **Icons not showing** next to objectives and rewards
2. **Preview always black** — 3D prefab preview never renders visible content
3. **Requirement icons still showing** when they should have been removed

---

## Bug 1: Icons Not Showing

### What Should Happen
Small 16x16 icons should appear to the LEFT of each objective line (e.g., Boar trophy icon next to "Boar x25") and each reward line (e.g., coin icon next to "Coins x75").

### How It Works (or Should)
1. `ShowQuestDetails()` builds description text with `<space=22>` indent on every objective/reward line to reserve room for icons
2. `CreateDescriptionIcons()` is called after setting the text
3. It calls `_questDescriptionText.ForceMeshUpdate()` to generate TMP character data
4. For each objective `t.Prefab` and reward `r.Target`, it calls `GetPrefabIcon(prefabName)` to get a Sprite
5. It then searches the TMP visible characters for the prefab name string to find which TMP line it's on
6. It creates an `Image` child of the text RectTransform at the line's Y position

### Root Cause Analysis
**The text search looks for `prefabName` in the visible text, but the visible text shows `GetTargetDisplayName()` which may differ from `Prefab`:**
- `GetTargetDisplayName()` returns `NameFilter` if set, otherwise `GetDisplayName(Prefab)` which tries localization
- For Boar: prefab is "Boar", display is "Boar" — should match
- For rewards: `r.Target` is "Coins", text shows "Coins" — should match

**BUT the real problem is `<space=22>` TMP rich tags.** The `<space=22>` tag adds whitespace but does NOT produce visible characters in `textInfo.characterInfo`. So when we build the `visibleText` string by iterating `characterInfo[ci].isVisible`, the spaces from `<space=22>` are EXCLUDED. The visible text has ALL the content from all lines concatenated with NO line-break separation, making the character-to-line mapping potentially correct but the search unreliable.

**CRITICAL: TMP `isVisible` is false for spaces.** This means the visible-text string omits spaces between words too. "Boar x25" becomes "Boarx25" in the visible string. The search for "Boar" would find "Boar" at the start of "Boarx25" — but the word boundary check `!char.IsLetter(visible[afterIdx])` sees 'x' which IS a letter... so it FAILS the word boundary check and rejects the match!

**This is the bug.** The word boundary check is too strict — 'x' after "Boar" is the multiplier "x25", not part of "Boarbreaker". The visible text strips spaces, so "Boar x25" becomes "Boarx25".

### Fix Needed
Change the visible-text matching approach. Instead of searching for `prefabName` as a substring in the concatenated visible text, search for it in the **actual text string** (which preserves spaces and rich tags get stripped by TMP). Or relax the word boundary check to also allow 'x' followed by digits.

---

## Bug 2: Preview Always Black

### What Should Happen
The preview area should show a 3D render of the quest target creature/item.

### How It Works
1. `LoadPreviewImage()` tries PNG files first, then falls back to `TryShowTargetIconAsPreview()`
2. `TryShowTargetIconAsPreview()` builds a list of previewable prefabs, calls `ShowPreviewAtIndex(0)`
3. `ShowPreviewAtIndex()` calls `QuestPreviewRenderer.EnsureInstance()` then `renderer.StagePreview(prefabName)`
4. `StagePreview()` finds the prefab via `ZNetScene.GetPrefab()`, instantiates it at (-9000, 3000, -9000), freezes it, frames the camera, enables rendering
5. The RenderTexture is assigned to the `_previewRawImage.texture`

### Root Cause Analysis
`StagePreview()` instantiates the prefab with dangerous components disabled on the SOURCE first (then re-enabled). `Character` and `Humanoid` were removed from the disable list (previous fix), but the clone's renderers may still have no mesh data because:

1. **Valheim creatures use LODGroup** — `FreezeClone` calls `lod.ForceLOD(0)` but LODGroup may already have culled renderers
2. **SkinnedMeshRenderers on creatures** need the `Animator` to run at least one frame to bind bones — but we disable everything immediately
3. **The camera renders in `LateUpdate`** via `ApplyCameraTransform()`, but the clone may not have its meshes ready until the next frame
4. **Camera culling mask** was set to UI layer only, but the clone's renderers might not be assigned to that layer correctly before bounds calculation

### What Was Tried
- Removed Character/Humanoid from pre-instantiate disable list
- Added `renderer.enabled = true` for all renderers
- Added directional light to camera
- Set clone objects to UI layer
- Set camera cullingMask to UI layer
- Dynamic near/far clip planes

### Fix Needed
The staged clone needs at least one frame to initialize its meshes. The camera should do a deferred `Render()` call, or the preview should be shown after a one-frame delay via coroutine.

---

## Bug 3: Requirement Icons Still Appearing

### Status
Previous fix removed requirements from `iconTargets` in `CreateDescriptionIcons()`. If they still appear, it may be a stale build or the `GetRequirementIconPrefix()` in the text builder was not fully removed.

### Check
`GetRequirementIconPrefix` was removed from the text building. Requirements use plain text with no `<space=22>` indent. Verified in code — this should be fixed.

---

## Action Plan

### Icon Fix — APPLIED
**Root cause**: TMP `isVisible` excludes spaces, so "Boar x25" becomes "Boarx25" in the visible-text search. The word boundary check sees 'x' as a letter and rejects the match. Every single icon fails the text search.

**Solution**: Replaced fragile visible-text searching with TMP `<link>` tags.
1. During `ShowQuestDetails()`, each objective/reward line wraps the name in `<link="obj_N">` or `<link="rwd_N">`
2. `CreateDescriptionIcons()` calls `ForceMeshUpdate()` then reads `textInfo.linkInfo[]`
3. For each link, `linkInfo.linkTextfirstCharacterIndex` ? `characterInfo[idx].lineNumber` gives the exact TMP line
4. Icons placed at that line's Y position — no text searching at all

Requirements have no `<link>` tags and no entries in `_pendingIconLines`, so they correctly get no icons.

### Preview Fix — APPLIED (Iteration 2)
**Root cause (original)**: Clone meshes need frames to initialize. Camera rendered same frame as instantiation.
**Root cause (iteration 2)**: Camera `cullingMask` was set to UI layer only (`1 << 5`), but moved objects to UI layer which conflicts with Unity's canvas rendering. The directional light renders all layers but camera only sees UI layer — mismatched lighting/rendering.

**Solution (final)**:
1. Camera now renders ALL layers (`cullingMask = ~0`) — matches the working PrefabPreviewCamera/PrefabIconRenderer from the other mod
2. No layer tricks — clone stays at (-9000, 3000, -9000) which is far enough from anything visible
3. `FinalizeStageNextFrame()` waits TWO frames (Awake/Start frame + Animator bind frame)
4. After delay: FreezeClone disables AI, renderers forced visible, LOD0 forced, bounds recalculated, camera reframed, explicit Render() call
5. `FreezeRigidbodies()` uses `constraints = FreezeAll` instead of setting velocity on kinematic bodies (eliminates "Setting velocity of kinematic body" warnings)

### Scrollbar Click/Drag Fix — APPLIED
**Root cause**: Codegen creates the SlidingArea image with `raycastTarget = false`. Unity's `Scrollbar` component needs the sliding area to accept raycasts for click-to-jump and handle dragging to work.

**Solution**: `FixCodegenScrollbar` now explicitly sets `raycastTarget = true` on:
- The scrollbar root Image
- The SlidingArea Image  
- The Handle Image

### Panel Padding Fix — APPLIED (Iteration 2)
Updated offsets to give more breathing room within the decorative book border:
- Left panel: offsetMin=(30,30) offsetMax=(-20,-30)
- Right panel: offsetMin=(20,30) offsetMax=(-30,-30)
- Buttons panel: anchorMin=(0.15, 0.05) anchorMax=(0.85, 0.12) — tight under panels
2. Camera is enabled immediately pointing at the stage position (so it starts rendering)
3. After `yield return null` (one frame), the coroutine: enables all renderers, forces LOD0, recalculates bounds, reframes camera, calls `_camera.Render()` explicitly
4. `ClearStage()` calls `StopAllCoroutines()` to prevent stale finalization
5. `RenderIconSprite()` (synchronous path) does inline bounds/framing since it can't wait
