# Reader Refactor Spec — Extract Chapter Loading Feature

**Goal:** Make ReaderWindow a pure composition root by extracting chapter loading logic into a modular feature, following the same pattern as Zoom, Dim, and other existing features.

**Problem:** `ReaderChapterCoordinator` is a 400+ line god object living at the same level as ReaderWindow. It does too much and isn't modular. The 3-surface loading system (Previous/Active/Next chapters with z-index layering) should be a pluggable feature, not infrastructure baked into the parent.

**Architect's Vision:** Every piece of Reader behavior should be a feature that can be added/removed from the catalog without touching ReaderWindow. The parent provides infrastructure (viewport, hubs, state) and loads features—nothing more.

---

## Target Folder Structure

```
Reader/
├── ReaderWindow.xaml
├── ReaderWindow.xaml.cs               ← composition root (gets thinner)
├── ReaderFeatureHost.cs               ← feature plugin system (unchanged)
├── ReaderFeatureCatalog.cs            ← feature registry (unchanged)
├── ReaderDefaultFeatureCatalog.cs     ← registers all features including ChapterLoading
├── ReaderFeatureContract.cs           ← feature interfaces (unchanged)
│
├── ReaderCore/                        ← NEW folder: stable infrastructure
│   ├── FrameContentHost.cs            ← move from Reader/
│   ├── ReaderSessionState.cs          ← already exists, might move here
│   ├── ReaderCommandHub.cs            ← already exists, might move here
│   ├── ReaderNotificationHub.cs       ← already exists, might move here
│   ├── ReaderActivityHub.cs           ← already exists, might move here
│   ├── ReaderInputRouter.cs           ← already exists, might move here
│   └── ReaderPreferencesStore.cs      ← already exists, might move here
│
└── Features/
    ├── ChapterLoading/                ← NEW: extracted from coordinator
    │   ├── ChapterLoadingFeature.cs   ← IReaderFeature entry point
    │   ├── ChapterCoordinator.cs      ← 3-surface manager (Previous/Active/Next)
    │   ├── ChapterPreloader.cs        ← boundary detection + neighbor preload
    │   └── ChapterNavigator.cs        ← jump-to-chapter logic
    │
    ├── Overlay/
    │   ├── ReaderOverlay.xaml
    │   └── ReaderOverlay.xaml.cs
    ├── Drawer/
    │   ├── ReaderDrawer.xaml
    │   └── ReaderDrawer.xaml.cs
    ├── Chrome/
    │   └── ReaderChromeController.cs
    ├── Toast/
    │   ├── ReaderToast.xaml
    │   └── ReaderToast.xaml.cs
    ├── [other existing features remain in their current structure]
```

---

## Step-by-Step Refactor Plan

### **Phase 1: Create New Folder Structure**

1. Create `Reader/ReaderCore/` folder
2. Create `Reader/Features/ChapterLoading/` folder
3. Create `Reader/Features/Overlay/` folder
4. Create `Reader/Features/Drawer/` folder
5. Create `Reader/Features/Chrome/` folder
6. Create `Reader/Features/Toast/` folder
7. *(Repeat for all existing feature files — see current Reader/ folder list)*

### **Phase 2: Move Infrastructure to ReaderCore/**

Move these files from `Reader/` → `Reader/ReaderCore/`:
- `FrameContentHost.cs` *(critical: viewport adapter)*
- `ReaderSessionState.cs` *(if exists at Reader/ root)*
- `ReaderCommandHub.cs` *(if exists)*
- `ReaderNotificationHub.cs` *(if exists)*
- `ReaderActivityHub.cs` *(if exists)*
- `ReaderInputRouter.cs` *(if exists)*
- `ReaderPreferencesStore.cs` *(if exists)*

**After each move:**
- Update namespace from `Module.Mangareader` → `Module.Mangareader.ReaderCore`
- Update all references in other files to the new namespace

### **Phase 3: Move Existing Features to Features/ Subfolders**

For each existing feature file:

**Overlay:**
- Move `ReaderOverlay.xaml` + `ReaderOverlay.xaml.cs` → `Features/Overlay/`
- Namespace stays `Module.Mangareader` (features stay in root namespace)

**Drawer:**
- Move `ReaderDrawer.xaml` + `ReaderDrawer.xaml.cs` + related files → `Features/Drawer/`

**Chrome:**
- Move `ReaderChromeController.cs` → `Features/Chrome/`

**Toast:**
- Move `ReaderToast.xaml` + `ReaderToast.xaml.cs` → `Features/Toast/`

**Repeat for:**
- AutoScroll
- Fullscreen
- Pin
- Zoom
- Dim
- Reset
- ChapterNavigation *(this might merge with ChapterLoading, evaluate during split)*

### **Phase 4: Split ReaderChapterCoordinator**

**Current file:** `Reader/ReaderChapterCoordinator.cs` (~400 lines)

**Split into 4 files in `Features/ChapterLoading/`:**

#### **4a. ChapterLoadingFeature.cs**
- Implements `IReaderFeature`
- `FeatureName` returns `"ChapterLoading"`
- `Attach(ReaderFeatureContext context)` creates and wires the other 3 pieces
- Exposes `IReaderChapterNavigation` interface for other features to call
- Owns disposal of child pieces

**What it does:**
- Entry point for the feature catalog
- Creates ChapterCoordinator, ChapterPreloader, ChapterNavigator
- Wires them together
- Exposes navigation methods to ReaderFeatureContext

#### **4b. ChapterCoordinator.cs**
- Manages the `_surfaces` ObservableCollection
- Handles 3-surface roles: Previous (z-index 10), Active (z-index 30), Next (z-index 20)
- Promotes/demotes surfaces based on viewport scroll position
- Publishes `ActiveChapterChanged` event
- **Does NOT handle loading**—receives `LoadedChapter` from navigator

**Key methods:**
- `AddSurface(int chapterIndex, LoadedChapter content, ChapterSurfaceRole role)`
- `PromoteSurface(int chapterIndex)` — Previous → Active
- `DemoteSurface(int chapterIndex)` — Active → Previous (with quality downgrade)
- `RemoveSurface(int chapterIndex)`
- `EvaluateActiveChapter()` — checks viewport position, updates roles

**State it owns:**
- `_surfaces` collection
- `_activeChapterIndex`

#### **4c. ChapterPreloader.cs**
- Watches viewport position via `ReaderActivityHub`
- Detects when user is approaching chapter boundary (top or bottom)
- Triggers preload for neighbor chapters
- Manages quality requests:
  - Next chapter: full quality
  - Previous chapter: preview quality + 4-page full-quality tail
- Coordinates with ChapterNavigator to perform actual loads

**Key methods:**
- `OnViewportChanged(ReaderViewportChangedEventArgs e)` — boundary detection
- `PreloadNextAsync()` — triggers next chapter load
- `PreloadPreviousAsync()` — triggers previous chapter load with tail logic
- `CancelPreload()` — stops in-flight preload

**Key constants (from original coordinator):**
- `PreviewPixelWidth = 220`
- `PreviousFullQualityTailPages = 4`

#### **4d. ChapterNavigator.cs**
- Handles `NavigateToChapterAsync(int index)` — the "jump to chapter" command
- Manages latest-request-wins cancellation (`_navigationGeneration`, `_navigationCancellation`)
- Orchestrates:
  1. Load the target chapter (or reuse existing if full quality)
  2. Clear `_surfaces`
  3. Add new active surface
  4. Scroll to top
  5. Request neighbors from ChapterPreloader
- Publishes `ActiveChapterChanged` event when navigation completes
- Handles loading status updates (via `IReaderStatusHost`)

**Key methods:**
- `NavigateToChapterAsync(int index)` — main entry point
- `LoadChapterAsync(int index, ChapterRenderRequest request, ...)` — wraps `IReaderChapterLoader`
- `IsCurrentNavigation(long generation, CancellationToken token)` — cancellation check

**State it owns:**
- `_navigationGeneration` (long)
- `_navigationCancellation` (CancellationTokenSource)

---

### **Phase 5: Update ReaderWindow.cs**

**Before (current):**
```cs
private readonly ReaderChapterCoordinator _coordinator;

_coordinator = new ReaderChapterCoordinator(...12 parameters...);
_coordinator.ActiveChapterChanged += OnActiveChapterChanged;
await _coordinator.StartLoadAsync();
```

**After (refactored):**
```cs
// ReaderWindow no longer directly owns coordinator
// ChapterLoadingFeature registers itself via catalog

var context = new ReaderFeatureContext(...);
_featureHost = new ReaderFeatureHost(context, hosts, catalog);

// ChapterLoadingFeature will wire ActiveChapterChanged internally
// ReaderWindow subscribes via ReaderNotificationHub or state changes
```

**Changes needed:**
1. Remove `_coordinator` field
2. Remove direct `_coordinator.StartLoadAsync()` call
3. Remove `_coordinator.ActiveChapterChanged` subscription
4. Add subscription to ChapterLoading events via notification hub (if needed)

**If ReaderWindow still needs to know about active chapter changes:**
- Subscribe to `ReaderNotificationHub` or `ReaderSessionState.ActiveChapter` property
- ChapterLoadingFeature updates state when chapter changes

### **Phase 6: Update ReaderDefaultFeatureCatalog.cs**

Add ChapterLoading registration:

```cs
.Add("ChapterLoading", () => new ChapterLoadingFeature(
    title,
    initialChapter,
    chapterLoader))
```

**Problem:** This requires passing `title`, `initialChapter`, `chapterLoader` to the catalog.

**Solution options:**
1. Add these to `ReaderFeatureContext` so features can access them
2. Create a specialized registration for ChapterLoading in ReaderWindow
3. Make ChapterLoadingFeature pull these from context services

**Recommended:** Add to `ReaderFeatureContext`:
```cs
public sealed record ReaderFeatureContext(
    ...,
    MangaTitle Title,                    // NEW
    ChapterInfo InitialChapter,          // NEW
    IReaderChapterLoader ChapterLoader,  // NEW
    ...);
```

### **Phase 7: Update Namespace Imports**

After all moves, update imports in:
- `ReaderWindow.cs` — add `using Module.Mangareader.ReaderCore;`
- All feature files — add `using Module.Mangareader.ReaderCore;` for infrastructure types
- Any file referencing moved types

### **Phase 8: Build & Test**

1. Build project — fix any compilation errors
2. Run Citadel
3. Open Manga Reader
4. Open a chapter → verify it loads
5. Scroll through chapter → verify surfaces stay in sync
6. Jump to different chapter → verify navigation works
7. Test all other features (Zoom, Dim, Fullscreen, etc.) still work

---

## Key Contracts to Preserve

### **IReaderChapterNavigation** (public interface)
Other features call this to navigate:
```cs
public interface IReaderChapterNavigation
{
    Task NavigateToChapterAsync(int index);
    Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken);
}
```

Ensure ChapterLoadingFeature exposes this via ReaderFeatureContext or a service.

### **ActiveChapterChanged Event**
```cs
public event EventHandler<ActiveChapterChangedEventArgs>? ActiveChapterChanged;
```

Currently fired by coordinator. After refactor, ChapterNavigator fires it.

### **IReaderChapterLoader** (unchanged)
```cs
public interface IReaderChapterLoader
{
    Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken);
}
```

ChapterNavigator consumes this.

---

## Testing Checklist

After refactor, verify:

- [ ] Chapter loads on reader open
- [ ] Scrolling updates active chapter correctly
- [ ] Previous chapter appears when scrolling up
- [ ] Next chapter preloads when approaching bottom
- [ ] Jumping to chapter (via navigation) works
- [ ] Z-index layering is correct (Active on top)
- [ ] Previous chapter is low-res preview (except 4-page tail)
- [ ] Active chapter is full quality
- [ ] Next chapter is full quality
- [ ] All other features (Zoom, Dim, Fullscreen, AutoScroll, etc.) still work
- [ ] Reader closes cleanly without errors
- [ ] No memory leaks (surfaces dispose correctly)

---

## Notes for Implementation Agent

- **Move files carefully** — use IDE refactoring tools if available to update references automatically
- **Namespace updates are critical** — a missed namespace will cause compile errors
- **Test after each phase** — don't move everything then build; build after Phase 2, Phase 3, etc.
- **The split of ReaderChapterCoordinator is the hardest part** — the 4 files need to coordinate but stay decoupled
- **ChapterCoordinator and ChapterNavigator both need access to `_surfaces`** — consider making ChapterCoordinator own it and ChapterNavigator call methods on it
- **Preservation over perfection** — if a method doesn't fit cleanly into one of the 4 files, put it where it makes most sense and document why
- **Keep existing behavior identical** — this is a refactor, not a rewrite; user shouldn't notice any difference

---

## Why This Refactor Matters

**Before:** ReaderWindow is tightly coupled to chapter loading logic. Adding a new loading strategy (e.g., streaming, lazy-load, different cache policy) requires editing ReaderWindow.

**After:** ReaderWindow is a pure composition root. Chapter loading is a feature like any other. You can:
- Swap loading strategies by replacing ChapterLoadingFeature
- Test loading logic in isolation
- Reuse Reader infrastructure for other content types (PDFs, images)
- Understand the system by folder structure alone

**The folder tree now tells the story:**
- `ReaderCore/` = the power outlets
- `Features/` = the devices you plug in
- `ReaderWindow` = the power strip that routes everything
