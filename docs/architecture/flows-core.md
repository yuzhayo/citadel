# CITADEL-FLOWS-CORE — fondasi, chrome, dan periferal shell

Melingkupi tiga proyek yang bukan module: `core/Citadel.Core` (fondasi bebas WPF),
`core/Citadel.Ui` (chrome milik core), dan periferal `core/Citadel.Shell` yang
bukan bagian dari alur registrasi/navigasi (yang itu ada di
`CITADEL-FLOWS-CROSS.md` §2).

Ditulis 2026-09-11 dari kode. Klaim bertanda `file:baris`.

---

## 1. Citadel.Core — fondasi yang tidak mereferensikan apa pun

**Terbukti, bukan diklaim:** `core/Citadel.Core/Citadel.Core.csproj` adalah 9 baris
tanpa satu pun `ProjectReference` atau `PackageReference`, dengan komentar
"No WPF, no other Citadel projects: Citadel.Core references nothing."
(`Citadel.Core.csproj:3`). `UseWPF` tidak diset di csproj maupun di
`Directory.Build.props:5-10` — jadi WPF mati karena tidak disebut, bukan karena
`false`. Tidak ada `System.Windows` di seluruh sumber Core.

Ini penting untuk `CITADEL-KERNEL-MAP.md`: **inilah satu-satunya lapisan di
citadel yang benar-benar daun.** Bandingkan dengan `Citadel.Contract` yang
menarik Core (lihat register, D6).

### 1.1 Node dan pemiliknya

| Node | Pekerjaan (satu) | Pemilik |
| --- | --- | --- |
| LIFETIME | kantong callback penghancur LIFO — token kepemilikan seluruh app | `rpl/Lifetime.cs` |
| EVENT STREAM | broadcast sinkron banyak pelanggan | `rpl/EventStream.cs` |
| PRODUCER | stream dingin, generator per langganan | `rpl/Producer.cs` |
| VARIABLE | nilai yang bisa diset + stream perubahan | `rpl/Variable.cs` |
| OPERATORS | dua operator `Producer`: dedupe & batch-di-main | `rpl/Operators.cs` |
| MAIN QUEUE | antrean FIFO kerja main-thread, tanpa Dispatcher | `crl/MainQueue.cs` |
| CRL | fasad statis kompatibilitas untuk marshalling | `crl/Crl.cs` |
| WATCHDOG | ping 1 Hz, catat stall main-thread | `crl/Watchdog.cs` |
| POWER SAVING | satu flag global: animasi mati | `PowerSaving.cs` (namespace `Citadel.Core.Crl`) |
| TOKENS | store token hidup: resolve, tema, preview, commit | `tokens/Tokens.cs` |
| DEFAULTS | tabel token bawaan di kode | `tokens/Defaults.cs` |
| OVERRIDES | simpan/muat `ui.json` sparse + kebijakan path | `tokens/Overrides.cs` |
| GUARD | sanitasi + putusan (warn/clamp/revert/refuse) | `tokens/Guard.cs` |
| THEME | satu tema sparse bernama | `tokens/Theme.cs` |
| TOKEN PAIRS | pasangan kontras fg/bg + ambang WCAG | `tokens/TokenPairs.cs` |
| LOG | dua sink bernama, ring 2048, thread penulis latar | `Log.cs` |

### 1.2 `rpl` — pustaka reaktif kecil, dan aturan kepemilikan app

Ini port `rpl` milik Telegram Desktop; komentarnya menyebut sumbernya
(`EventStream.cs:4`, `Lifetime.cs:3`, `Producer.cs:3`, `Variable.cs:3`,
`Operators.cs:9`). Empat primitif + dua operator:

```text
LIFETIME ── kantong callback LIFO; Destroy() idempoten, newest-first
│           "Every subscription in this app must be owned by a Lifetime —
│            leaks become structurally impossible instead of merely discouraged"
│           (Lifetime.cs:4-6)
│           menambah ke lifetime yang sudah mati → callback jalan SEKARANG (Lifetime.cs:8)
│
├── EVENT STREAM<T>  panas · Fire sinkron di thread pemanggil
│                    "fire on main (or marshal via Core.Crl) when subscribers
│                     touch WPF objects" (EventStream.cs:4-6)
│                    dua penyimpangan sengaja dari upstream: snapshot pelanggan
│                    ber-lock, dan try/catch per pelanggan (EventStream.cs:8-12)
│
├── PRODUCER<T>      dingin · generator per langganan
│                    "Cold matters — a port built on a hot event silently
│                     diverges on every re-subscription" (Producer.cs:4-6)
│                    pabrik: Single(T), FromStream(EventStream<T>) (Producer.cs:24-39)
│
├── VARIABLE<T>      Current + Value (settable) + Changes (EventStream) + replay-1
│                    "Fires unconditionally ... deliberately does NOT suppress
│                     equal values" karena mutasi in-place akan terbanding sama
│                     dan "leave the UI stale" (Variable.cs:31-37)
│                    "Not thread-safe by design ... Marshal with MainQueue before
│                     setting from a background thread" (Variable.cs:12-15)
│
└── OPERATORS        DistinctUntilChanged · BatchOnMain(MainQueue)
                     "This is the only place de-duplication happens"
                     (Operators.cs:8-11)
                     BatchOnMain: "Citadel invention — no rpl precedent, nothing
                     in scope streams yet, so it ships unexercised"
                     (Operators.cs:28-30)
```

**Aturan komposisi seluruh app:** setiap `Subscribe` mengembalikan `Lifetime`, dan
`Subscribe(next, into)` menyerapnya ke pemilik (`Producer.cs:18-21`,
`EventStream.cs:40-43`). Jadi berlangganan dan berhenti berlangganan adalah
operasi yang sama: matikan lifetime.

**Siapa memakainya — ini idiom seluruh app, bukan detail internal Core.**
`using Citadel.Core.Rpl` muncul di: `Citadel.Contract/IModule.cs:2`;
`Citadel.Ui` (`ThemeResources.xaml.cs:3`, `Sidebar.cs:7`,
`AnimationManager.cs:2`, `Animation.cs:2`); `Citadel.Shell` (`App.xaml.cs:6`,
`Router.cs:6`, `ModuleGate.cs:4`, `MainWindow.xaml.cs:8`, `LayoutApplier.cs:6`,
`SettingsWindow.xaml.cs:4`, `BuiltInRoute.cs:3`); `setting/Screens` (6 layar);
**setiap citizen** (`BlankModule.cs:3`, `CamoprofModule.cs:3`, `FtfModule.cs:3`,
`ProxyModule.cs:3`, `MangaReaderModule.cs:3`, `MangaReaderView.xaml.cs:7`); dan
tests. Inilah alasan teknis kenapa `Citadel.Contract` menarik Core (D6): kontrak
citizen memakai `Lifetime` di tanda tangannya.

**Siapa memiliki Lifetime untuk sebuah view:** shell, bukan module.
`Router` membuat `new Lifetime()` tepat sebelum `CreateView`
(`Router.cs:190,195` jalur citizen; `Router.cs:226,230` jalur built-in),
mematikannya saat gagal (`Router.cs:211,246`), dan mematikan lifetime view
sebelumnya saat navigasi/dispose (`Router.cs:307,424`). Kontraknya menyebut ini
eksplisit: "the view's lifetime is supplied by the shell rather than owned by the
module" (`IModule.cs:7-8`).

### 1.3 `crl` — disiplin threading yang menjaga Core bebas WPF

Masalahnya: Core harus menyediakan "jalan di thread UI" tanpa mereferensikan WPF.
Penyelesaiannya adalah **membalik kepemilikan**:

```text
Citadel.Shell (tahu Dispatcher)
│   new MainQueue(wake: drain => Dispatcher.BeginInvoke(drain),
│                 isMain: () => Dispatcher.CheckAccess())     App.xaml.cs:65-67
▼
MainQueue  — "the queue does not own a dispatcher — the app injects the wake
│            delegate ..., which is what keeps Citadel.Core free of WPF types"
│            (MainQueue.cs:7-10)
│            jaminan: never inline · wake coalescing · drain ber-generation ·
│            FIFO (MainQueue.cs:13-17)
│            guarded post memeriksa lifetime.Alive SAAT eksekusi, jadi
│            "Stale background results never touch dead views" (MainQueue.cs:43-47)
│            wake gagal → flag direset, tidak macet permanen (MainQueue.cs:68-75)
▼
Crl (fasad statis) — "Post never runs inline, and Initialize takes the WPF-free
│            MainQueue instead of a Dispatcher" (Crl.cs:8-11)
│            Post sebelum Initialize → DIJATUHKAN dan DICATAT, tidak diam
│            (Crl.cs:59-63)
│            Run = "Blocking marshal ... Edge-case API only" (Crl.cs:24-26)
▼
Watchdog — ping 1 Hz ke MainQueue, catat stall
             "Citadel original, not a port: upstream's deadlock detector is
              opt-in, pings every 60 s, and crashes instead of logging"
             (Watchdog.cs:4-6)
             yang diukur = latensi ping→pong, BUKAN "waktu sejak pong terakhir"
             (Watchdog.cs:8-9); ping tidak pernah menumpuk (Watchdog.cs:16)

PowerSaving — flag global animasi mati
             "Polarity is v0's and is pinned by tests: Enabled == true means
              saving is ON" (PowerSaving.cs:9-10)
             "Snap, not freeze": animasi dilompatkan ke nilai akhir, tidak ada
             keadaan beku untuk dilanjutkan (PowerSaving.cs:3-6)
```

Dua kejujuran yang layak dicatat:

- **Celah sebelum `Initialize`:** `Crl.IsMain` mengembalikan `true` saat belum
  diinisialisasi (`Crl.cs:22`) dan `Run` kemudian mengeksekusi inline
  (`Crl.cs:30-34`) — default permisif yang melunakkan aturan "never inline"
  untuk jendela sebelum Initialize.
- **`Rpl` dan `Crl` saling import.** `Operators.cs:1` memakai `Citadel.Core.Crl`,
  sementara `Crl.cs:1` dan `MainQueue.cs:2` memakai `Citadel.Core.Rpl`. Sah secara
  compiler (satu assembly), tapi ini **siklus di tingkat namespace** — contoh
  literal "saling import" yang tidak dijaga gate mana pun (register C4).

### 1.4 `tokens` — subsistem tema, bebas WPF

Warna disimpan sebagai ARGB `uint` (`Tokens.cs:12`); konversi ke sisi WPF hidup di
`Citadel.Ui`'s `ThemeResources` (`ThemeResources.xaml.cs:29` `Bind(Tokens, Lifetime)`).

```text
        Defaults (tabel bawaan DI KODE, tak pernah di disk)
        │  "so a corrupt override file can never take the fallback with it"
        │  (Defaults.cs:4-5)
        ▼
   Resolve = preview ?? override ?? default
        │  "every token always resolves — there is no partial state"
        │  (Tokens.cs:84-88, 126-138)
        ▲
        │           Overrides (ui.json SPARSE, tulis atomik)
        │           "Absent key → default. Unknown key → ignored and logged.
        │            Invalid value → that one token dropped, rest kept.
        │            Reset a token = delete its key; reset all = delete the file"
        │           (Overrides.cs:8-11)
        │           "write a temp file beside the real one, flush it, then replace
        │            atomically" (Overrides.cs:40-42); File.Move lintas volume
        │            adalah copy, bukan atomik (Overrides.cs:101-102)
        │
        └── Guard dijalankan di SEMUA jalur mutasi (Tokens.cs:180, 225, 251, 368)
            "Runs on load, on theme activation, and on commit" (Guard.cs:8-9)
            putusan: Warned (kontras) · Clamped (ukuran) · Revert (bound) ·
            Refused (transparansi) (Guard.cs:12-18)
            "a rail wider than FullMax → revert both, because expanding must
             never narrow the sidebar" (Guard.cs:14-15)
            "A rejected edit must change nothing." (Tokens.cs:187-188)
            preview = "Transient resolved snapshot, never persisted"
            (Tokens.cs:99-101)
            saat drag: "the guard runs exactly once for the whole drag"
            (Tokens.cs:285-287)
```

| File | Pemilik atas | Catatan |
| --- | --- | --- |
| `Tokens.cs` | store hidup + sinyal perubahan | memuat **empat** tipe publik: `TokenKind` (:6), `TokenValue` (:12), `Tokens` (:90), `TokenCommitResult` (:486) → register D3 |
| `Defaults.cs` | tabel bawaan (11 metrik + 10 warna) | provenance: "Rail and FullMax come from v0's MainWindow.xaml.cs:62-65" (:5-6, file luar, tak terverifikasi di repo ini) |
| `Overrides.cs` | path store + `ui.json` atomik | **juga** memegang validasi properti layout `IsValidLayoutProperty` (:246-274) yang dipanggil `Tokens.CommitLayout` (`Tokens.cs:319`) → tiga pekerjaan, register D4 |
| `Guard.cs` | kebijakan sanitasi + math kontras | `ContrastRatio` (:181-187) adalah helper publik dari pekerjaan yang sama |
| `Theme.cs` | satu tema sparse | "One engine, two consumers: core chrome tokens and module screen layouts keyed by route" (:7-9) |
| `TokenPairs.cs` | 8 pasangan kontras + `MinContrast = 4.5` | "Contrast needs declared pairs — a flat list cannot infer that NavTitleFg is drawn on BgRail" (:4-5). Catatan: `NavTitleFg` bukan token di `Defaults.cs` — ilustratif. "Upstream silently adjusts (EnsureContrast); Citadel warns instead" (:5-8) |

Satu ketidakcocokan kata: `TokenPairs.cs:11` menulis "pairs enforced at launch"
padahal Guard hanya **memperingatkan** soal kontras (`Guard.cs:155-164`).
"Enforced" di sini berarti "diperiksa", bukan "diblokir" → register A4.

### 1.5 Log

Satu pekerjaan: logging process-wide dengan dua sink bernama (`Main`, `Modules`),
ring 2048 entri, dan thread penulis latar ke `log.txt` (`Log.cs:36-105`).

- "Entries never block the caller: they enqueue and the writer thread flushes
  batches to disk." (`Log.cs:8-9`)
- "Safe to call once; later calls are ignored. Entries logged before Start are
  staged" (`Log.cs:41-43`); gagal buka file bisa dicoba lagi, `Started` tetap
  false (`Log.cs:59-60`); ada guard reentrancy "never recurse from our own failure
  paths" (`Log.cs:109`).
- Lifecycle dimiliki shell: `Log.Start()` / `Log.Finish()` (`App.xaml.cs:62,218`).

**Mekanisme:** dominan **statis global** (`public static class Log`, `Log.cs:16`).
Hanya ada **satu** seam injeksi-callback di seluruh Core:
`Overrides.TryLoad(..., Action<string> warn)` (`Overrides.cs:137-141`), yang diisi
`Log.Main` oleh `Tokens.Load` (`Tokens.cs:362`). Tidak ada event/delegate sink pada
`Log` sendiri.

Bandingkan dengan searcher, yang justru **tidak** memakai `Log` dan menerima
callback (`Watcher.cs:66-78`, disuntik `Log.Modules` di `App.xaml.cs:151`). Jadi
dua disiplin logging hidup berdampingan: statis global di dalam core, injeksi di
batas proyek.

Pemanggil di luar Core: `Citadel.Shell` (App, Router, ModuleGate, ResidentShell,
SingleInstanceHost, SidebarGroupingStore, ShellSettingHost, SettingsWindow,
MainWindow, LayoutApplier), `setting` (DeclarationReader, PresetStore), dan tests
membaca ring lewat `Log.Full()`.

---

## 2. Citadel.Ui — chrome milik core, bukan gudang komponen

**Batas yang paling sering tertukar:** `Citadel.Ui` BUKAN gudang komponen bersama.
Gudang itu `setting/Components` (slice 3). `Citadel.Ui` adalah chrome navigasi
milik core + pompa animasi + jembatan token→WPF.

| Node | Pekerjaan | Pemilik |
| --- | --- | --- |
| SIDEBAR | render + animasi sidebar navigasi | `Controls/Sidebar.cs` |
| NAV ENTRY | model data entri sidebar | `Controls/NavEntry.cs` |
| RAIL BUTTON | satu gaya tombol ikon | `Controls/RailButton.cs` + `.xaml` + `.xaml.cs` |
| ANIMATION | satu interpolasi milik lifetime | `Animations/Animation.cs` |
| ANIMATION MANAGER | **satu** pompa frame-clock untuk semua animasi | `Animations/AnimationManager.cs` |
| EASINGS | dua kurva | `Animations/Easings.cs` |
| THEME RESOURCES | jembatan token→resource WPF + template shell/sidebar | `Theme/ThemeResources.xaml(.cs)` |
| GENERIC | merge dictionary sebagai tema default assembly | `Themes/Generic.xaml` |

Referensi: hanya `Citadel.Core` (`Citadel.Ui.csproj:8`); `InternalsVisibleTo` ke
`Citadel.Ui.Tests` dan `Citadel.Uia` (`:12-13`). `ThemeInfo(None, SourceAssembly)`
supaya WPF menemukan `Themes/Generic.xaml` (`Properties/AssemblyInfo.cs:3-5`).

### 2.1 Keputusan yang alasannya tertulis

- **Sidebar tidak pernah melihat registri.** "Selection is the route; no view,
  descriptor, registry, or module type crosses into Citadel.Ui"
  (`NavEntry.cs:4-5`); "Core-owned navigation chrome... no stage-4 registry is
  invented here" (`Sidebar.cs:14-21`).
- **`RouteSelected` hanya untuk pilihan user.** "Raised only for a user/list
  selection; setting SelectedRoute is silent" (`Sidebar.cs:163`) — inilah yang
  membuat `Router.Navigated → Sidebar.SelectedRoute` tidak berputar balik
  (`MainWindow.xaml.cs:244-249`).
- **Settings selalu disuplai kontrol, bukan data.** "Registered entries only;
  Settings is supplied by the control" (`Sidebar.cs:126`); `RebuildViews`
  memaksa menambah `NavEntry.Settings` dan membuang entri apa pun yang memakai
  route settings (`Sidebar.cs:310-313`). Ini penjaga kedua setelah
  `ModuleGate.ReservedRoutes` — kebijakan yang sama dijaga di dua tempat.
- **Satu langganan `CompositionTarget.Rendering` untuk semua animasi**, dan
  melepas saat tidak ada kerja; start/stop yang diminta dari dalam callback
  ditahan sampai pass selesai (`AnimationManager.cs:6-10`).
- **Power saving diperiksa di dalam `Tick`**, jadi mematikan motion menuntaskan
  animasi di frame berikutnya alih-alih meninggalkan nilai antara yang beku
  (`Animation.cs:7-9`, `71-74`) — konsisten dengan "Snap, not freeze"
  (`PowerSaving.cs:3-6`).
- **Dua kurva easing saja.** "More curves stay out until a real control needs
  one; importing lib_ui's easing catalogue here would be feature creep"
  (`Easings.cs:4-6`).

### 2.2 Tepi runtime Ui → Setting (tidak ada di graf compile-time)

`ThemeResources.xaml` punya **dua pekerjaan** dalam satu dictionary:

1. jembatan token→resource WPF yang hidup: "Replacing a dictionary value
   invalidates every DynamicResource consumer... no template takes a one-time
   StaticResource snapshot" (`ThemeResources.xaml.cs:8-11`), `Bind(Tokens, Lifetime)`
   (`:29`), `Apply` (`:50`), daftar NumberTokens/ColorTokens (`:15-23`);
2. konstanta layout shell + seluruh template sidebar: "Shell composition
   resources; these are not persisted tokens" (`ThemeResources.xaml:21-29`:
   `AppHeaderHeight`, `HeaderButtonSize`, dst.) dan gaya `ShellCardStyle`,
   `SidebarListItemStyle`, `SidebarListStyle`, `SidebarEntryTemplate`
   (`:70-293`).

Di dalam pekerjaan kedua itu ada tepi yang menarik: `SidebarListStyle`
menyelesaikan `SettingScrollViewerStyle` lewat `DynamicResource`, dengan komentar
jujur: "The shared scrollbar owner lives in Citadel.Setting, which this assembly
does not reference, so the style is resolved at runtime from the merged
application dictionaries. Where it is unavailable the reference sets nothing and
the default template remains in effect." (`ThemeResources.xaml:138-147`).

Artinya **graf dependensi runtime ≠ graf compile-time** yang dikunci hook: Ui
bergantung pada Setting saat runtime, dengan degradasi anggun, dan tidak ada
penegakan bahwa app benar-benar me-merge dictionary itu (merge terjadi di
`App.xaml:14-16`). → register C5.

---

## 3. Periferal shell

### 3.1 Titik masuk dan kepemilikan proses

```text
Program.Main                                   (Program.cs)
│  1. VelopackApp.Build().Run()                SEBELUM gate owner
│     "install/update/uninstall hooks launch this EXE with fast-exit arguments
│      that Velopack owns" (Program.cs:12-14)
▼
SingleInstanceHost.Start(args, exePath, timeout)   (Program.cs:32)
│  "Wins process ownership before WPF is initialized. A secondary process does
│   only one bounded named-pipe exchange and exits" (SingleInstanceHost.cs:36-38)
│  identitas = SHA256(user SID, session, exe path)
│            → mutex Local\Citadel.{id}.Owner + pipe Citadel.{id}.Activation
│            (SingleInstanceHost.cs:79-86)
│  bukan owner → kirim "SHOW", aktifkan window yang ada secara native, keluar
│            (SingleInstanceHost.cs:229, 267)
│  --reset-ui DITOLAK selagi app jalan (SingleInstanceHost.cs:101-110)
▼
new App(owner); app.InitializeComponent(); app.Run()   (Program.cs:45-47)
▼
App.OnStartup  → lihat CITADEL-FLOWS-CROSS.md §2.2
```

`SingleInstanceHost.cs` memuat **dua tipe**: `SingleInstanceHost` (:40) dan
`NativeWindowActivation` (:450-478, aktivasi foreground Win32) — berhubungan tapi
bisa dipisah → register D5.

### 3.2 Lifecycle window: close ≠ exit

| Perilaku | Pemilik | Bukti |
| --- | --- | --- |
| Ukuran & posisi awal: clamp ke work area monitor, center, **sekali** | `WindowBoundsPolicy.cs` (pure, stateless) diterapkan `MainWindow.ApplyStartupBounds` saat `OnSourceInitialized` | `WindowBoundsPolicy.cs:6,11-27,29-38`; `MainWindow.xaml.cs:138-168` |
| Ukuran awal **berhenti mengikuti token setelah load**, supaya edit token tak membatalkan resize manual user | `MainWindow` | `MainWindow.xaml:12-17`, ditegakkan `MainWindow.xaml.cs:130-135` (`if (!IsLoaded)`) |
| Close → batal + sembunyikan, kecuali exit diminta | `ResidentShell` | `ResidentShell.cs:105-113` |
| Hide/Show **tidak pernah** merobohkan Router, view lifetime, langganan token, atau theme resource | `ResidentShell` | `ResidentShell.cs:9-10` |
| Tray open → restore + activate | `ResidentShell` | `ResidentShell.cs:139-158` |
| Exit → dispose tray → dispose instance listener → Shutdown | `ResidentShell` | `ResidentShell.cs:119-137, 172-194` |
| `ShutdownMode.OnExplicitShutdown` hanya bila tray ada | `App` | `App.xaml.cs:118-121` |
| Tray gagal dibuat → degradasi jujur: "Close will exit" | `App` | `App.xaml.cs:105-109` |
| Tray tidak memegang state aplikasi apa pun | `TrayHost` | `TrayHost.cs:13` |
| Power saving mengikuti visibility window — **satu-satunya tempat** | `MainWindow` | `MainWindow.xaml.cs:19-26, 208-227` |
| Refresh berdasar visibility, menghindari false positive sebelum load | `MainWindow` | `MainWindow.xaml.cs:210-214` |

Tidak ada persistensi bounds: posisi/ukuran dihitung ulang tiap start (tidak
ditemukan penyimpan; di luar cakupan slice ini).

### 3.3 Pengelompokan sidebar

Tiga pemilik, tiga pekerjaan, dan **tidak ada persistensi ganda**:

| Pekerjaan | Pemilik |
| --- | --- |
| Data + disk (grup, keanggotaan, expanded) | `SidebarGroupingStore.cs` |
| Bentuk record yang menyeberang ke Setting | `SidebarGroup` di `setting/ISettingHost.cs:28-32` |
| UI edit grup | `setting/Screens/SidebarGroupsScreen.cs:12-20` |
| Merakit grup → `Sidebar.Entries` | `MainWindow.SyncSidebarEntries` (`MainWindow.xaml.cs:279-309`; anak grup yang collapsed dilewati di `:291`) |
| Toggle expand bolak-balik lewat host | `MainWindow.xaml.cs:231-237` |

Jaminannya tercatat: "Routes are opaque identities: this store never discovers,
creates, or owns module instances" (`SidebarGroupingStore.cs:10-11`). Tulis atomik
(tmp + flush + move, `:114-138`); path portable vs `%AppData%\Citadel\sidebar-groups.json`
(`:152-158`); satu grup per route ditegakkan (`:65`).

Catatan kecil: file ini `using Citadel.Core.Tokens` (`:4`) **hanya** untuk
`Overrides.PortableMarker` (`:155`) — tepi tipis yang mudah disalahartikan sebagai
ketergantungan pada subsistem tema.

### 3.4 Alur update app

Eksplisit, digerakkan user, **tanpa timer**: "It creates no timer or resident
worker" (`AppUpdateService.cs:28`).

```text
Settings UI
▼
ISettingHost.CheckForUpdates / InstallUpdate      (ISettingHost.cs:59-62)
▼
ShellSettingHost → AppUpdateController            (ShellSettingHost.cs:133-135)
│  satu operasi pada satu waktu (AppUpdateController.cs:44-78)
│  publikasi status/progress di dispatcher (AppUpdateController.cs:149-207)
▼
VelopackUpdateService.CheckAsync / DownloadAsync   (AppUpdateService.cs:81-177)
│  "Settings sees AppUpdateState; Velopack and the GitHub source never leak into
│   Setting, Core, or Contract" (AppUpdateService.cs:9-10)
│  repo dipin di :32; dev-build fallback menjaga kartu "useful and honest" (:70-77)
▼
100% → TryScheduleRestart = WaitExitThenApplyUpdates(restart:true)
│                                              (AppUpdateService.cs:189-193)
▼
controller memanggil requestExit (= App.Shutdown, disambung App.xaml.cs:88)
   di thread UI                              (AppUpdateController.cs:134-138)
```

Pemisahan service/controller adalah **pemisahan dua pekerjaan yang nyata**
(service = batas containment Velopack; controller = state machine thread-affine),
dan alasannya tercatat: "matching dhepz's service/controller split"
(`AppUpdateController.cs:7-9`).

Tapi satu pekerjaan ditulis dua kali: **membangun state kegagalan**. Service
menulis `Status = $"Update check failed: {ErrorText(exception)}"` + CanCheck +
CanInstall + Busy (`AppUpdateService.cs:124-133`), dan controller secara
terpisah menulis `Status = $"{prefix}: {message}"` + CanCheck + CanInstall + Busy
(`AppUpdateController.cs:162-175`). Dua bentuk `AppUpdateState` kegagalan yang
hampir identik dari dua lapisan → register B/C (duplikasi, C7).

### 3.5 SettingsWindow

Satu pekerjaan: "One reusable, modeless editor window for Settings' three
sub-screens" (`SettingsWindow.xaml.cs:12`). Keputusan yang menarik: ThemeResources-nya
**sengaja dibiarkan default** alih-alih bind ke token store target, supaya edit
Appearance tidak bisa mengubah gaya editor-nya sendiri (`:14-16`; `new ThemeResources()`
tak ter-bind di `:26`).

Dua catatan:

- Klaim "three sub-screens" **basi**: `ShellSettingHost.PopupRoute` memetakan
  **empat** route — Appearance, Layout, Gallery, SidebarGroups
  (`ShellSettingHost.cs:217-233`). `App.xaml.cs:170-172` bahkan menyebut dua angka
  dalam satu komentar: "Its three reserved sub-screens ... none of the four names
  passes through the module gate", sementara `ModuleGate.ReservedRoutes` berisi
  **lima** entri (`ModuleGate.cs:46-53`). → register A2.
- XAML-nya meng-hardcode ukuran 760/720/640/520 (`SettingsWindow.xaml:7-10`)
  sementara `MainWindow.xaml:12-17` melarang literal ukuran. Larangan itu memang
  khusus MainWindow, tapi dua window ini berdisiplin berbeda.

---

## 4. Jembatan `ISettingHost`: seam yang membuat Setting tidak perlu tahu Shell

```text
setting/                          core/Citadel.Shell/
┌──────────────────────┐          ┌────────────────────────┐
│ ISettingHost.cs      │◀─────────│ ShellSettingHost.cs    │
│  (kontrak + record)  │  imple-  │  (mengisi seam)        │
│                      │  men     │                        │
│ Screens              │          │ + AttachSearcher(...)  │ internal,
│ Failures             │          │ + NotifyChanged        │ BUKAN di
│ RequestRediscovery   │          │ + OpenWindow/Close     │ interface
│ UpdateState          │          │ + Detach               │
│ CheckForUpdates      │          └───────────┬────────────┘
│ InstallUpdate        │                      │ satu-satunya file Shell
│ OpenSettings(route)  │                      │ selain App yang
│ SidebarGroups + CRUD │                      │ meng-import Citadel.Searcher
│ Changed              │                      │ (ShellSettingHost.cs:2-6)
│ SidebarGroupsChanged │
└──────────────────────┘
   hanya referensi Core + Contract (Citadel.Setting.csproj:14-15;
   "not Shell, not the searcher, not module/", :5-7)
```

Alasannya tercatat dua kali, dari dua sisi:

- "Settings must not reference Shell or the searcher... So it declares the shape
  and App fills it in" (`setting/ISettingHost.cs:38-41`).
- "`Citadel.Setting` owns the interface and references only Core and Contract,
  while Shell — which knows both — supplies the data. Settings therefore never
  references Shell or the searcher" (`ShellSettingHost.cs:14-17`).

**Dua daftar kegagalan digabung, tidak saling disembunyikan:** "the gate refuses
duplicate and reserved routes and knows nothing about folders, while the searcher
fails folders... Neither is hidden behind the other" (`ShellSettingHost.cs:18-22`);
`Failures()` (`:102-117`) menyambung kegagalan gate lalu kegagalan searcher.

**Akses searcher sengaja internal, tidak dilebarkan ke interface:** "An internal
setter rather than a wider ISettingHost... the searcher stays invisible to it"
(`ShellSettingHost.cs:74-79`).

Konsumen: `setting/Screens/{SettingsScreen:29,38, ModuleLayoutScreen:26,35,
GalleryScreen:28,43, SidebarGroupsScreen:12,20}` (semua lewat injeksi konstruktor),
`MainWindow.xaml.cs:32,44` (parameter opsional), `App.xaml.cs:177`
(`BuiltInRoutes(ISettingHost)`), dan test double `tests/Citadel.Uia/StubSettingHost.cs:11`.

**Catatan peran:** file ini memegang tiga peran — jembatan `ISettingHost`, pemilik
`SettingsWindow` (`:151-171`), dan pabrik `AppUpdateController` (`:59-62`).
Semuanya berdekatan dengan "mengisi seam", tapi namanya tidak menyebut dua peran
tambahan itu → register D5.

---

## 5. Yang sudah cocok dengan konsep kernel yuz-ui

| Konsep yuz-ui | Padanan citadel | Bukti |
| --- | --- | --- |
| Domain zero-dependency | `Citadel.Core` tidak mereferensikan apa pun, dan itu terbukti dari csproj 9 baris | `Citadel.Core.csproj:1-9` |
| Host menyuntik kemampuan platform | `MainQueue` menerima delegate `wake`; Core tak pernah melihat Dispatcher | `MainQueue.cs:7-10`, `App.xaml.cs:65-67` |
| Lifecycle kontrak kelas satu (R2 yuz-ui) | `Lifetime` adalah idiom kepemilikan seluruh app; kebocoran jadi mustahil secara struktural | `Lifetime.cs:4-6` |
| Satu pemilik loop | satu langganan `CompositionTarget.Rendering` untuk semua animasi, lepas saat nol kerja | `AnimationManager.cs:6-10` |
| Kontrak sebagai satu-satunya pintu antar sisi | `ISettingHost`: Setting mendeklarasikan bentuk, Shell mengisi | `ISettingHost.cs:38-41`, `ShellSettingHost.cs:14-17` |
| Kebijakan dijaga di pemiliknya | Guard jalan di semua jalur mutasi token; resolve selalu lengkap | `Guard.cs:8-9`, `Tokens.cs:126-138` |
| Batas ditegakkan mesin, bukan niat | hook graf proyek memblokir; `VerifyCitizenIsolation` menggagalkan build | `check-project-refs.mjs:15-32`, `Citizen.targets:142-151` |

---

## 6. Rujukan register dari slice ini

A2 (angka sub-screen/reserved route basi) · A4 ("enforced" vs "warn") ·
C4 (`Rpl` ↔ `Crl` saling import) · C5 (tepi runtime Ui→Setting) ·
C7 (state kegagalan update ditulis dua lapisan) ·
D2 (`PowerSaving.cs` di root, namespace `.Crl`) · D3 (`Tokens.cs` empat tipe publik) ·
D4 (`Overrides.cs` tiga pekerjaan) · D5 (`SingleInstanceHost.cs` dua tipe;
`ShellSettingHost.cs` tiga peran) · D6 (Contract tidak tipis).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.
