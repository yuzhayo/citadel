# CITADEL-FLOWS-MANGAREADER — citizen terbesar

229 file, 45.348 baris = 57% seluruh kode produksi citadel. Ditulis 2026-09-11 dari
kode. Klaim bertanda `file:baris`. Kontrak yang mengadili:
`.agents/skills/citadel-feature-modularity/SKILL.md` (FM-1..FM-5) dan
`.agents/skills/citadel-shared-ui/SKILL.md` (SU-1..SU-5).

**Temuan utama, dan ini inti seluruh tugas:** mangareader memuat **dua pola yang
bertolak belakang di dalam satu citizen**.

- `Reader/` adalah **implementasi referensi yang klaimnya terbukti**: composition
  root 168 baris yang tidak menyebut satu pun tipe feature konkret, nol tepi
  feature→feature, semua lalu lintas lewat empat hub. Menambah feature drawer-card
  atau visual = **nol edit di host**.
- `Features/Downloader/` + `Features/Catalog/` memakai pola lain: registrasi `new`
  eksplisit, panggilan konkret antar sibling, dan **dua provider Comix yang
  diduplikasi hampir utuh** (1.621 + 1.503 baris).

Jadi keluhan owner bukan seragam: ia benar untuk sisi Downloader/Catalog, dan
**tidak** benar untuk sisi Reader.

Bagian yang terdokumentasi di sini: Reader (§1–§5), Downloader (§6–§9),
parent module (§10), Library/Grouping/UpdateChecker (§11), History (§12),
CatalogMirror (§13–§14), shareLogic (§15), tepi lintas subsistem (§16),
duplikasi (§17), dan rujukan register (§18).

---

## 1. Reader — klaim "reference implementation" diuji

Klaimnya ada di `SKILL.md:262-297`, dengan bentuk yang dijanjikan:
`ReaderWindow.cs` (composition root tipis) · `ReaderCore/` (infrastruktur) ·
`Features/<nama>/` (feature pluggable).

**Bentuk nyata cocok, dengan satu drift nama:** yang ada adalah pasangan WPF
`Reader/ReaderWindow.xaml` (170 baris) + `ReaderWindow.xaml.cs` (168 baris), bukan
`ReaderWindow.cs` tunggal seperti ditulis `SKILL.md:269`.

### 1.1 Composition root: 168 baris, nol tipe feature

`ReaderWindow.xaml.cs` hanya menyebut **infrastruktur**:

| Baris | Infrastruktur |
| --- | --- |
| `:31` | `CbzReaderChapterLoader` |
| `:50-53` | `ReaderSessionState` |
| `:56-58` | `ReaderCommandHub`, `ReaderNotificationHub`, `ReaderActivityHub` |
| `:59-67` | `FrameContentHost`, `ReaderStatusHost` |
| `:68` | `ReaderChapterNavigationHub` |
| `:71-82` | `ReaderInputRouter`, `ReaderFeatureContext` |
| `:92-99` | `ReaderFeatureHost` |

Kelima belas tipe feature konkret muncul **hanya** di
`Reader/ReaderDefaultFeatureCatalog.cs:18-32`. Window tidak pernah menyentuhnya.

### 1.2 Vonis per aturan FM

| Aturan | Vonis | Bukti |
| --- | --- | --- |
| **FM-1** parent tak tahu internal feature | **PATUH** | grep repo-wide: tidak ada tipe internal feature (`ChapterCoordinator`, `ChapterPreloader`, `ChapterNavigator`, `IChapterLoadingRuntime`, `ReaderDrawerPolicy`, `ReaderAutoScrollPolicy`, `ReaderFullscreenGeometry`) yang muncul di luar foldernya pada kode produksi. Kecocokan hanya di folder sendiri, katalog, test, dan dokumen |
| **FM-2** komunikasi lewat event/hub | **PATUH** | Reset menjangkau Zoom/Dim/AutoScroll/Pin **hanya** lewat perintah hub (`ReaderResetController.cs:46-53`); tidak ditemukan satu pun panggilan langsung feature→feature |
| **FM-3** parent tak cek "feature X ada?" | **PATUH** | host hanya menegakkan invarian bentuk: tepat satu `IReaderDrawerContributionHost` (`ReaderFeatureHost.cs:42-47`), key/name cocok (`:31-35`). Registrasi lewat katalog terpusat, sesuai `SKILL.md:277` ("ideally via a catalog") — bukan self-registration |
| **FM-4** state bersama lewat context | **CAMPURAN** | **Untuk:** `ReaderFeatureContext` adalah service container (`ReaderFeatureContract.cs:250-288`), feature menarik saat Attach (`ChapterLoadingFeature.cs:93-98`, `ReaderOverlay.xaml.cs:19-24`). **Melawan:** 8 dari 15 feature menerima `ReaderSessionState` + `ReaderCommandHub` konkret yang **diantar tangan** lewat closure konstruktor di katalog (`ReaderDefaultFeatureCatalog.cs:20,24-30`); `Attach(context)` milik Drawer mengabaikan parameter context-nya sama sekali (`ReaderDrawer.xaml.cs:53-59`) |
| **FM-5** parent hanya koordinasi | **SEBAGIAN BESAR PATUH** | window = bangun infrastruktur (`:47-99`), wiring permukaan (`:101-103`), lifecycle (`:125-167`). **Melawan (ringan):** window menduplikasi pekerjaan judul milik feature Chrome (`ReaderWindow.xaml.cs:141-142` vs `ReaderChromeController.cs:29-33`), dan XAML parent memiliki template render halaman termasuk transform ZoomScale (`ReaderWindow.xaml:50-89`, `:57-61`) |

### 1.3 Peta feature Reader

Semua mengimplementasikan `IReaderFeature` (`ReaderFeatureContract.cs:10-14`).

| Folder | Pekerjaan | Kontrak tambahan | Rujukan internal dari luar folder |
| --- | --- | --- | --- |
| `ChapterLoading` | muat bergulir 3-permukaan, preload, lompat | `IReaderStartableFeature`, `IReaderChapterNavigation` | tidak ada (katalog hanya menyebut tipe entri; test mengompilasi sumbernya langsung) |
| `Overlay` | permukaan klik 3-zona | `IReaderVisualFeature` | tidak ada — **tapi** ia meng-instantiate `ReaderViewportNavigator` yang tinggal di root Reader (`ReaderOverlay.xaml.cs:8,22`) |
| `Drawer` | buka/tutup drawer + host kontribusi | `IReaderVisualFeature`, `IReaderDrawerContributionHost` | `ReaderDrawerPolicy` hanya dipakai di dalam folder (`ReaderDrawer.xaml.cs:69`) + test |
| `Chrome` | adapter judul `SettingWindowChrome` | `IReaderVisualFeature` | tidak ada |
| `Toast` | pesan sementara | `IReaderVisualFeature` | tidak ada |
| `ChapterNavigation` | pemilih + kartu drawer prev/next | `IReaderDrawerContributionProvider` | tidak ada |
| `Fullscreen` | fullscreen monitor + restore | provider | `Geometry` publik, dikonsumsi hanya di dalam folder (`:84`) + test |
| `AutoScroll` | gulir berbasis waktu | provider | `Policy` di dalam folder (`:179,192`) + test |
| `ManualScroll` | gulir manual + momentum | provider | `Policy` di dalam folder (`:16`, `:70`) + test |
| `ResumePosition` | simpan/pulihkan posisi baca | `IReaderFeature` biasa | tidak ada |
| `Diagnostics` | statistik frame + kontinuitas gulir | `IReaderFeature` biasa | `Policy` di dalam folder (`:41`, `:88`) + test |
| `Pin` / `Zoom` / `Dim` / `Reset` | pin toggle / pemilik zoom / overlay+slider dim / reset global | provider (keempatnya) | tidak ada |

### 1.4 Mekanisme registrasi dan biaya menambah feature

Katalog statis eksplisit, **tanpa refleksi dan tanpa self-registration**:

```text
ReaderDefaultFeatureCatalog.cs:12-32    15 panggilan .Add(nama, factory)
        │  menyebut dirinya "single explicit registration point" (:6-9)
        ▼
ReaderWindow.xaml.cs:92-99              dikonsumsi di sini
        ▼
ReaderFeatureHost.cs:27-40              loop attach
        :49-63   kartu drawer diagregasi otomatis
        :72-84   visual di-mount otomatis
        :92-97   start
        :104-105 dispose urutan terbalik
```

**Biaya menambah satu feature Reader:**

| Langkah | Wajib? |
| --- | --- |
| buat folder `Reader/Features/<Nama>/` + kelas `IReaderFeature` | ya |
| satu baris di `ReaderDefaultFeatureCatalog.cs:18-32` | ya |
| entri `Compile`/`Page` di csproj test | **tidak perlu** — `tests/Module.Mangareader.Reader.Tests/Module.Mangareader.Reader.Tests.csproj:43-44` memakai glob `Reader\**` |
| edit `IReaderCommands` + `ReaderCommandHub` (`ReaderFeatureContract.cs:51-108`) | **hanya bila** butuh perintah baru |
| edit `ReaderLayer` (`ReaderFeatureCatalog.cs:3-11`), XAML ContentControl (`ReaderWindow.xaml:93-122`), peta host (`ReaderWindow.xaml.cs:83-91`) | **hanya bila** butuh lapisan baru |
| edit host untuk feature drawer-card / visual | **NOL** |

Ini kontras tajam dengan Downloader (§6) dan camoprof. **Reader membuktikan bentuk
yang owner inginkan sudah mungkin di repo ini.**

### 1.5 Empat hub (pemilik lalu lintas antar feature)

| Hub | Pemilik | Contoh lalu lintas |
| --- | --- | --- |
| `ReaderCommandHub` | `ReaderFeatureContract.cs:71-109` | Overlay→Drawer (`ReaderOverlay.xaml.cs:40`); Reset→AutoScroll/Zoom/Dim/Pin (`ReaderResetController.cs:42-60`); InputRouter→Fullscreen/Dim/Zoom (`ReaderInputRouter.cs:93-138`) |
| `ReaderNotificationHub` | `ReaderFeatureContract.cs:117-127` | Fullscreen (`ReaderFullscreenController.cs:126`), peringatan prefs window (`ReaderWindow.xaml.cs:144-145`) → Toast (`ReaderToast.xaml.cs:24`) |
| `ReaderActivityHub` | `ReaderCore/ReaderActivityHub.cs:27-38` | Drawer melapor `DrawerOpened` (`ReaderDrawer.xaml.cs:85`); AutoScroll berhenti saat aktivitas manual (`ReaderAutoScrollController.cs:111,190-193`) |
| `ReaderChapterNavigationHub` | `ReaderCore/ReaderChapterNavigationHub.cs:15-24` | ChapterLoading mendaftar (`ChapterLoadingFeature.cs:132-136`); Chrome (`:23`), ChapterNavigation (`:86,121`), Zoom (`:174`), AutoScroll (`:135,187`) mengonsumsi |

Satu jalur tidak langsung yang tetap patuh FM-2: AutoScroll bereaksi terhadap
`IsDrawerOpen` milik Drawer lewat `PropertyChanged`
(`ReaderAutoScrollController.cs:208-213`) — mediated by state, bukan panggilan.

**Nol tepi tipe-konkret antar folder feature** (grep repo-wide; semua kecocokan ada
di folder sendiri, katalog, test, atau dokumen).

### 1.6 ReaderCore — infrastruktur

| File | Pekerjaan |
| --- | --- |
| `FrameContentHost.cs` | adapter `IReaderViewport` di atas ScrollViewer/ItemsControl (`:14-110`) |
| `ReaderStatusHost.cs` | panel loading/error + fasad `IReaderStatusHost` (`:1-89`) |
| `ReaderActivityHub.cs` | satu stream activity-origin bertipe (`:27`) |
| `ReaderChapterNavigationHub.cs` | fasad navigasi late-bound + registri (`:15-24`) |
| `ReaderDrawerContributions.cs` | kontrak kartu drawer opaque bersama + factory kartu bergaya Setting (`:15`, `:34-53`) |
| `ReaderInputRouter.cs` | satu-satunya pemilik preview-input tingkat window (`:11`) |
| `ReaderPreferencesStore.cs` | persistensi tervalidasi, atomik, ter-debounce + seam FileIO (`:29`, `:371-390`) |
| `ReaderSessionState.cs` | state sesi observable; baca publik `IReaderStateView` (`:5`), tulis internal (`:62-98`) |

Context bersama (FM-4) adalah `ReaderFeatureContext` di
`ReaderFeatureContract.cs:250-288` — **tinggal di root Reader, bukan di
`ReaderCore/`**. Tempat feature menarik: `ChapterLoadingFeature.cs:93-98`,
`ReaderOverlay.xaml.cs:19-24`, `ReaderZoomController.cs:87-98`,
`ReaderChromeController.cs:21-23`, `ReaderToast.xaml.cs:23-24`,
`ReaderChapterNavigation.cs:31-86`. Catatan: `SessionState` pada context bersifat
`internal` (`:278`), jadi sisi tulis state sampai ke feature lewat injeksi
konstruktor katalog — itulah sisi "melawan" pada FM-4.

---

## 2. Flow Reader (untuk diagram)

```text
ReaderWindow (composition root, 168 baris)
│  bangun: SessionState · CommandHub · NotificationHub · ActivityHub ·
│          FrameContentHost · StatusHost · ChapterNavigationHub · InputRouter ·
│          FeatureContext · CbzReaderChapterLoader
▼
ReaderDefaultFeatureCatalog.cs:18-32   ← 15 feature, SATU-SATUNYA tempat namanya disebut
▼
ReaderFeatureHost.cs:27-40  attach → :49-63 kartu drawer → :72-84 visual
                            → :92-97 start → :104-105 dispose terbalik
▼
feature hidup di foldernya masing-masing
│  saling bicara HANYA lewat empat hub
│  menarik kebutuhan dari ReaderFeatureContext
▼
ReaderInputRouter  ← satu-satunya pemilik preview-input tingkat window
```

---

## 3. Pelanggaran dan ketegangan di Reader

| Temuan | Bukti |
| --- | --- |
| **Logika feature tinggal di luar folder feature.** `ReaderViewportNavigator.cs:8` ("Coalesced 90%-viewport **Overlay** navigation") dan `ReaderViewportStepPolicy.cs` ada di root Reader; satu-satunya konsumen adalah Overlay (`ReaderOverlay.xaml.cs:22`) | FM-5 / ST-1 |
| **`ReaderFeatureContract.cs` 289 baris memuat ±15 tipe**, termasuk dua hub konkret (`:71`, `:117`). `SKILL.md:270` menempatkan "hubs" di `ReaderCore/`, tapi 2 dari 4 hub tinggal di root Reader | ST-1 + drift dari bentuk yang dijanjikan skill |
| **Judul window ditulis dua kali.** `ReaderWindow.xaml.cs:141-142` vs `ReaderChromeController.cs:32` | duplikasi |
| **Zoom mengekspor ulang konstanta `ReaderValuePolicy`** (`ReaderZoomController.cs:15-18`) | duplikasi ringan |
| **Klaim komentar tidak umum.** `ChapterLoadingFeature.cs:66-68` "all dependencies arrive through Attach" — benar hanya untuk feature ini; Drawer/Pin/Reset memakai injeksi ctor dan mengabaikan/memakai sebagian context (`ReaderDrawer.xaml.cs:53-59`, `ReaderPinController.cs:55-60`, `ReaderResetController.cs:41-42`) | A-kategori |
| **Klaim katalog TERBUKTI benar.** `ReaderDefaultFeatureCatalog.cs:7-8` dan `ChapterLoadingFeature.cs:33-35` — diverifikasi grep | positif |
| **`CoverBuilderView.xaml.cs` 440 baris** membawa logika alur di luar wiring: orkestrasi batal/sibuk (`UpdateAvailability :297`, `EndOperation :325`), pemuatan preview (`LoadSelectedCover :342`, `LoadCoverAsync :384`), penyusunan teks status (`BakeSkippedDetail :282`) | ST-1 |
| **`ReaderPreferencesStore.cs`** = store + 3 record + `IReaderPreferencesFileIO` + implementasi (`:9-23`, `:371-390`) — kohesif tapi ±4 tipe | ST-1 ringan |

---

## 4. Components/ — combo component, dengan satu pengecualian

Namespace `Module.Mangareader.Components`.

| Komponen | Ukuran | Penilaian SU |
| --- | --- | --- |
| `MangaDetailView` | xaml 116 / cs 127 | **combo murni.** 7 dependency property; komentarnya eksplisit "composition of existing Citadel primitives only" (`MangaDetailView.xaml:9-18`). Tidak memakai elemen `setting:` tapi mengikat style bersama `SettingCardStyle` (`:74`), `SettingScrollViewerStyle` (`:78`), `SidebarItemCornerRadius` (`:51`). Konsumen: `Library/ChapterSelectorView.xaml:31`, `Features/Downloader/Catalog/CatalogScreen.xaml:170`, `Features/CatalogMirror/CatalogMirrorView.xaml:307` |
| `MangaViewModeSelector` | xaml 40 / cs 67 | **combo murni.** Toggle Grid/List = dua `setting:SettingButton` (`:4,16,27`), tanpa kepemilikan state (`cs:7-11`). Konsumen: `History/HistoryView.xaml:32`, `Library/LibraryView.xaml:27` |
| `MangaMultiSelectFilter` | xaml 48 / cs 189 | **menambah perilaku interaksi baru.** Memakai `setting:` (`xaml:4`) + `setting:SettingButton` (`:10`) dan mengimplementasikan kontrak bersama `IUiPreferenceControl` (`cs:49`, interface di `setting/Components/UiPreference.cs:13`). Tapi Popup/CheckBox/chevron adalah WPF polos (`xaml:16-46`, `cs:76-84`) — **satu-satunya komponen yang menambah perilaku rendah baru**. Tidak ada primitif multi-select di `setting/Components/`, jadi **tidak ada yang disalin**. Konsumen: `CatalogMirrorView.xaml:165-185`, `ComixFilterPanel.xaml:40-90`, `CucumberMangaFilterPanel.xaml:18-24`, `DrakeScansFilterPanel.xaml:7-10`. Tidak dipakai History/Library |

Soal `MangaMultiSelectFilter`: SU-4 mengatakan primitif baru butuh persetujuan
eksplisit dan satu-satunya pengecualian adalah combo component yang "introduces no
new low-level rendering or interaction behavior". Komponen ini **memakai** primitif
bersama dan mengimplementasikan kontrak bersama, tapi menambahkan perilaku
popup/multi-select sendiri. Ia dipakai empat layar (bukan satu), jadi peran
reusabel-nya nyata. Penilaiannya tergantung tafsir: bukan penyalinan (SU-2 aman),
tapi bukan combo murni (SU-4 abu-abu). → register C11, dicatat sebagai abu-abu,
bukan pelanggaran tegas.

---

## 5. CoverBuilder

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `CoverSourceReference.cs` | 21 | input bertipe LocalPath/RemoteUrl (`:12-20`) |
| `CoverSourceLoader.cs` | 217 | muat/fetch/konversi-PNG/cache (`:16`) |
| `CoverBuilderService.cs` | 86 | **satu-satunya pemilik bake**, kebijakan fetch-sebelum-bake (`:10-19`, `:34-51`) |
| `CoverBuilderView.xaml(.cs)` | 147 / 440 | UI: `setting:SettingViewport` (`xaml:7`), `SettingField` (`:67`), `SettingButton` (`:71,79,110`) |

Konsumen: `MangaReaderView.xaml:26` (tab module); test lewat
`tests/Module.Mangareader.Downloader.Tests/Module.Mangareader.Downloader.Tests.csproj:45-50`.

---

## 6. Downloader — pola yang berbeda

### 6.1 Pohon feature

| Folder | file | Pekerjaan | Kontrak |
| --- | --- | --- | --- |
| `Downloader/` (root) | 7 | komposisi + plumbing bersama | `DownloaderContext` = container yang dihadapi child (`DownloaderContract.cs:18`); `DownloaderView.xaml.cs:13` host route (Catalog ↔ ManualUrl, **hanya meneruskan event**); `DownloaderPyHostClient.cs:46` pemilik host browser python; `DownloadSourceIndex.cs:33` pemetaan judul→folder + catatan chapter terbit; `DownloaderPathContainment.cs` penjaga path-traversal |
| `Catalog/` | 6 | layar jelajah/detail online | `CatalogState` (`CatalogFeature.cs:10`), `CatalogFeature` (`:76`); adapter presentasi `RemoteTitleCardModel.cs:16`, `RemoteTitleDetailAdapter.cs:16` |
| `Queue/` | 14 | state job, penjadwalan, staging halaman, terbit CBZ | `DownloadQueueFeature` (`:29`), record di `DownloadQueueModels.cs:11-165`, `PageTransport`/`PageFetchResult` (`PageTransport.cs:62,38`), `PipelineResult` (`ChapterDownloadPipeline.cs:30`), `PublicationOutcome` (`CbzChapterPublisher.cs:48`), `DownloadQueueStore` (`:151`); UI `DownloadListScreen.xaml.cs:16` **hanya mengikat snapshot** |
| `Sources/` | 27 | registri + 6 folder provider (AsuraScans, Comix, CucumberManga, DrakeScans, ThunderScans, WeebCentral) | `MangaSourceRegistry`/`MangaSourceRegistration`/`IRemoteFilterContribution`/`IRemoteFilterState` (`MangaSourceRegistry.cs:55,42,13,27`), `IQueueSourceReadiness` (`QueueSourceReadiness.cs:8`) |
| `ManualUrl/` | 8 | resolusi URL tempel | `IManualUrlProbe` (`ManualUrlContracts.cs:10`), `ManualUrlResolution` (`:45`), `ManualUrlFeature` (`:7`); plus provider bantu `DynamicManualSource` (`:19`) |
| `FilterSearch/` | 12 | panel filter per provider (Comix 3, CucumberManga 2, DrakeScans 3, WeebCentral 2) | `FilterSearchFeature.cs:11` memegang kontribusi aktif, membangun snapshot jelajah immutable |
| `Lister/` | 1 | probe ketersediaan lokal, read-only | `ListerFeature.cs:36`, `ListerResult` (`:12`) |
| `AutoCover/` | 1 | menulis `cover.png` | `AutoCoverFeature.cs:55`, `CoverCandidate:13`, `AutoCoverOutcome:37` |
| `mangareader_downloader/` | 2 py | plugin pyhost | perintah `downloader.open/api/fetch/render/close/egress` (`plugin.py:17-24`) |

### 6.2 Flow unduh end-to-end

```text
[1]  Catalog/CatalogScreen.xaml.cs:444   _context.Queue.QueueChapters(...)
     (handoff lewat parent: MangaReaderHandoffs.cs:54 EnqueueUpdate / :160 QueueHandoff)
[2]  Queue/DownloadQueueFeature.cs:168   QueueChapters — admission · dedupe · persist
[3]  EnsureStarted :851 · RunDownloadClaimedAsync :879
     PARALELISME #1: JobConcurrency = 4 (:31)
[4]  per job: cari source _sources.FindSource :915
     gerbang readiness opsional lewat INTERFACE IQueueSourceReadiness :931
[5]  source.GetManifestAsync :954
     Comix = python: ComixSource.cs:1489 → DownloaderPyHostClient.ApiAsync :415
             → "downloader.api" (DownloaderPyHostClient.cs:444, plugin.py:19)
     CucumberManga / DrakeScans / DynamicManual = C# murni HttpClient
             (CucumberMangaSource.cs:119, DrakeScansSource.cs:11, DynamicManualSource.cs:19)
[6]  ChapterDownloadPipeline.RunAsync :154 (dibangun DownloadQueueFeature.cs:1045,
     dipanggil :1051)
     PARALELISME #2: DirectPageConcurrency = 2 · ProxyPageConcurrency = 8
             (:112-113) lewat Parallel.ForEachAsync (:320)
     retry 1/2/4 detik (:105-109) · pass pemulihan :212 · jurnal staging job.json (:91-92, :114)
[7]  PageTransport.FetchNativeAsync :147
     C# native HTTP streaming DULU
     fallback browser HANYA untuk NetworkFailed/Challenge (:51-53)
       → FetchBrowserFallbackAsync :286 → DownloaderPyHostClient.FetchToStagingAsync
         :454 → "downloader.fetch" (:466, plugin.py:20)
     python menulis byte ke staging; HANYA bukti yang kembali
       (PageTransport.cs:56-60 doc, :294-349)
[8]  deteksi format gambar dari byte (PageTransport.cs:408)
     transform milik provider (ChapterDownloadPipeline.cs:511)
       → IMangaSource.TransformPageAsync (MangaSourceContracts.cs:267)
       mis. Comix/ComixPageDecoder.cs, CucumberManga/CucumberMangaHtmlParser.cs
[9]  CbzChapterPublisher.PublishAsync / PublishIncompleteAsync (:87,115)
     atomik temp→final · provenance META-INF/citadel-source.json (:70)
     memakai shareLogic ArchiveValidator / IArchiveLockCoordinator /
     LatestCoverBackupStore (:73-85)
[10] catat index DownloadQueueFeature.cs:1171 (TryRecordPublished)
     refresh Library SENGAJA tidak dirantai (Lister/ListerFeature.cs:24-29)
     reader nanti membuka CBZ lewat shareLogic/CbzChapterLoader.cs:11
       → shareLogic/Archive/ArchivePageReader.cs:33-51
     scanner yang menemukan folder baru: Library/LibraryIndexCoordinator.cs:109
       (Reconcile) + Library/LibraryIndexWatcher.cs:72,80
[11] cabang sampul: CatalogScreen.xaml.cs:490 menaikkan CoverCandidateAvailable
     → DownloaderView.xaml.cs:97-113 → AutoCoverFeature.SaveCoverAsync (:78)
```

### 6.3 Mekanisme plugin provider

**Interface utama:** `IMangaSource` di `shareLogic/Sources/MangaSourceContracts.cs:227-294`
(Browse, Lookup, GetTitle, GetGroups, GetChapters, GetManifest, TransformPage,
FindAlternateGroups). Facet opsional: `IQueueSourceReadiness`
(`Sources/QueueSourceReadiness.cs:8`), `ICatalogSnapshotSource`
(`shareLogic/Sources/CatalogSnapshotContracts.cs:123`), `IManualUrlProbe`
(`ManualUrl/ManualUrlContracts.cs:10`), `IRemoteFilterContribution`
(`Sources/MangaSourceRegistry.cs:13`).

**Tempat registrasi:** `MangaSourceRegistry.CreateDefault` di
`Sources/MangaSourceRegistry.cs:110-156` — `new` eksplisit tiap source (`:118-136`)
dan tiap lambda kontribusi filter (`:139-155`). Tanpa refleksi/discovery **secara
sengaja** (`:51-54`).

**Biaya menambah SATU provider:**

| Tindakan | File |
| --- | --- |
| BUAT | `Sources/<P>/<P>Source.cs` (+ parser/kontrak; DrakeScans 5 file, CucumberManga 3 file) |
| BUAT | `FilterSearch/<P>/<P>FilterContribution.cs` + `<P>FilterPanel.xaml` (+ `.xaml.cs` bila perlu — CucumberManga tidak punya, DrakeScans punya) |
| BUAT (opsional) | `Sources/<P>/<P>ManualUrlProbe.cs` |
| **EDIT** | `Sources/MangaSourceRegistry.cs:110-156` — **dua tempat**: `new` + entri registrasi |
| **EDIT (opsional)** | `ManualUrl/ManualUrlProbeRegistry.cs:12-48` — hanya bila provider mau dukungan manual-URL (CucumberManga punya, DrakeScans punya) |
| tidak perlu | `tests/Module.Mangareader.Downloader.Tests/Module.Mangareader.Downloader.Tests.csproj` — `Compile`/`Page` memakai glob `Features\Downloader\**` (`:60-61`) |
| **EDIT** | `version.props` |
| tidak perlu | `Module.Mangareader.csproj` — SDK globbing; csproj hanya memuat payload Rar/pyhost/sqlite (`:9-79`) |

**Kontribusi UI:** ya. `IRemoteFilterContribution.CreatePanel()` mengembalikan
`FrameworkElement` (`MangaSourceRegistry.cs:13-18`); file registri meng-import
`System.Windows` (`:1`) dan record registrasinya menyimpan
`Func<IRemoteFilterContribution>` (`:42-44`). Yang dibangun registri adalah
**objek kontribusi** (pemilik UI), bukan panelnya; panel dibangun malas oleh layar
(`CatalogScreen.xaml.cs:160-167`, lewat `FilterSearchFeature.SelectSource`,
`FilterSearchFeature.cs:17-21`).

**Isolasi provider: bersih.** Tidak ada folder provider yang mereferensikan folder
provider lain (diverifikasi grep lintas `Sources/` dan `FilterSearch/`). File
FilterSearch tiap provider hanya meng-import namespace Sources-nya sendiri
(`CucumberMangaFilterContribution.cs:5`, `ComixFilterPanel.xaml.cs:7`,
`DrakeScansFilterPanel.xaml.cs:4`). Kode bersama hanya tingkat kontrak.
**Satu-satunya titik agregasi** yang menyentuh beberapa provider konkret di luar
registri adalah `ManualUrlProbeRegistry.cs:2-7,18-45` (Comix + CucumberManga +
DrakeScans + AsuraScans + ThunderScans + WeebCentral + DynamicManual sebagai tipe
konkret).

### 6.4 Tepi antar sub-folder di dalam Downloader

| Tepi | Jenis | Bukti |
| --- | --- | --- |
| Queue → Sources | **campuran**: konkret `MangaSourceRegistry` + interface `IQueueSourceReadiness` | `DownloadQueueFeature.cs:3,38,915` vs `:931` |
| Catalog(screen) → Queue/AutoCover/Lister/Index/ManualUrl | **konkret** via `DownloaderContext`; panggilan metode langsung, bukan event | `CatalogScreen.xaml.cs:444,508,825,872`; field context `DownloaderContract.cs:44-60` |
| Catalog(screen) → parent | **event** | `OpenDownloadList` (`CatalogScreen.xaml.cs:108`), `CoverCandidateAvailable` (`:121`, dinaikkan `:490`), `OpenDownloadList` dinaikkan `:495`; ditangani `DownloaderView.xaml.cs:41-46` |
| CatalogFeature → FilterSearch | konkret | `CatalogFeature.cs:3,80,160` |
| AutoCover → Lister | konkret | `AutoCoverFeature.cs:3,60,112` |
| ManualUrl → Sources | tipe provider konkret | `ManualUrlProbeRegistry.cs:18-45`; `ManualUrlFeature` sendiri interface-only atas `IManualUrlProbe` (`ManualUrlFeature.cs:9`) |
| Lister → (dalam Downloader) | tidak ada | memakai `shareLogic/LocalTitleProbe` (`ListerFeature.cs:3,38`) |
| event yang dipakai | `QueueSummaryChanged` (`DownloadQueueFeature.cs:119` → `DownloadListScreen.xaml.cs:60`), `StateChanged` (CatalogFeature/ManualUrlFeature → layarnya) |

**Pola yang terlihat:** event untuk **notifikasi perubahan state**, panggilan
konkret untuk **perintah**. FM-2 menghendaki komunikasi lewat event/hub; Downloader
memakai event setengah jalan.

Dua klaim komentar yang perlu dikoreksi:

- `DownloaderContract.cs:11-17` — "no child reaches into a sibling's mutable state
  through this". Tidak ditemukan pembacaan mutable state, **tapi** child memanggil
  perintah sibling pada tipe konkret (`CatalogScreen.xaml.cs:444,872`), yaitu
  panggilan langsung, bukan pola event/hub yang diminta skill.
- `MangaSourceRegistry.cs:105-109` — "The one explicit registration point… the
  Downloader parent and Catalog composition are never edited". Benar untuk parent
  dan Catalog, **tapi** `ManualUrlProbeRegistry.cs:12-48` adalah titik registrasi
  per-provider **kedua** (diedit untuk probe DrakeScans). → register A10.

### 6.5 Duplikasi terbesar di citadel: dua provider Comix

| Aspek | `Features/Downloader/Sources/Comix/ComixSource.cs` | `Features/Catalog/Sources/Comix/CatalogComixSource.cs` |
| --- | --- | --- |
| Ukuran | **1.621 baris** | **1.503 baris** |
| `ComixOptions` | `:10` | `:11` |
| `ComixContract` | `:153` | `:153` |
| `ComixBrowseQuery` | `:323` | `:313` |
| `ComixContractException` | `shareLogic/Sources/MangaSourceContracts.cs:26` — **satu** definisi dipakai keduanya | idem |
| Decoder halaman | `Comix/ComixPageDecoder.cs` | `Catalog/Sources/Comix/CatalogComixPageDecoder.cs` |

3.124 baris yang mendefinisikan provider yang sama dua kali. Tiga tipe
(`ComixOptions`, `ComixContract`, `ComixBrowseQuery`) masih punya dua definisi
hampir identik, dan decoder halaman masih terpisah. Ini **satu pekerjaan, dua
pemilik** dalam skala terbesar di repo. → register C12.

Yang **sudah** tidak terduplikasi: exception-nya. Satu `ComixContractException`
di `shareLogic/Sources/MangaSourceContracts.cs:26` dipakai kedua provider, dan
`LevelBGuardTests` menahannya dari dua arah —
`NoSameNamedPublicTypeInTwoFeatureFolders` (`:111-150`) gagal bila muncul nama
tipe duplikat baru di luar tiga di atas, dan `FixedCouplingsStayRemoved`
(`:153-172`) gagal bila `CatalogMirrorSyncFeature` meng-import Downloader lagi.

### 6.6 Duplikasi lain

| Temuan | Bukti |
| --- | --- |
| Cek magic-byte PNG ditulis **delapan** kali, bukan dua | `PageTransport.cs:411`, `AutoCoverFeature.cs:225`, `ComixSource.cs:1597`, `CatalogComixSource.cs:1479`, `DynamicManualSource.cs:272`, `CucumberMangaSource.cs:573`, `DrakeScansSource.cs:340`, `WeebCentralSource.cs:342`. Satu-satunya yang terpusat: `shareLogic/Archive/CoverArchiveWriter.cs:26` (`PngSignature`) |
| Dua sanitizer nama file dengan **aturan berbeda** untuk pekerjaan mirip | `DownloadQueueFeature.SanitizeFolder :1656` vs `ChapterDownloadPipeline.Sanitize :595` |
| `MangaSourceRegistry.cs` memuat 2 interface UI + record registrasi + registri + factory dalam satu file | `:13,27,42,55,110-113` |
| `CucumberMangaSource.cs` memuat kontrak, exception, enum, options, record query, dan source | `:13-119` |
| `ComixSource.cs` serupa | `:10-472` |
| `DownloadQueueStore.cs` juga menjadi rumah helper JSON generik `DownloaderJson` (`:39`) yang dipakai `DownloadSourceIndex.cs:43` dan `DownloaderBackgroundService.cs:36` | helper bersama tinggal di dalam satu feature |

---

## 7. shareLogic (milik module, ejaan kecil) vs sharedLogic (milik repo)

`module/mangareader/shareLogic/` (namespace `Module.Mangareader.ShareLogic`) adalah
kode bersama **tingkat module**, bukan Downloader-only — berbeda dari
`module/sharedLogic/` yang di-compile masuk lewat `Module.Mangareader.csproj:10`.

| File | Konsumen |
| --- | --- |
| `Sources/MangaSourceContracts.cs` | Downloader (registri, queue, semua provider), Library UpdateChecker (`MangaReaderView.xaml.cs:56-66`), `Features/Catalog/CatalogSourceDirectory.cs:3`, seluruh `Features/CatalogMirror/*`. Alasannya tertulis di header (`:3-11`) |
| `Sources/ComixContracts.cs` | `Features/Downloader/Sources/Comix/ComixSource.cs`, `Features/Catalog/Sources/Comix/CatalogComixSource.cs`, CatalogMirror |
| `Sources/CatalogSnapshotContracts.cs` | kedua provider Comix, `Features/CatalogMirror/*` (`CatalogSnapshotStore.cs`, `CatalogMirrorSyncFeature.cs`) |
| `Archive/` (10 file) | terbit Downloader (`CbzChapterPublisher.cs`), CoverBuilder (`CoverBuilderService.cs` → `ArchiveReplacementTransaction.cs:49`), muat reader/library (`CbzChapterLoader.cs`, `MangaCoverLoader.cs` → `ArchivePageReader`), probe (`LocalTitleProbe.cs:102`) |

### Ekstraksi arsip: satu pemilik, dan dijaga mesin

| Bagian | Pemilik |
| --- | --- |
| Pemanggilan proses RAR (`Rar.exe` arg `x/a/t`) | `shareLogic/Archive/RarArchiveEngine.cs` |
| Ekstraksi ZIP + dispatch format | `shareLogic/Archive/ArchivePageReader.cs` |
| Bake sampul | `shareLogic/Archive/CoverArchiveWriter.cs:26` |
| Penggantian aman | `shareLogic/Archive/ArchiveReplacementTransaction.cs:49` |
| Pelepasan lock (Windows Restart Manager) | `shareLogic/Archive/ArchiveLockCoordinator.cs` |

`module/mangareader/Features/Rar/` tinggal berisi `Rar.exe` dan `rarreg.key` — tidak
ada satu pun `.cs` di sana. Seluruh logika arsip ada di satu folder, jadi siklus
`shareLogic ↔ Features/Rar` tidak bisa terbentuk lagi. Yang menahannya sekarang
bukan disiplin, tapi build:
`LevelBGuardTests.NoCrossFeatureImportBeyondAcceptedDebtAndNoSharedToFeatureImport`
(`tests/Module.Mangareader.Downloader.Tests/Architecture/LevelBGuardTests.cs:65-108`)
gagal bila file mana pun di bawah `shareLogic/` meng-import folder feature.
→ register C13 (selesai).

---

## 8. Features/Catalog vs Features/CatalogMirror

`Features/Catalog` (4 file root + `Runtime/`, `Sources/Comix/`, paket python)
adalah **pemilik runtime tab Catalog**: klien browser sendiri
(`CatalogBrowserClient.cs:27`, plugin pyhost `mangareader_catalog` `:38,498`,
perintah `catalog.*` `plugin.py:17-22`), direktori provider tertutup sendiri
(`CatalogSourceDirectory.cs:11-19`, satu `CatalogComixSource`), context/lifetime
sendiri (`CatalogContext.cs:13`).

**Tepinya dua arah:** Catalog → CatalogMirror (`CatalogContext.cs:2,13,18`
membungkus `CatalogMirrorFeature`; `CatalogView.xaml.cs:2`), dan CatalogMirror →
Catalog (`CatalogMirrorView.xaml.cs:51,88,94` menerima `CatalogContext`). Kedua
ujungnya ada sebagai tipe, tapi belum dipasang di aplikasi - lihat §13.

**Pemisahan dari Downloader terverifikasi di sisi C#:** kelas klien terpisah
(`DownloaderPyHostClient.cs:46` vs `CatalogBrowserClient.cs:27`), nama plugin
terpisah (`"mangareader_downloader"` `:69` vs `"mangareader_catalog"` `:38`),
proses `PyHost.Start` terpisah (`:944` vs `:498`). `CatalogSourceDirectory.cs:8-9`
mendokumentasikan tidak meng-impor registri Downloader, dan tidak ada `using`
Downloader di `CatalogComixSource.cs:1-8`. Klaim csproj bahwa kedua paket "never
share a browser/session host" (`Module.Mangareader.csproj:45-47`) **terbukti**.

---

## 9. Rujukan register dari slice ini

A10 (klaim "satu-satunya titik registrasi" vs `ManualUrlProbeRegistry`) ·
C11 (`MangaMultiSelectFilter` abu-abu terhadap SU-4) ·
C12 (dua provider Comix, 3.124 baris; tiga nama tipe masih duplikat) ·
C13 (siklus import `shareLogic/Archive` ↔ `Features/Rar`) — **selesai**, dijaga
`LevelBGuardTests` ·
B3 (registri sumber = daftar `new` eksplisit, sudah dicatat slice 2) ·
B6 (biaya menambah feature Downloader vs Reader berbeda jauh) ·
D17 (`ReaderFeatureContract.cs` ±15 tipe; hub tinggal di luar `ReaderCore/`) ·
D18 (logika feature Overlay tinggal di root Reader) ·
D19 (dua sanitizer nama file dengan aturan berbeda; cek magic-byte PNG ganda di
delapan tempat) ·
D20 (`CoverBuilderView.xaml.cs` 440 baris; `DownloadQueueStore.cs` menjadi rumah
helper JSON bersama).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.

---

## 10. Parent module - `MangaReaderView` dan `MangaReaderModule`

File root: `MangaReaderModule.cs` (82 baris), `MangaReaderHandoffs.cs` (194),
`MangaReaderView.xaml` (45), `MangaReaderView.xaml.cs` (171),
`MangaTitleCard.xaml` (108)/`.cs` (8).

**Tidak ada file katalog/registry feature di tingkat module.**
`MangaSourceRegistry` dan `CatalogSourceDirectory` mendaftarkan **sumber manga**,
bukan feature. Jadi di tingkat module, registrasi feature masih manual.

### 10.1 Pembagian pekerjaan: module, view, handoffs

| Berkas | Pekerjaan |
| --- | --- |
| `MangaReaderModule.cs` | `IModule` + `IRuntimeModuleLifecycle`. Memegang pemilik berumur-panjang (`ReadingHistory`, `LibraryRootContext`, `ProxyLeaseRegistry`), mengonstruksi `DownloaderBackgroundService` di `StartRuntimeAsync` (`:42`), mendaftarkan dispose ke runtime lifetime (`:44-48`), menjalankan pause-and-drain di `StopRuntimeAsync` (`:59`), abort di `ForceStopRuntime` (`:66`) |
| `MangaReaderView.xaml.cs` | komposisi view + navigasi tab. Menerima seluruh konteks lewat konstruktor (`:26-31`): `Lifetime`, `ReadingHistory`, `LibraryRootContext`, `DownloaderContext`, `ProxyLeaseRegistry`. Tidak mengonstruksi Downloader maupun CatalogMirror |
| `MangaReaderHandoffs.cs` | lima method statis untuk kebijakan lintas-feature: `ReadConfirmedMapping` (`:34`), `EnqueueUpdate` (`:54`), `CheckCatalogAvailability` (`:93`), `ConfirmQueueTarget` (`:127`), `QueueHandoff` (`:160`) |
| `DownloaderBackgroundService.cs` (144 baris, di dalam `Features/Downloader/`) | pemilik runtime Downloader: `ProxyPoolAdapter`, `ProxyHttpTransport`, `DownloaderPyHostClient`, `MangaSourceRegistry.CreateDefault`, `DownloadSourceIndex`, queue, cover client |

Komposisi Downloader dan CatalogMirror karena itu **tinggal di dalam folder
feature-nya sendiri**, bukan di parent. Yang masih dikonstruksi view adalah internal
UpdateChecker: `LocalTitleProbe`, `SourceBindingStore`, `UpdateMatcher`,
`UpdateCheckerFeature` (`MangaReaderView.xaml.cs:56-66`).

### 10.2 Yang masih terbuka di parent

| Temuan | Bukti |
| --- | --- |
| Internal UpdateChecker dikonstruksi view | `MangaReaderView.xaml.cs:56-66` - empat tipe konkret milik `Library/UpdateChecker/` |
| Kebijakan lintas-feature tinggal di file module, bukan di feature | `MangaReaderHandoffs.cs` - lima method. FM-5 menempatkan kalkulasi satu-feature di feature dan kebijakan lintas-feature di composition root; bentuk ini adalah composition root, bukan pelanggaran FM-5 |
| Tidak ada katalog feature | menambah tab tetap berarti menyunting dua berkas parent (lihat §10.3) |

### 10.3 Biaya menambah satu tab tingkat atas

**Dua file parent harus diedit:**

1. `MangaReaderView.xaml` - tambah xmlns, `TabItem`, dan view;
2. `MangaReaderView.xaml.cs` - wiring di konstruktor plus teardown di `DisposeView`
   (`:146-170`).

Ini masih melanggar FM-3 ("adding/removing a feature touches only that feature's
folder", `SKILL.md:299-300`). **Bandingkan dengan Reader:** menambah feature
drawer-card/visual di Reader = **nol** edit host (§1.4). Dua pola, dua biaya, satu
citizen.

### 10.4 Navigasi tab dimiliki view, tanpa router

`setting:SettingTabs` (`MangaReaderView.xaml:14`) dengan **lima tab** (Library,
History, CoverBuilder, Downloader, Queue), dan perpindahan dilakukan dengan menyetel
`TabItem.IsSelected` secara programatik - bukan router atau switch:
`QueueTabItem.IsSelected = true` (`:107`), `DownloaderTabItem.IsSelected = true`
(`:112`).

---

## 11. Library, Grouping, UpdateChecker

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `LibraryView.xaml.cs` | 894 | layar Library; grid/list, sort, filter grup, kartu |
| `LibraryIndex.cs` | 42 | `LibraryIndexEntry`/`LoadResult`/`SaveResult` murni data + seam `ILibraryCoverThumbnails` |
| `LibraryIndexCoordinator.cs` | 309 | reconcile root → entri; satu gerbang serialisasi |
| `LibraryIndexStore.cs` | 296 | persistensi indeks, atomik |
| `LibraryIndexWatcher.cs` | 213 | `FileSystemWatcher` → `TitleChanged` / `ResyncRequested` |
| `LibraryTitleLoader.cs` | 145 | entri indeks → `MangaTitle` lengkap saat dibutuhkan |
| `LibraryCoverCache.cs` | 180 | thumbnail sampul (implementasi `ILibraryCoverThumbnails`) |
| `LibraryRootContext.cs` | 98 | satu pemilik root seumur module + `RootChanged` |
| `LibraryPathStore.cs` | 213 | persistensi atomik `library-path.txt` + seam `ILibraryPathFileIO` |
| `LibraryScanPersistence.cs` | 48 | aturan capture-awal / persist-bila-sukses |
| `LibraryViewModeFeature.cs` | 84 | preferensi Grid/List |
| `LocalTitleDetailAdapter.cs` | 51 | title → `MangaDetailPresentation` |
| `ChapterSelectorView.xaml.cs` | 186 | overlay pemilih chapter |

### Flow scan

```text
[1] LibraryView_Loaded                  LibraryView.xaml.cs:120 → _root.Restore() :130
[2] RefreshLibraryAsync(path, manual)   :176
[3] _coordinator.Reconcile(path)        :248-249 → LibraryIndexCoordinator.Reconcile :109
                                           ReconcileLocked :117
[4] entri → kartu                       FillCards :526 / FillCardsAsync :540
[5] NotifyTitlesChanged → TitlesChanged :735-736 (event :114)
[6] CompleteSuccessfulScan :636 → _root.CompleteScan :643
       → LibraryScanPersistence.CompleteScan :35 → LibraryPathStore.Save :143
[7] LoadCachedCoversAsync :565 → LibraryCoverCache
[8] UpdateGroupFilter :756 → _titlesView.Refresh
```

### Watcher: jalur kesegaran kedua

```text
Watcher_TitleChanged      :367 → SyncCardsFromWatcher :488
Watcher_ResyncRequested   :404 → Reconcile :414 → SyncCardsFromResync :464
RestartWatcher            :338
```

### Cara keduanya menempel — dan statusnya sebagai feature

**Grouping dan UpdateChecker adalah INTERNAL Library** (sub-folder `Library/`), bukan
feature tingkat atas. Cara menempelnya berbeda:

- **Grouping: konstruksi langsung + event.** `new GroupingFeature(_groupingStore)`
  (`LibraryView.xaml.cs:47`), `Grouping.UseGrouping` (`:63`),
  `_grouping.Changed += Grouping_Changed` (`:69`), filter `_grouping.IsVisible` (`:56`).
  Ia memasang dirinya sendiri.
- **UpdateChecker: disuntik parent.** `UseUpdateChecker(feature)` (`:97`) →
  `new UpdateCheckerEntry(feature).Install(...)` (`:102-103`); feature menaikkan
  `Changed`, dan enqueue dirutekan balik lewat delegate parent
  (`MangaReaderHandoffs.cs:54`, `EnqueueUpdate`). Ia di-wire parent karena butuh
  rute lintas feature: queue, `IMangaSourceDirectory`, dan library root.

---

## 12. History

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `ReadingHistory.cs` | 68 | pemilik pencatatan, satu jalur mutasi, menaikkan `Changed` |
| `ReadingHistoryStore.cs` | 185 | `history.json` durable, tulis atomik (`:173`), retensi 15 non-pin (`:26`, `:127`) |
| `HistoryView.xaml.cs` | 410 | presentasi |
| `HistoryCardModel.cs` | 85 | VM kartu |
| `ClearHistoryFeature.cs` | 72 | perintah clear lewat satu pemilik |
| `PinnedHistoryFeature.cs` | 63 | perintah pin lewat satu pemilik |
| `HistoryViewModeFeature.cs` | 83 | preferensi Grid/List |

**Siapa menulis history: READER, dirutekan parent.** Parent memiliki `_history`
(`MangaReaderView.xaml.cs:20`), menyuntik `HistoryTab.UseHistory(_history)`
(`:42`); `OpenReader` memanggil `_history.Record` (`:127`) dan
`reader.ActiveChapterChanged → _history.Record` (`:133-136`). Bukan Library, bukan
Downloader.

Persistensi: `%LocalAppData%\Citadel\MangaReader\history.json`
(`ReadingHistoryStore.cs:33-37`).

Tepi: parent→History `UseHistory` (`:42`), `SetLibrary` (`:80`;
`HistoryView.xaml.cs:86`), `Refresh` (`:119`, `:93`); History→parent
`OpenChapterRequested` (`HistoryView.xaml.cs:56,212-214`, disambung
`MangaReaderView.xaml:23-24`); Library→History lewat parent
(`LibraryTab_TitlesChanged → HistoryTab.SetLibrary`, parent `:78-80`).

---

## 13. Features/CatalogMirror — feature terbesar (6.197 baris, 16 file)

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `CatalogMirrorContracts.cs` | 323 | record/enum immutable saja, tanpa I/O |
| `CatalogMirrorPaths.cs` | 108 | kosakata path, direktori identitas SHA-256 (`:95`), penjaga keluar-root (`:62`) |
| `CatalogMirrorDatabase.cs` | 240 | skema/PRAGMA/migrasi SQLite (v3, `:13`) |
| `CatalogSnapshotStore.cs` | 1.273 | lifecycle staging → checkpoint → dedupe → aktivasi → retensi |
| `CatalogMirrorQuery.cs` | 319 | search/filter/sort/page lokal, terparameterisasi |
| `CatalogEnrichmentStore.cs` | 121 | `metadata.json` per judul, atomik |
| `CatalogGenreStore.cs` | 255 | taksonomi genre + keanggotaan (SQLite) |
| `CatalogMirrorCoverCache.cs` | 224 | fetch/decode/cache sampul, terbatas |
| `CatalogMirrorSyncFeature.cs` | 487 | mesin keadaan traversi 4-partisi |
| `CatalogGenreSyncFeature.cs` | 279 | loop pengayaan genre per judul |
| `CatalogMirrorDetailFeature.cs` | 419 | muat title/group/chapter + bangun handoff |
| `CatalogMirrorLoadFeature.cs` | 139 | pilih generasi DB→display |
| `CatalogMirrorFeature.cs` | 461 | koordinator tipis: gabung state, teruskan intent |
| `CatalogMirrorCardModel.cs` | 75 | VM kartu |
| `CatalogMirrorView.xaml(.cs)` | 468 / 1.006 | wiring UI, Dispatcher, render |

### Flow sync

```text
[1] trigger   CatalogMirrorFeature.StartSyncAsync  CatalogMirrorFeature.cs:262 → _sync.StartAsync :270
[2]           CatalogMirrorSyncFeature.RunAsync    SyncFeature.cs:148 (clear staging :174, refresh :178-183)
[3] paging    RefreshPartitionsAsync :431 · FetchPageAsync :385 → _source.GetSnapshotPageAsync :398
[4] dedupe    _store.AppendPageAsync :285 → CatalogSnapshotStore.AppendPageAsync :45
                 upsert :150 · loop-guard sidik jari :136-142 · checkpoint :156
[5] partisi   CompletePartitionAsync :335 / CatalogSnapshotStore.cs:407
[6] aktivasi  _store.ActivateAsync :362 / CatalogSnapshotStore.cs:459
                 balik active_generation :498-501 · retensi active+previous :526-535
[7] query     CatalogMirrorFeature.QueryAsync :202 → LoadFeature.QueryAsync :96
                 → CatalogMirrorQuery.Execute :55
```

### Apakah self-contained? **TIDAK**

Referensi keluar folder:

| Ke | Bukti |
| --- | --- |
| `Module.Mangareader.Sources` (kernel kontrak) | Contracts `:4`, Feature `:4`, Sync `:4`, Query `:6`, SnapshotStore `:9`, Detail `:4`, GenreStore `:2`, GenreSync `:1`, CardModel `:5`, View.cs `:13` |
| `Citadel.Setting.Components` | View.cs `:9` |
| `Module.Mangareader.Components` | View.cs `:10` |
| `Module.Mangareader.ShareLogic` | CardModel `:4`, View.cs `:12` |
| `Module.Mangareader.Features.Catalog` | View.cs `:11` (memakai `CatalogContext`) |

**Referensi dari LUAR ke internalnya hampir nol — karena CatalogMirror tidak
dipasang di aplikasi.** `MangaReaderView.xaml` hanya punya lima tab (Library,
History, CoverBuilder tersembunyi, Downloader, Queue); tidak ada tab Catalog.
`CatalogView` tidak pernah di-instantiate di kode produksi mana pun, dan
`CatalogContext` tidak pernah dikonstruksi di luar test. Yang tersisa hanya dua
seam tanpa pemanggil: `CatalogView.UseContext` (`CatalogView.xaml.cs:23`) dan
`CatalogMirrorView.UseFeature` (`CatalogMirrorView.xaml.cs:103`). Di dalam
foldernya sendiri `CatalogContext.cs:2,13` masih mengonsumsi
`CatalogMirrorFeature`, dan `CatalogView.xaml:12` masih meng-host
`<mirror:CatalogMirrorView>`.

### Komposisi view: PATUH shared-UI

`CatalogMirrorView.xaml` memakai `setting:SettingViewport / ActionCard / Field /
Button / Toggle / Table` (`:16,26,35,42,71,269`) dan style bersama lewat
`DynamicResource SettingComboBoxStyle / SettingCardStyle / SettingScrollViewerStyle`
(`:61,173,252`), plus komponen module `mangacomponents:MangaMultiSelectFilter`
(`:177-197`), `mangacomponents:MangaDetailView` (`:370`) dan
`manga:MangaTitleCard` (`:261`). **Tidak ada template/primitif yang disalin lokal**
— komentar `:11-15` cocok dengan kode.

---

## 14. Batas CatalogMirror dan provider: satu kontrak bersama

`CatalogMirrorSyncFeature.cs` hanya mengimpor `Module.Mangareader.Sources` (`:4`),
bukan namespace Downloader. `ComixContractException` dideklarasikan **sekali** di
`shareLogic/Sources/MangaSourceContracts.cs:26` dan dipakai kedua provider
(`Features/Downloader/Sources/Comix/ComixSource.cs`,
`Features/Catalog/Sources/Comix/CatalogComixSource.cs`); tidak ada definisi lokal di
masing-masing provider.

Exception itu membawa `HttpStatus` (`MangaSourceContracts.cs:29`) supaya UI bisa
membedakan "bentuk provider berubah" dari "jaringan mati". Sync sendiri **tidak**
bercabang pada status itu: satu percobaan, lalu kegagalan terpropagasi dan traversal
berhenti.

**Kenapa tidak ada retry otomatis - ini aturan, bukan kelalaian.** Provider Comix
punya toggle sumber dan challenge yang diselesaikan manual lewat headed browser.
Otomatisasi yang berhenti saat di-throttle justru yang menyerahkan kendali ke
manusia. Retry otomatis terhadap 429/502/503 sudah pernah dicoba dan menghasilkan
hammering berkelanjutan; test kini menegaskan satu percobaan lalu berhenti.

### 14.1 Duplikasi yang masih nyata

Dua provider Comix tetap ada, dan keduanya masih besar:

| Provider | File | Baris |
| --- | --- | --- |
| Downloader | `Features/Downloader/Sources/Comix/ComixSource.cs` | 1.621 |
| Catalog | `Features/Catalog/Sources/Comix/CatalogComixSource.cs` | 1.503 |

Tiga nama tipe masih duplikat dan ter-enumerasi sebagai debt di
`tests/Module.Mangareader.Downloader.Tests/Architecture/LevelBGuardTests.cs`
(`AcceptedDuplicateTypeNames`): `ComixContract`, `ComixOptions`, `ComixBrowseQuery`.
Guard gagal bila muncul nama duplikat baru di luar daftar itu. → register C12
(sebagian), C3.

---

## 15. shareLogic (root module) — kernel bersama, dengan tiga file nyasar

34 file, 4.811 baris (21 di antaranya di akar folder, sisanya di `Archive/` dan
`Sources/`). Sebagian besar memang kernel bersama (jumlah folder konsumen):

| File | Konsumen |
| --- | --- |
| `MangaLibrary.cs` (`MangaTitle`/`ChapterInfo`) | hampir semua folder |
| `NaturalStringComparer.cs` | Library, Downloader/Queue, shareLogic/Archive |
| `ChapterNumberText.cs` | Library/UpdateChecker, Downloader/Catalog, Downloader/Lister |
| `LocalTitleProbe.cs` | Library/UpdateChecker, Downloader/Lister |
| `MangaCardPresentation.cs` | History, CatalogMirror, Downloader/Catalog, shareLogic |
| `MangaDetailPresentation.cs` | Components, Library, Downloader/Catalog |
| `MangaViewMode.cs` | Library, History, Components |
| `MangaCoverLoader.cs` | Library, History, CoverBuilder |
| `MangaReaderEvents.cs` | parent, Library, CoverBuilder |
| `OpenChapterRequestedEventArgs.cs` | Library, History, parent, Reader |
| `ViewModePreferenceStore.cs` | Library, History |
| `MangaTitleCardModel.cs` | Library, CoverBuilder |
| `ProxyPoolAdapter.cs` / `ProxyHttpTransport.cs` / `ProxyLeaseRegistry.cs` | adapter mangareader ke pool proxy: Downloader (Queue, ManualUrl), Catalog/CatalogMirror, parent (`MangaReaderModule.cs:23`) |
| `ReadingPositionStore.cs` | Reader (`ResumePositionFeature`), Library (`ChapterSelectorView`), parent (`MangaReaderView.xaml.cs:98`) |
| `TitleCoverFiles.cs` | Library (`LibraryCoverCache`, `LibraryTitleLoader`), shareLogic (`MangaCoverLoader`) |

**Tiga file dengan SATU konsumen — kandidat pindah ke dalam konsumennya:**

| File | Baris | Satu-satunya konsumen |
| --- | --- | --- |
| `CbzChapterLoader.cs` | 293 | `Reader/ReaderCore/CbzReaderChapterLoader.cs:13` |
| `ChapterSurfaceModel.cs` | 97 | Reader (6 file, semuanya di `Reader/`) |
| `ChapterLoadingContracts.cs` | 84 | Reader (`PageRenderQuality`, `ChapterRenderRequest`, `LoadedPage`, `LoadedChapter`, `ChapterLoadProgress`) |

Ketiganya 474 baris kontrak chapter-loading milik Reader yang tinggal di folder
"bersama". Jadi `shareLogic/` bukan dumping ground, tapi **sebagian** isinya adalah
internal Reader yang salah alamat. → register D21.

(`ChapterRenderCache.cs` 195 baris **bukan** single-consumer: konsumen langsungnya
`History/HistoryView.xaml.cs:21`, tapi ia menjangkau Reader/Library/CoverBuilder
lewat `CbzChapterLoader`/`MangaCoverLoader`.)

---

## 16. Daftar tepi lintas subsistem

| Dari → Ke | Jenis | Bukti |
| --- | --- | --- |
| `MangaReaderModule` → Downloader | konkret (pemilik runtime) | `MangaReaderModule.cs:4,24,42` |
| `MangaReaderModule` → Library | konkret | `MangaReaderModule.cs:6,20` |
| `MangaReaderModule` → History | konkret | `MangaReaderModule.cs:5,19` |
| `MangaReaderModule` → shareLogic | konkret | `MangaReaderModule.cs:7,23` |
| `MangaReaderView` → Library | konkret | `MangaReaderView.xaml.cs:12,47` |
| `MangaReaderView` → History | konkret | `MangaReaderView.xaml.cs:11,42` |
| `MangaReaderView` → Downloader | konkret | `MangaReaderView.xaml.cs:5-10,68,72` |
| `MangaReaderView` → UpdateChecker | konkret | `MangaReaderView.xaml.cs:13,56-66` |
| `MangaReaderView` → shareLogic | konkret | `MangaReaderView.xaml.cs:14,98` |
| `MangaReaderView` → Components/Setting | xaml | `MangaReaderView.xaml:7,13` |
| `MangaReaderHandoffs` → CatalogMirror | **kontrak-saja** (record murni) | `MangaReaderHandoffs.cs:6,93,127` |
| `MangaReaderHandoffs` → Downloader | konkret | `MangaReaderHandoffs.cs:7-9,34,54,127` |
| `MangaReaderHandoffs` → Library + UpdateChecker | konkret | `MangaReaderHandoffs.cs:10-11` |
| `MangaReaderHandoffs` → Components/Setting | konkret | `MangaReaderHandoffs.cs:5` |
| Library → Grouping | konkret | `LibraryView.xaml.cs:12,33-34,47,63,69` |
| Library → UpdateChecker | konkret, disuntik | `LibraryView.xaml.cs:13,97-103` |
| Library → shareLogic | kontrak | `LibraryTitleLoader.cs:117`, `LibraryCoverCache.cs:159`, `LibraryView.xaml.cs:12` |
| UpdateChecker → shareLogic | konkret | `UpdateMatcher.cs:3,368,437` |
| UpdateChecker → Sources | kontrak | `UpdateCheckerFeature.cs:2,67,87` |
| UpdateChecker → Downloader | **tidak ada** — lewat delegate `EnqueueUpdateChapters` | `UpdateCheckerFeature.cs:52,69,89` |
| History → shareLogic | kontrak | `ReadingHistory.cs:2`, `ReadingHistoryStore.cs:3`, `HistoryCardModel.cs:4`, `HistoryView.xaml.cs:8` |
| History → parent | event | `HistoryView.xaml.cs:56` (`OpenChapterRequested`) |
| CatalogMirror → Sources | kontrak | `CatalogMirrorContracts.cs:4` dll. |
| CatalogMirror → Catalog | konkret (host) | `CatalogMirrorView.xaml.cs:11,51,88` |
| CatalogMirror → Components/Setting | konkret | `CatalogMirrorView.xaml.cs:9-10` |
| Catalog → CatalogMirror | xaml + konkret (host) | `CatalogView.xaml:12`, `CatalogContext.cs:2` |
| Components → shareLogic | kontrak | `MangaDetailView.xaml.cs`, `MangaViewModeSelector.xaml.cs` |
| Reader → shareLogic | konkret | `CbzReaderChapterLoader.cs:13`, `Reader/Features/ChapterLoading/*` |

**Dua tepi yang dulu ada dan sekarang hilang, dijaga test:**
`CatalogMirror → Downloader` (BUG-1) dan `shareLogic/Archive → Features/Rar` (C13)
di-assert tidak ada oleh
`LevelBGuardTests.FixedCouplingsStayRemoved`
(`tests/Module.Mangareader.Downloader.Tests/Architecture/LevelBGuardTests.cs:153-172`).

---

## 17. Duplikasi dan komentar yang dibantah kode

| Temuan | Bukti |
| --- | --- |
| **Dua provider Comix** 3.124 baris; tiga nama tipe masih duplikat | §6.5, §14 |
| Cek magic-byte PNG ditulis delapan kali | §6.6 |
| `LibraryViewModeFeature.cs` dan `HistoryViewModeFeature.cs` hampir identik (pemilik pref Grid/List di atas `ViewModePreferenceStore`) — sengaja, kunci berbeda `Library.ViewMode` / `History.ViewMode` | duplikasi yang dibenarkan |
| Pola tulis-JSON-atomik (tmp→move) diulang di 20+ tempat, termasuk lima store preferensi/path | `LibraryPathStore.cs:160`, `LibraryIndexStore.cs:183`, `GroupingStore.cs:140`, `SourceBindingStore.cs:169`, `ReadingHistoryStore.cs:173`, `ReaderPreferencesStore.cs:219`, `ReadingPositionStore.cs:118`, `ViewModePreferenceStore.cs:117` |
| `MangaReaderHandoffs.cs:22-25` "one file holds composition and one file holds coordination" — **tidak akurat**: komposisi tersebar di dua file (`MangaReaderModule.cs` untuk runtime Downloader, `MangaReaderView.xaml.cs` untuk view), dan koordinasi di file ketiga | A-kategori (ringan) |
| `MangaReaderHandoffs.cs:69-71` — penjumlahan dua pencacah skip queue **dijelaskan inline**, jadi bukan lagi penafsiran ulang tanpa dokumentasi | A13 turun jadi catatan, bukan pelanggaran |
| `CatalogMirrorView.xaml.cs` 1.006 baris: render + navigasi + bangun opsi filter + paginasi + host detail + batching sampul | besar, tapi komposisi/wiring |
| `CatalogSnapshotStore.cs` 1.273 baris: staging + checkpoint + dedupe + aktivasi + retensi dalam satu tipe | kohesif tapi sangat besar |

---

## 18. Rujukan register tambahan dari bagian ini

A13 (klaim "queue's answer returned as-is" vs penjumlahan ulang pencacah) —
**turun jadi catatan**: penjumlahan itu sekarang dijelaskan inline
(`MangaReaderHandoffs.cs:69-71`) ·
B7 (biaya tambah tab module = 2 file parent, vs Reader = nol) ·
C12 (dua provider Comix, 1.621 + 1.503 baris; tiga nama tipe masih duplikat) ·
C14 (parent masih mengonstruksi internal UpdateChecker —
`MangaReaderView.xaml.cs:56-66`; jangkauan ke internal CatalogMirror sudah hilang,
sisa kebijakan koordinasi tinggal di `MangaReaderHandoffs.cs`) ·
D21 (tiga file Reader-only tinggal di `shareLogic/`) ·
D22 (`MangaReaderView.xaml.cs` 168 baris; komposisi Downloader sudah pindah ke
`MangaReaderModule.cs` + `DownloaderBackgroundService`).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.
