# Manga Reader tasklist

This file belongs to the Manga Reader citizen. It records only this screen's
work; it does not change Citadel Core or the shared module contract.

## Vertical slice — implemented

- [x] Register `Manga Reader` as an independently built citizen.
- [x] Keep all source under `module/mangareader/`.
- [x] Provide an internal tab container with `Library` as the first tab.
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
- [x] Cancel scanning/loading and close the reader window when the screen lifetime ends.
- [x] Show explicit empty, loading, and error states.

## Runtime proof — completed

- [x] Direct citizen build and deployment: 0 warnings, 0 errors.
- [x] CBZ smoke: one title, one chapter, three decoded pages.
- [x] Natural page order smoke: `page-1`, `page-2`, `page-10`.
- [x] Shell discovery: route `manga-reader` registered successfully.
- [x] Visible runtime: Library cards and a real CBZ chapter opened normally.
- [x] User judgment: drag-scroll is smooth on a representative long chapter.

## Natural-width directional cache — approved plan

- [x] Remove the fixed reader-column width.
- [x] Read each page's native pixel dimensions before choosing its render size.
- [x] Never upscale a full-quality page beyond its native pixel width.
- [x] Downscale proportionally only when a page is wider than the available
      fullscreen viewport; never distort its aspect ratio.
- [x] Centre different natural page widths inside the fullscreen dark gutter.
- [x] Persist screen-sized and preview-sized render results under
      `%LocalAppData%\Citadel\MangaReader\cache` using a source fingerprint.
- [x] Keep a maximum of three connected chapter surfaces with distinct role
      z-index values: previous, active, next.
- [x] Keep the active chapter fully decoded.
- [x] Preload the next chapter fully and aggressively before the boundary.
- [x] Demote the previous chapter to a low-resolution full preview while
      retaining a small full-quality tail at the chapter boundary.
- [x] When reading direction reverses, promote the previous chapter to full
      quality before continuing backward and demote forward work.
- [x] Preserve page dimensions in every quality tier so replacing preview with
      full quality never changes the scrollbar extent.
- [x] Preserve the visible scroll position when inserting or removing a chapter
      above the viewport.
- [x] Release chapter references as soon as they leave the three-surface window.
- [x] Verify Debug/Release build, cache reuse, chapter rotation, and visible
      natural-width rendering before requesting user judgment.

## Library covers and chapter selector — approved plan

- [x] Use the first supported image from the first naturally sorted chapter as
      the title cover; a missing or broken cover must not hide the title.
- [x] Decode only a card-sized cover off the UI thread, freeze it before
      presentation, and reuse the module render cache when possible.
- [x] Keep title metadata local and deterministic: folder title, chapter count,
      first chapter, latest chapter, and folder path.
- [x] Show the cover and compact metadata on each Library card.
- [x] Rename the first tab from `Scanner` to `Library`.
- [x] Open the title-detail selector as an overlay inside the Library content
      area; only the reader owns a separate window.
- [x] List every chapter in natural order and open the exact selected chapter in
      the existing rolling reader window.
- [x] Cancel unfinished cover work and dismiss the selector when the screen
      lifetime ends.
- [x] Verify the module build and the visible card → selector → chosen chapter
      path before requesting user judgment.

## History — approved plan

- [x] Keep each tab in a same-named source folder (`Library/`, `History/`,
      `CoverBuilder/`) and the dedicated reader surface in `Reader/`, while
      cross-feature code stays in root-owned `shareLogic/` and reusable UI
      stays at the Manga Reader root.
- [x] Keep History in its own tab so Library ordering remains predictable.
- [x] Persist the last opened chapter and timestamp under the Manga Reader's
      LocalAppData folder.
- [x] Reuse the title-card treatment while ordering History by most recently
      opened and labelling each card with `Last read`.
- [x] Resume the recorded chapter from a History card without adding another
      screen or window.
- [x] Keep History logic and presentation in their own files.

## Cover Builder — approved plan

- [x] Keep Cover Builder in its own tab and files.
- [x] Select a scanned title and accept either a local image path or an HTTP(S)
      image URL.
- [x] Keep URL handling as an explicit two-step flow: `Fetch` downloads with a
      bounded size, validates and converts the image, then saves it under
      Manga Reader LocalAppData. Enable `Bake cover` for that URL only after a
      successful fetch; local image paths remain directly bakeable.
- [x] Convert the chosen source to one deterministic generated PNG page and
      place it first in the earliest naturally sorted chapter.
- [x] Stream the rewritten CBZ through a same-folder temporary file, preserve
      all other entries, and replace the original only after completion.
- [x] Save a recoverable original CBZ backup under the module's LocalAppData
      before replacement.
- [x] Add a refresh action beside the Manga Reader content-header title so
      Library, History, and Cover Builder can reload the latest library state.
- [x] Refresh the title cover after a successful bake.
- [x] Keep source loading, archive writing, and Cover Builder UI in separate
      files.
- [x] If another user application locks the first chapter during replacement,
      use Windows Restart Manager to close that exact locker (forcing an
      unresponsive app only after the normal shutdown request). Never stop
      Citadel, Explorer, a Windows service, or a critical process; no private
      archive dependency is needed for ordinary CBZ files.

## Deferred — define one by one before implementation

- [ ] Persist the selected library path.
- [x] Add a shared card-presentation sub-feature under `shareLogic/`:
      normalize underscores in folder titles to spaces for display only, collapse
      repeated whitespace, and expose the compact Library/History card data.
- [x] Render Library and History covers in one fixed `2:3` frame with no empty
      bands or crop; allow slight stretch so every card remains uniform.
- [x] Move the normalized title into a fixed-height bottom cover overlay with a
      dark gradient, top-align it within a maximum of two wrapped lines, and
      clip any overflow after the second line. Remove `First chapter` and `Latest chapter` from
      the cards; retain chapter count and History's `Last read` progress only.
- [x] Add session-only `Ctrl + mouse wheel` zoom inside `Reader/`: ordinary
      wheel input must keep scrolling; zoom the joined chapter surface without
      resizing the reader window or scaling reader chrome/overlays.
- [x] Keep the content point beneath the pointer anchored while zooming, clamp
      the zoom range, coalesce rapid wheel input, and avoid re-decoding chapter
      images for each zoom step. Clamp it to `50%–300%` in `10%` steps and use
      `Ctrl+0` to reset to `100%`; future fit modes remain separately deferred.
- [ ] Add the temporary three-zone active-page overlay:
      Previous / Menu / Next; hide visuals while retaining hit areas.
- [ ] Define Previous and Next behavior for each reading mode.
- [ ] Define the Suwayomi-inspired menu controls one at a time.
- [ ] Define fullscreen chrome, menu pinning, fit modes, direction, and auto-scroll.
