# Reader continuous scroll — smoothness

## Result

Perpindahan chapter saat continuous scroll tidak lagi memaksa dua synchronous
layout pass, koreksi scroll position tidak lagi memakai pixel delta yang bisa
meleset, dan pelepasan decoded bitmap tidak lagi terjadi di frame yang sama dengan
forced layout.

Observable reading behavior tidak berubah: active chapter tetap ditentukan oleh
viewport center, chapter di luar rolling window tetap hilang dari scroll, dan
history tetap satu commit per active-chapter change.

## Owner

- `module/mangareader/shareLogic/ChapterSurfaceModel.cs` — two-phase surface (live / evicted)
- `module/mangareader/shareLogic/ChapterLoadingContracts.cs` — `LoadedPage.Bitmap` jadi nullable
- `module/mangareader/Reader/Features/ChapterLoading/ChapterCoordinator.cs` — boundary transaction
- `module/mangareader/Reader/Features/ChapterLoading/ChapterPreloader.cs` — prepare-vs-insert split
- `module/mangareader/Reader/Features/ChapterLoading/ChapterNavigator.cs` — forward real bitmap estimate
- `tests/Module.Mangareader.Reader.Tests/` — coverage for the new boundary mechanics

## Problem, dengan bukti

### Dua synchronous layout pass per boundary

`ChapterCoordinator.EvaluateActiveSurfaceAsync` menjalankan:

1. `RemoveOutsideRollingWindow()` -> remove + `Viewport.UpdateLayout()` + correction
2. `await EnsureNextFullAsync(...)` -> `AddSurfaceOrdered` -> insert + `Viewport.UpdateLayout()` + correction

`ChapterCoordinator.cs:148-158`, `:188-207`, `:209-230` (sebelum perubahan)

Karena chapter panel dan page panel keduanya `StackPanel` non-virtualized
(`ReaderWindow.xaml:45-49`, `:68-72`), setiap `UpdateLayout()` meng-measure
seluruh strip.

### Pixel-delta correction

```csharp
_scroll.ScrollToVerticalOffset(Math.Max(0, oldOffset - removedHeight), ReaderActivityOrigin.LayoutRestore);
```

`ChapterCoordinator.cs:225-227` (sebelum perubahan), dan `oldOffset + RenderedHeight(surface)`
di `:203-205`.

`removedHeight` dihitung dari `RenderedHeight` yang memakai `container.ActualHeight`
kalau container tersedia, kalau tidak `surface.SurfaceHeight * ZoomScale`
(`ChapterCoordinator.cs:243-248`). Dua sumber itu bisa berbeda, dan ketika remove
dan insert terjadi di dua momen terpisah, delta-nya tidak dijamin konsisten dengan
komposisi akhir strip.

### Pelepasan bitmap di frame yang sama dengan forced layout

`_surfaces.Remove(surface)` melepaskan satu-satunya reference ke
`LoadedChapter` -> `LoadedPage.Bitmap` (`ChapterSurfaceModel.cs:15`, `:32`).
`ChapterRenderCache` disk-only (`ChapterRenderCache.cs:45-49`) dan
`ChapterPreloader` tidak menyimpan chapter (`ChapterPreloader.cs:18-19`), jadi
memang tidak ada cache lain. Buffer pixel yang unmanaged menjadi collectible tepat
pada frame yang menjalankan forced layout.

## Changes

### 1. `LoadedPage.Bitmap` nullable

`ChapterLoadingContracts.cs:29-36`. `Bitmap` jadi `BitmapSource?`.

Alasan: surface yang sudah di-evict tetap expose page list dengan tinggi yang
sama tetapi tanpa bitmap. XAML `Source="{Binding Bitmap}"` tidak berubah
(`ReaderWindow.xaml:76`). Tidak ada consumer C# yang men-dereference `Bitmap`.

### 2. `ChapterSurfaceModel` two-phase

`Evict()` melepas `_content` dan menyimpan page metadata. `SurfaceWidth`,
`SurfaceHeight`, `Chapter`, dan `Pages` tetap dapat dibaca, sehingga
`RenderedHeight` dan binding XAML tidak berubah.

`IsFullQuality` jadi `false` setelah evict. Ini invariant yang dipakai
`ChapterNavigator.NavigateToChapterCoreAsync` yang memilih reuse surface
existing daripada load ulang — surface evicted tidak boleh direuse karena
bitmaps-nya sudah dilepas.

`Evict()` hanya aman pada surface yang sudah detach dari `_surfaces`. Yang
dipakai di sini sudah selalu lewat `DetachOutsideRollingWindow`, jadi surface
sudah keluar dari visual tree dan evict tidak bisa meng-invalidasi layout.

### 3. Boundary transaction di coordinator

`EvaluateActiveSurfaceAsync` berubah dari dua layout pass menjadi satu:

```
anchor  = CaptureScrollAnchor()
detached= DetachOutsideRollingWindow()        // remove saja, tanpa layout
next     = await Preloader.PrepareNextAsync() // load tanpa insert
previous = await Preloader.PreparePreviousAsync()
Insert(next) / Insert(previous)                // tanpa layout
Viewport.UpdateLayout()                        // SATU pass
RestoreScrollAnchor(anchor)
ScheduleDeferredEviction(detached)
```

`AddSurfaceOrdered` tetap ada untuk path yang lain (initial load, overlay
`PrepareBoundaryAsync`) dan tetap melakukan layout + correction sendiri.

### 4. Anchor-based correction

`CaptureScrollAnchor()` mencatat chapter mana yangADA di bagian atas viewport
plus jaraknya. `RestoreScrollAnchor` mengukur ulang posisi chapter itu dari
komposisi akhir:

```csharp
var found = TryTopOfSurface(anchor.ChapterIndex, out var top);
var target = found ? Math.Max(0, top + anchor.OffsetWithinSurface) : 0d;
```

Karena position dihitung ulang dari komposisi akhir, remove dan insert yang
sekaligus tidak bisa saling salah hitung. Kalau anchorya sudah keluar dari
window, target-nya 0 — sama dengan clamp `Math.Max(0, ...)` di kode lama.

Correction hanya dipancarkan kalau target beda lebih dari 0.5px dari offset
sekarang, jadi tidak ada scroll event sia-sia.

### 5. Deferred eviction

Surface yang keluar dari window di-`Evict()` setelah transaction selesai, lewat
satu dispatcher post di `DispatcherPriority.Background`.

`Evict()` dipanggil pada surface yang sudah tidak ada di `_surfaces`, jadi tidak
ada layout invalidation sama sekali dan pixel buffer dibebaskan setelah forced
layout selesai, bukan di dalamnya.

Hold berakhir saat dispatcher kembali ke background work, bukan menunggu scroll
berikutnya. Kalau menunggu input, reader yang berhenti scroll akan menahan satu
chapter penuh selama itu masih hidup.

Hold dibatasi satu batch: crossing kedua sebelum flush akan melepas batch
pertama, supaya chapter yang lebih pendek dari viewport tidak menumpuk.

Tidak ada surface evicted yang masih reachable dari `SurfaceAt`, karena evict
selalu terjadi setelah surface keluar dari `_surfaces`. Sebagai pengaman kedua,
`IsFullQuality` sudah `false` setelah evict, sehingga jalur reuse di
`ChapterNavigator.cs:133` otomatis jatuh ke load ulang dan tidak pernah memakai
page tanpa bitmap.

## Deliberately out of scope

| Item | Alasan |
|---|---|
| Last-page boundary trigger | Mengubah kapan active chapter flip = mengubah behavior |
| Monotonic window + visible stubs | User bisa scroll ke area kosong = mengubah behavior |
| Per-page lazy image loading | Ditolak operator |
| Virtualized `StackPanel` | Item terpisah; satu `UpdateLayout()` masih O(all pages) |
| Early N+2 prefetch | Butuh concurrency guard pada `EnsureNextFullAsync` yang saat ini aman karena selalu awaited berurutan |
| Reload evicted chapter dari disk cache | Tidak ada surface evicted di DOM pada desain ini |

## Review findings yang sudah diperbaiki

| # | Severity | Temuan | Perbaikan |
|---|---|---|---|
| F1 | Critical | Kalau load neighbor gagal, `UpdateLayout` / `RestoreScrollAnchor` / `ScheduleDeferredEviction` dilewati karena berada di dalam `try`. Reader tertinggal di scroll offset yang salah dan chapter yang sudah detach tidak pernah di-evict sampai session ditutup. | Pindahkan settle ke `finally`. |
| F2 | High | Reference point eviction di-overwrite setiap crossing. Chapter yang lebih pendek dari viewport membuat reference ikut bergerak, jadi hold tidak pernah jatuh dan pending list menumpuk tanpa batas. | Hold di-trigger dispatcher post, bukan jarak scroll. |
| F3 | High | Eviction hanya event-driven. Reader yang berhenti scroll menahan chapter yang sudah detach. | Dispatcher post. |
| F4 | Medium | `ClearSurfaces()` evict sebelum collection dikosongkan, memaksa `ItemsControl` membaca `Pages` kosong untuk strip yang akan hilang. | Clear dulu, baru evict. |
| F5 | Low | `ChapterNavigator` fabricate `EstimatedBitmapBytes: 0` saat reuse surface, padahal nilainya tersedia. | Forward `existing.EstimatedBitmapBytes`. |
| F6 | Low | `ChapterSurfaceModel.Quality` public property tidak dipakai consumer mana pun. | Dihapus; `IsFullQuality` pakai field `_quality`. |
| F7 | Low | `TopOfSurface` dan `RestoreScrollAnchor` duplikasi loop walk surfaces. | `TryTopOfSurface` dipakai keduanya. |
| F8 | Low | `DetachOutsideRollingWindow()` berada di luar `try`, padahal versi lama berada di dalam, jadi error containment melemah. | Pindahkan ke dalam `try`. |
| F9 | Low | `ApplyDueEviction` + `_evictAfterVerticalOffset` jadi dead code setelah F2/F3 diperbaiki: dispatcher post selalu jalan lebih dulu karena FIFO within priority. | Dihapus. |

## Acceptance criteria

| # | Kriteria | Status |
|---|---|---|
| 1 | `RollingWindow_RotatesForwardAndReverseAndEmitsHistoryOncePerCommit` hijau tanpa perubahan assertion | PASS |
| 2 | `BoundaryPreparation_InsertsPreviousAndPreservesViewportAnchor` hijau | PASS |
| 3 | Satu `UpdateLayout()` per boundary, bukan dua | PASS |
| 4 | `RemoveOutsideRollingWindow` tidak lagi ada | PASS |
| 5 | Surface evicted tidak pernah dikembalikan ke `_surfaces` | PASS |
| 6 | Reader suite hijau | PASS — 119/119 |
| 7 | Full solution suite hijau | PASS — 1103/1103 |

## Verification notes

Setiap klaim "satu layout pass" dan "hold selesai setelah layout" diuji dengan
membalik fix-nya sebentar dan memastikan test gagal:

| Fix dibalik | Test yang gagal | Bukti |
|---|---|---|
| dua `UpdateLayout()` | `CostsOneLayoutPassAndLeavesAnUnmovedAnchorAlone` | Expected 1, Actual 2 |
| evict langsung di boundary | `HoldsTheDetachedChapterBitmapsUntilTheReaderMovesOn` | Expected False, Actual True |
| settle dipindah keluar `finally` | `SettlesTheStripEvenWhenTheNewNeighbourFailsToLoad` | Expected 1200, Actual 2200 |

Satu test ekspektasi awal saya salah: `ChapterJump_ReleasesPendingEvictionInsteadOfKeepingTheOldChapter`
mengira offset akhir 0. Nilai sebenarnya 2000, dan sama dengan perilaku kode lama
sebelum perubahan — chapter target berada di coordinate 2000 di strip karena
chapter sebelumnya ada di atasnya. Assertion dikoreksi, bukan kodenya.

Belum terverifikasi: perilaku visual di aplikasi. Semua test memakai `TestViewport`
dengan `ItemContainerFor` yang selalu `null`, jadi `RenderedHeight` selalu jatuh ke
`surface.SurfaceHeight * ZoomScale` dan tidak pernah mengukur container WPF
sungguhan. Butuh smoke test manual di Shell.
