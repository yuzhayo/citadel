# CITADEL-FLOWS-MANGAREADER — citizen terbesar

183 file, 36.703 baris = 63% seluruh kode produksi citadel. Ditulis 2026-09-11 dari
kode. Klaim bertanda `file:baris`. Kontrak yang mengadili:
`.agents/skills/citadel-feature-modularity/SKILL.md` (FM-1..FM-5) dan
`.agents/skills/citadel-shared-ui/SKILL.md` (SU-1..SU-5).

**Temuan utama, dan ini inti seluruh tugas:** mangareader memuat **dua pola yang
bertolak belakang di dalam satu citizen**.

- `Reader/` adalah **implementasi referensi yang klaimnya terbukti**: composition
  root 165 baris yang tidak menyebut satu pun tipe feature konkret, nol tepi
  feature→feature, semua lalu lintas lewat empat hub. Menambah feature drawer-card
  atau visual = **nol edit di host**.
- `Features/Downloader/` + `Features/Catalog/` memakai pola lain: registrasi `new`
  eksplisit, panggilan konkret antar sibling, dan **dua provider Comix yang
  diduplikasi hampir utuh** (1.606 + 1.537 baris).

Jadi keluhan owner bukan seragam: ia benar untuk sisi Downloader/Catalog, dan
**tidak** benar untuk sisi Reader.

Bagian yang sudah terdokumentasi: Reader (§1–§4), Downloader (§5–§8), Components
dan CoverBuilder (§9). Library, History, CatalogMirror, dan parent module
(`MangaReaderView`) menyusul.

---

## 1. Reader — klaim "reference implementation" diuji

Klaimnya ada di `SKILL.md:262-297`, dengan bentuk yang dijanjikan:
`ReaderWindow.cs` (composition root tipis) · `ReaderCore/` (infrastruktur) ·
`Features/<nama>/` (feature pluggable).

**Bentuk nyata cocok, dengan satu drift nama:** yang ada adalah pasangan WPF
`Reader/ReaderWindow.xaml` (178 baris) + `ReaderWindow.xaml.cs` (165 baris), bukan
`ReaderWindow.cs` tunggal seperti ditulis `SKILL.md:269`.

### 1.1 Composition root: 165 baris, nol tipe feature

`ReaderWindow.xaml.cs` hanya menyebut **infrastruktur**:

| Baris | Infrastruktur |
| --- | --- |
| `:31` | `CbzReaderChapterLoader` |
| `:48` | `ReaderSessionState` |
| `:53-55` | `ReaderCommandHub`, `ReaderNotificationHub`, `ReaderActivityHub` |
| `:56-57` | `FrameContentHost`, `ReaderStatusHost` |
| `:65` | `ReaderChapterNavigationHub` |
| `:68-69` | `ReaderInputRouter`, `ReaderFeatureContext` |
| `:89` | `ReaderFeatureHost` |

Keduabelas tipe feature konkret muncul **hanya** di
`Reader/ReaderDefaultFeatureCatalog.cs:18-29`. Window tidak pernah menyentuhnya.

### 1.2 Vonis per aturan FM

| Aturan | Vonis | Bukti |
| --- | --- | --- |
| **FM-1** parent tak tahu internal feature | **PATUH** | grep repo-wide: tidak ada tipe internal feature (`ChapterCoordinator`, `ChapterPreloader`, `ChapterNavigator`, `IChapterLoadingRuntime`, `ReaderDrawerPolicy`, `ReaderAutoScrollPolicy`, `ReaderFullscreenGeometry`) yang muncul di luar foldernya pada kode produksi. Kecocokan hanya di folder sendiri, katalog, test, dan dokumen |
| **FM-2** komunikasi lewat event/hub | **PATUH** | Reset menjangkau Zoom/Dim/AutoScroll/Pin **hanya** lewat perintah hub (`ReaderResetController.cs:46-53`); tidak ditemukan satu pun panggilan langsung feature→feature |
| **FM-3** parent tak cek "feature X ada?" | **PATUH** | host hanya menegakkan invarian bentuk: tepat satu `IReaderDrawerContributionHost` (`ReaderFeatureHost.cs:42-47`), key/name cocok (`:31-35`). Registrasi lewat katalog terpusat, sesuai `SKILL.md:277` ("ideally via a catalog") — bukan self-registration |
| **FM-4** state bersama lewat context | **CAMPURAN** | **Untuk:** `ReaderFeatureContext` adalah service container (`ReaderFeatureContract.cs:241-280`), feature menarik saat Attach (`ChapterLoadingFeature.cs:93-98`, `ReaderOverlay.xaml.cs:19-24`). **Melawan:** 8 dari 12 feature menerima `ReaderSessionState` + `ReaderCommandHub` konkret yang **diantar tangan** lewat closure konstruktor di katalog (`ReaderDefaultFeatureCatalog.cs:20,24-29`); `Attach(context)` milik Drawer mengabaikan parameter context-nya sama sekali (`ReaderDrawer.xaml.cs:53-59`) |
| **FM-5** parent hanya koordinasi | **SEBAGIAN BESAR PATUH** | window = bangun infrastruktur (`:47-96`), route (`:102-107`), lifecycle (`:122-164`). **Melawan (ringan):** window menduplikasi pekerjaan judul milik feature Chrome (`ReaderWindow.xaml.cs:138-139` vs `ReaderChromeController.cs:29-33`), dan XAML parent memiliki template render halaman termasuk transform ZoomScale (`ReaderWindow.xaml:48-97`, `:55-58`) |

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
| `Pin` / `Zoom` / `Dim` / `Reset` | pin toggle / pemilik zoom / overlay+slider dim / reset global | provider (keempatnya) | tidak ada |

### 1.4 Mekanisme registrasi dan biaya menambah feature

Katalog statis eksplisit, **tanpa refleksi dan tanpa self-registration**:

```text
ReaderDefaultFeatureCatalog.cs:12-29    12 panggilan .Add(nama, factory)
        │  menyebut dirinya "single explicit registration point" (:6-9)
        ▼
ReaderWindow.xaml.cs:89-96              dikonsumsi di sini
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
| satu baris di `ReaderDefaultFeatureCatalog.cs:17-29` | ya |
| entri `Compile`/`Page` di `tests/Module.Mangareader.Reader.Tests.csproj:33-104` | hanya bila ingin ter-cover test |
| edit `IReaderCommands` + `ReaderCommandHub` (`ReaderFeatureContract.cs:51-105`) | **hanya bila** butuh perintah baru |
| edit `ReaderLayer` (`ReaderFeatureCatalog.cs:3-11`), XAML ContentControl (`ReaderWindow.xaml:101-134`), peta host (`ReaderWindow.xaml.cs:80-88`) | **hanya bila** butuh lapisan baru |
| edit host untuk feature drawer-card / visual | **NOL** |

Ini kontras tajam dengan Downloader (§6) dan camoprof. **Reader membuktikan bentuk
yang owner inginkan sudah mungkin di repo ini.**

### 1.5 Empat hub (pemilik lalu lintas antar feature)

| Hub | Pemilik | Contoh lalu lintas |
| --- | --- | --- |
| `ReaderCommandHub` | `ReaderFeatureContract.cs:70-105` | Overlay→Drawer (`ReaderOverlay.xaml.cs:40`); Reset→AutoScroll/Zoom/Dim/Pin (`ReaderResetController.cs:46-53`); InputRouter→Fullscreen/Dim/Zoom (`ReaderInputRouter.cs:93-138`) |
| `ReaderNotificationHub` | `ReaderFeatureContract.cs:113-123` | Fullscreen (`ReaderFullscreenController.cs:126`), peringatan prefs window (`ReaderWindow.xaml.cs:141-142`) → Toast (`ReaderToast.xaml.cs:24`) |
| `ReaderActivityHub` | `ReaderCore/ReaderActivityHub.cs:27-38` | Drawer melapor `DrawerOpened` (`ReaderDrawer.xaml.cs:85`); AutoScroll berhenti saat aktivitas manual (`ReaderAutoScrollController.cs:111,190-193`) |
| `ReaderChapterNavigationHub` | `ReaderCore/ReaderChapterNavigationHub.cs:15-17` | ChapterLoading mendaftar (`ChapterLoadingFeature.cs:132-136`); Chrome (`:23`), ChapterNavigation (`:86,121`), Zoom (`:174`), AutoScroll (`:135,187`) mengonsumsi |

Satu jalur tidak langsung yang tetap patuh FM-2: AutoScroll bereaksi terhadap
`IsDrawerOpen` milik Drawer lewat `PropertyChanged`
(`ReaderAutoScrollController.cs:208-213`) — mediated by state, bukan panggilan.

**Nol tepi tipe-konkret antar folder feature** (grep repo-wide; semua kecocokan ada
di folder sendiri, katalog, test, atau dokumen).

### 1.6 ReaderCore — infrastruktur

| File | Pekerjaan |
| --- | --- |
| `FrameContentHost.cs` | adapter `IReaderViewport` di atas ScrollViewer/ItemsControl (`:15-111`) — **dan** kelas kedua `ReaderStatusHost` (`:114-190`) di file yang sama |
| `ReaderActivityHub.cs` | satu stream activity-origin bertipe (`:27`) |
| `ReaderChapterNavigationHub.cs` | fasad navigasi late-bound + registri (`:15-17`) |
| `ReaderDrawerContributions.cs` | kontrak kartu drawer opaque bersama + factory kartu bergaya Setting (`:15`, `:34-53`) |
| `ReaderInputRouter.cs` | satu-satunya pemilik preview-input tingkat window (`:11`) |
| `ReaderPreferencesStore.cs` | persistensi tervalidasi, atomik, ter-debounce + seam FileIO (`:26`, `:357-382`) |
| `ReaderSessionState.cs` | state sesi observable; baca publik `IReaderStateView` (`:5`), tulis internal (`:57-88`) |

Context bersama (FM-4) adalah `ReaderFeatureContext` di
`ReaderFeatureContract.cs:241-280` — **tinggal di root Reader, bukan di
`ReaderCore/`**. Tempat feature menarik: `ChapterLoadingFeature.cs:96-97`,
`ReaderOverlay.xaml.cs:22-23`, `ReaderZoomController.cs:87-98`,
`ReaderChromeController.cs:21-23`, `ReaderToast.xaml.cs:23-24`,
`ReaderChapterNavigation.cs:31-86`. Catatan: `SessionState` pada context bersifat
`internal` (`:269`), jadi sisi tulis state sampai ke feature lewat injeksi
konstruktor katalog — itulah sisi "melawan" pada FM-4.

---

## 2. Flow Reader (untuk diagram)

```text
ReaderWindow (composition root, 165 baris)
│  bangun: SessionState · CommandHub · NotificationHub · ActivityHub ·
│          FrameContentHost · StatusHost · ChapterNavigationHub · InputRouter ·
│          FeatureContext · CbzReaderChapterLoader
▼
ReaderDefaultFeatureCatalog.cs:12-29   ← 12 feature, SATU-SATUNYA tempat namanya disebut
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
| **Dua kelas dalam satu file.** `FrameContentHost.cs` = adapter viewport (`:15`) + adapter status-host (`:114`) | ST-1 |
| **`ReaderFeatureContract.cs` 280 baris memuat ±15 tipe**, termasuk dua hub konkret (`:70`, `:113`). `SKILL.md:270` menempatkan "hubs" di `ReaderCore/`, tapi 2 dari 4 hub tinggal di root Reader | ST-1 + drift dari bentuk yang dijanjikan skill |
| **Judul window ditulis dua kali.** `ReaderWindow.xaml.cs:138-139` vs `ReaderChromeController.cs:32` | duplikasi |
| **Zoom mengekspor ulang konstanta `ReaderValuePolicy`** (`ReaderZoomController.cs:15-18`) | duplikasi ringan |
| **Klaim komentar tidak umum.** `ChapterLoadingFeature.cs:66-68` "all dependencies arrive through Attach" — benar hanya untuk feature ini; Drawer/Pin/Reset memakai injeksi ctor dan mengabaikan/memakai sebagian context (`ReaderDrawer.xaml.cs:53-59`, `ReaderPinController.cs:55-60`, `ReaderResetController.cs:41-42`) | A-kategori |
| **Klaim katalog TERBUKTI benar.** `ReaderDefaultFeatureCatalog.cs:7-8` dan `ChapterLoadingFeature.cs:33-35` — diverifikasi grep | positif |
| **`CoverBuilderView.xaml.cs` 352 baris** membawa logika alur di luar wiring: orkestrasi batal/sibuk (`:262-281`), pemuatan preview (`:289-340`), penyusunan teks status (`:192-208`, `:230-243`) | ST-1 |
| **`ReaderPreferencesStore.cs`** = store + 3 record + `IReaderPreferencesFileIO` + implementasi (`:9-23`, `:357-382`) — kohesif tapi多 tipe | ST-1 ringan |

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

## 5. CoverBuilder

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `CoverSourceReference.cs` | 21 | input bertipe LocalPath/RemoteUrl (`:12-20`) |
| `CoverSourceLoader.cs` | 217 | muat/fetch/konversi-PNG/cache (`:16`) |
| `CoverBuilderService.cs` | 86 | **satu-satunya pemilik bake**, kebijakan fetch-sebelum-bake (`:10-19`, `:34-51`) |
| `CoverBuilderView.xaml(.cs)` | 122 / 352 | UI: `setting:SettingViewport` (`xaml:7`), `SettingField` (`:67`), `SettingButton` (`:71,79,110`) |

Konsumen: `MangaReaderView.xaml:26` (tab module); test lewat
`tests/Module.Mangareader.Downloader.Tests.csproj:157-162`.

---

## 6. Downloader — pola yang berbeda

### 6.1 Pohon feature

| Folder | file | Pekerjaan | Kontrak |
| --- | --- | --- | --- |
| `Downloader/` (root) | 6 | komposisi + plumbing bersama | `DownloaderContext` = container yang dihadapi child (`DownloaderContract.cs:16`); `DownloaderView.xaml.cs:13` host route (Catalog ↔ ManualUrl, **hanya meneruskan event**); `DownloaderPyHostClient.cs:26` pemilik host browser python; `DownloadSourceIndex.cs:33` pemetaan judul→folder + catatan chapter terbit; `DownloaderPathContainment.cs` penjaga path-traversal |
| `Catalog/` | 5 | layar jelajah/detail online | `CatalogState` (`CatalogFeature.cs:10`), `CatalogFeature` (`:76`); adapter presentasi `RemoteTitleCardModel.cs:16`, `RemoteTitleDetailAdapter.cs:16` |
| `Queue/` | 8 | state job, penjadwalan, staging halaman, terbit CBZ | `DownloadQueueFeature` (`:29`), record di `DownloadQueueModels.cs:11-165`, `PageTransport`/`PageFetchResult` (`PageTransport.cs:60,37`), `PipelineResult` (`ChapterDownloadPipeline.cs:29`), `PublicationOutcome` (`CbzChapterPublisher.cs:48`), `DownloadQueueStore` (`:151`); UI `DownloadListScreen.xaml.cs:15` **hanya mengikat snapshot** |
| `Sources/` | 12 | registri + 3 folder provider | `MangaSourceRegistry`/`MangaSourceRegistration`/`IRemoteFilterContribution`/`IRemoteFilterState` (`MangaSourceRegistry.cs:53,40,11,25`), `IQueueSourceReadiness` (`QueueSourceReadiness.cs:8`) |
| `ManualUrl/` | 8 | resolusi URL tempel | `IManualUrlProbe` (`ManualUrlContracts.cs:10`), `ManualUrlResolution` (`:45`), `ManualUrlFeature` (`:7`); plus provider bantu `DynamicManualSource` (`:18`) |
| `FilterSearch/` | 9 | panel filter per provider (Comix 3, CucumberManga 2, DrakeScans 3) | `FilterSearchFeature.cs:11` memegang kontribusi aktif, membangun snapshot jelajah immutable |
| `Lister/` | 1 | probe ketersediaan lokal, read-only | `ListerFeature.cs:36`, `ListerResult:12` |
| `AutoCover/` | 1 | menulis `cover.png` | `AutoCoverFeature.cs:55`, `CoverCandidate:13`, `AutoCoverOutcome:37` |
| `mangareader_downloader/` | 2 py | plugin pyhost | perintah `downloader.open/api/fetch/close` (`plugin.py:17-21`) |

### 6.2 Flow unduh end-to-end

```text
[1]  Catalog/CatalogScreen.xaml.cs:368   _context.Queue.QueueChapters(...)
     (ada juga handoff lewat parent: MangaReaderView.xaml.cs:245,355)
[2]  Queue/DownloadQueueFeature.cs:108-226   admission · dedupe · persist
     commit durable lewat DownloadQueueStore.Commit (:1020-1032)
[3]  :224 → EnsureStarted :476 → RunSchedulerAsync :492
     PARALELISME #1: JobConcurrency = 2 chapter (:31, worker task :515-519)
     ← inilah perubahan "run queue jobs in parallel" (commit 172f193)
[4]  per job: containment path :588 · cari source _sources.FindSource :609
     gerbang readiness opsional lewat INTERFACE IQueueSourceReadiness :616
[5]  source.GetManifestAsync :646
     Comix = python: ComixSource.cs:1480 → DownloaderPyHostClient.ApiAsync
             → downloader.api (DownloaderPyHostClient.cs:153-157, plugin.py:19)
     CucumberManga / DrakeScans / DynamicManual = C# murni HttpClient
             (CucumberMangaSource.cs:119, DrakeScansSource.cs:12, DynamicManualSource.cs:22)
[6]  ChapterDownloadPipeline.RunAsync :675-687
     PARALELISME #2: PageConcurrency = 2 halaman via Parallel.ForEachAsync
             (ChapterDownloadPipeline.cs:105, 273-279)
     retry 1/2/4 detik (:97-102) · pass pemulihan :178-213 · jurnal staging job.json :511-537
[7]  PageTransport.FetchAsync :79-102
     C# native HTTP streaming DULU (FetchNativeAsync :104-223)
     fallback browser HANYA untuk NetworkFailed/Rejected/Challenge (:49-51)
       → FetchThroughBrowserAsync :225 → DownloaderPyHostClient.FetchToStagingAsync
         :168-179 → python downloader.fetch (plugin.py:20)
     python menulis byte ke staging; HANYA bukti yang kembali
       (PageTransport.cs:56-58 doc, :245-257)
[8]  deteksi format gambar dari byte (PageTransport.cs:322-349)
     transform milik provider (ChapterDownloadPipeline.cs:399)
       → IMangaSource.TransformPageAsync (MangaSourceContracts.cs:241)
       mis. Comix/ComixPageDecoder.cs, CucumberManga/CucumberMangaHtmlParser.cs
[9]  CbzChapterPublisher.PublishAsync / PublishIncompleteAsync
     (DownloadQueueFeature.cs:717-737; CbzChapterPublisher.cs:87,115)
     atomik temp→final · provenance META-INF/citadel-source.json (:70)
     memakai shareLogic ArchiveValidator / IArchiveLockCoordinator /
     LatestCoverBackupStore (:73-85)
[10] commit selesai DownloadQueueFeature.cs:754-766 · catat index :768
     refresh Library SENGAJA tidak dirantai (Lister/ListerFeature.cs:25-28)
     reader nanti membuka CBZ lewat shareLogic/CbzChapterLoader.cs:11
       → shareLogic/Archive/ArchivePageReader.cs:34-53 · scanner Library/LibraryScanner.cs:17
[11] cabang sampul: CatalogScreen.xaml.cs:414 menaikkan CoverCandidateAvailable
     → DownloaderView.xaml.cs:97-113 → AutoCoverFeature.SaveCoverAsync (:78)
```

### 6.3 Mekanisme plugin provider

**Interface utama:** `IMangaSource` di `shareLogic/Sources/MangaSourceContracts.cs:201-255`
(Browse, Lookup, GetTitle, GetGroups, GetChapters, GetManifest, TransformPage,
FindAlternateGroups). Facet opsional: `IQueueSourceReadiness`
(`Sources/QueueSourceReadiness.cs:8`), `ICatalogSnapshotSource`
(`shareLogic/Sources/CatalogSnapshotContracts.cs:123`), `IManualUrlProbe`
(`ManualUrl/ManualUrlContracts.cs:10`), `IRemoteFilterContribution`
(`Sources/MangaSourceRegistry.cs:11`).

**Tempat registrasi:** `MangaSourceRegistry.CreateDefault` di
`Sources/MangaSourceRegistry.cs:108-129` — `new` eksplisit tiap source (`:111-114`)
dan tiap lambda kontribusi filter (`:117-127`). Tanpa refleksi/discovery **secara
sengaja** (`:50-52`).

**Biaya menambah SATU provider:**

| Tindakan | File |
| --- | --- |
| BUAT | `Sources/<P>/<P>Source.cs` (+ parser/kontrak; DrakeScans 5 file, CucumberManga 2 file) |
| BUAT | `FilterSearch/<P>/<P>FilterContribution.cs` + `<P>FilterPanel.xaml` (+ `.xaml.cs` bila perlu — CucumberManga tidak punya, DrakeScans punya) |
| BUAT (opsional) | `Sources/<P>/<P>ManualUrlProbe.cs` |
| **EDIT** | `Sources/MangaSourceRegistry.cs:108-129` — **dua tempat**: `new` + entri registrasi |
| **EDIT (opsional)** | `ManualUrl/ManualUrlProbeRegistry.cs:10-28` — hanya bila provider mau dukungan manual-URL (CucumberManga melewatinya) |
| **EDIT** | `tests/Module.Mangareader.Downloader.Tests.csproj` — entri `Compile`/`Page` manual per file (bukti: `:93-119` untuk Cucumber/DrakeScans) |
| **EDIT** | `version.props` |
| tidak perlu | `Module.Mangareader.csproj` — SDK globbing; csproj hanya memuat payload Rar/pyhost/sqlite (`:9-79`) |

**Kontribusi UI:** ya. `IRemoteFilterContribution.CreatePanel()` mengembalikan
`FrameworkElement` (`MangaSourceRegistry.cs:11-16`); file registri meng-import
`System.Windows` (`:1`) dan record registrasinya menyimpan
`Func<IRemoteFilterContribution>` (`:40-42`). Yang dibangun registri adalah
**objek kontribusi** (pemilik UI), bukan panelnya; panel dibangun malas oleh layar
(`CatalogScreen.xaml.cs:163`, lewat `FilterSearchFeature.SelectSource`,
`FilterSearchFeature.cs:17-21`).

**Isolasi provider: bersih.** Tidak ada folder provider yang mereferensikan folder
provider lain (diverifikasi grep lintas `Sources/` dan `FilterSearch/`). File
FilterSearch tiap provider hanya meng-import namespace Sources-nya sendiri
(`CucumberMangaFilterContribution.cs:5`, `ComixFilterPanel.xaml.cs:7`,
`DrakeScansFilterPanel.xaml.cs:4`). Kode bersama hanya tingkat kontrak.
**Satu-satunya titik agregasi** yang menyentuh beberapa provider konkret di luar
registri adalah `ManualUrlProbeRegistry.cs:2-3,14-25` (Comix + DrakeScans +
DynamicManual sebagai tipe konkret).

### 6.4 Tepi antar sub-folder di dalam Downloader

| Tepi | Jenis | Bukti |
| --- | --- | --- |
| Queue → Sources | **campuran**: konkret `MangaSourceRegistry` + interface `IQueueSourceReadiness` | `DownloadQueueFeature.cs:3,34,609` vs `:616` |
| Catalog(screen) → Queue/AutoCover/Lister/Index/ManualUrl | **konkret** via `DownloaderContext`; panggilan metode langsung, bukan event | `CatalogScreen.xaml.cs:14-17,368,766,432,719`; field context `DownloaderContract.cs:36-48` |
| Catalog(screen) → parent | **event** | `OpenDownloadList`, `CoverCandidateAvailable` (`CatalogScreen.xaml.cs:111,414,419`), ditangani `DownloaderView.xaml.cs:41-46` |
| CatalogFeature → FilterSearch | konkret | `CatalogFeature.cs:3,80,160` |
| AutoCover → Lister | konkret | `AutoCoverFeature.cs:3,60,112` |
| ManualUrl → Sources | tipe provider konkret | `ManualUrlProbeRegistry.cs:14-25`; `ManualUrlFeature` sendiri interface-only atas `IManualUrlProbe` (`ManualUrlFeature.cs:9`) |
| Lister → (dalam Downloader) | tidak ada | memakai `shareLogic/LocalTitleProbe` (`ListerFeature.cs:3,38`) |
| event yang dipakai | `QueueSummaryChanged` (`DownloadQueueFeature.cs:67` → `DownloadListScreen.xaml.cs:40`), `StateChanged` (CatalogFeature/ManualUrlFeature → layarnya) |

**Pola yang terlihat:** event untuk **notifikasi perubahan state**, panggilan
konkret untuk **perintah**. FM-2 menghendaki komunikasi lewat event/hub; Downloader
memakai event setengah jalan.

Dua klaim komentar yang perlu dikoreksi:

- `DownloaderContract.cs:12-14` — "no child reaches into a sibling's mutable state
  through this". Tidak ditemukan pembacaan mutable state, **tapi** child memanggil
  perintah sibling pada tipe konkret (`CatalogScreen.xaml.cs:368,766`), yaitu
  panggilan langsung, bukan pola event/hub yang diminta skill.
- `MangaSourceRegistry.cs:104-107` — "The one explicit registration point… the
  Downloader parent and Catalog composition are never edited". Benar untuk parent
  dan Catalog, **tapi** `ManualUrlProbeRegistry.cs:10-28` adalah titik registrasi
  per-provider **kedua** (diedit untuk probe DrakeScans), dan csproj test menuntut
  edit per file (`Downloader.Tests.csproj:93-119`). → register A10.

### 6.5 Duplikasi terbesar di citadel: dua provider Comix

| Aspek | `Features/Downloader/Sources/Comix/ComixSource.cs` | `Features/Catalog/Sources/Comix/CatalogComixSource.cs` |
| --- | --- | --- |
| Ukuran | **1.606 baris** | **1.537 baris** |
| Kontrak sendiri | `ComixContract` (`:26`) | `ComixContract` sendiri juga (`:27`) |
| `ICatalogSnapshotSource` | logika snapshot/`ReadSnapshotItem` paralel (`:514,752`) | logika paralel (`:514,683`) |
| Decoder halaman | `Comix/ComixPageDecoder.cs` | `Catalog/Sources/Comix/CatalogComixPageDecoder.cs` |

±3.100 baris yang mendefinisikan provider yang sama dua kali, dengan kontrak dan
decoder masing-masing. Ini **satu pekerjaan, dua pemilik** dalam skala terbesar di
repo. → register C12.

### 6.6 Duplikasi lain

| Temuan | Bukti |
| --- | --- |
| Cek magic-byte PNG ditulis dua kali | `PageTransport.cs:324-327` dan `AutoCoverFeature.cs:224-227` |
| Dua sanitizer nama file dengan **aturan berbeda** untuk pekerjaan mirip | `DownloadQueueFeature.SanitizeFolder :1091` vs `ChapterDownloadPipeline.Sanitize :491` |
| `MangaSourceRegistry.cs` memuat 2 interface UI + record registrasi + registri + factory dalam satu file | `:11,25,40,53,108` |
| `CucumberMangaSource.cs` memuat kontrak, exception, enum, options, record query, dan source | `:11-116` |
| `ComixSource.cs` serupa | `:26-513` |
| `DownloadQueueStore.cs` juga menjadi rumah helper JSON generik `DownloaderJson` (`:39`) yang dipakai `DownloadSourceIndex.cs:43` dan `MangaReaderView.xaml.cs:49` | helper bersama tinggal di dalam satu feature |

---

## 7. shareLogic (milik module, ejaan kecil) vs sharedLogic (milik repo)

`module/mangareader/shareLogic/` (namespace `Module.Mangareader.*`) adalah kode
bersama **tingkat module**, bukan Downloader-only — berbeda dari
`module/sharedLogic/` yang di-compile masuk lewat `Module.Mangareader.csproj:10`.

| File | Konsumen |
| --- | --- |
| `Sources/MangaSourceContracts.cs` | Downloader (registri, queue, semua provider) **dan** Library UpdateChecker (`MangaReaderView.xaml.cs:79-88`) **dan** Features/Catalog (`CatalogSourceDirectory.cs:3`). Alasannya tertulis di header (`:3-11`) |
| `Sources/CatalogSnapshotContracts.cs` | `Features/Catalog/Sources/Comix/CatalogComixSource.cs:514`, `Features/Downloader/Sources/Comix/ComixSource.cs:514`, `Features/CatalogMirror/*` (`CatalogSnapshotStore.cs:46`, `CatalogMirrorSyncFeature.cs:17`), `MangaReaderView.xaml.cs:107` |
| `Archive/` (9 file) | terbit Downloader (`CbzChapterPublisher.cs:6,73-85`), CoverBuilder (`CoverBuilderService.cs:23` → `ArchiveReplacementTransaction.cs:49`), muat reader/library (`CbzChapterLoader.cs:11`, `MangaCoverLoader.cs:20`, `LibraryScanner.cs:17` → `ArchivePageReader`), probe (`LocalTitleProbe.cs:102`) |

### Ekstraksi arsip: satu pekerjaan, dua pemilik, dengan **siklus dependensi**

| Bagian | Pemilik |
| --- | --- |
| Pemanggilan proses RAR (extract `ReadPages :41-71`, tulis-ulang sampul `WriteCover :73-117`, spawn `Rar.exe` arg `x/a/t` `:54-56,95-98,129-160`) | `Features/Rar/RarArchiveFeature.cs:13` |
| Ekstraksi ZIP + dispatch format | `shareLogic/Archive/ArchivePageReader.cs:34-53` |
| Bake sampul | `CoverArchiveWriter.cs:22` |
| Penggantian aman | `ArchiveReplacementTransaction.cs:49` |
| Pelepasan lock (Windows Restart Manager) | `ArchiveLockCoordinator.cs:15-32` |

**Siklus:** `shareLogic/Archive/ArchivePageReader.cs:3` meng-import `Features.Rar`,
sementara `Features/Rar/RarArchiveFeature.cs:3,80,90` meng-import `shareLogic`.
Jadi folder "bersama" bergantung pada satu feature, dan feature itu bergantung
balik ke folder bersama. Ini **saling import literal** di dalam satu citizen —
persis bentuk yang dikeluhkan owner, dan tidak terlihat gate mana pun.
→ register C13.

### `Rar.exe` dan `rarreg.key` — fakta provenance

- Tidak ada kode C# yang membaca `rarreg.key` (grep: nol rujukan kode). Ia hanya
  disalin di sebelah `Rar.exe` saat build (`Module.Mangareader.csproj:66-68,76-78`;
  proyek test melakukan hal sama).
- `RarArchiveFeature.cs:134` menyetel `WorkingDirectory` ke folder exe, jadi
  `Rar.exe` menemukannya secara implisit — ini pola file lisensi WinRAR standar.
- Apakah `Rar.exe` benar-benar mengonsumsinya: **unverified** dari kode.

Dicatat apa adanya karena owner peduli provenance aset/biner pihak ketiga: ini
biner komersial pihak ketiga (WinRAR) + file kunci lisensi yang ikut ter-deploy ke
folder runtime citizen dan ke installer rilis.

---

## 8. Features/Catalog vs Features/CatalogMirror

`Features/Catalog` (4 file root + `Runtime/`, `Sources/Comix/`, paket python)
adalah **pemilik runtime tab Catalog**: klien browser sendiri
(`CatalogBrowserClient.cs:26`, plugin pyhost `mangareader_catalog` `:37,396`,
perintah `catalog.*` `plugin.py:17-21`), direktori provider tertutup sendiri
(`CatalogSourceDirectory.cs:11-19`, satu `CatalogComixSource`), context/lifetime
sendiri (`CatalogContext.cs:12`).

**Tepinya dua arah:** Catalog → CatalogMirror (`CatalogContext.cs:2,13,19`
membungkus `CatalogMirrorFeature`; `CatalogView.xaml.cs:2`), dan CatalogMirror →
Catalog (`CatalogMirrorView.xaml.cs:11,48,79` menerima `CatalogContext`).

**Pemisahan dari Downloader terverifikasi di sisi C#:** kelas klien terpisah, nama
plugin terpisah (`DownloaderPyHostClient.cs:40` vs `CatalogBrowserClient.cs:37`),
proses `PyHost.Start` terpisah (`:409` vs `:396`), root staging terpisah
(`MangaReaderView.xaml.cs:49-50` vs `:96-104`). `CatalogSourceDirectory.cs:8-9`
mendokumentasikan tidak meng-impor registri Downloader, dan tidak ada `using`
Downloader di `CatalogComixSource.cs:1-8`. Klaim csproj bahwa kedua paket "never
share a browser/session host" (`Module.Mangareader.csproj:48-50`) **terbukti**.

---

## 9. Rujukan register dari slice ini

A10 (klaim "satu-satunya titik registrasi" vs `ManualUrlProbeRegistry` + csproj test) ·
C11 (`MangaMultiSelectFilter` abu-abu terhadap SU-4) ·
C12 (dua provider Comix, ±3.100 baris duplikat) ·
C13 (siklus import `shareLogic/Archive` ↔ `Features/Rar`) ·
B3 (registri sumber = daftar `new` eksplisit, sudah dicatat slice 2) ·
B6 (biaya menambah feature Downloader vs Reader berbeda jauh) ·
D17 (`FrameContentHost.cs` dua kelas; `ReaderFeatureContract.cs` ±15 tipe; hub
tinggal di luar `ReaderCore/`) ·
D18 (logika feature Overlay tinggal di root Reader) ·
D19 (dua sanitizer nama file dengan aturan berbeda; cek magic-byte PNG ganda) ·
D20 (`CoverBuilderView.xaml.cs` 352 baris; `DownloadQueueStore.cs` menjadi rumah
helper JSON bersama).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.

---

## 10. Parent module — `MangaReaderView` (sisi yang TIDAK seperti Reader)

File root: `MangaReaderModule.cs` (12 baris), `MangaReaderView.xaml` (45),
`MangaReaderView.xaml.cs` (**412**), `MangaTitleCard.xaml` (108)/`.cs` (8).

**Tidak ada file katalog/registry feature di tingkat module.**
`MangaSourceRegistry` dan `CatalogSourceDirectory` mendaftarkan **sumber manga**,
bukan feature. Jadi di tingkat module, registrasi feature sepenuhnya manual.

### 10.1 Sembilan subsistem disebut dengan tipe konkret

`MangaReaderView.xaml.cs` menyebut: History, Library, Downloader (+Queue/Lister/
AutoCover), Sources, UpdateChecker, Catalog, CatalogMirror, Reader, CoverBuilder.

Tidak ada katalog — yang ada adalah urutan `new XFeature()` eksplisit di konstruktor
(`:36-186`):

| Baris | Konstruksi |
| --- | --- |
| `:50` | `new DownloaderPyHostClient` |
| `:53` | `new DownloadQueueFeature(...)` |
| `:63` | `new ListerFeature(...)` |
| `:71` | `new AutoCoverFeature(...)` |
| `:79-88` | `new UpdateCheckerFeature(new SourceBindingStore(), new UpdateMatcher(...))` |
| `:101-105` | `new CatalogMirrorPaths / CatalogSnapshotStore / CatalogBrowserClient / CatalogSourceDirectory` |
| `:109` | `new CatalogMirrorSyncFeature` |
| `:112-114` | `new CatalogGenreSyncFeature(new CatalogGenreStore(catalogStore.Database))` |
| `:162-169` | `new CatalogMirrorDetailFeature / LoadFeature / Feature` |
| `:170` | `new CatalogContext(...)` |
| `:307` | `new ReaderWindow` |

### 10.2 Parent memuat logika feature (uji FM-5 gagal)

Empat blok yang bisa dijelaskan **tanpa menyebut feature lain** — artinya itu logika
feature, bukan koordinasi:

| Blok | Baris | Isi |
| --- | --- | --- |
| `CheckCatalogAvailability` | `:116-137` | menghitung verdict ketersediaan-lokal per chapter; memanggil `lister.ListAsync` (`:125`) dan `ListerFeature.IsLocallyAvailable` (`:129`). Ini logika detail CatalogMirror, tinggal di parent |
| `ConfirmQueueTarget` | `:139-160` | resolusi tabrakan folder + `DownloadQueueFeature.SanitizeFolder` (`:148`) + `SettingDialog.Confirm` (`:151`). Aturan bisnis di parent |
| `EnqueueUpdate` | `:349-379` | **menjumlah ulang** dua pencacah skip milik queue (`:367-370`) — parent menafsirkan ulang hasil feature lain |
| `ReadConfirmedMapping` | `:329-342` | membaca `index.Load().Mappings` (`:333`), yaitu store internal Downloader |

### 10.3 Parent menjangkau internal feature (FM-1)

- CatalogMirror: **10 tipe internal konkret** (`:101-169`), termasuk
  `catalogStore.Database` (`:114`).
- Downloader: `DownloadQueueFeature`, `DownloadSourceIndex`, `DownloadQueueStore`,
  `ListerFeature`, `AutoCoverFeature`, `DownloaderContext`, `DownloaderJson`
  (`:49-91`), plus static `ListerFeature.IsLocallyAvailable` (`:129`) dan static
  `DownloadQueueFeature.SanitizeFolder` (`:148`).
- UpdateChecker: `SourceBindingStore`, `UpdateMatcher`, `UpdateCheckerFeature`
  (`:79-88`).
- Reader: `new ReaderWindow` (`:307`).

### 10.4 Biaya menambah satu tab tingkat atas

**Tepat dua file parent harus diedit:**

1. `MangaReaderView.xaml` — tambah xmlns + `<TabItem>` + view (di `:14-43`);
2. `MangaReaderView.xaml.cs` — konstruksi/wiring di konstruktor (`:36-186`) +
   teardown di `DisposeView` (`:381-411`).

State bersama apa pun (root/queue/sources) memaksa wiring konstruktor tambahan.
Ini melanggar FM-3 ("adding/removing a feature touches only that feature's
folder", `SKILL.md:299-300`).

**Bandingkan dengan Reader:** menambah feature drawer-card/visual di Reader =
**nol** edit host (§1.4). Dua pola, dua biaya, satu citizen.

### 10.5 Navigasi tab dimiliki parent, tanpa router

`setting:SettingTabs` (`MangaReaderView.xaml:14`) dengan **6 `<TabItem>` hardcoded**
(`:16-42`), dan perpindahan dilakukan dengan menyetel `TabItem.IsSelected` secara
programatik — bukan router atau switch:
`QueueTabItem.IsSelected = true` (`:204`, `:246`), `DownloaderTabItem.IsSelected = true` (`:209`).

---

## 11. Library, Grouping, UpdateChecker

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `LibraryView.xaml.cs` | 466 | layar Library; memiliki scan, grid/list, sort |
| `LibraryScanner.cs` | 77 | scan folder → `MangaTitle` |
| `LibraryRootContext.cs` | 98 | satu pemilik root seumur module + `RootChanged` |
| `LibraryPathStore.cs` | 213 | persistensi atomik `library-path.txt` + seam `ILibraryPathFileIO` |
| `LibraryScanPersistence.cs` | 48 | aturan capture-awal / persist-bila-sukses |
| `LibraryViewModeFeature.cs` | 84 | preferensi Grid/List |
| `LocalTitleDetailAdapter.cs` | 51 | title → `MangaDetailPresentation` |
| `ChapterSelectorView.xaml.cs` | 125 | overlay pemilih chapter |

### Flow scan

```text
[1] LibraryView_Loaded                     LibraryView.xaml.cs:108 → _root.Restore() :118
[2] ScanLibraryAsync                       :151 → _root.BeginScan :167
[3] _scanner.ScanAsync                     :180 → LibraryScanner.Scan (LibraryScanner.cs:31)
       enum dir :40 · natural sort :42 · bangun MangaTitle :63
       AddedUtc = GetCreationTimeUtc :71
[4] bangun MangaTitleCardModel             :186
[5] NotifyTitlesChanged → TitlesChanged    :188 / :317
[6] CompleteSuccessfulScan :245 → _root.CompleteScan :252
       → LibraryScanPersistence.CompleteScan (LibraryScanPersistence.cs:35)
       → LibraryPathStore.Save (LibraryPathStore.cs:143)
[7] LoadCoversAsync → MangaCoverLoader.LoadAsync   :278
[8] UpdateGroupFilter → _titlesView.Refresh        :346
```

### Cara keduanya menempel — dan statusnya sebagai feature

**Grouping dan UpdateChecker adalah INTERNAL Library** (sub-folder `Library/`), bukan
feature tingkat atas. Cara menempelnya berbeda:

- **Grouping: konstruksi langsung + event.** `new GroupingFeature(_groupingStore)`
  (`LibraryView.xaml.cs:41`), `Grouping.UseGrouping` (`:57`),
  `_grouping.Changed += Grouping_Changed` (`:61`), filter `_grouping.IsVisible` (`:49`).
  Ia memasang dirinya sendiri.
- **UpdateChecker: disuntik parent.** `UseUpdateChecker(feature)` (`:89`) →
  `new UpdateCheckerEntry(feature).Install(...)` (`:94-97`); feature menaikkan
  `Changed`, dan enqueue dirutekan balik lewat delegate parent `EnqueueUpdate`
  (parent `:88`, `:349`). Ia di-wire parent karena butuh rute lintas feature
  (queue, `IMangaSourceDirectory`, library root).

---

## 12. History

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `ReadingHistory.cs` | 68 | pemilik pencatatan, satu jalur mutasi, menaikkan `Changed` |
| `ReadingHistoryStore.cs` | 185 | `history.json` durable, tulis atomik (`:164`), retensi 15 non-pin (`:26`, `:127`) |
| `HistoryView.xaml.cs` | 329 | presentasi |
| `HistoryCardModel.cs` | 85 | VM kartu |
| `ClearHistoryFeature.cs` | 72 | perintah clear lewat satu pemilik |
| `PinnedHistoryFeature.cs` | 63 | perintah pin lewat satu pemilik |
| `HistoryViewModeFeature.cs` | 83 | preferensi Grid/List |

**Siapa menulis history: READER, dirutekan parent.** Parent memiliki `_history`
(parent `:27`), menyuntik `HistoryTab.UseHistory(_history)` (parent `:42`);
`OpenReader` memanggil `_history.Record` (parent `:305`) dan
`reader.ActiveChapterChanged → _history.Record` (parent `:311-313`).
Bukan Library, bukan Downloader.

Persistensi: `%LocalAppData%\Citadel\MangaReader\history.json`
(`ReadingHistoryStore.cs:33-37`).

Tepi: parent→History `UseHistory` (`:42`), `SetLibrary` (`:190`;
`HistoryView.xaml.cs:73`), `Refresh` (`:285`, `:297`); History→parent
`OpenChapterRequested` (`HistoryView.xaml.cs:48,134`, disambung
`MangaReaderView.xaml:23`); Library→History lewat parent
(`LibraryTab_TitlesChanged → HistoryTab.SetLibrary`, parent `:188-190`).

---

## 13. Features/CatalogMirror — feature terbesar (6.104 baris, 16 file)

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `CatalogMirrorContracts.cs` | 323 | record/enum immutable saja, tanpa I/O |
| `CatalogMirrorPaths.cs` | 108 | kosakata path, direktori identitas SHA-256 (`:95`), penjaga keluar-root (`:62`) |
| `CatalogMirrorDatabase.cs` | 240 | skema/PRAGMA/migrasi SQLite (v3, `:13`) |
| `CatalogSnapshotStore.cs` | 1.273 | lifecycle staging → checkpoint → dedupe → aktivasi → retensi |
| `CatalogMirrorQuery.cs` | 319 | search/filter/sort/page lokal, terparameterisasi |
| `CatalogEnrichmentStore.cs` | 121 | `metadata.json` per judul, atomik |
| `CatalogGenreStore.cs` | 255 | taksonomi genre + keanggotaan (SQLite) |
| `CatalogMirrorCoverCache.cs` | 200 | fetch/decode/cache sampul, terbatas |
| `CatalogMirrorSyncFeature.cs` | 519 | mesin keadaan traversi 4-partisi |
| `CatalogGenreSyncFeature.cs` | 279 | loop pengayaan genre per judul |
| `CatalogMirrorDetailFeature.cs` | 419 | muat title/group/chapter + bangun handoff |
| `CatalogMirrorLoadFeature.cs` | 139 | pilih generasi DB→display |
| `CatalogMirrorFeature.cs` | 461 | koordinator tipis: gabung state, teruskan intent |
| `CatalogMirrorCardModel.cs` | 75 | VM kartu |
| `CatalogMirrorView.xaml(.cs)` | 405 / 968 | wiring UI, Dispatcher, render |

### Flow sync

```text
[1] trigger   CatalogMirrorFeature.StartSyncAsync  CatalogMirrorFeature.cs:262 → _sync.StartAsync
[2]           CatalogMirrorSyncFeature.RunAsync    SyncFeature.cs:157 (seed :169 / clear :183)
[3] paging    :224-360 · FetchWithBackoffAsync :257 / :391 → _source.GetSnapshotPageAsync :409
[4] dedupe    _store.AppendPageAsync :293 → CatalogSnapshotStore.AppendPageAsync :45
                 upsert last-wins :817 · loop-guard sidik jari :136-142 · checkpoint :156
[5] partisi   CompletePartitionAsync :343 / CatalogSnapshotStore.cs:407
[6] aktivasi  _store.ActivateAsync :370 / CatalogSnapshotStore.cs:459
                 balik active_generation :500 · retensi active+previous :525
[7] query     CatalogMirrorFeature.QueryAsync :202 → LoadFeature.QueryAsync :96
                 → CatalogMirrorQuery.Execute :55
```

### Apakah self-contained? **TIDAK**

Referensi keluar folder:

| Ke | Bukti |
| --- | --- |
| `Module.Mangareader.Sources` (kernel kontrak) | Contracts `:4`, Feature `:4`, Sync `:5`, Query `:6`, SnapshotStore `:9`, Detail `:4`, GenreStore `:2`, GenreSync `:1`, CardModel `:5`, View.cs `:13` |
| `Citadel.Setting.Components` | View.cs `:9` |
| `Module.Mangareader.Components` | View.cs `:10` |
| `Module.Mangareader.ShareLogic` | CardModel `:4`, View.cs `:12` |
| `Module.Mangareader.Features.Catalog` | View.cs `:11` (memakai `CatalogContext`) |
| **`Features.Downloader.Sources.Comix`** | **SyncFeature.cs `:4`** ← pelanggaran lintas feature, lihat §14 |

Referensi dari LUAR ke internalnya: parent mengonstruksi ±10 internal
(`MangaReaderView.xaml.cs:101-169`, plus `catalogStore.Database` `:114`);
`CatalogContext.cs:2,13` mengonsumsi `CatalogMirrorFeature`;
`CatalogView.xaml:12` meng-host `<mirror:CatalogMirrorView>`.

### Komposisi view: PATUH shared-UI

`CatalogMirrorView.xaml` memakai `setting:SettingViewport / ActionCard / Field /
Button / Toggle / Table` (`:16,26,35,42,68,350`) dan style bersama lewat
`DynamicResource SettingComboBoxStyle / SettingCardStyle / SettingScrollViewerStyle`
(`:61,161,240`), plus komponen module `mangacompons:MangaDetailView /
MangaMultiSelectFilter` dan `manga:MangaTitleCard`. **Tidak ada template/primitif
yang disalin lokal** — komentar `:11-15` cocok dengan kode.

---

## 14. BUG: `catch` yang tidak pernah cocok, akibat provider Comix ganda

**Ini temuan paling serius dalam seluruh dokumentasi, dan ia terverifikasi baris
per baris olehku sendiri (bukan hanya laporan agent).**

Rantai buktinya:

| # | Fakta | Bukti |
| --- | --- | --- |
| 1 | Ada **dua** tipe `ComixContractException`, keduanya `sealed : InvalidOperationException`, nama sama, namespace berbeda | `Features/Downloader/Sources/Comix/ComixSource.cs:195` (namespace `…Features.Downloader.Sources.Comix`, `:9`) dan `Features/Catalog/Sources/Comix/CatalogComixSource.cs:196` (namespace `…Features.Catalog.Sources.Comix`, `:10`) |
| 2 | Sumber snapshot yang benar-benar dipakai CatalogMirror adalah `CatalogComixSource` | `Features/Catalog/CatalogSourceDirectory.cs:18` `_sources = [new CatalogComixSource(browser)]`, dengan `using …Features.Catalog.Sources.Comix` di `:2` |
| 3 | `CatalogComixSource` melempar exception dari namespace-**nya sendiri**, dengan `HttpStatus` terisi | `CatalogComixSource.cs:198` (`public int? HttpStatus { get; set; }`), lemparan di `:619,628,664,805,861,1104,1114,1128`; `GetSnapshotPageAsync` di `:568`, bahkan punya `catch (ComixContractException …) when (HttpStatus is 401 or 403)` di `:597` |
| 4 | Tapi `CatalogMirrorSyncFeature` meng-import namespace **Downloader** | `Features/CatalogMirror/CatalogMirrorSyncFeature.cs:4` `using Module.Mangareader.Features.Downloader.Sources.Comix;` |
| 5 | Sehingga `catch` di sana mengikat ke tipe Downloader | `CatalogMirrorSyncFeature.cs:416-418` `catch (ComixContractException exception) when (exception.HttpStatus is 429 or 502 or 503)` |
| 6 | Kedua tipe `sealed` dan tidak saling menurunkan → exception bertipe Catalog **tidak mungkin** tertangkap | fakta 1 |

**Akibat:** retry backpressure bertipe untuk HTTP 429/502/503 — backoff eksponensial
`wait × 2^attempt` dengan cap 30 detik plus jitter (`:419-425`) — adalah **kode mati**
pada sync CatalogMirror. Exception penyedia jatuh ke handler generik, sehingga
sinkronisasi ±90 ribu judul tidak melakukan backoff bertipe saat provider
throttling.

Komentarnya justru menjelaskan niat yang benar: "Typed backpressure only: v1 serves
one source, so its contract exception is the only throttle signal. Never
message-sniffed; a second source would bring its own typed signal here"
(`:413-415`). Niatnya tepat; tipe yang diimpor salah.

**Klaim yang ikut gugur:** `.docs/REPORT-catalog-mirror-handoff-2026-09-08.md:47`
mencatat "backoff typed 429/502/503 (2 dtk ×2ⁿ, cap 30 dtk, maks 4 percobaan)"
sebagai keputusan terkunci yang terverifikasi. Mekanisme itu ada di kode tapi tidak
pernah aktif untuk sumber yang benar.

**Akar penyebabnya adalah C12** (dua provider Comix yang diduplikasi) **ditambah C3**
(tidak ada penegak untuk import lintas feature). Satu `using` yang salah arah tidak
terdeteksi gate apa pun: compiler senang (tipe itu ada), test hijau (tidak ada test
yang melempar 429 dari sumber Catalog ke sync), dan review sulit menemukannya karena
kedua tipe bernama identik.

**Bentuk lurusnya nanti:** satu `ComixContractException` di tempat netral
(`shareLogic/Sources/`, tempat `MangaSourceContracts.cs` dan
`CatalogSnapshotContracts.cs` sudah tinggal), dipakai kedua provider; atau — lebih
baik dan sekaligus menghapus C12 — satu provider Comix, bukan dua.

---

## 15. shareLogic (root module) — kernel bersama, dengan tiga file nyasar

16 file, 1.488 baris. Sebagian besar memang kernel bersama (jumlah folder konsumen):

| File | Konsumen |
| --- | --- |
| `MangaLibrary.cs` (`MangaTitle`/`ChapterInfo`) | hampir semua folder (27 file) |
| `NaturalStringComparer.cs` | Library, Downloader/Queue, shareLogic/Archive (4) |
| `ChapterNumberText.cs` | Library/UpdateChecker ×2, Downloader/Catalog, Downloader/Lister (4) |
| `LocalTitleProbe.cs` | parent, Library/UpdateChecker ×2, Downloader/Lister (4) |
| `MangaCardPresentation.cs` | History, CatalogMirror, Downloader/Catalog, shareLogic (4) |
| `MangaDetailPresentation.cs` | Components, Library, Downloader/Catalog (3) |
| `MangaViewMode.cs` | Library, History, Components (3) |
| `MangaCoverLoader.cs` | Library, History, CoverBuilder (3) |
| `MangaReaderEvents.cs` | parent, Library, CoverBuilder (3) |
| `OpenChapterRequestedEventArgs.cs` | Library, History, parent, Reader |
| `ViewModePreferenceStore.cs` | Library, History (2) |
| `MangaTitleCardModel.cs` | Library ×3, CoverBuilder |

**Tiga file dengan SATU konsumen — kandidat pindah ke dalam konsumennya:**

| File | Baris | Satu-satunya konsumen |
| --- | --- | --- |
| `CbzChapterLoader.cs` | 296 | `Reader/CbzReaderChapterLoader.cs` |
| `ChapterSurfaceModel.cs` | 65 | Reader (5 file, semuanya di `Reader/`) |
| `ChapterLoadingContracts.cs` | 63 | Reader |

Ketiganya ±424 baris kontrak chapter-loading milik Reader yang tinggal di folder
"bersama". Jadi `shareLogic/` bukan dumping ground, tapi **sebagian** isinya adalah
internal Reader yang salah alamat. → register D21.

(`ChapterRenderCache.cs` 195 baris **bukan** single-consumer: konsumen langsungnya
`History/HistoryView.xaml.cs:20`, tapi ia menjangkau Reader/Library/CoverBuilder
lewat `CbzChapterLoader`/`MangaCoverLoader`.)

---

## 16. Daftar tepi lintas subsistem

| Dari → Ke | Jenis | Bukti |
| --- | --- | --- |
| parent → Library | konkret | `MangaReaderView.xaml.cs:28,47,284` |
| parent → History | konkret | `:27,42,190,285,305,313` |
| parent → Downloader | konkret | `:49-91,148,245,349` |
| parent → Catalog | konkret | `:103,105,170` |
| parent → CatalogMirror | **konkret, internal** (10 tipe) | `:101-169`, `catalogStore.Database` `:114` |
| parent → Reader | konkret | `:307,311` |
| parent → UpdateChecker | konkret | `:79-88` |
| parent → shareLogic | konkret | `:64,78` |
| parent → Components/Setting | xaml + konkret | `xaml:14`; `cs:151,260` |
| Library → Grouping | konkret + event | `LibraryView.xaml.cs:41,57,61,49` |
| Library → UpdateChecker | konkret, disuntik | `:89,94` |
| Library → shareLogic | kontrak | `LibraryScanner.cs:3`, `LibraryView.xaml.cs:12,27,31` |
| UpdateChecker → shareLogic | konkret | `UpdateMatcher.cs:3,74,368` |
| UpdateChecker → Sources | kontrak | `UpdateCheckerFeature.cs:2,67` |
| UpdateChecker → Downloader | kontrak-saja, lewat delegate parent | `UpdateCheckerFeature.cs:52`; jembatan parent `:88,349` |
| History → shareLogic | kontrak | `ReadingHistory.cs:2`, `HistoryView.xaml.cs:19-20`, `HistoryCardModel.cs:43` |
| History → parent | event | `HistoryView.xaml.cs:48,134` |
| CatalogMirror → Sources | kontrak | `Contracts.cs:4` dll. |
| **CatalogMirror → Downloader** | **konkret, pelanggaran** | `SyncFeature.cs:4,416` |
| CatalogMirror → Catalog | konkret (host) | `View.xaml.cs:11,48` |
| CatalogMirror → Components | konkret | `View.xaml.cs:10`, `View.xaml:165,307` |
| Catalog → CatalogMirror | xaml + konkret (host) | `CatalogView.xaml:12`, `CatalogContext.cs:2` |
| Components → shareLogic | kontrak | `MangaDetailView.xaml.cs`, `MangaViewModeSelector.xaml.cs` |
| Reader → shareLogic | konkret | `CbzReaderChapterLoader.cs`, `Reader/Features/ChapterLoading/*` |
| **shareLogic/Archive ↔ Features/Rar** | **siklus** | `ArchivePageReader.cs:3` ↔ `RarArchiveFeature.cs:3,80,90` |

---

## 17. Duplikasi dan komentar yang dibantah kode

| Temuan | Bukti |
| --- | --- |
| **Dua provider Comix** ±3.100 baris, masing-masing dengan `ComixContractException` sendiri | §6.5, §14 |
| `LibraryViewModeFeature.cs` dan `HistoryViewModeFeature.cs` hampir identik (pemilik pref Grid/List di atas `ViewModePreferenceStore`) — sengaja, kunci berbeda `Library.ViewMode` / `History.ViewMode` | duplikasi yang dibenarkan |
| Pola tulis-JSON-atomik (tmp→move) diulang di empat store | `LibraryPathStore`, `GroupingStore`, `SourceBindingStore`, `ReadingHistoryStore` |
| Komentar parent `:75-77` "The root only translates record shapes and forwards results: it never interprets provider behavior and owns no update-checking rule" — **dibantah**: parent mengonstruksi `UpdateMatcher`/`UpdateCheckerFeature` (`:79-88`) dan mengimplementasi `ReadConfirmedMapping` (`:329`) yang membaca `index.Load().Mappings` (`:333`) | A-kategori |
| Komentar parent `:93-95` "Catalog Mirror composes … through its neutral projection, plus its own snapshot store" — **dibantah**: parent, bukan CatalogMirror, yang mengonstruksi setiap internal (`:101-169`) | A-kategori |
| Komentar parent `:216-218` "the queue's own answer is returned as-is … never reinterpreted here" — **dibantah** oleh `EnqueueUpdate` yang menjumlah ulang pencacah skip (`:367-370`) | A-kategori |
| `UpdateCheckerFeature.cs:35-37` "Update Checker never calls a queue itself — the composition root supplies the route" — **akurat**, tapi rute yang disediakan parent menafsirkan ulang hasil queue | lihat baris di atas |
| `CatalogMirrorView.xaml.cs` 968 baris: render + navigasi + bangun opsi filter + paginasi + host detail + batching sampul | besar, tapi komposisi/wiring |
| `CatalogSnapshotStore.cs` 1.273 baris: staging + checkpoint + dedupe + aktivasi + retensi dalam satu tipe | kohesif tapi sangat besar |

---

## 18. Rujukan register tambahan dari bagian ini

**BUG-1** (`catch` mati di CatalogMirrorSyncFeature, §14) — dicatat sebagai entri
tersendiri di puncak register karena ini defek nyata, bukan penyimpangan struktur.

A11 (klaim parent "never interprets provider behavior" vs kode) ·
A12 (klaim "Catalog Mirror composes its own snapshot store" vs parent yang
mengonstruksi) ·
A13 (klaim "queue's answer returned as-is" vs penjumlahan ulang pencacah) ·
B7 (biaya tambah tab module = 2 file parent, vs Reader = nol) ·
C14 (parent menjangkau 10 internal CatalogMirror + static milik Downloader) ·
D21 (tiga file Reader-only tinggal di `shareLogic/`) ·
D22 (`MangaReaderView.xaml.cs` 412 baris memegang komposisi + logika availability +
aturan tabrakan folder + translasi queue + navigasi + disposal).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.
