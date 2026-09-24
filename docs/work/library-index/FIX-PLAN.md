# Library Index — remediation plan for the post-implementation review

Status: F-2 DONE. F-1 dilewati (opsional). F-3 smoke manual tetap terbuka (operator).
Sumber: review ulang implementasi `docs/work/library-index/PLAN.md` (inc 1–6).
Lima temuan Required dari review (Sanitize path, coordinator lock, watcher
off-UI, dispatcher guard, History memo) SUDAH diperbaiki dan hijau
(Library.Tests 83/83 stabil, full Release suite 0 gagal, 5 citizen 0E/0W).
File ini hanya mencakup SISA-nya: 2 perbaikan kode opsional + 1 smoke manual.

## Temuan yang sudah tutup (tidak dikerjakan lagi)

| # | Temuan review | Perbaikan | Bukti |
|---|---|---|---|
| 1 | Sanitize loloskan path relatif/malformed → crash History, ghost-key | Sanitize kanonikalisasi + tolak non-rooted | 2 test baru, 83/83 |
| 2 | Race pool-vs-UI di coordinator dict | Satu gate semua public method | transparan, 83/83 |
| 3 | Watcher imaging di UI thread | IO ke pool, sync grid marshal | build 0W/0E |
| 4 | Unobserved exception dispatcher saat shutdown | catch-non-OCE di assign | build 0W/0E |
| 5 | History re-resolve tiap Refresh (UI hitch) | Memo per snapshot, clear di SetLibrary | build 0W/0E |

## F-1 — First-page-only archive read (opsional, kecil-dampak-besar)

Thumbnail cache hari ini (`LibraryCoverCache.ReadCoverSource`) memanggil
`ArchivePageReader.ReadPages` yang memuat SEMUA halaman archive pertama ke
memori hanya untuk mengambil halaman pertama. Untuk chapter omnibus (GB-an)
itu transien besar sekali per title — sekali seumur hidup title, ter-cache
selamanya, di pool thread, app 64-bit — tapi tetap bom memori yang
bisa dihindari.

### Target behavior

- `EnsureThumbnail` untuk title tanpa `cover.*` hanya membaca SATU halaman
  pertama dari archive natural-first, bukan seluruh archive.
- RAR dan ZIP sama-sama didukung (atau RAR fallback eksplisit ke full-read
  dengan batas ukuran + warning — keputusan implementasi, bukan plan).

### Perubahan

- Owner: `shareLogic/Archive/ArchivePageReader.cs` (+ method, mis.
  `ReadFirstPage`) dan `Library/LibraryCoverCache.cs` (pakai method baru).
- Tidak ada perubahan kontrak keluar: `ILibraryCoverThumbnails` tetap.
- Tidak ada perubahan fingerprint/selection (R-02 tidak tersentuh).

### Acceptance

- Test baru di `LibraryCoverCacheTests`: cbz berisi 1 halaman kecil + 1
  entry sampah besar (mis. 200MB zero-bytes, stored tanpa kompresi agar
  file test kecil? TIDAK — zero bytes terkompresi jadi kecil di disk tapi
  tetap besar saat dibaca; justru itu yang diukur) → `EnsureThumbnail`
  berhasil dengan peak working set jauh di bawah ukuran entry sampah.
  Ukur via `GC.GetTotalMemory` sebelum/sesudah, atau assert halaman yang
  dipakai = halaman natural-first (kontrak), dan peak dialokasikan
  proporsional halaman, bukan archive.
- Full suite + citizen tetap hijau.

### Non-goal F-1

- Tidak mengubah urutan natural, format RAR engine, atau kualitas JPEG.

## F-2 — Watcher overflow memicu reconcile tenang (kecil)

`FileSystemWatcher` buffer overflow (ledakan tulis massal) hari ini hanya
restart watcher — event yang hilang diam-diam menunggu Scan/restart
berikutnya. Itu sesuai desain (watcher = bonus), tapi bisa ditutup murah:

### Target behavior

- `LibraryIndexWatcher.Error` → event baru `ResyncRequested` → view
  menjalankan reconcile pool-thread + sync grid yang sama dengan watcher
  sync (tanpa dismiss detail yang tidak terdampak).
- Tidak ada status gaduh: status teks biasa seperti watcher sync.

### Perubahan

- Owner: `Library/LibraryIndexWatcher.cs` (+ event) dan
  `Library/LibraryView.xaml.cs` (subscribe, panggil path reconcile yang
  sudah ada — JANGAN duplikasi logika sync).
- Test: `Error` tidak bisa dipicu deterministik tanpa timing → catat sebagai
  gap smoke (F-3), bukan unit test yang meniru kode.

### Acceptance

- Build + suite hijau; tidak ada perilaku berubah saat tidak overflow
  (path normal tak tersentuh).

### Hasil eksekusi (DONE)

- `LibraryIndexWatcher`: event `ResyncRequested` naik dari `RestartWatcher`
  hanya saat watcher hidup kembali (di luar gate agar tak block teardown).
- `LibraryView`: subscribe di `RestartWatcher` (view); handler reconcile
  pool-thread + sync UI tanpa recreate watcher, tanpa busy chrome; detail
  terbuka hanya tertutup bila title-nya hilang dari index.
- Verifikasi aktual: module 0W/0E (2 warning CS4014 sempat muncul,
  diperbaiki via discard eksplisit), full Release suite 0 gagal semua
  project. Jalur overflow tak bisa dipicu deterministik — gap dicatat,
  perilaku normal terbukti tak berubah oleh suite.

## F-3 — Smoke manual: visual + watcher live (operator)

Unit + build tidak membuktikan tampilan. Jalankan sekali di shell asli
(`bump-build-run.bat`), catat PASS/FAIL per baris di file ini:

| # | Langkah | Diharapkan |
|---|---|---|
| S1 | Cold start module dengan library besar | Grid tampil dari index dalam sekejap; status `Loaded N indexed titles`; TIDAK ada scan chapter library-wide (cek: CPU/IO idle cepat) |
| S2 | Tunggu reconcile latar | Status `Checking library changes…` → count final; grid stabil |
| S3 | Klik satu title | Status `Loading chapters…` → detail chapter tampil; klik ganda saat loading tidak ganda |
| S4 | Hapus folder title dari Explorer saat grid tampil | Card hilang sendiri + status count turun (watcher), tanpa Scan |
| S5 | Copy title baru dari luar | Card muncul sendiri + cover terisi menyusul (thumbnail cache) |
| S6 | Buka History | Record lama resolve (cover + resume); tidak ada hang |
| S7 | Cover Builder → pilih title → preview → bake | Preview dari title terpilih saja; bake jalan seperti dulu |
| S8 | Kombinasi: detail terbuka + title itu berubah di disk | Detail tertutup + status suruh buka ulang; detail lain tetap terbuka |
| S9 | Scan manual setelah perubahan di USB/network share | Grid sinkron tanpa watcher (bukti reconcile primer) |

### Exit criteria F-3

- Semua baris PASS, atau yang FAIL difilekan sebagai finding baru dengan
  repro (bukan diperbaiki diam-diam di tempat).

## Urutan eksekusi

```text
F-1 (opsional, bisa dilewati bila omnibus belum nyata)
  │
  ▼
F-2 (kecil, independen — bisa sebelum/sesudah F-1)
  │
  ▼
F-3 (smoke operator — terakhir, di atas build final)
  │
  ▼
(opsional, hanya bila operator minta) commit-push-release.bat
```

Satu increment tidak mewajibkan commit maupun full suite — yang wajib: check
perilaku increment itu hijau sebelum dependennya mulai. F-3 tidak mulai
sebelum kode yang di-smoke adalah build final.

## Peta file

| Area | File |
|---|---|
| F-1 | `shareLogic/Archive/ArchivePageReader.cs`, `Library/LibraryCoverCache.cs`, `tests/.../LibraryCoverCacheTests.cs` |
| F-2 | `Library/LibraryIndexWatcher.cs`, `Library/LibraryView.xaml.cs` |
| F-3 | file ini (hasil smoke) |
| Tidak disentuh | kontrak event, card model, store, coordinator, History/CoverBuilder flows |

## Risiko

| Risiko | Mitigasi |
|---|---|
| F-1 menyentuh RAR engine path | RAR fallback eksplisit + test ZIP dulu; RAR hanya bila engine mendukung baca parsial |
| Test memori flaky di CI | Assert proporsional + halaman benar, bukan angka byte absolut |
| F-2 duplikasi logika sync | Wajib reuse path sync watcher yang ada; reviewer menolak duplikasi |
| Smoke menemukan defect visual | File sebagai finding baru, jangan campur ke F-1/F-2 |

Tidak ada commit sampai diminta eksplisit.
