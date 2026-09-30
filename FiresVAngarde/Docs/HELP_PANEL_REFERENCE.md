# Verdant's Ascent — Help Panel UI Reference

Reference implementation for a comprehensive in-game help panel, adapted from the
PrefabEditorHelpPanel pattern used in the Steamy Dumps mod.

## Architecture

The help panel follows a consistent pattern:

```
HelpPanel (MonoBehaviour)
??? _root (backdrop overlay — click to close)
?   ??? Dialog (main window)
?       ??? TitleBar (header + close X)
?       ??? Sidebar (scrollable nav buttons)
?       ?   ??? Nav buttons (one per section, click to navigate)
?       ??? ContentArea (scrollable right pane)
?           ??? Content (VerticalLayoutGroup with text helpers)
```

## Panel Construction Pattern

```csharp
public class VerdantAscentHelpPanel : MonoBehaviour
{
    private GameObject _root;
    private ScrollRect _contentScroll;
    private RectTransform _contentArea;
    private List<GameObject> _navButtons = new List<GameObject>();
    private string _currentSection;

    // Theme colors — match the existing Valheim/mod aesthetic
    private static readonly Color PanelBg    = new Color(0.06f, 0.05f, 0.04f, 0.97f);
    private static readonly Color SidebarBg  = new Color(0.08f, 0.07f, 0.05f, 0.95f);
    private static readonly Color ContentBg  = new Color(0.07f, 0.06f, 0.04f, 0.95f);
    private static readonly Color NavNormal  = new Color(0.10f, 0.08f, 0.06f, 0.9f);
    private static readonly Color NavActive  = new Color(0.20f, 0.16f, 0.08f, 0.95f);
    private static readonly Color NavHover   = new Color(0.16f, 0.12f, 0.07f, 0.9f);
    private static readonly Color TextGold   = new Color(1f, 0.85f, 0.5f, 1f);
    private static readonly Color TextLight  = new Color(0.85f, 0.75f, 0.55f, 1f);
    private static readonly Color TextMuted  = new Color(0.6f, 0.5f, 0.35f, 0.8f);
    private static readonly Color HeaderColor = new Color(0.95f, 0.8f, 0.45f, 1f);
    private static readonly Color CodeBg     = new Color(0.06f, 0.06f, 0.08f, 0.9f);
    private static readonly Color DividerColor = new Color(0.4f, 0.3f, 0.15f, 0.4f);

    private const float SidebarWidth = 200f;
}
```

## Section Navigation

Each section name maps to a nav button on the left sidebar. Clicking a button calls
`NavigateTo(section)` which:

1. Updates the highlight on the active nav button
2. Destroys existing content children
3. Calls the corresponding `Build*()` method
4. Scrolls to top

```csharp
private void NavigateTo(string section)
{
    _currentSection = section;
    UpdateNavHighlight();
    PopulateContent(section);
    if (_contentScroll != null)
        _contentScroll.verticalNormalizedPosition = 1f;
}
```

## Text Helper Methods

The content is built programmatically using these helpers:

| Method | Purpose |
|--------|---------|
| `AddHeader(string)` | Large bold gold header (16pt) |
| `AddSubHeader(string)` | Medium bold gold sub-header (13pt) |
| `AddParagraph(string)` | Body text with word wrap (11pt) |
| `AddBullet(string)` | Indented bullet point `  - text` (11pt) |
| `AddCodeBlock(string)` | Dark background monospace block (10pt) |
| `AddDivider()` | Thin horizontal divider line |

Each helper creates a new `GameObject` parented to the `_contentArea` VerticalLayoutGroup.
`ContentSizeFitter` on text elements ensures proper height calculation.

## Key Implementation Notes

- **Font**: Use Valheim's built-in fonts via `UIFontConfig` or the game's TMP font assets
- **Input blocking**: Call `GUIManager.BlockInput(true)` when shown
- **ESC handling**: Close on Escape key in `Update()`
- **Backdrop click-to-close**: The root `Image` + `Button` dismisses the panel
- **Dialog click-blocks-close**: The dialog has its own `Button` that does nothing, preventing click-through
- **Scrollbar**: Both sidebar and content area get `ScrollRect` with clamped movement
- **Layout**: `VerticalLayoutGroup` + `ContentSizeFitter.PreferredSize` for auto-sizing content

## Integration Point

The help panel should be accessible from:
1. A `?` button on the Quest Journal UI
2. The NPC interaction screen (info button)
3. A console command (`va_help`)
4. The ESC menu (settings area)

```csharp
// Singleton access
public static VerdantAscentHelpPanel Instance { get; private set; }

public void Initialize(Transform parent) { CreatePanel(parent); Hide(); }
public void Show()   { _root.SetActive(true); _root.transform.SetAsLastSibling(); }
public void Hide()   { _root.SetActive(false); GUIManager.BlockInput(false); }
public void Toggle() { if (IsVisible) Hide(); else Show(); }
public bool IsVisible => _root != null && _root.activeSelf;
```
