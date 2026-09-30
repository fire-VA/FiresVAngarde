# Dialogue Background Sprite Bug Tracker

## Bug Summary
The dialogue panel shows a **solid white background** instead of the dark 9-sliced
`crafting_panel_bkg` sprite for new players who connect to a server without having
previously run a SteamyDumps asset cache.

The InfoNpcPlayerPanel uses the **same background sprite** and renders correctly for
those same new players.

## Symptoms
| Who sees it | Dialogue BG | Info NPC BG |
|---|---|---|
| Host / player with SteamyDumps cache | ? Correct | ? Correct |
| New client without cache | ? Solid white | ? Correct |

## Root Cause Analysis

### Why InfoNpcPlayerPanel works (CreatePanel, line ~203)
```csharp
// 1. Try vanilla sprite via resolver chain
var bgSprite = CodegenUIWiring.ResolveSpriteByNamePublic("crafting_panel_bkg");
if (bgSprite != null)
{
    bgImage.sprite = bgSprite;        // ? applied directly to the Image component
    bgImage.type = Image.Type.Sliced;
    bgImage.color = new Color(1f, 1f, 1f, 0.85f);
}
else
{
    // 2. Fallback: npcuibkg from asset bundle (ALWAYS available)
    var fallbackSprite = UIPrefabFactory.GetBackgroundSprite();
    if (fallbackSprite != null)
    {
        bgImage.sprite = fallbackSprite;   // ? applied directly
        bgImage.type = Image.Type.Sliced;
        bgImage.color = new Color(1f, 1f, 1f, 0.85f);
    }
    else
    {
        bgImage.color = UIFontConfig.Colors.PanelBackground;  // solid color fallback
    }
}
```
**Key:** The Image component is created by `_panelInstance.AddComponent<Image>()` —
it starts with **no sprite and no visible color** until explicitly set. The fallback
chain always applies *something* to the Image.

### Why DialogueUI fails (BuildHardCodedUI, line ~267)
```csharp
// The codegen creates the BACKGROUND Image with:
//   img_2.color = new Color(1f, 1f, 1f, 1f);   ? FULL WHITE
//   img_2.type = (Image.Type)1;                  ? Sliced
//   // NO sprite assigned

// Then BuildHardCodedUI does:
_defaultBgSprite = ResolveCraftingPanelBkg();
if (_defaultBgSprite != null)                     // ? NULL for new clients
{
    _backgroundImage.sprite = _defaultBgSprite;   // ? SKIPPED
    _backgroundImage.type = Image.Type.Sliced;    // ? SKIPPED
}
_backgroundImage.color = new Color(1f, 1f, 1f, 0.625f);  // ? ALWAYS runs
// Result: white Image with no sprite, 62.5% opacity = SOLID WHITE BOX
```

The `ResolveCraftingPanelBkg()` method has 3 tiers:
1. Search InventoryGui for `crafting_panel_bkg` — fails if sprite name doesn't match
2. `Resources.FindObjectsOfTypeAll<Sprite>()` — fails for new clients
3. `UIPrefabFactory.GetBackgroundSprite()` — loads `npcuibkg` from asset bundle

**The 3rd tier should work** but is failing because `UIPrefabFactory.GetBackgroundSprite()`
caches its result after the first call. If it was called earlier (before the asset bundle
loaded or in a failed state), `_backgroundLoaded = true` sticks and returns null forever.

Even if the fallback DOES return a sprite, there's a second bug: **the codegen BACKGROUND
Image starts with `color = (1,1,1,1)` (white)**. If the sprite has any transparency in
its 9-slice border, the white shows through. The InfoNpcPlayerPanel doesn't have this
problem because its Image starts with no color until the sprite is applied.

### Attempt History
| # | Change | Result |
|---|---|---|
| 1 | Added per-dialogue BG_Image support in PopulateDialogue | ? Still white — didn't address the root sprite resolution failure |
| 2 | Added `_defaultBgSprite` field + cached at build time | ? Caches null on first failure, white persists |
| 3 | Added `ResolveCraftingPanelBkg()` with InventoryGui search + Resources.FindAll + UIPrefabFactory fallback | ? All 3 tiers may fail for new clients; even if tier 3 works, codegen white color bleeds through |
| 4 | Added lazy-resolve in PopulateDialogue | ? Still white — same resolution chain, same result |

## Fix Plan

Match exactly what InfoNpcPlayerPanel does: apply the sprite (or fallback) directly
to the Image component immediately, and don't rely on the codegen's pre-set white color.

1. In `BuildHardCodedUI`: after finding the BACKGROUND Image, use the **same 3-step
   fallback** as InfoNpcPlayerPanel (resolver ? UIPrefabFactory ? solid color), applied
   directly to `_backgroundImage` with proper color.
2. Remove `ResolveCraftingPanelBkg()` — it over-engineers the problem and doesn't match
   the working pattern.
3. In `PopulateDialogue`: keep lazy-resolve for BG_Image overrides but ensure the
   default path always applies the sprite + color together.
4. Also reset `_defaultBgSprite` in `Destroy()` so stale caches don't persist across
   logout/login.

## Additional Issue: Info NPC PNG Sync
The Info NPC panel's **custom PNG images** (set per-NPC via the admin editor) are not
properly synced from server to new connecting clients. The panel background itself works
(it comes from the asset bundle), but the per-NPC images stored in RuntimeSprites/ on
the server aren't being pushed to clients on login. This is a separate
RuntimeSpriteSync issue — not addressed in this fix.

## Changes Made

### File: `FiresNPCs/Modules/Dialogues/DialogueUI.cs`

**1. Replaced `BuildHardCodedUI` background section (line ~267)**
- Old: Called `ResolveCraftingPanelBkg()` then conditionally set sprite, always set white color
- New: Calls `ApplyBackgroundSprite(_backgroundImage)` which applies sprite+type+color
  as an atomic unit — a missing sprite never leaves a white Image

**2. Replaced `ResolveCraftingPanelBkg()` with `ApplyBackgroundSprite(Image)`**
- Removed the 3-tier search method (InventoryGui scan, Resources.FindAll, UIPrefabFactory)
  that returned a sprite but left the Image in a broken state when null
- New method mirrors `InfoNpcPlayerPanel.CreatePanel` exactly:
  ```
  Step 1: CodegenUIWiring.ResolveSpriteByNamePublic("crafting_panel_bkg")
  Step 2: UIPrefabFactory.GetBackgroundSprite()  (npcuibkg from asset bundle)
  Step 3: UIFontConfig.Colors.PanelBackground    (solid dark color)
  ```
  Each step sets sprite + type + color together before returning.

**3. Simplified `PopulateDialogue` background handling**
- If `_defaultBgSprite` is still null when a dialogue opens, re-runs
  `ApplyBackgroundSprite` (lazy retry for edge-case timing)
- Per-dialogue `BG_Image` override applied cleanly
- Background always set active (never hidden — worst case is solid color)

**4. Added `_defaultBgSprite = null` in `Destroy()`**
- Prevents stale sprite references persisting across logout/login cycles

### File: `FiresNPCs/Docs/DIALOGUE_BG_BUGFIX_TRACKER.md`
- Created this bug tracker documenting the issue history and fix
