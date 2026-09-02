# Task: Refactor Reader Module to Extract Chapter Loading Feature

## Context

You're working on the **Manga Reader module** in the Citadel project. The Reader currently has chapter loading logic (the 3-surface system: Previous/Active/Next chapters with z-index layering) baked into `ReaderChapterCoordinator.cs`, making it tightly coupled to `ReaderWindow`. 

Your job is to extract that logic into a modular feature, following the same plug-and-play pattern used by other features like Zoom, Dim, and Fullscreen.

---

## Your Workspace Skills (READ THESE FIRST)

Before you start, read these two skill files in the workspace. They contain the rules you must follow:

### 1. **citadel-shared-ui skill**
**Location:** `C:\VSCODE\citadel\.agents\skills\citadel-shared-ui\SKILL.md`

**What it covers:**
- How to use shared UI components from `setting/Components/`
- When you can create new components vs reusing existing ones
- The approval boundary for UI changes
- How to create combo components (behavior wrappers)

**Key rule:** Always reuse shared components. Don't create new primitive components without approval. UI behavior wrappers are allowed if they follow the combo component pattern.

### 2. **citadel-feature-modularity skill**
**Location:** `C:\VSCODE\citadel\.agents\skills\citadel-feature-modularity\SKILL.md`

**What it covers:**
- How features should be structured (self-contained folders)
- How parent/feature communication works (event bus, not direct calls)
- When logic belongs in parent vs feature folders
- The pattern for plug-and-play features

**Key rule:** Parent = dumb router. Features = smart workers. Parent provides infrastructure, features do the work. No feature-specific logic in parent files.

---

## Your Detailed Plan

**Location:** `C:\VSCODE\citadel\module\mangareader\Reader\REFACTOR-SPEC.md`

This file contains the complete step-by-step refactor plan with 8 phases:

1. Create new folder structure
2. Move infrastructure to `ReaderCore/`
3. Move existing features to `Features/` subfolders
4. Split `ReaderChapterCoordinator.cs` into 4 focused files
5. Update `ReaderWindow.cs` to remove direct coordinator dependency
6. Update `ReaderDefaultFeatureCatalog.cs` to register ChapterLoading
7. Update namespace imports
8. Build & test

**Read the entire spec before starting.** It explains what each file does, where it goes, and why.

---

## What Success Looks Like

**After your refactor:**

### Folder Structure
```
Reader/
├── ReaderWindow.cs                    ← thin composition root
├── ReaderCore/                        ← infrastructure (new)
│   ├── FrameContentHost.cs
│   └── [hubs, state, routers]
└── Features/
    ├── ChapterLoading/                ← new feature (extracted from coordinator)
    │   ├── ChapterLoadingFeature.cs
    │   ├── ChapterCoordinator.cs
    │   ├── ChapterPreloader.cs
    │   └── ChapterNavigator.cs
    ├── Overlay/
    ├── Drawer/
    └── [other features]/
```

### Behavior
- Manga Reader still works exactly the same
- Chapter loads on reader open
- Scrolling updates active chapter correctly
- Previous/next chapter preloading works
- Jump-to-chapter works
- All other features (Zoom, Dim, etc.) still work
- No visual changes for the user

### Code Quality
- ReaderWindow doesn't know ChapterLoading internals
- ChapterLoading is self-contained (can be removed by deleting folder + catalog entry)
- Each piece has one clear job (coordinator = surfaces, preloader = boundaries, navigator = jumps)
- No feature-specific logic in ReaderWindow

---

## Working Instructions

### Step 1: Read First, Then Act
1. Read `REFACTOR-SPEC.md` completely
2. Read both workspace skills (citadel-shared-ui, citadel-feature-modularity)
3. Understand the current structure by exploring `Reader/` folder
4. Then start Phase 1 of the spec

### Step 2: Work Phase-by-Phase
- Don't skip phases or combine them
- Build after Phase 2, Phase 3, etc. to catch errors early
- If a phase doesn't compile, fix it before moving to next phase
- Comment your progress in this prompt file (add a "Progress Log" section at the bottom)

### Step 3: Follow the Skills
- **citadel-shared-ui:** If you touch any XAML or UI component, check this skill first
- **citadel-feature-modularity:** If you're deciding where logic goes (parent vs feature), check this skill first
- When in doubt, ask me before proceeding

### Step 4: Test Thoroughly
After Phase 8, test everything from the checklist in `REFACTOR-SPEC.md`:
- Chapter loads
- Scrolling works
- Navigation works
- All features work
- No errors in output

### Step 5: If You Get Stuck
**Stop and report:**
- What phase you're on
- What the error/problem is
- What you've tried
- What you think the solution might be

Don't guess or make up solutions. I'd rather you stop and ask than introduce bugs.

---

## Key Constraints

### Things You MUST Do
- ✅ Follow the spec exactly (don't improvise a "better" structure)
- ✅ Preserve all existing behavior (user shouldn't notice any change)
- ✅ Build after each phase to verify compilation
- ✅ Update namespaces when moving files
- ✅ Keep feature logic out of ReaderWindow
- ✅ Use the workspace skills as your rulebook

### Things You MUST NOT Do
- ❌ Skip phases or combine them
- ❌ Create new shared UI components without asking
- ❌ Add feature-specific logic to ReaderWindow
- ❌ Change behavior (this is a refactor, not a rewrite)
- ❌ Leave compilation errors for "later"
- ❌ Make up your own folder structure

---

## Communication Protocol

### When Reporting Progress
Use this format:
```
Phase [X] complete:
- Created/moved: [files]
- Updated: [files]
- Build status: [green/errors]
- Next: Phase [X+1]
```

### When Reporting Problems
Use this format:
```
Stuck at Phase [X]:
- Error: [exact error message]
- Context: [what I was doing]
- Tried: [solutions attempted]
- Need: [guidance/clarification]
```

### When Asking Questions
Use this format:
```
Question about [topic]:
- Spec says: [quote from spec]
- Current situation: [what you're seeing]
- Options: [A or B]
- Recommend: [your suggestion]
```

---

## Final Checklist (Run Before Saying "Done")

- [ ] All phases complete
- [ ] Project builds with 0 errors, 0 warnings
- [ ] Manga Reader module launches
- [ ] Chapter loads and displays correctly
- [ ] Scrolling between chapters works
- [ ] Jump-to-chapter works
- [ ] All Reader features (Zoom, Dim, Fullscreen, etc.) work
- [ ] No console errors or exceptions
- [ ] Folder structure matches target in REFACTOR-SPEC.md
- [ ] All moved files have correct namespaces
- [ ] ReaderWindow is thin (no chapter loading logic)
- [ ] ChapterLoading is self-contained in Features/ folder

---

## Resources

- **Refactor Plan:** `C:\VSCODE\citadel\module\mangareader\Reader\REFACTOR-SPEC.md`
- **UI Skill:** `C:\VSCODE\citadel\.agents\skills\citadel-shared-ui\SKILL.md`
- **Modularity Skill:** `C:\VSCODE\citadel\.agents\skills\citadel-feature-modularity\SKILL.md`
- **Current Reader Code:** `C:\VSCODE\citadel\module\mangareader\Reader\`

---

## Progress Log

*(You fill this in as you work)*

### Phase 1: Create Folder Structure
- Status: Complete
- Notes: Created Reader/ReaderCore/ and Reader/Features/{ChapterLoading,Overlay,Drawer,Chrome,Toast,AutoScroll,Fullscreen,Pin,Zoom,Dim,Reset,ChapterNavigation}/. Baseline captured before any moves: citizen builds 0 warn/0 err; Reader test project builds 0/0; 91 tests pass, 0 fail. Empty folders do not affect the SDK-globbed citizen build or the explicit-path test build.

### Phase 2: Move Infrastructure to ReaderCore/
- Status: Complete
- Notes: git mv'd FrameContentHost.cs (incl. ReaderStatusHost), ReaderSessionState.cs, ReaderActivityHub.cs, ReaderInputRouter.cs, ReaderPreferencesStore.cs into ReaderCore/ and changed their namespace to Module.Mangareader.ReaderCore. ReaderCommandHub/ReaderNotificationHub were NOT moved (they live inside ReaderFeatureContract.cs, which the spec keeps unchanged at root). Created ReaderCore/ReaderChapterNavigationHub.cs (implements IReaderChapterNavigation, late-bound via RegisterImplementation, seeded with title+initial chapter so consumers are order-independent) — unused until Phase 4/5. Added `using Module.Mangareader.ReaderCore;` to 15 citizen consumers + 10 test consumers. Updated the test .csproj Include/Link paths for the 5 moved files and added the hub. Build: citizen 0/0, tests 0/0, 91/91 pass (behavior preserved).

### Phase 3: Move Features to Features/ Subfolders
- Status: Complete
- Notes: git mv'd feature files (namespaces unchanged = Module.Mangareader, so no consumer using-edits): Overlay(xaml+cs), Drawer(xaml+cs+Policy+Contributions), Chrome(controller), Toast(xaml+cs), AutoScroll(controller+policy), Fullscreen(controller+geometry), Pin, Zoom, Dim, Reset, ChapterNavigation into Features/<name>/. Cross-cutting helpers the spec did not assign a destination were intentionally LEFT at Reader/ root: ReaderValuePolicy, ReaderViewportStepPolicy, ReaderInputPolicy, ReaderViewportNavigator, CbzReaderChapterLoader (plus the unchanged catalog/contract/host/window/coordinator). Updated 14 test .csproj Include/Link entries to the new subfolder paths (Overlay is not linked in tests). Build: citizen 0/0, tests 0/0, 91/91 pass.

### Phase 4: Split ReaderChapterCoordinator
- Status: Complete (expand/contract 4a+4b; old file deleted in Phase 6)
- Notes: Created Features/ChapterLoading/{ChapterLoadingFeature,ChapterCoordinator,ChapterPreloader,ChapterNavigator}.cs. Feature = IReaderFeature + IReaderChapterNavigation + internal IChapterLoadingRuntime (owns lifetime CTS, load gate, tracked-async disposal; keeps internal AsyncResourcesDisposed/ActiveAsyncOperationCount). Coordinator owns surfaces/roles/active-index/evaluation + ActiveChapterChanged; Preloader owns render-config + neighbor ensures + boundary + resize; Navigator owns initial load + latest-wins jumps + PrepareNeighbors. Cycle broken via internal IChapterNeighborPreloader seam; logic moved verbatim to preserve behavior. Attach resolves the hub via `context.Chapters as ReaderChapterNavigationHub` (hub implements IReaderChapterNavigation) — deviates from the literal "add ChapterNavigation property" wording but meets all Q2 goals with zero consumer churn; flagged for review. Split the 10 coordinator tests into ChapterLoadingFeatureTests(6, incl. new hub-forwarding test)/ChapterNavigatorTests(2)/ChapterCoordinatorTests(2)/ChapterPreloaderTests(1); linked the 4 files in the test csproj. Old ReaderChapterCoordinator + its tests left intact and green (removed in Phase 6). Build: citizen 0/0, tests 0/0, 102/102 pass (91 prior + 11 new).

### Phase 5: Update ReaderWindow.cs
- Status: Complete
- Notes: ReaderWindow is now a thin composition root. It creates ReaderChapterNavigationHub(title, chapter), passes it as the context's IReaderChapterNavigation (context.Chapters), and feeds title/initialChapter/chapterLoader/status into ReaderDefaultFeatureCatalog.Create. It binds ChapterList.ItemsSource from hub.Surfaces AFTER the feature host attaches (live collection only exists post-registration), triggers the initial load via hub.StartLoadingAsync() in OnLoaded (preserving the after-viewport-width timing), and derives the title from the hub. Removed the _coordinator field, its StartLoadAsync call, its ActiveChapterChanged subscription, and its Dispose (the feature is disposed by _featureHost.Dispose). Hub gained StartLoadingAsync + a start callback in RegisterImplementation so the parent triggers loading without knowing feature internals. Build: citizen 0/0, tests 102/102.

### Phase 6: Update Catalog
- Status: Complete
- Notes: Catalog registers "ChapterLoading" FIRST (so it populates the hub before the ChapterNavigation UI attaches), constructed with title/initialChapter/chapterLoader/status/state via the Create closure. Relocated IReaderChapterLoader (was declared inside the deleted coordinator) into ReaderFeatureContract.cs (same Module.Mangareader namespace, so no reference churn). Deleted ReaderChapterCoordinator.cs and ReaderChapterCoordinatorTests.cs (git rm -f; substance preserved in the 4 new files + 4 new test files, proven by the passing suite). Retargeted ReaderCbzIntegrationTests (real-CBZ end-to-end) from the coordinator to ChapterLoadingFeature (identical ctor). Removed the coordinator link from the test csproj. Build: citizen full rebuild 0/0, tests 92/92 (102 − 10 superseded old-coordinator tests).

### Phase 7: Update Namespace Imports
- Status: Complete (satisfied incrementally)
- Notes: All `using Module.Mangareader.ReaderCore;` imports were added in Phase 2 as files moved. Because ReaderChapterNavigationHub implements IReaderChapterNavigation and flows through the existing context.Chapters, the ChapterNavigation UI feature and every other consumer required ZERO changes — they consume the hub transparently. Green citizen + test builds confirm no missing imports.

### Phase 8: Build & Test
- Status: In progress — automated verification green; full-solution build + live UI check blocked by a running app
- Notes: Citizen clean full rebuild = 0 warnings/0 errors. Reader test suite = 92/92 pass (behavioral + real-CBZ integration + hub forwarding). Full `dotnet build Citadel.slnx` FAILED only with MSB3026/MSB3027/MSB3021 file-lock copy errors: Citadel.Shell (PID 32040) is running and locking Citadel.Ui/Core/Setting/Contract/Searcher.dll in its own bin. Zero CS compilation errors — all projects compiled. Blocked pending: (a) close the running Citadel.Shell to complete a clean solution build, and (b) relaunch for the REFACTOR-SPEC manual UI checklist (open reader, load chapter, scroll/roll surfaces, jump-to-chapter, verify Zoom/Dim/Fullscreen/etc.), which cannot be claimed correct from compilation alone per citadel-shared-ui.

---

## Start Here

Read the REFACTOR-SPEC.md file now, then begin with Phase 1.

Good luck. Be thorough, not fast. Quality over speed.
