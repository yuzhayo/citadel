# Downloader: provider flow audit + generic/URL-driven downloader design

Status: research + design recorded — **belum ada implementasi**. Semua klaim kode di bawah
sudah diverifikasi lewat pembacaan file (citasi `file:line`); yang masih berupa
hipotesis ditandai eksplisit.

Tanggal riset: 2026-09-25. Locus: `module/mangareader/Features/Downloader/**`,
`module/mangareader/shareLogic/Sources/**`, `module/mangareader/Library/UpdateChecker/**`.
Referensi eksternal: `C:\VSCODE\ARTEFACT\Suwayomi-Server` (read-only study).

---

## 0. TL;DR untuk operator

1. **Generic downloader berbasis URL sudah ada setengahnya**: `DynamicManualSource`
   (auxiliary source `manual-url`) menerima URL title arbitrer, mem-parse signature
   HTML generik, lalu masuk ke queue/publisher yang sama. Untuk situs yang cocok
   signature-nya, **tambah situs = 0 file C#, 0 baris registrasi**.
2. Yang **belum** ada: profil per-situs sebagai **data** (selector/route/header/UA
   dibekukan jadi konstanta di dalam `DynamicManualHtmlParser`).
3. **Biaya sebenarnya menambah provider ke-6** (terukur): 4 file baru + 2 daftar
   registrasi + opsional 2–3 file filter + 1 assertion test.registrasi.
4. **Core Citadel tidak provider-agnostic**: ada 4 cabang `comix` hardcode di
   `DownloadQueueFeature` + `new ComixSource()` di `QueueManifestSession`. Ini
   penyakit yang **Suwayomi tidak punya** (audit grep: 0 branch per-provider di
   core Suwayomi).
5. **Rekomendasi: 3 tier** — Tier 0 profil JSON (mayoritas situs, 0 kode),
   Tier 1 provider module DLL (situsmedian: WAF/signing/descramble — pakai module
   loader yang sudah ada), Tier 2 in-tree (jarang). Ketiganya memenuhi
   `IMangaSource` yang sama → queue/publisher tidak berubah.

---

## 1. Arus provider Citadel hari ini (FAKTA)

```
Catalog (browse/search) → pilih title → chapter list → pilih chapter
  → Queue job (Queued, TIDAK auto-start; perlu Start/Resume)
  → Resolving → source.GetManifestAsync → ManifestReady (+ manifest.json snapshot)
  → Downloading → PageTransport (native HTTP → proxy → browser fallback)
       → opsional source.TransformPageAsync per-page
  → Publishing → CbzChapterPublisher (validasi → backup → atomic rename)
  → Completed + provenance META-INF/citadel-source.json di dalam CBZ
       + source-index.json "Published"
  → staging dibersihkan
```

**5 provider terdaftar** (`MangaSourceRegistry.CreateDefault`, L105–150):

| Source | Mekanisme | Parser | Transform | Aktivasi/session |
|---|---|---|---|---|
| `comix` | browser pyhost → site Axios (signed/decrypted JSON) | inline di source (1.621 baris) | descramble xorshift | ✅ persistent browser profile, WAF wait, token via interceptor |
| `cucumber-manga` | WordPress/Madara HTML + AJAX POST | `CucumberMangaHtmlParser` (372) | — | cookie client; tolak CF |
| `drake-scans` | JSON + Next.js RSC (`?_rsc=1`) | `DrakeScansJsonParser` + `DrakeScansRscParser` (545 total) | — | — |
| `asura-scans` | API JSON + HTML + XML sitemap | 2 parser (279 total) | — | — |
| `thunder-scans` | sitemap + HTML + embedded reader JSON | `ThunderScansHtmlParser` + `TitleAdapter` (205 total) | — | — |
| `manual-url` (auxiliary) | native HTTP GET/POST + HTML generik | `DynamicManualHtmlParser` (238) | — | — |

`manual-url` sengaja **tersembunyi** dari dropdown Catalog (auxiliary source,
`MangaSourceRegistry.cs:131–149`) tapi bisa di-resolve `FindSource("manual-url")`
oleh Queue.

---

## 2. Kontrak yang sudah generic (`IMangaSource`)

`shareLogic/Sources/MangaSourceContracts.cs:227–294`. 11 member wajib:

```
Id, DisplayName, Capabilities
BrowseAsync, LookupAsync, GetTitleAsync, GetGroupsAsync, GetChaptersAsync,
GetManifestAsync, TransformPageAsync(3-arg), FindAlternateGroupsAsync
(+ default TransformPageAsync 4-arg untuk response-header-aware transform)
```

**Fakta penting (hemat biaya):** saat runtime, queue **hanya memanggil 3 member**:

| Dipanggil | Kapan | Lokasi |
|---|---|---|
| `GetManifestAsync` | resolve + refresh manifest | `DownloadQueueFeature.cs:944–956`, `1220–1226` |
| `TransformPageAsync` | hanya jika `page.Transform != null \|\| Capabilities.TransformsPages` | `ChapterDownloadPipeline.cs:490–502` |
| `FindAlternateGroupsAsync` | hanya setelah page failure | `DownloadQueueFeature.cs:1332–1337` |

`BrowseAsync`/`LookupAsync`/`GetTitleAsync`/`GetGroupsAsync`/`GetChaptersAsync`
**tidak pernah** dipanggil queue — itu milestone Catalog/ManualURL. Artinya:
source yang cuma perlu "bisa di-download" (kasus bookmark) **tidak perlu search
sama sekali** — `DynamicManualSource` sudah membuktikannya (throw di
Browse/Lookup, tetap queueable, `DynamicManualSource.cs:68–75`).

---

## 3. Jalur URL generic yang SUDAH ada (`manual-url`)

Alur: `Catalog → Manual → tempel URL → ManualUrlFeature.LoadAsync → probes
(Asura, Thunder, Drake, Cucumber, Comix) → fallback DynamicManualSource →
RemoteTitleDetail → Catalog detail → queue → CBZ`.

- `ManualUrlProbeRegistry.cs:13–43` — urutan probe fixed; dynamic **terakhir**.
- `DynamicManualSource.cs:57–66` `TryResolveAsync(Uri)` → `RemoteTitleDetail?`
- `DynamicManualHtmlParser.cs:23–36` — title dikenali kalau ada heading
  (`h1.entry-title`, `div.post-title h1`, `#manga-title > h1`, `main h1`) **dan**
  (chapter link ATAU marker Madara `#manga-chapters-holder`/`input.rating-post-id`/`div.summary_image`).
- Identitas durable: `TitleId = TitleHid = Slug = URL`, `ChapterId = URL chapter`
  (`DynamicManualHtmlParser.cs:152–161`).

### Yang masih hardcoded (belum jadi data)

| Hardcoded | Lokasi | Dampak |
|---|---|---|
| 5 selector chapter link | `DynamicManualHtmlParser.cs:19–21` | situs non-Madara gagal |
| chapter-number regex `(?:chapter\|ch)[\s\-_:#]*(\d+(?:\.\d+)?)` | `:230–231` | episode-only/roman/URL tanpa "ch" gagal |
| 3 selector gambar + 7 atribut lazy (`data-src`→`srcset`) | `:92–113`, `:163–185` | situs unik gagal |
| fallback chapter **hanya** `POST <title-url>/ajax/chapters/` | `DynamicManualSource.cs:103–114` | JSON endpoint/ganti GET gagal |
| native HTTP saja; tolak 4 string Cloudflare; HTML ≤ 8 MiB | `:179–209`, `:253–257` | situs JS-only/CF gagal |
| header tetap: UA Chrome 140, `Accept-Language` | `:284–300` | situs butuh header khusus gagal |
| `Capabilities = (false,false,[],false)`; `Browse`/`Lookup` throw | `:55`, `:68–75` | tidak ada search/filter |
| `FindAlternateGroupsAsync` selalu kosong | `:156–163` | tidak ada cross-group |

**Kesimpulan:** URL-only **sudah jalan** untuk situs server-rendered yang cocok
signature; **bukan** URL-only universal.

---

## 4. Biaya menambah provider ke-6 (terukur)

| Yang | File | Wajib? |
|---|---|---|
| Contract/konstanta (id, host, base URL, group, batas) | 1 baru | ya |
| `IMangaSource` (11 member) | 1 baru (Comix 1.621 baris!) | ya |
| Parser (JSON/HTML/RSC/embedded) | 1–2 baru | ya |
| `XManualUrlProbe` (host+path+slug→delegate) | 1 baru | ya |
| Registrasi | `MangaSourceRegistry.CreateDefault` **+** `ManualUrlProbeRegistry.Create` (**2 daftar!**) | ya |
| Filter contribution | 2–3 baru | opsional |
| Assertion registry order test | `DrakeScansSourceTests.cs:208–218` | ya |

Duplikasi terukur:
- **55 member wajib** (5 source × 11)
- 5× setup `HttpClient`/proxy transport; 5× `ManifestHash` builder; 5× page-format
  detector; 5× manifest `Referer` dict; 5× normalisasi+dedup chapter; 5× URL
  validator; 4× `GetGroupsAsync` singleton; 4× `FindAlternateGroupsAsync` empty;
  12 helper validasi; 5× `ManualUrlProbe` (algoritmanya identik, hanya beda
  8–33 baris formatting).
- 3 source punya advanced-filter UI panel terpisah.

---

## 5. Temuan defect /Observasi (evidence-based)

| # | Temuan | Lokasi | Dampak |
|---|---|---|---|
| **D1** | **Manifest `Referer` diabaikan.** `PageTransport` eksplisit skip `Referer` dari `manifest.RequestHeaders`; yang dikirim hanya `ComixReferer(sourceId)` hardcode. | `PageTransport.cs:123–140` + `DownloadQueueFeature.cs:1378–1381` | chapter-referer yang dideklarasikan `DynamicManualSource.cs:135–142` **tidak pernah terkirim**; situs yang mewajibkan referer akan gagal |
| **D2** | **3 state mati**: `Recovering`, `Decoding`, `Validating` dideklarasikan + punya label UI, nol production assignment. | `DownloadQueueModels.cs:16–22`, `QueueGroupProjection.cs:117–136` | state machine lebih besar dari runtime sebenarnya |
| **D3** | **4 cabang Comix di "generic" queue**: 3× `sourceId == "comix"` gate session independen + 1 `ComixReferer`. | `DownloadQueueFeature.cs:922–929, 1201–1208, 1313–1320, 1378–1381` | core tidak provider-agnostic; provider ke-6 tidak akan ikut dapat session/referer path |
| **D4** | `QueueManifestSession` meng-instantiate `new ComixSource(client)` langsung. | `QueueManifestSession.cs:44–52` | tidak bisa jadi session generik tanpa signature factory |
| **D5** | **Tidak ada tabel transisi**; state dijaga tersebar (Start/Resume/StopMany/SetState/Commit). | `DownloadQueueFeature.cs` (5 method) | refactor berisiko |
| **D6** | **Update Matcher tidak.find `manual-url`**: iterasi `AvailableSources` (primary only). | `UpdateMatcher.cs:69–72` + `MangaSourceRegistry.cs:131–149` | title yang di-import manual hanya bisa update-check kalau sudah punya `SourceBinding`/provenance |
| **D7** | **3 catatan binding terpisah**: `SourceBindingStore` (Update Checker, keyed `LocalFolderName`, butuh folder lokal) · `DownloadSourceIndex` (Downloader, keyed `SourceId+TitleId`) · provenance `META-INF/citadel-source.json` (dalam CBZ, otoritatif). | masing-masing file | tidak satu pun bisa jadi "bookmark" remote-only |
| **D8** | `Features/Catalog/CatalogSourceDirectory.cs:7–25` = daftar Comix-only kedua, **tidak dipakai** komposisi aktif. | file tsb + `docs/operations/mangareader-handoff.md:85–98` | dead/parallel stack; provider baru tidak muncul di sana |
| **D9** | Pipeline memakai **posisi list** manifest sebagai ordinal otoritatif, mengabaikan `page.Ordinal`. | `ChapterDownloadPipeline.cs:320–323` | aman selama sumber mengurutkan benar; rapuh kalau tidak |
| **D10** | `ExpectedBytes` dicatat ke `StagedPageRecord` tapi **tidak** dibandingkan ke `ObservedBytes`. | `ChapterDownloadPipeline.cs:535–545` | field dekoratif |
| **D11** | `DownloadSourceIndex` ditulis saat `QueueChapters` dipanggil, **sebelum** job diterima. | `DownloadQueueFeature.cs:168–197` | mapping bisa ada tanpa download |

### Yang SUDAH benar (jangan diubah)

- `CbzChapterPublisher`: validasi archive, deteksi provenance, backup promote,
  atomic rename, tidak ada "(2)" copy (`CbzChapterPublisher.cs:203–301, 341–453`).
- Incomplete-publish policy: hanya boleh jika semua failure `NotFound`, halaman
  downloaded kontigu dari 0, failed trailing (`ChapterDownloadPipeline.cs:49–83`;
  publisher mengulang cek `:109–161`).
- `TerminalPublicationGuardTests` melarang completion owners menyentuh
  Library/Cover/Update/History — batas arsitektur ini dijaga test.
- 3 catatan binding di D7 sengaja terpisah; jangan digabung diam-diam.

---

## 6. Studi referensi: Suwayomi extension architecture

Repo: `C:\VSCODE\ARTEFACT\Suwayomi-Server` (Kotlin, Suwayomi server).

### 6.1 Model (FAKTA)

```
extension store index (JSON/protobuf, signed metadata)
  → install: unduh APK/JAR → <data-root>/extensions/*.jar
  → load: ChildFirstURLClassLoader per-JAR
          entry class dari AndroidManifest meta "tachiyomi.extension.class"
  → entry = satu Source, ATAU SourceFactory.createSources(): List<Source>
  → SourceTable/ExtensionTable = INDEKS (bukan scan folder!)
  → source instance di-cache process-wide, lazy saat pertama dipakai
```

- Artifact APK/JAR; APK di-convert **dex2jar** → JAR (`Extension.kt:159–223`).
- **Discovery = database, bukan directory scan.** Drop JAR ke `extensions/` tidak
  menghasilkan apa-apa tanpa row (`GetSource.kt:43–58`).
- Entry class di-instantiate via **no-arg ctor** (`PackageTools.kt:182–195`).
- ABI gate `LIB_VERSION_MIN=1.3` / `MAX=1.6` (`PackageTools.kt:44–54`).
- Classloader: `ChildFirstURLClassLoader` (parent=`null`), **bukan security
  sandbox** (`ChildFirstURLClassLoader.kt:15–53`).
- Tidak ada execute remote — extension dimuat **in-process** di JVM server.
- Signature `signingKey` ada di store metadata tapi **tidak diverifikasi** saat
  install (`PackageTools.kt:100–153` — `getSignatureHash()` no caller).
- Publisher template / gradle module **tidak ada** di checkout ini (unknowable
  dari repo ini).

### 6.2 Kontrak source Suwayomi (FAKTA, disingkat)

`Source` (wajib): `id: Long`, `name`, `supportsLatest`, `getPopularManga(page)`,
`getLatestUpdates(page)`, `getSearchManga(page, query, filters)`,
`getMangaUpdate(manga, chapters, fetchDetails, fetchChapters)`, `getPageList(chapter)`.
`HttpSource` (abstract) menambah `baseUrl` + default request `GET(baseUrl + manga.url)`.
`ConfigurableSource` → preferences. `ResolvableSource` → `getUriType(uri)` /
`getManga(uri)` / `getChapter(uri)`.

Model: `SManga.url` = **identitas** (bukan display) bersama `sourceId`; `SChapter.url`
= identitas dalam manga; `Page(index, url, imageUrl)`. Source mengembalikan
**URL**, host yang ambil byte.

### 6.3 Temuan KUNCI Suwayomi

1. **Core Suwayomi benar-benar provider-agnostic.** Audit grep seluruh
   `server/src/main/kotlin` untuk `comix/mangadex/thunder/asura/drake/cucumber` +
   hostname dalam kondisi: **0 branch per-provider**. Satu-satunya kondisi
   per-"source" = `LocalSource` (ID 0, compiled-in) dan `StubSource` fallback.
   Bandingkan D3/D4 Citadel.
2. **Host pemilik: persistence + image proxy + archive.** Manga identity
   `(sourceId, url)`; chapter `UNIQUE(url, manga)`; page `UNIQUE(index, chapter)`.
   Host yang tulis CBZ (`ArchiveProvider`), host yang fetch+re-serve image bytes
   (`/api/v1/manga/{id}/chapter/{i}/page/{p}`) — client tidak pernah lihat URL
   origin. Extension **tidak pernah pegang filesystem**.
3. **Bookmark Suwayomi = `mangaId` numeric, bukan URL situs.** `ResolvableSource`
   **ada di API tapi TIDAK di-wire** di server ini (nol call site). Jadi
    Suwayomi sendiri tidak bisa "paste URL situs" — model yang kamu mau **lebih
    maju** dari Suwayomi.
4. Capability flags, bukan subclassing: `supportsLatest`, `source is
   ConfigurableSource`, dll.

### 6.4 Yang bisa dipinjam dari Suwayomi

| Pinjam | Kenapa |
|---|---|
| Larangan branch per-provider di core | Suwayomi bukti ini bisa; Citadel belum |
| Capability flag pada kontrak | Citadel sudah punya (`MangaSourceCapabilities`, `IQueueSourceReadiness`, `ILibraryCoverThumbnails`) — tinggal lengkap |
| Registry = data/DB, bukan list hardcoded | Ganti `MangaSourceRegistry.CreateDefault` |
| ABI version gate | Dynamic reject provider lama |

### 6.5 Yang TIDAK perlu tiru

- **APK/dex2jar/AndroidManifest metadata** — Citadel sudah punya plugin loader
  sendiri (citizen module DLL di `module/<n>/`, runtime registration, deploy dari
  build). Suwayomi butuh semua itu hanya karena server JVM harus emulate Android.
- `StubSource` per-load (`GetSource.kt:66–74`).

---

## 7. Desain: 3 tier (REKOMENDASI)

Semua tier memenuhi `IMangaSource` yang sama → queue/publisher tidak berubah.

```
Tier 0 — Site profile (JSON, DATA)   → mayoritas situs server-rendered.
   Tambah situs = 1 file JSON + paste URL. 0 C#.
   [dasar sudah ada: DynamicManualSource — bekukan selector jadi data]

Tier 1 — Provider module (DLL)      → situs median: WAF/signing/JS/descramble
   (mis. Comix). Tambah = 1 project citizen, publish ke module/.
   Pakai module loader yang SUDAH ADA (bukan classloader baru).

Tier 2 — In-tree                    → butuh integrasi App khusus. Jarang.
```

### Bentuk `SiteProfile` (sketsa, target Inc 1)

```jsonc
{
  "id": "madara-generic",
  "displayName": "Generic (Madara)",
  "strategy": "html",              // html | json | rsc | embeddedJson | sitemap
  "hosts": ["example-manga.com"],
  "title": {
    "urlPatterns": ["/manga/{slug}/", "/series/{slug}"],
    "selectors": { "name": ["h1.entry-title", "..."], "cover": ["..."] }
  },
  "chapters": {
    "linkSelectors": [".wp-manga-chapter a[href]"],
    "numberRegex": "(?:chapter|ch)\\D*(\\d+(?:\\.\\d+)?)",
    "lazyEndpoint": { "method": "POST", "path": "/ajax/chapters/" }
  },
  "pages": {
    "imageSelectors": [".reading-content img"],
    "attributePriority": ["data-src","data-lazy-src","src","srcset"],
    "headers": { "Referer": "{chapterUrl}" }
  },
  "limits": { "maxHtmlBytes": 8388608, "allowedImageHosts": ["cdn.example.com"] }
}
```

`strategy` = enum, bukan dynamic eval. Comix-style (signed API, WAF, descramble)
tidak bisa jadi data → `strategy: "comix-page-axios"` + `namedStrategy: "comix-scramble-v3"` (kode bernama, bukan data).

---

## 8. Increment rencana (eko, masing-masing hijau sebelum lanjut)

| Inc | Isi | Nilai |
|---|---|---|
| **Inc 0** | Fix D1 (referer bug) + D2 (hapus 3 state mati) | hygiene, kecil |
| **Inc 1** | `SiteProfile` schema + loader JSON + `ProfileSource` (strategy `html` saja) | generic path pertama yang datadriven |
| **Inc 2** | Migrasi 5 provider → JSON; **hapus 5 `ManualUrlProbe`**; 2 daftar registrasi → 1 | tambah situs = 1 file |
| **Inc 3** | Tambah strategy `json`/`rsc`/`embeddedJson`/`sitemap` | \<80% situs covered |
| **Inc 4** | Hilangkan D3/D4: generic session factory (`Func<ProxyLease, IMangaSource>`), referer dari manifest | inti benar-benar generik |
| **Inc 5** | Tier 1: provider module (DLL) via citizen loader | situsmedian tanpa edit app |
| **Inc 6** | Bookmark store (remote-only, durable) — D7 | sesuai kalimat "di-bookmark" |

Inc 0 dan Inc 4 adalah prasyarat: tanpa Inc 4, menambah provider ke-6 akan
menabrak cabang Comix.

---

## 9. Item terbuka (belum diputuskan)

| # | Item | Status |
|---|---|---|
| O1 | **`ForceAbort` race (CI red).** `ForceAbort` menulis warning `"Force stopped"` di background Task yang **skip job yang sudah Paused** (`DownloadQueueFeature.cs:640–669`); test `DownloadQueuePauseDrainTests.ForceAbort_SetsTerminal_…:171` gagal di CI karena `Dispose` menang balapan → job Paused duluan → warning tak ditulis. Lolos lokal, gagal runner lambat. | reported ke operator; **belum diperbaiki** (operator: "drop itu"). Release v3.0.31 terblokir CI. |
| O2 | Smoke F-3 (Library index S1–S9) belum dijalankan operator. | terbuka |
| O3 | Peta exact "provider → strategy" (Comix→signed, Cucumber→html, Drake→rsc, Asura→json+sitemap, Thunder→sitemap+embeddedJson) perlu dikonfirmasi saat Inc 1. | proposal |
| O4 | Lokasi `SiteProfile` JSON: repo (versioned, bisa PR) vs LocalAppData (user-editable). | **belum diputuskan** |

---

## 10. Peta file (untuk implementasi)

| Area | File |
|---|---|
| Kontrak | `module/mangareader/shareLogic/Sources/MangaSourceContracts.cs` |
| Registry (hardcoded) | `module/mangareader/Features/Downloader/Sources/MangaSourceRegistry.cs` |
| Probe registry (hardcoded) | `module/mangareader/Features/Downloader/ManualUrl/ManualUrlProbeRegistry.cs` |
| Generic URL source | `module/mangareader/Features/Downloader/ManualUrl/DynamicManualSource.cs` + `DynamicManualHtmlParser.cs` |
| Core queue (jangan disentuh) | `Features/Downloader/Queue/{DownloadQueueFeature,ChapterDownloadPipeline,CbzChapterPublisher,PageTransport,DownloadQueueStore}.cs` |
| Comix branches (target Inc 4) | `QueueManifestSession.cs`, `DownloadQueueFeature.cs:922,1201,1313,1378` |
| Binding | `Features/Downloader/DownloadSourceIndex.cs`, `Library/UpdateChecker/SourceBindingStore.cs` |
| Composition | `Features/Downloader/DownloaderBackgroundService.cs:36–71`, `MangaReaderView.xaml.cs:52–69` |

## 11. Referensi Suwayomi (path di `C:\VSCODE\ARTEFACT\Suwayomi-Server`)

- Extension load: `server/src/main/kotlin/suwayomi/tachidesk/manga/impl/extension/Extension.kt`
- Classloader/ABI: `.../util/PackageTools.kt`, `.../util/ChildFirstURLClassLoader.kt`
- Source API: `server/src/main/kotlin/eu/kanade/tachiyomi/source/**` (`Source.kt`, `CatalogueSource.kt`, `online/HttpSource.kt`, `online/ResolvableSource.kt`, `ConfigurableSource.kt`, `SourceFactory.kt`)
- Host pipeline: `.../manga/impl/{Manga,MangaList,Chapter,Page,Source}.kt`, `.../impl/download/**`
