# Manga Reader tasklist

This file belongs to the Manga Reader citizen. It records only this screen's
work; it does not change Citadel Core or the shared module contract.

## Vertical slice — implemented

- [x] Register `Manga Reader` as an independently built citizen.
- [x] Keep all source under `module/mangareader/`.
- [x] Provide an internal tab container with `Scanner` as the first tab.
- [x] Let the user choose or type a manga-library path.
- [x] Scan immediate child folders and create one title card for each folder
      that contains CBZ files.
- [x] Sort title folders, chapter filenames, and page filenames naturally.
- [x] Open the first naturally sorted CBZ when a title card is clicked.
- [x] Open the reader in a separate maximized window.
- [x] Read and decode every supported image in the active chapter before the
      reader and scrollbar become interactive.
- [x] Render the complete chapter in one non-virtualized vertical page stack.
- [x] Keep decode work off the UI thread and freeze bitmaps before presentation.
- [x] Cancel scanning/loading and close the popup when the screen lifetime ends.
- [x] Show explicit empty, loading, and error states.

## Runtime proof — completed

- [x] Direct citizen build and deployment: 0 warnings, 0 errors.
- [x] CBZ smoke: one title, one chapter, three decoded pages.
- [x] Natural page order smoke: `page-1`, `page-2`, `page-10`.
- [x] Shell discovery: route `manga-reader` registered successfully.
- [x] Visible runtime: Scanner cards and a real CBZ chapter opened normally.
- [ ] User judgment: drag-scroll smoothness on representative long chapters.

## Deferred — define one by one before implementation

- [ ] Persist the selected library path.
- [ ] Decide cover extraction and title metadata.
- [ ] Decide chapter selection from a title with multiple CBZ files.
- [ ] Add the maximum-three rolling chapter surfaces:
      `A B` -> `A B C` -> `B C D`.
- [ ] Fully prepare the next chapter before automatic boundary crossing.
- [ ] Preserve scroll position atomically when the three-chapter window rotates.
- [ ] Add the temporary three-zone active-page overlay:
      Previous / Menu / Next; hide visuals while retaining hit areas.
- [ ] Define Previous and Next behavior for each reading mode.
- [ ] Define the Suwayomi-inspired menu controls one at a time.
- [ ] Define fullscreen chrome, menu pinning, fit modes, direction, and auto-scroll.
