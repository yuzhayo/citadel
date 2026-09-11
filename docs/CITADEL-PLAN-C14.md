# PLAN C14/D22 — koreksi diagnosa + pemisahan aturan koordinasi

**Status: METODE TERTULIS. Eksekusi hanya setelah dokumen ini ada (aturan pelaksanaan 2026-09-11).**

## 1. Analisa (membaca `MangaReaderView.xaml.cs` penuh, 412 baris)

Isi parent terbagi empat pekerjaan:

| Pekerjaan | Baris | Vonis FM-5 |
| --- | --- | --- |
| Komposisi & wiring 6 tab + fitur | 36-186 | **koordinasi** — sah di composition root |
| Navigasi tab & refresh & open-reader | 188-322 | **koordinasi** — sah |
| Aturan koordinasi lintas-feature: `CheckCatalogAvailability`, `ConfirmQueueTarget`, `CatalogTab_QueueHandoffRequested`, `ReadConfirmedMapping`, `EnqueueUpdate` | 116-160, 219-256, 329-379 | **koordinasi lintas-feature** — sah di root, TAPI saat ini tercampur di dalam file komposisi sehingga satu file memegang dua pekerjaan (D22) |
| Lifecycle/disposal | 381-411 | **koordinasi** — sah |

**Koreksi terhadap framing lama (C14/D22 di CITADEL-VIOLATIONS):** memindahkan blok-blok
ini ke dalam satu feature adalah **salah**, karena masing-masing menyentuh 2–3 feature
sekaligus (CatalogMirror+Downloader+Library+UI). FM-5 menempatkan kalkulasi satu-feature
di feature, tetapi **kebijakan lintas-feature di composition root**. Memaksanya masuk ke
satu feature juga akan menciptakan tepi lintas-feature baru yang justru ditolak oleh
guard Level-B. Jadi parent tidak melanggar FM-5 dengan memegangnya; pelanggarannya hanya
ST-1 (satu file dua pekerjaan).

## 2. Metode (transformasi persis)

1. File BARU `module/mangareader/MangaReaderHandoffs.cs`, namespace `Module.Mangareader`,
   `static class MangaReaderHandoffs`, memuat lima aturan koordinasi sebagai method
   statis dengan dependensi eksplisit sebagai parameter:
   - `ReadConfirmedMapping(DownloadSourceIndex index, string folderName)`
   - `EnqueueUpdate(UpdateDownloadRequest request, DownloadQueueFeature queue)`
   - `CheckCatalogAvailability(ListerFeature lister, CatalogLocalAvailabilityRequest request, CancellationToken token)`
   - `ConfirmQueueTarget(LibraryRootContext libraryRoot, DownloadSourceIndex index, Func<Window> ownerWindow, CatalogQueueTargetRequest request)`
   - `QueueHandoff(CatalogQueueHandoffRequest handoff, DownloadQueueFeature queue, Func<Window> ownerWindow)` mengembalikan bool sukses/penanganan-persistence
   Isi method = pemindahan verbatim badan method parent (perilaku identik, nol logika baru).
2. `MangaReaderView.xaml.cs`: hapus lima badan tersebut; panggil
   `MangaReaderHandoffs.*` dengan dependensi yang sudah dimiliki parent.
   `ConfirmQueueTarget`/`CheckCatalogAvailability` tetap dilewatkan sebagai delegate ke
   `CatalogMirrorDetailFeature` / `CatalogTab.UseQueueTarget`, kini berupa lambda tipis
   yang meneruskan ke handoffs.
3. Tidak ada perubahan: namespace feature, interface, delegate contract, csproj,
   perilaku, dan tepi import (file baru berada di module root = composition root,
   satu-satunya tempat yang boleh meng-import semua feature).
4. Guard Level-B tidak berubah dan harus tetap hijau (file baru bukan di dalam
   `Features/*` maupun `shareLogic/`, jadi tidak menambah tepi yang dijaga).

## 3. Verifikasi

- `dotnet test Citadel.slnx` → 929 test hijau (termasuk 3 guard test).
- `dotnet build module/mangareader/Module.Mangareader.csproj` sukses.
- `MangaReaderView.xaml.cs` turun dari 412 baris menjadi ±290 baris; seluruh aturan
  koordinasi punya satu rumah bernama.

## 4. Rollback

Satu commit; `git revert` mengembalikan keadaan checkpoint `b7ba530`.

## 5. Yang TIDAK dilakukan

- Tidak memindah logika ke dalam feature (salah secara FM-5, ditolak guard).
- Tidak memperkenalkan katalog/gate seragam untuk 6 tab (itu refactor interface besar;
  tercatat terpisah sebagai sisa C14, bukan bagian plan ini).
- Tidak mengubah perilaku apa pun.
