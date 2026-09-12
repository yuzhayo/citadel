# Manga Reader — Offline Catalog dan Standalone Download Queue

Status: **implementation plan only**. Dokumen ini bukan izin implementasi, version bump, commit, push, atau release.

## 0. Yuzskill contract

Plan ini dikunci dengan:

- `planning-and-task-breakdown`: setiap phase adalah vertical slice yang bisa diuji sendiri;
- `api-and-interface-design`: kontrak snapshot/provider/queue ditetapkan sebelum implementasi;
- `citadel-feature-modularity`: parent hanya composition dan navigation;
- `citadel-shared-ui`: reuse komponen/presentation yang sudah ada;
- `incremental-implementation`: satu phase selesai sebelum phase berikutnya;
- `code-review-and-quality`: tidak ada keputusan opsional atau file tersembunyi yang baru diketahui saat implementasi.

Aturan umum:

- Jangan membuat shared UI primitive baru.
- Jangan mengubah transport/browser architecture.
- Jangan membuat queue atau worker kedua.
- Jangan mengisi `MangaReaderView` dengan parsing, sync, query, atau queue policy.
- Jika live provider contract berbeda dari kontrak yang dikunci di sini, eksekusi berhenti sebagai external blocker; agent tidak boleh menebak endpoint/field pengganti.

## 1. Goal terkunci

Tambahkan dua tab top-level:

1. **Catalog**
   - Mengunduh index seluruh title Comix ke PC melalui explicit `Sync Catalog`.
   - Search, filter, sort, dan pagination index berjalan lokal tanpa request web.
   - Setiap title menyimpan provider identity, canonical URL, cover URL, dan metadata browse yang tersedia.
   - Detail, source group, dan chapter hanya diminta online setelah user membuka satu title.
   - Title yang pernah dibuka mendapat enrichment detail dan cover cache lokal.
   - Chapter terpilih diserahkan ke queue yang sudah ada.

2. **Download Queue**
   - Menampilkan `DownloadListScreen` yang sudah ada sebagai tab top-level mandiri.
   - Tetap memakai satu `DownloadQueueFeature`, satu `queue.json`, dan satu queue worker.
   - Screen Queue menerima dependency Queue saja, bukan seluruh `DownloaderContext`.

Urutan dan visible header tab:

`Library | History | Cover Builder | Downloader | Catalog | Queue`

Header `Queue` wajib memakai `AutomationProperties.Name="Download Queue"` dan tooltip `Download Queue`. Keenam `TabItem` memakai local `Padding="12,9"`; jangan mengubah `SettingTabs` shared style. Header tidak wrap dan wajib lolos smoke pada minimum supported window width tanpa overlap atau clipping.

## 2. Scope yang tidak boleh melebar

Tidak termasuk:

- direct decoding atau provider Comix baru;
- perubahan PyHost protocol, Python browser plugin, profile, challenge solver, atau browser type;
- parallel browser session atau browser process kedua;
- perubahan Start/Stop/filters/selection pada online Downloader;
- perubahan queue state machine, retry, resume, page ordering, download pipeline, atau CBZ compiler;
- perubahan Library scan, History, Cover Builder, Reader, Auto Cover, sidebar, CamoProf, FTF, atau Proxy;
- bulk chapter download atau bulk cover download;
- full-catalog genre/format/demographic/author/artist crawl;
- SQLite/NuGet/dependency baru — kecuali tepat `Microsoft.Data.Sqlite` 10.0.11 untuk Catalog Mirror (revisi SQLite 2026-09-08, D0.1, otorisasi operator);
- background scheduler, auto-sync, notifications, atau cloud sync;
- version bump, commit, push, atau release.

## 3. Product contract

### 3.1 Catalog adalah index metadata lokal

Network hanya boleh terjadi melalui aksi berikut:

- `Sync Catalog` atau `Resume Sync`: mengambil browse pages;
- membuka satu title: mengambil detail, groups, chapters, dan satu cover title;
- mengganti source group pada detail: mengambil chapters group tersebut;
- queue worker existing: menjalankan download yang memang dipilih user.

Aksi berikut harus 100% lokal:

- membuka tab Catalog ketika snapshot sudah ada;
- search;
- filter;
- sort;
- pagination hasil;
- kembali dari detail ke hasil sebelumnya.

### 3.2 Filter lokal versi pertama

Filter offline yang wajib dan lengkap:

- content rating: Safe, Suggestive, Erotica, Pornographic;
- type;
- status;
- original language;
- year from/to;
- minimum latest chapter.

Search lokal mencakup display title dan alternate titles.

Sort lokal yang wajib:

- Title A–Z;
- Title Z–A;
- Year newest/oldest;
- Latest chapter highest/lowest.

Genre, format, demographic, author, dan artist **bukan filter global Catalog versi pertama**. Field tersebut boleh tampil pada title yang sudah mendapat enrichment, tetapi tidak boleh menghasilkan filter partial yang terlihat lengkap. Full provider filters tetap tersedia pada online Downloader.

Tidak ada “optional facet sweep” dalam implementation plan ini.

### 3.3 Empat partition rating

Base sync tidak mengirim empat rating dalam satu request. Sync menjalankan empat partition berurutan:

1. `safe`
2. `suggestive`
3. `erotica`
4. `pornographic`

Setiap partition memakai captured Comix query `content_rating[]=<rating>` dan sort `title_asc`. Dengan demikian rating setiap record diketahui dari request partition tanpa detail call atau facet sweep tambahan.

Tidak ada filter lain pada sync request. Page size tetap milik provider contract.

### 3.4 Arti “seluruh catalog”

Comix tidak menyediakan snapshot token point-in-time. Karena catalog dapat berubah selama ribuan request, “seluruh catalog” berarti:

- keempat rating partition telah ditelusuri sampai provider memberi `HasMore=false`;
- tidak ada page invalid, loop, atau contract error;
- semua record mempunyai identity valid;
- record dideduplikasi secara deterministik.

`meta.total` dicatat sebagai diagnostic awal/akhir, tetapi exact equality tidak menjadi syarat activation. UI menampilkan jumlah unique title yang benar-benar tersimpan dan waktu capture, bukan menjanjikan point-in-time consistency yang provider tidak sediakan.

### 3.5 Cover

- Base sync hanya menyimpan `CoverUrl`; tidak mengunduh gambar.
- Card yang belum pernah dibuka memakai placeholder existing.
- Setelah title dibuka, Catalog mengambil maksimal satu cover untuk title tersebut dan menyimpannya ke enrichment cache.
- Search/filter/sort/pagination tidak pernah memicu cover request.
- Cover failure tidak menggagalkan detail, chapter list, snapshot, atau queue.

### 3.6 Refresh incremental / delta (revisi sync improvement 2026-09-08)

Full Sync (empat partition `title_asc` sampai terminal) adalah backfill manual yang jarang. Operasi rutin adalah Refresh: traversi `latest`-first (`order[chapter_updated_at]=desc`, captured sort `latest`) per partition dengan single rating, berhenti setelah **satu page penuh tanpa identity baru** (overlap 28 berurutan yang sudah dikenal; tunable const, bukan tebakan dinamis).

- Refresh butuh active snapshot; tanpa snapshot, tombol Refresh disabled dan Sync penuh yang ditawarkan.
- Identity yang sudah dikenal = `SourceId + TitleId` ada di snapshot aktif (in-memory hash set saat refresh mulai).
- Record baru/berubah di-upsert: staging di-seed dari `titles.jsonl` aktif (stream copy), lalu delta lines di-append sesudahnya sehingga aturan dedupe "last wins" yang sama menyembuhkan tanpa kode merge kedua. Seed memakai marker partition khusus yang **tidak** memicu `rating drift` dan selalu kalah dari record partition nyata.
- Manifest mencatat watermark refresh (`LastDeltaUtc`); UI menampilkannya sebagai "last refresh".
- Choir: tidak ada tombstone dari provider, jadi **penghapusan tak terlihat oleh Refresh** — full backfill berkala tetap satu-satunya penyembuh hapus. UI tidak boleh menjanjikan deletion sync.
- Pacing §6.5, soft-stop, checkpoint, bounds, dan activation order berlaku identik; Refresh yang berhenti bersifat resumable seperti Sync.

## 4. Architecture dan ownership

```text
MangaReaderView (composition + tab navigation only)
├── DownloaderView
│   └── CatalogScreen (online Downloader yang sekarang)
├── CatalogMirrorView
│   └── CatalogMirrorFeature (thin coordinator)
│       ├── CatalogMirrorSyncFeature
│       │   └── CatalogSnapshotStore
│       ├── CatalogMirrorQuery
│       ├── CatalogMirrorDetailFeature
│       │   ├── CatalogEnrichmentStore
│       │   └── CatalogMirrorCoverCache
│       └── IMangaSourceDirectory / ICatalogSnapshotSource
└── DownloadListScreen
    └── existing DownloadQueueFeature
```

Ownership:

- `MangaReaderView`: membuat dependency, memasang context/delegates, mengganti selected top-level tab, dispose.
- `DownloaderView`: host online `CatalogScreen`, relay `OpenDownloadQueue`, relay Auto Cover candidate.
- `DownloadListScreen`: presentasi queue dan action forwarding saja.
- `CatalogMirrorFeature`: menyatukan immutable state dari query/sync/detail; tidak membaca file atau memanggil provider langsung.
- `CatalogMirrorSyncFeature`: Start/Stopping/Stopped/Resume state dan traversal.
- `CatalogSnapshotStore`: staging titles, checkpoint, validation, dan atomic activation.
- `CatalogEnrichmentStore`: satu atomic metadata file per title.
- `CatalogMirrorQuery`: local immutable index dan result paging.
- `CatalogMirrorDetailFeature`: explicit title/group online flow, selection, dan pemanggilan narrow local-availability delegate.
- `CatalogMirrorCoverCache`: bounded fetch/decode/cache untuk title yang dibuka.
- `ComixSource`: route/query/provider parsing dan satu bounded session refresh.
- `DownloadQueueFeature`: satu-satunya owner queue state dan persistence.

Catalog tidak mengimpor screen, filter feature, card model, atau queue internal milik Downloader. Downloader tidak mengimpor Catalog.

## 5. Provider and data contracts

### 5.1 Optional snapshot source

Tambahkan neutral `ICatalogSnapshotSource`. Source yang tidak mengimplementasikannya tetap bekerja di Downloader tetapi tidak ditawarkan pada Catalog sync.

Contract wajib:

- `SnapshotPartitions`: ordered immutable partition list;
- `GetSnapshotPageAsync(partition, page, cancellationToken)`;
- `CatalogSnapshotPage`: items, provider total, page, `HasMore`;
- `CatalogSnapshotItem`: identity, normalized canonical URL, title/alternate titles, normalized cover URL, latest chapter value/label, rating, type, status, language, year, synopsis, capture timestamp. Title memakai placeholder `(Untitled <hid>)` plus `IsTitlePlaceholder=true` bila provider tidak memberi title (revisi 2026-09-08).

Normalized canonical URL dan cover URL pada `CatalogSnapshotItem` adalah output kontrak yang memang diizinkan. Raw request URL, raw query string, token/cookie/header, dan provider JSON tidak boleh keluar dari adapter.

`CatalogMirrorContracts.cs` juga memiliki dua kontrak UI-free untuk composition root:

- `CatalogLocalAvailabilityRequest`/`CatalogLocalAvailabilityResult`, dipanggil hanya setelah chapter group berhasil dimuat;
- `CatalogQueueHandoffRequest`, berisi normalized title, selected group, selected chapters, dan confirmed target folder.

`MangaReaderView.xaml.cs` mengadaptasi local-availability request ke existing `ListerFeature.ListAsync` dan `ListerFeature.IsLocallyAvailable`. Catalog Mirror tidak membaca Library snapshot, tidak memindai seluruh Library, dan tidak mengimpor `ListerFeature` secara langsung.

### 5.2 Identity dan duplicate policy

Primary key:

`SourceId + TitleId`

`TitleHid` dan canonical URL wajib dipertahankan. Display title bukan identity.

Duplicate policy:

- duplicate identity dalam rating partition yang sama: record dengan capture timestamp terakhir menggantikan yang lama;
- duplicate identity pada rating partition berbeda: partition yang diproses paling akhir menang dan warning `rating drift` dicatat pada manifest;
- duplicate tidak menambah unique title count;
- invalid/missing identity menggagalkan page, bukan dilewati diam-diam.

### Title-absent items — placeholder policy (revisi 2026-09-08, evidence Phase 0)

Live probe 2026-09-08 (3x, `title_asc` page 1): safe/suggestive/pornographic lolos (28 items, total 63133/8906/7010, `HasMore=true`); erotica page 1 deterministik memuat 1 item tanpa `title` tetapi `hid`/`id`/`url` valid. Karena policy di atas hanya menggagalkan page untuk invalid/missing *identity*, item identity-valid tanpa display title diperlakukan sebagai berikut (snapshot path only; online Downloader tidak berubah):

- Snapshot adapter menerima item identity-valid tanpa title dan menyimpannya dengan display title placeholder deterministik `(Untitled <hid>)` plus flag `IsTitlePlaceholder=true` pada `CatalogSnapshotItem`.
- Record placeholder tetap dihitung unique title dan mengikuti duplicate policy biasa (partition terakhir menang; flag ikut dengan record pemenang).
- Placeholder terurut/masuk search sebagai teks biasa; `hid` di dalam placeholder membuat pencarian hid tetap menemukan record.
- Detail open dan enrichment menggantikan placeholder dengan judul provider bila tersedia.
- Manifest mencatat jumlah placeholder-title records sebagai warning diagnostic (seperti `rating drift`).
- Online `ReadSummary`/`ReadCatalogPage` tetap strict; leniency hanya di wrapper snapshot (Phase 4), bukan dengan melonggarkan parser bersama.

### 5.3 Provider traversal bounds

Batas tetap:

- maksimum `5,000` pages per rating partition;
- maksimum `250,000` unique titles per snapshot;
- maksimum `1 MiB` per JSONL record;
- maksimum `512 MiB` masing-masing untuk `titles.staging.jsonl` dan `titles.jsonl`;
- page dengan `HasMore=true` tetapi tanpa item adalah contract error;
- fingerprint identity page yang sama muncul pada dua page berurutan adalah pagination loop error.

Melewati batas menghentikan sync, mempertahankan checkpoint dan snapshot aktif lama, serta menampilkan error lokal.

### 5.4 Storage layout (historis — runtime sudah SQLite §13)

Layout JSONL di bawah ini tidak lagi dibaca kode mana pun sejak P7: importer satu-kalinya dihapus bersama path JSONL atas putusan operator (fitur belum rilis; data legacy produksi tak mungkin ada). Bagian ini dipertahankan sebagai catatan arkeologi format pra-SQLite.

Root:

`%LOCALAPPDATA%\Citadel\MangaReader\catalog\`

```text
catalog\
├── manifest.json
├── sync-state.json
├── snapshots\
│   └── <snapshot-id>\
│       ├── titles.staging.jsonl
│       └── titles.jsonl
└── enrichment\
    └── <sha256-sourceid-titleid>\
        ├── metadata.json
        └── cover.cache
```

Rules:

- staging snapshot memakai snapshot id baru;
- setiap sukses page: tulis page lengkap, panggil `FileStream.Flush(flushToDisk: true)` pada `titles.staging.jsonl`, baru tulis `sync-state.json` atomik;
- trailing partial/malformed line akibat crash dipotong sebelum Resume;
- Resume dimulai dari page setelah checkpoint terakhir;
- setelah empat partition terminal, activation membaca staging, menerapkan duplicate policy, lalu menulis satu `titles.jsonl` final yang hanya berisi unique identities;
- `manifest.json` tidak boleh menunjuk `titles.staging.jsonl`;
- activation hanya mengganti small `manifest.json` secara atomik;
- urutan terminal activation terkunci: tulis dan durable-flush `titles.jsonl`, atomic replace `manifest.json`, reload active snapshot/query, lalu hapus `sync-state.json` dan `titles.staging.jsonl`;
- setelah reload, search/filter/sort dipertahankan dan page di-clamp ke page terakhir yang valid; hasil snapshot baru tidak boleh mengganti state query dari generation yang lebih baru;
- kegagalan cleanup checkpoint/staging setelah manifest aktif hanya menjadi warning dan tidak boleh membatalkan snapshot baru;
- active snapshot lama tidak dihapus sebelum snapshot baru aktif;
- setelah activation sukses, simpan snapshot aktif dan satu previous snapshot; snapshot lebih lama dihapus dengan exact validated paths saja;
- enrichment filename memakai SHA-256 identity, bukan title/path provider;
- `metadata.json` maksimum `4 MiB` per title;
- `cover.cache` maksimum `20 MiB`, wajib lolos image decode sebelum publish;
- `CatalogMirrorCoverCache` menerima `HttpMessageInvoker`/`HttpClient` dari composition root, memakai `HttpCompletionOption.ResponseHeadersRead`, menolak non-HTTP(S), memeriksa `Content-Length` bila tersedia, dan menghentikan bounded stream segera setelah melebihi 20 MiB; root tidak boleh lebih dahulu mem-buffer response menjadi `byte[]`;
- schema yang lebih baru dilaporkan dan tidak ditimpa.
- Refresh (§3.6) men-seed staging dari `titles.jsonl` aktif via stream copy sebelum delta di-append; seed memakai marker partition khusus yang dikecualikan dari `rating drift` dan selalu kalah dari record nyata.
- Manifest mencatat `LastDeltaUtc` setiap Refresh sukses selain `CapturedAtUtc` milik backfill.

Tidak memakai `DownloaderJson` atau folder state Downloader.

## 6. Sync, Stop, session, dan concurrency

### 6.1 Soft Stop

`Stop` adalah logical soft-stop:

- set `stopRequested=true`;
- UI berubah menjadi `Stopping…`;
- jangan cancel token milik in-flight PyHost/provider request;
- setelah request terminal, commit page bila valid, tulis checkpoint, lalu state menjadi `Stopped`;
- late result tidak boleh memulai page berikutnya;
- hanya module disposal yang membatalkan lifetime token.

Kontrak ini mengikuti logical Stop online Catalog dan mencegah TIMEOUT palsu/start-stop-start race.

### 6.2 Session expiry

Khusus `GetSnapshotPageAsync` Comix:

1. request current partition/page;
2. jika response HTTP 401/403, release existing Downloader browser session;
3. bootstrap ulang melalui existing `EnsureSessionAsync`;
4. retry **page yang sama tepat satu kali**;
5. jika retry gagal, jangan increment checkpoint; berhenti dengan error yang dapat di-Resume.

Tidak retry contract error, malformed JSON, timeout, atau arbitrary exception tanpa policy.

HTTP status harus dibawa sebagai typed property pada `ComixContractException` (nullable untuk non-HTTP contract failure). Retry memeriksa property tersebut; dilarang mengenali 401/403 melalui pencarian text message. Snapshot parser boleh reuse helper Comix existing, tetapi wajib menolak non-object item serta missing/invalid identity; perilaku `ReadCatalogPage` online Downloader yang sekarang tidak diubah.

Satu snapshot page beserta satu kemungkinan 401/403 refresh+retry adalah satu logical in-flight request. Jika Stop ditekan di tengahnya, UI tetap `Stopping…` sampai logical request tersebut terminal; retry tidak memulai page berikutnya.

Existing Downloader `BrowseAsync` behavior tidak berubah.

### 6.3 Satu browser session existing

Catalog Mirror memakai source registry dan `DownloaderPyHostClient` yang sama. Tidak ada browser/profile/client kedua.

- Setiap provider request tetap diserialkan oleh gate client existing.
- Sync tidak memegang gate lintas-page; online Downloader request dapat berjalan pada request boundary berikutnya.
- Pergantian `Show browser` tetap memakai preference existing Downloader.
- Catalog Mirror tidak menambah toggle/browser settings sendiri.
- Jika user menjalankan dua operasi, keduanya boleh menunggu gate tetapi tidak pernah berjalan paralel.
- Catalog progress harus tetap menunjukkan `Waiting for provider…` ketika menunggu, bukan terlihat crash.

### 6.4 Threading contract

- membaca JSONL, recovery, compaction/dedupe, activation, index construction, dan local query berjalan di background task;
- tidak ada WPF control atau `ObservableCollection` yang dibuat untuk seluruh snapshot;
- feature memublikasikan immutable state/result page saja;
- `CatalogMirrorView` me-marshall `StateChanged` ke Dispatcher sebelum menyentuh control;
- generation guard berlaku pada load snapshot, query, dan detail supaya hasil lama tidak menimpa intent user terbaru.

### 6.5 Polite pacing dan backpressure (sync improvement 2026-09-08)

Satu serial browser session tidak boleh menembak page bertubi-tubi:

- jeda sopan antar provider page: base `1000ms` + full jitter `0–500ms` (`PoliteDelay`, tunable; test memakai nol);
- jeda adalah tunggu kooperatif yang terbangun oleh Stop/supersede — bukan in-flight request, jadi tidak melanggar kontrak Stop §6.1;
- backpressure bertipe saja: `429`/`502`/`503` dari `HttpStatus` (tidak pernah sniffing pesan) dicoba ulang di page yang sama maksimal 4 percobaan total (1 + 3 retry) dengan base `2s` digandakan + full jitter, cap `30s`;
- halaman pertama run tidak kena jeda; checkpoint tidak maju selama backoff; kegagalan menetap menjadi error yang dapat di-Resume;
- contract error, malformed, timeout, dan 401/403 (jalur §6.2 sendiri) tidak masuk skema ini.

## 7. UI contract

### 7.1 Catalog tab

Action bar:

- read-only provider label `Comix`; tidak ada provider selector pada versi pertama;
- local search field;
- local filter button/panel untuk filter section 3.2;
- local sort selector;
- satu stateful command slot: `Sync Catalog`, `Stop`, `Stopping…`, atau `Resume Sync`;
- tombol `Refresh` di sampingnya, enabled hanya bila active snapshot ada (operasi rutin §3.6);
- last synced, unique title count, partition/page progress, dan warning count;
- `Open Queue` dengan accessible name dan tooltip `Open Download Queue`.

Content:

- grid view saja pada versi pertama;
- reuse `MangaTitleCard` dengan feature-local `CatalogMirrorCardModel`;
- fixed local page size `40` cards;
- detail reuse `Components/MangaDetailView`;
- chapter list reuse `SettingTable`, header Select All, row checkbox, local state dari injected local-availability delegate, dan existing button/action presentation;
- `Queue Selected` disabled tanpa selection atau ketika action busy;
- source group change mengosongkan selection sebelum chapters baru dimuat;
- Back mengembalikan query, filter, sort, page, dan scroll anchor tanpa network.

State:

- `empty`: belum ada active snapshot;
- `loading`: membaca/indexing local snapshot atau online detail;
- `syncing`: current rating partition/page/count;
- `stopping`: menunggu current provider request terminal;
- `stopped`: checkpoint valid dan Resume aktif;
- `error`: pesan lokal; active snapshot lama tetap tampil;
- `ready`: local result siap.

### 7.2 Download Queue tab

- Existing `DownloadListScreen` hanya di-host satu kali di top-level tab.
- `UseContext(DownloaderContext)` diganti menjadi `UseQueue(DownloadQueueFeature)`.
- Comment lama “this screen is not a tab” dihapus/diperbarui.
- `Back to Catalog` memilih top-level Downloader tab.
- Tombol `Download List (#)` online Downloader memilih Download Queue tab.
- Tombol Catalog `Open Queue` memilih tab yang sama.
- Queue worker berjalan selama module lifetime, bukan lifetime tab.

### 7.3 Downloader tab

Downloader hanya berhenti menjadi host Queue screen. Online Catalog, filter, Start/Stop, browser toggle, result/detail, selection, Auto Cover relay, dan queue behavior tetap.

## 8. Target file tree

### 8.1 New production files

```text
module/mangareader/
├── Features/
│   └── CatalogMirror/
│       ├── CatalogMirrorContracts.cs
│       ├── CatalogMirrorPaths.cs
│       ├── CatalogSnapshotStore.cs
│       ├── CatalogEnrichmentStore.cs
│       ├── CatalogMirrorQuery.cs
│       ├── CatalogMirrorSyncFeature.cs
│       ├── CatalogMirrorDetailFeature.cs
│       ├── CatalogMirrorFeature.cs
│       ├── CatalogMirrorCardModel.cs
│       ├── CatalogMirrorCoverCache.cs
│       ├── CatalogMirrorView.xaml
│       └── CatalogMirrorView.xaml.cs
└── shareLogic/
    └── Sources/
        └── CatalogSnapshotContracts.cs
```

### 8.2 New test files

```text
tests/Module.Mangareader.Downloader.Tests/CatalogMirror/
├── CatalogSnapshotStoreTests.cs
├── CatalogMirrorLocalDataTests.cs
├── CatalogMirrorSyncTests.cs
├── CatalogSnapshotSourceTests.cs
├── CatalogMirrorDetailTests.cs
└── CatalogMirrorFeatureTests.cs
```

Dua file terakhir ditambah oleh improvement review 2026-09-08 (revisi ini): DetailFeature dan Koordinator UI-free dan constructible dengan fake sehingga layak uji langsung; boundary masing-masing tetap satu file.

### 8.3 Satu file = satu feature/responsibility + boundary

Tidak boleh menambah `Utils`, `Helpers`, `Services`, partial class, atau file baru untuk memindahkan tanggung jawab yang sudah dikunci di bawah. Private helper boleh berada di file owner-nya selama hanya melayani responsibility file tersebut. Jika satu responsibility ternyata membutuhkan owner baru, implementation berhenti dan plan direvisi lebih dahulu.

| New production file | Satu responsibility yang dimiliki | Boundary file — dilarang |
|---|---|---|
| `shareLogic/Sources/CatalogSnapshotContracts.cs` | Kontrak provider-neutral `ICatalogSnapshotSource`, partition, page, dan item snapshot, termasuk flag `IsTitlePlaceholder` untuk item identity-valid tanpa title (revisi 2026-09-08). | Tidak mengenal Comix, WPF, filesystem, queue, browser client, atau implementation feature. |
| `Features/CatalogMirror/CatalogMirrorContracts.cs` | Immutable state/query/result/enrichment/local-availability/queue-handoff records milik Catalog Mirror. | Tidak menjalankan logic, I/O, network, rendering, atau bergantung pada class internal Downloader. |
| `Features/CatalogMirror/CatalogMirrorPaths.cs` | Menghasilkan dan memvalidasi exact path di bawah Catalog root. | Tidak membaca/menulis/menghapus file, tidak serialisasi JSON, dan tidak menerima provider title sebagai nama path. |
| `Features/CatalogMirror/CatalogSnapshotStore.cs` | Seluruh lifecycle persistence snapshot: staging append, durable checkpoint, recovery, bounds, dedupe/compaction, atomic activation, active/previous retention. | Tidak memanggil provider, tidak menjalankan traversal/Stop state, tidak melakukan query/filter, enrichment, cover, queue, atau UI. |
| `Features/CatalogMirror/CatalogEnrichmentStore.cs` | Atomic read/replace satu `metadata.json` per normalized title identity. | Tidak menyimpan cover bytes, tidak append history, tidak mengambil network, tidak mengubah snapshot, Library, atau queue. |
| `Features/CatalogMirror/CatalogMirrorQuery.cs` | Membangun immutable in-memory index dan menjalankan search/filter/sort/page lokal. | Tidak membaca file sendiri, tidak network, tidak menyimpan state UI, tidak membuat WPF controls, dan tidak mengubah input snapshot. |
| `Features/CatalogMirror/CatalogMirrorSyncFeature.cs` | State machine dan traversal empat partition untuk Start/Soft Stop/Resume, termasuk mode Refresh incremental (§3.6), pacing sopan dan backpressure bertipe (§6.5). | Tidak menyusun query Comix, parsing provider JSON, melakukan file I/O langsung, merender UI, mengambil detail/cover, atau menyentuh queue. |
| `Features/CatalogMirror/CatalogMirrorDetailFeature.cs` | Explicit title detail/group/chapter/selection flow dan pemanggilan injected local-availability/enrichment/cover dependencies. | Tidak sync catalog, tidak membaca Library snapshot, tidak memanggil `ListerFeature` langsung, tidak menulis queue, dan tidak merender control. |
| `Features/CatalogMirror/CatalogMirrorFeature.cs` | Thin coordinator yang menggabungkan immutable query/sync/detail state dan meneruskan intent ke owner feature. | Tidak mengakses filesystem/network/provider/queue langsung dan tidak mengandung parsing, filtering, download, atau WPF logic. |
| `Features/CatalogMirror/CatalogMirrorCardModel.cs` | Adapter satu `CatalogSnapshotItem` ke binding contract existing `MangaTitleCard`. | Tidak I/O, tidak network, tidak cache cover, tidak selection, dan tidak menyimpan business state. |
| `Features/CatalogMirror/CatalogMirrorCoverCache.cs` | Fetch satu cover melalui injected HTTP client, enforce bounded stream, decode validation, dan atomic cache publish. | Tidak mengambil detail/chapter, tidak bulk-prefetch, tidak mengubah snapshot/enrichment metadata, tidak memakai browser, dan tidak menerima unvalidated path. |
| `Features/CatalogMirror/CatalogMirrorView.xaml` | Layout Catalog memakai existing shared tabs/card/detail/table/action/scroll presentation. | Tidak membuat shared primitive/style baru dan tidak menaruh data/provider/business logic di XAML. |
| `Features/CatalogMirror/CatalogMirrorView.xaml.cs` | UI event wiring, Dispatcher marshaling, page-row/card rendering, dan emit navigation/queue-handoff events. | Tidak memanggil provider/HTTP/filesystem/queue secara langsung, tidak parsing JSON, dan tidak memiliki sync/query/detail policy. |

### 8.4 Satu test file = satu verification boundary

| New test file | Yang dibuktikan | Boundary test — dilarang |
|---|---|---|
| `CatalogSnapshotStoreTests.cs` | Recovery, bounds, checkpoint durability order, dedupe, activation, retention, dan safe cleanup milik snapshot store. | Tidak WPF, tidak provider live, tidak query/detail/queue behavior. |
| `CatalogMirrorLocalDataTests.cs` | Pure query/filter/sort/page, enrichment replace, serta bounded/valid cover cache melalui fake HTTP response. | Tidak browser, tidak Comix live, tidak full crawl, dan tidak membuat UI controls. |
| `CatalogMirrorSyncTests.cs` | Start/Soft Stop/Resume, four-partition traversal, checkpoint, generation guard, dan old-snapshot preservation memakai fake source/store boundary. | Tidak network live, tidak WPF, tidak detail/cover/queue behavior. |
| `CatalogSnapshotSourceTests.cs` | Comix snapshot query mapping, strict item parsing, typed 401/403 exact-page retry, dan tidak berubahnya online parser behavior melalui fixtures/fake client. | Tidak full crawl, tidak browser launch, tidak menyimpan credential/cookie/token, dan tidak menguji UI. |
| `CatalogMirrorDetailTests.cs` | Explicit open/group/chapters/selection, availability delegate, enrichment, satu cover, dan handoff builder melalui fake directory/HTTP/delegate (revisi improvement 2026-09-08). | Tidak browser live, tidak Library/queue nyata, tidak merender control. |
| `CatalogMirrorFeatureTests.cs` | Initialize/query/generation-guard, reload pasca-sync, teruskan intent detail, dan error tanpa source melalui fake source + store temp (revisi improvement 2026-09-08). | Tidak WPF, tidak provider live, tidak queue nyata. |

## 9. Existing-file allowlist

Only these existing files may change:

| File | Allowed edit |
|---|---|
| `module/mangareader/MangaReaderView.xaml` | Add Catalog and Queue tabs dengan locked local header/accessibility sizing. |
| `module/mangareader/MangaReaderView.xaml.cs` | Compose features, inject narrow availability/queue delegates dan HTTP client, navigate tabs, dispose in locked order. |
| `module/mangareader/Features/Downloader/DownloaderView.xaml` | Remove nested Queue host; keep CatalogScreen. |
| `module/mangareader/Features/Downloader/DownloaderView.xaml.cs` | Relay queue navigation; remove internal Queue routing; preserve Auto Cover relay. |
| `module/mangareader/Features/Downloader/DownloaderContract.cs` | Remove obsolete `DownloaderRoute`; existing context otherwise unchanged. |
| `module/mangareader/Features/Downloader/Queue/DownloadListScreen.xaml.cs` | Replace whole Downloader context dependency with `DownloadQueueFeature`; update stale tab comment. |
| `module/mangareader/Features/Downloader/Sources/Comix/ComixSource.cs` | Add snapshot partition/page export and snapshot-only one-time 401/403 rebootstrap. |
| `tests/Module.Mangareader.Downloader.Tests/Module.Mangareader.Downloader.Tests.csproj` | Link new pure Catalog Mirror sources/contracts required by the test files (revisi improvement 2026-09-08: tambah `CatalogMirrorDetailFeature.cs` dan `CatalogMirrorFeature.cs` untuk dua file test koordinator/detail). | |

No existing file outside this table may change. If another file is required, stop and revise this plan before editing.

## 10. Hard boundary

Forbidden:

- `setting/Components/**` and `setting/SettingResources.xaml`;
- `module/sharedLogic/pyhost/**`, `module/sharedLogic/cs/PyHost.cs`, Python plugin/browser;
- `Features/Downloader/Catalog/CatalogScreen.xaml*`;
- `Features/Downloader/FilterSearch/**`;
- `Features/Downloader/DownloaderPyHostClient.cs`;
- `Features/Downloader/Queue` except the narrow dependency edit in `DownloadListScreen.xaml.cs`;
- `Features/Downloader/Sources/Comix/**` except `ComixSource.cs`;
- Library, History, Cover Builder, Reader, RAR/CBZ, Auto Cover, FTF, Proxy, CamoProf, Shell, Sidebar, Settings;
- project/solution topology other than test source links explicitly allowed above;
- NuGet and release files;
- Git remote.

## 11. Execution phases

### Phase 0 — Preflight, read-only

Files changed: none.

1. Confirm root, branch, HEAD, and `git status --short`.
2. Read `AGENTS.md`, this plan, required Yuzskills, `SHARED-UI-BEHAVIOR.md`, current composition, source contract, Comix parser, Queue screen/owner, and test csproj.
3. Record pre-existing modified/untracked files and preserve them.
4. Run current Downloader tests and MangaReader Release build once.
5. Through the existing Comix path, probe exactly one first page for each of the four single-rating partitions with `title_asc`.
6. Confirm HTTP success, captured response shape, `HasMore`, and rating partition acceptance. Do not retain token/cookie payloads.

Gate: all four partition probes must pass. If one fails because provider contract changed, stop before code changes.

Revisi 2026-09-08: gate lolos-bersyarat. Tiga partition lolos penuh; erotica page 1 deterministik memuat 1 item identity-valid tanpa title (evidence di §5.2). Probe boleh gagal di item tersebut sampai Phase 4 mengimplementasikan placeholder policy snapshot-only; online parser tetap strict. Re-verifikasi penuh menjadi acceptance Phase 4 (fixture title-less + live page per rating).

### Phase 1 — Standalone Download Queue dan narrow dependency

Files: enam; perubahan ini satu compile-atomic slice karena pemindahan host dan perubahan injection tidak dapat dibangun terpisah tanpa compatibility shim sementara.

- `MangaReaderView.xaml`
- `MangaReaderView.xaml.cs`
- `DownloaderView.xaml`
- `DownloaderView.xaml.cs`
- `DownloaderContract.cs`
- `DownloadListScreen.xaml.cs`

Work:

- add only the top-level Download Queue tab and host existing screen once; Catalog tab is added later in the same phase that creates its view;
- replace `_context`/`UseContext` pada Queue screen dengan `_queue`/`UseQueue`;
- construct one `DownloaderContext`, give it to Downloader, give only its `Queue` to Queue screen;
- relay navigation through parent;
- preserve Queue engine module lifetime;
- dispose order wajib: `DownloadQueueTab.Dispose()` untuk unsubscribe event, lalu dispose Downloader/Catalog views, baru `_queue.Dispose()` dan shared browser/HTTP clients;
- remove obsolete nested routing dan stale “this screen is not a tab” comment.

Acceptance:

- one Queue screen, owner, store, and worker;
- tab switches preserve Catalog/Queue state;
- Download List button opens top-level Queue;
- Back opens Downloader;
- Downloader Start behavior unchanged.
- Queue rendering, Resume All, row actions, Clear Completed, dan event unsubscribe tetap sama.

Verification: Release build and live navigation only.

### Phase 2 — Contracts, paths, snapshot store

Files: enam; test project link ikut dalam slice agar test benar-benar mengompilasi production source sejak phase ini.

- `shareLogic/Sources/CatalogSnapshotContracts.cs`
- `Features/CatalogMirror/CatalogMirrorContracts.cs`
- `Features/CatalogMirror/CatalogMirrorPaths.cs`
- `Features/CatalogMirror/CatalogSnapshotStore.cs`
- `CatalogSnapshotStoreTests.cs`
- `Module.Mangareader.Downloader.Tests.csproj`

Work:

- implement locked records, schema, path containment, bounds, staging/checkpoint/activation;
- implement malformed trailing-line recovery;
- keep active plus one previous snapshot;
- link exactly these production files into the existing test project:
  - `shareLogic/Sources/CatalogSnapshotContracts.cs`;
  - `Features/CatalogMirror/CatalogMirrorContracts.cs`;
  - `Features/CatalogMirror/CatalogMirrorPaths.cs`;
  - `Features/CatalogMirror/CatalogSnapshotStore.cs`.

Acceptance:

- corrupt/newer schema preserved and reported;
- failed activation cannot destroy active snapshot;
- duplicate policy and exact bounds match sections 5.2–5.4;
- cleanup resolves and validates every exact target under Catalog root.

Verification: focused `CatalogSnapshotStoreTests` lalu MangaReader Release build.

### Phase 3 — Enrichment and local query

Files: enam; test-project edit hanya menambah linked sources phase ini.

- `CatalogEnrichmentStore.cs`
- `CatalogMirrorQuery.cs`
- `CatalogMirrorCardModel.cs`
- `CatalogMirrorCoverCache.cs`
- `CatalogMirrorLocalDataTests.cs`
- `Module.Mangareader.Downloader.Tests.csproj`

Work:

- atomic per-title enrichment;
- cached-cover validation;
- in-memory normalized index;
- exact local filters/sorts from section 3.2;
- local page size 40;
- latest-query-wins generation guard untuk query background;
- link exactly these additional production files into the existing test project:
  - `Features/CatalogMirror/CatalogEnrichmentStore.cs`;
  - `Features/CatalogMirror/CatalogMirrorQuery.cs`;
  - `Features/CatalogMirror/CatalogMirrorCoverCache.cs`.

Acceptance:

- search/filter/sort/page makes zero source/browser calls;
- generated 72,000-record fixture returns one page without building 72,000 WPF controls;
- hasil query lama tidak boleh menggantikan hasil query baru yang selesai lebih dulu;
- uncached covers remain placeholders;
- invalid/oversized cover is not published;
- repeated enrichment replaces one file rather than growing an append log.

Verification: focused `CatalogMirrorLocalDataTests` lalu MangaReader Release build.

### Phase 4 — Comix export and resumable sync

Files: five.

- `ComixSource.cs`
- `CatalogMirrorSyncFeature.cs`
- `CatalogSnapshotSourceTests.cs`
- `CatalogMirrorSyncTests.cs`
- `Module.Mangareader.Downloader.Tests.csproj`

Work:

- add exactly `Features/CatalogMirror/CatalogMirrorSyncFeature.cs` as the remaining production link to the existing test project;
- implement four ordered rating partitions;
- reuse Comix constants/query/parser;
- implement bounds, fingerprint guard, checkpoint, soft-stop;
- implement snapshot-only one-time 401/403 release/rebootstrap/retry.

Acceptance:

- no request before Sync/Resume;
- no raw provider shape leaks past source adapter;
- Stop never cancels in-flight transport;
- start-stop-resume neither skips checkpointed pages nor creates duplicate identities;
- 401/403 retries the exact page once;
- existing Downloader source methods behave unchanged.
- title-less identity-valid item disimpan sebagai placeholder `(Untitled <hid>)` + flag hanya di snapshot path (revisi 2026-09-08); online parser tidak berubah. Fixture mencakup minimal satu item tanpa title.

Verification: focused fixtures/fake source plus one live page per rating. Full crawl is not an automated test.

### Phase 5 — Catalog offline UI

Files: five.

- `CatalogMirrorFeature.cs`
- `CatalogMirrorView.xaml`
- `CatalogMirrorView.xaml.cs`
- `MangaReaderView.xaml`
- `MangaReaderView.xaml.cs`

Work:

- add the Catalog top-level tab in the locked final order and compose its exact state/action contract;
- load active snapshot on tab initialization;
- render page of shared cards and local filters;
- connect Sync/Stop/Resume and Queue navigation;
- preserve local query/page/scroll state across tab navigation.

Acceptance:

- restart opens active snapshot without browser;
- search/filter/sort/page/back controls produce zero network requests;
- old snapshot remains visible during sync and after sync error;
- UI does not materialize all records;
- empty/loading/syncing/stopping/stopped/error/ready are visible and local.

Verification: Release build and visible state smoke.

### Phase 6 — Explicit detail, chapters, and queue handoff

Files: five; composition root ikut dalam slice karena detail tidak dapat dijalankan tanpa source directory, local-availability delegate, enrichment store, cover cache, dan HTTP client yang diinjeksikan.

- `CatalogMirrorDetailFeature.cs`
- `CatalogMirrorFeature.cs`
- `CatalogMirrorView.xaml`
- `CatalogMirrorView.xaml.cs`
- `MangaReaderView.xaml.cs`

Work:

- on title click: resolve source, load detail, groups, first group chapters;
- same as online Catalog, first provider group becomes selected;
- on group change: clear chapters/selection, then load new group;
- invoke the injected local-availability delegate for only the selected title/group and map its verdict to chapter rows; do not read Library snapshot;
- save detail enrichment and, only when no valid cache exists, attempt one cover fetch;
- create immutable queue-handoff request event.
- composition root constructs detail dependencies and injects only the source directory, local-availability delegate, enrichment store, cover cache, and existing HTTP client; Phase 6 does not subscribe the queue handoff yet.

Acceptance:

- no detail/chapter/cover call before title click;
- failed cover does not fail detail;
- failed detail/group/chapter remains local;
- back restores exact local result state;
- Queue Selected disabled without selection or during action.

Verification: MangaReader Release build dan visible smoke satu title sampai chapter list; jangan queue/download massal.

### Phase 7 — Composition handoff and final audit

Files: one.

- `MangaReaderView.xaml.cs`

Work:

- translate Catalog queue-handoff record into existing `DownloadQueueFeature.QueueChapters` call;
- return existing queue result without reinterpreting dedupe/persistence rules;
- finalize Queue navigation and enforce disposal order: Queue screen unsubscribe first, feature views next, Queue owner and shared clients last.

Validation:

1. Focused four Catalog Mirror test files.
2. Existing Downloader suite once.
3. MangaReader Release build once.
4. `git diff --check`.
5. Live smoke:
   - online Downloader Start still works;
   - Download List opens standalone Queue;
   - Queue Back opens Downloader;
   - local Catalog opens without request;
   - Sync → Stop → Stopping → Resume works;
   - title click loads detail/group/chapter;
   - cached title cover appears after reopen;
   - Queue Selected appears in the same queue.
6. Confirm every changed file is in section 8/9.

Jika salah satu validation gagal, phase berhenti dan melaporkan failure tersebut. Jangan memperluas allowlist atau mengganti kontrak untuk membuat gate hijau.

### Phase 8 — Refresh incremental (revisi sync improvement 2026-09-08, dieksekusi 2026-09-08)

Pengerasan review pasca-implementasi (wajib, sudah dikerjakan): seed streaming O(1) objek + bound penuh + kembalikan identity keys (tanpa baca ganda); checkpoint marker `(nextPartition, 0)` setiap partition selesai (backfill dan delta) agar Resume tak memasuki partition yang sudah terminal; resume delta membangun known dari active + staging; non-object gagalkan page (§6.2); enumerasi direktori milik Store, bukan Paths.

Files: tidak ada file baru. Hanya file yang sudah ada pada slice ini:

- `Features/CatalogMirror/CatalogMirrorSyncFeature.cs` (mode Refresh + stop-overlap)
- `Features/CatalogMirror/CatalogSnapshotStore.cs` (seed staging + pengecualian drift seed + watermark manifest)
- `Features/CatalogMirror/CatalogMirrorContracts.cs` (field `LastDeltaUtc`, marker seed)
- `Features/CatalogMirror/CatalogMirrorView.xaml(.cs)` (tombol Refresh + last-refresh)
- `Module.Mangareader.Downloader.Tests.csproj` (tanpa link baru)
- `CatalogMirrorSyncTests.cs` + `CatalogSnapshotStoreTests.cs` (kasus delta; tanpa file test baru)

Work:

- traversal `latest`-first per single-rating partition, berhenti setelah satu page penuh tanpa identity baru (overlap 28, const);
- seed staging dari active snapshot, delta di-append, aktivasi dan dedupe yang sama;
- watermark `LastDeltaUtc` pada sukses; `Resume` melanjutkan checkpoint Refresh seperti Sync;
- pacing, soft-stop, bounds, dan larangan tetap §6.5/§10.

Acceptance:

- fixture: overlap berhenti tepat (tanpa over-fetch halaman berikutnya), upsert mengganti record lama, tanpa duplikat, seed tak memicu drift, hapus tak diklaim tersinkron;
- live: satu Refresh memakai orde puluhan page, bukan ribuan; full Sync tetap tersedia manual.

Verification: dua file test existing + satu live Refresh + Release build. Full crawl bukan automated test.

No bump/commit/push/release.

## 12. Final acceptance

Implementation selesai hanya bila:

- Catalog dan Download Queue adalah top-level tabs;
- online Downloader tidak di-rewrite dan masih berfungsi;
- Queue screen bergantung hanya pada existing Queue owner;
- ada satu Queue owner/store/worker;
- four-rating catalog traversal dapat Start/Soft Stop/Resume;
- active snapshot lama aman selama staging/failure;
- termination and storage bounds ditegakkan;
- search/filter/sort/page benar-benar offline;
- global filter hanya memakai complete fields yang dikunci;
- detail/groups/chapters/cover hanya dimulai dari explicit title click;
- provider URL/identity tidak ditebak;
- item tanpa title tetapi identity-valid tidak menggagalkan sync (placeholder + flag, revisi 2026-09-08);
- Refresh incremental berhenti pada overlap dan meng-upsert tanpa duplikat; hapus hanya via backfill penuh (revisi sync improvement 2026-09-08);
- session expiry hanya mendapat satu bounded retry;
- shared UI dipakai tanpa primitive baru;
- tests benar-benar meng-compile source Catalog Mirror melalui linked-source csproj;
- tidak ada perubahan di luar allowlist;
- live behavior, bukan hanya test, membuktikan tab routing dan sync lifecycle.

## 13. SQLite persistence track (revisi 2026-09-08, otorisasi operator)

JSONL staging (`titles.staging.jsonl`/`titles.jsonl` + checkpoint + manifest + compaction manual) diganti SQLite tepat setelah parity terbukti (Phase P7). Alasan tercatat: kebutuhan Catalog sudah berupa database lokal interaktif (multi-filter, sort, pagination, resume, partial results selama first sync); offset-index di atas JSONL akan membangun ulang query engine/index/concurrency/checkpoint buatan sendiri.

### 13.1 Keputusan terkunci (D0)

- Satu-satunya dependensi baru yang diizinkan: `Microsoft.Data.Sqlite` **10.0.11** pin (ter-restore dan terverifikasi di mesin build 2026-09-08; insert 20 ribu row 115 ms, filter+sort+limit 40 baris 2 ms).
- Lokasi: `%LOCALAPPDATA%\Citadel\MangaReader\catalog\catalog.db`. Enrichment detail dan cover cache tetap file.
- Koneksi pendek per operasi (`Pooling=false` eksplisit agar deterministik dan test dapat menghapus file temp), `WAL`, `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout=5000`, satu transaction per provider page, tanpa koneksi lintas thread.
- Native `e_sqlite3.dll` tidak ikut payload citizen otomatis (hanya `runtimes/<rid>/native/`); citizen wajib membawa `runtimes/win-x64/native/e_sqlite3.dll` via entry eksplisit pola `DeployMangaReaderRar` (tanpa ubah file shared). Gate P1 membuktikan resolve dari layout folder citizen.
- Tanpa FTS5 sampai benchmark membuktikan `LIKE` lambat. Queue/Library/transport/browser tak tersentuh. Tanpa scheduler/service.

### 13.2 Skema (milik `CatalogMirrorDatabase.cs`)

`catalog_state` (version, active/building generation, source) · `catalog_generation` (mode full/refresh, state, waktu, count) · `catalog_title` (identity unik `source_id+title_id`, title/search_text/URL/cover/rating/type/status/language/year/chapter/capture/placeholder/synopsis/alternates + kolom partisi `partition_key/partition_index` untuk aturan drift §5.2 + indeks generation, rating, type, status, language, year, chapter) · `catalog_partition_checkpoint` (partition, last page, provider_total, staged, fingerprint, waktu) · `catalog_warning` (terikat generation). Kolom partisi/synopsis/alternates adalah superset parity vs draft awal agar upsert, drift, dan search tak kehilangan data JSONL. DDL rinci wewenang implementer dalam batas ini.

### 13.3 File dan boundary

| File | Boleh | Dilarang |
|---|---|---|
| `Features/CatalogMirror/CatalogMirrorDatabase.cs` (baru) | factory koneksi, DDL schema, versi migrasi, PRAGMA | query bisnis, traversal, UI |
| `Features/CatalogMirror/CatalogSnapshotStore.cs` (ubah) | transaksi page/generation/checkpoint/activation/retention via Database | SQL mentah di luar transaksi store; jalur JSONL setelah P7 |
| `Features/CatalogMirror/CatalogMirrorQuery.cs` (ubah) | `LIKE` parameterized, SQL statis per enum sort, `LIMIT 40 OFFSET`, count | nama kolom dari UI; FTS5 |
| `Features/CatalogMirror/CatalogMirrorSyncFeature.cs` (ubah) | traversal + Start/Stop/Resume/Refresh di atas store | SQL langsung |
| `Features/CatalogMirror/CatalogMirrorContracts.cs` (ubah) | progress total, partial flag, generation identity | logika |
| `Features/CatalogMirror/CatalogMirrorFeature.cs` (ubah) | ownership worker module-lifetime, koordinasi query/progress | SQL, filesystem DB |
| `Features/CatalogMirror/CatalogMirrorView.xaml(.cs)` (ubah) | progress bar + label `Partial`, render state | buka database, SQL |
| `Features/CatalogMirror/CatalogMirrorPaths.cs` (ubah) | lokasi `catalog.db` + enrichment/cover | baca DB |
| `Module.Mangareader.csproj` + test csproj | satu PackageReference pin 10.0.11 (+ entry native win-x64 di citizen) | package lain |
| 6 file test existing + `CatalogMirrorDatabaseTests.cs` (baru, satu-satunya file test baru yang diizinkan track ini) | schema/transaksi/resume/visibility/activation/query | file test lain |

### 13.4 Phase SQLite track (P0–P7)

- **P0 — Revisi plan ini** (D0 + §13 + allowlist). Gate: plan disetujui. Tanpa kode.
- **P1 — Database tanpa UI:** package + schema/migrasi + PRAGMA + entry native + bukti resolve dari layout citizen + test schema. Gate: test hijau, `e_sqlite3.dll` + `runtimes` di deps.json staging.
- **P2 — Importer legacy** (dieksekusi lalu **dihapus total** atas putusan operator: fitur belum rilis sehingga data legacy produksi tak mungkin ada; importer + path JSONL + 5 test-nya dibuang, tersisa DatabaseTests schema-only). Riwayat keputusan ini tetap tercatat di sini agar tak diusulkan ulang tanpa konteks.
- **P3 — Store → transaksi** (1 tx/page, generasi, flip atomik, retensi previous). Gate: 6+1 file test hijau di atas SQLite.
- **P4 — Query → SQL** + indeks; parity hasil vs perilaku lama per filter/sort/page. Gate: test parity.
- **P5 — Partial + progress bar** (baca building-gen, label Partial, progres per-partition lalu overall dari `provider_total` tersimpan). Gate: build + inspeksi.
- **P6 — Live smoke singkat:** Start → 28 pertama tampil → search lokal → pindah tab → Stop → restart → partial + Resume. Tanpa full 90 ribu. Gate: checklist operator.
- **P7 — Pensiunkan JSONL** (dieksekusi: importer + path JSONL + konstanta/batas file + string basi dihapus; §5.4 jadi catatan historis). Gate: parity P3–P6 + audit file + diff-check. Tanpa bump/commit/push.

### 13.5 Final acceptance SQLite (tambahan atas §12)

- Satu `catalog.db` + satu pinned dep; native resolve dari folder citizen.
- Page pertama tampil tanpa menunggu full sync; search/filter/sort atas data partial; label Partial jujur.
- Stop commit page aktif; restart tampilkan partial + Resume.
- Progres per-partition lalu overall hanya setelah semua total diketahui (tanpa persen palsu).
