# CITADEL-STRUCTURE — peta global

Dokumen induk untuk membaca citadel lewat nama, tanpa membaca kode. Ditulis
2026-09-11 dari kode dan git, bukan dari plan lama.

Dokumen turunan:

| Dokumen | Isi |
| --- | --- |
| `CITADEL-FLOWS-CROSS.md` | flow lintas module: discovery, registrasi, navigasi, lapisan penegak batas |
| `CITADEL-FLOWS-CORE.md` | `Citadel.Core` (rpl/crl/tokens/Log), `Citadel.Ui`, periferal shell, jembatan `ISettingHost` |
| `CITADEL-FLOWS-*.md` per module | flow dalam tiap citizen |
| `CITADEL-VIOLATIONS.md` | register penyimpangan ber-bukti `file:baris` + bentuk lurusnya |
| `CITADEL-KERNEL-MAP.md` | pemetaan ke konsep kernel yuz-ui |
| `CITADEL-INVENTORY.md` | checklist cakupan; penjamin "tidak ada yang tertinggal" |

---

## 1. Apa citadel, satu paragraf

Aplikasi desktop Windows modular (.NET 10, WPF), tinggal di tray, dan **menemukan
screen-nya dari disk saat runtime**: setiap folder di `<output>\module\` yang punya
`module.json` adalah satu screen ("citizen") yang dimuat ke dalam
`AssemblyLoadContext` collectible-nya sendiri. Menambah screen = menyalin folder +
`dotnet build`; tidak ada satu pun file di luar `module/` yang berubah
(`module/blank/Module.Blank.csproj:11`, `core/Citizen.targets`).

## 2. Skala repo (terukur, tanpa `bin/`, `obj/`, `artifacts/`)

| Wilayah | file cs/xaml | baris | file py | Peran |
| --- | --- | --- | --- | --- |
| `module/mangareader` | 229 | 45.348 | 4 | citizen penuh |
| `tests` | 143 | 29.627 | 1 | jaring pengaman |
| `setting` | 50 | 5.989 | 0 | gudang komponen bersama + layar setting |
| `module/yuzvid` | 26 | 6.051 | 6 | citizen sedang - browser + queue |
| `core/Citadel.Shell` | 26 | 5.744 | 0 | composition root + shell |
| `module/agentrouter` | 24 | 3.892 | 4 | citizen sedang - 3 feature |
| `module/camoprof` | 27 | 3.161 | 6 | citizen sedang |
| `module/proxy` | 23 | 2.309 | 0 | citizen sedang - 4 feature |
| `core/Citadel.Core` | 15 | 1.865 | 0 | fondasi bebas WPF |
| `module/sharedLogic` | 6 | 1.661 | 12 | payload bersama (cs + python) |
| `core/Citadel.Searcher` | 5 | 1.358 | 0 | mesin discovery |
| `core/Citadel.Ui` | 12 | 1.172 | 0 | chrome navigasi milik core |
| `core/Citadel.Contract` | 9 | 322 | 0 | kontrak citizen |
| `module/blank` | 3 | 44 | 0 | template |
| `module/credenz` | 0 | 0 | 0 | **bukan citizen** — vault kredensial ±499 MB |
| `tools` | 0 | 0 | 1 | rilis |

Total repo: 598 file cs/xaml + 34 file py. Angka `baris` adalah baris fisik file
(`wc -l`), bukan baris berisi — jadi bisa diverifikasi langsung dengan membuka
filenya. Cara yang sama dipakai di seluruh `docs/`.

**Yang langsung terbaca dari angka ini:** `mangareader` memuat 45.348 dari 78.916
baris kode produksi (tanpa `tests/`) — **57%**. Empat citizen sisanya
(`yuzvid` 6.051, `agentrouter` 3.892, `camoprof` 3.161, `proxy` 2.309) berbagi
sisanya. `proxy` dan `agentrouter` adalah citizen sedang; keduanya berisi (§6).

## 3. Peta peran: mana kernel, mana plugin

Ini lensa terpenting, dan ia **tidak** sama dengan pembagian folder di disk.

```text
┌───────────────────────────────────────────────────────────────────────┐
│ KERNEL  (mekanisme: menemukan, memuat, mendaftarkan, menavigasi)      │
│                                                                       │
│   core/Citadel.Contract     kontrak citizen (5 tipe publik)           │
│   core/Citadel.Core         fondasi bebas WPF: rpl · crl · tokens · Log│
│   core/Citadel.Shell        composition root, gate, router, layout    │
│   core/Citadel.Ui           chrome navigasi + pompa animasi + tema    │
│   core/Citadel.Searcher     discovery: watcher, loader, manifest      │
│   core/Citizen.targets      kontrak build citizen                     │
└───────────────────────────────┬───────────────────────────────────────┘
                                │  folder hadir = terdaftar
                                ▼
┌───────────────────────────────────────────────────────────────────────┐
│ PLUGIN  (screen drop-in; semuanya boleh dihapus bersih)               │
│                                                                       │
│   module/blank         template 44 baris                              │
│   module/mangareader   45.348 baris, 229 file                         │
│   module/camoprof      3.161 baris, 27 file                           │
│   module/agentrouter   3.892 baris, 24 file                           │
│   module/yuzvid        6.051 baris, 26 file                           │
│   module/proxy         2.309 baris, 23 file                           │
└───────────────────────────────────────────────────────────────────────┘

┌───────────────────────────────────────────────────────────────────────┐
│ GUDANG BERSAMA  (bukan kernel, bukan plugin)                          │
│                                                                       │
│   setting/            komponen UI reusable + layar setting bawaan      │
│   module/sharedLogic  sumber cs (di-compile ke citizen) + pyhost       │
└───────────────────────────────────────────────────────────────────────┘

┌───────────────────────────────────────────────────────────────────────┐
│ BUKAN KODE                                                            │
│   module/credenz      vault kredensial ±499 MB (gitignored, 2 file     │
│                       ter-track) — TIDAK punya csproj, bukan citizen    │
│   tests/              13 folder test (11 di antaranya proyek)         │
│   tools/ · Releases/ · artifacts/ · .github/    kemasan & rilis        │
└───────────────────────────────────────────────────────────────────────┘
```

Catatan: **kernel seluruhnya tinggal di `core/`.** Mesin discovery
(`core/Citadel.Searcher`) dan kontrak build (`core/Citizen.targets`) ikut pindah ke
`core/` di commit `a313f06` (step 13 / D1), jadi `module/` murni berisi citizen dan
payload bersama dan bisa dibaca sebagai "semuanya boleh dihapus".

## 4. Pohon struktur dengan peran per folder

```text
citadel/
├─ AGENTS.md                  kontrak agent: skills first, code second
├─ Citadel.slnx               17 proyek — CITIZEN TIDAK TERMASUK
├─ Directory.Build.props      TargetFramework net10.0-windows + default
├─ version.props · global.json
├─ .agents/
│   ├─ hooks/
│   │   ├─ check-project-refs.mjs   MENEBLOKIR grafis dependensi csproj
│   │   └─ gate-on-stop.mjs         dotnet test saat turn berakhir (melaporkan)
│   └─ skills/                2 kontrak Citadel yang di-version-kan
├─ docs/                      SATU-SATUNYA pohon dokumentasi
│   ├─ architecture/          peta as-built + flow
│   ├─ contracts/             kontrak perilaku aktif
│   ├─ governance/            review dan remediation
│   ├─ operations/            release + smoke procedure
│   ├─ work/                  plan/checklist aktif
│   └─ history/               plan, audit, report, riset terdahulu
│
├─ core/
│   ├─ Citadel.Contract/      IModule · IModuleGate · ModuleDescriptor ·
│   │                         LayoutDeclaration · IContentHeaderActionProvider
│   ├─ Citadel.Core/          rpl/ (Lifetime, EventStream, Producer, Variable,
│   │                         Operators) · crl/ (MainQueue, Crl, Watchdog) ·
│   │                         tokens/ (Tokens, Defaults, Overrides, Guard, Theme,
│   │                         TokenPairs) · Log.cs · PowerSaving.cs
│   ├─ Citadel.Ui/            Controls/ (Sidebar, NavEntry, RailButton) ·
│   │                         Animations/ (Animation, AnimationManager, Easings) ·
│   │                         Theme/ (ThemeResources) · Themes/Generic.xaml
│   ├─ Citadel.Searcher/      Watcher (satu-satunya wajah publik) · Reader ·
│   │                         Loader · ModuleManifest · ModuleFailure
│   ├─ Citizen.targets        referensi + deploy + verifikasi isolasi citizen
│   └─ Citadel.Shell/         App (composition root) · Program · ModuleGate ·
│                             Router · LayoutApplier · BuiltInRoute ·
│                             ShellSettingHost · MainWindow · SettingsWindow ·
│                             ResidentShell · TrayHost · SingleInstanceHost ·
│                             WindowBoundsPolicy · SidebarGroupingStore ·
│                             AppUpdateController · AppUpdateService
│
├─ setting/                   GUDANG KOMPONEN BERSAMA
│   ├─ Components/            SettingButton/Field/PasswordField/Toggle/Slider/
│   │                         Tabs/Viewport/ActionCard/Table/TableActions/
│   │                         Dialog/ScrollBar/Drawer/WindowChrome/List/Combo
│   ├─ Screens/               Settings · Appearance · ModuleLayout · Gallery ·
│   │                         SidebarGroups
│   ├─ LayoutEditor/          editor Gallery + .presets.json
│   ├─ ISettingHost.cs        seam: Setting mendeklarasikan bentuk, Shell mengisi
│   └─ SettingResources.xaml  style/token/template bersama
│
├─ module/
│   ├─ blank/                 TEMPLATE: 6 file, semuanya yang dibutuhkan screen
│   ├─ mangareader/           Components · CoverBuilder · History · Library
│   │                         (+Grouping, UpdateChecker) · Reader (+ReaderCore,
│   │                         Features) · Features/{Catalog, CatalogMirror,
│   │                         Downloader, Rar} · shareLogic/{Sources, Archive}
│   ├─ camoprof/              Providers(+Google) · Network · Launcher · Runtime ·
│   │                         Features/{AddProfile, ProfileActions} · sharedLogic
│   ├─ agentrouter/           AgentrouterModule/View + Features/{Claim, Proxy,
│   │                         Shortcuts} + sharedLogic/ + tests/
│   ├─ yuzvid/                YuzvidModule/View + Features/{Browser, Extraction,
│   │                         Queue, Runtime} + tests/ + WIKI.md
│   ├─ proxy/                 ProxyModule/ProxyView + Features/{Pool, Sync,
│   │                         Webshare, Settings} + sharedLogic/ + tests/
│   ├─ sharedLogic/           cs/ (di-compile ke citizen) · pyhost/ (+providers) ·
│   │                         reference/ · tests/
│   └─ credenz/               VAULT: google/accounts, google/profiles (gitignored)
│
├─ tests/                     Citadel.{Core,Ui,Uia,Uia.Citizen,
│                             Uia.Citizen.Dependency}.Tests +
│                             Module.Mangareader.{Archive,Downloader,Engines,
│                             History,Library,Reader}.Tests +
│                             Module.Camoprof.Tests + Module.Proxy.Tests
└─ tools/ · Releases/ · artifacts/ · .github/workflows/
```

## 5. Dua mekanisme registrasi — temuan pusat

Citadel punya **dua** tingkat "child mendaftar ke parent", dan keduanya berperilaku
sangat berbeda.

### Tingkat 1 — module → shell: SUDAH berbentuk kernel

```text
folder module/<screen>/ hadir
   + module.json valid
        ▼
   Watcher melihat (FileSystemWatcher, debounce 250 ms, retry ≤4×)
        ▼
   Loader memuat ke ALC collectible sendiri
        ▼
   IModuleGate.Register(descriptor)   ← core MENDENGARKAN, tidak mencari
        ▼
   sidebar + router tahu screen baru
```

- Menambah screen: salin `module/blank/`, ganti nama, `dotnet build`. **Nol edit di
  luar `module/`** — citizen bahkan tidak masuk `Citadel.slnx`.
- Menghapus screen: hapus folder. Unregister otomatis (`Watcher.cs:629-660`).
- Screen rusak: terisolasi. Pump tetap hidup (`Watcher.cs:370-375`), `CreateView`
  dibungkus di Router (`Router.cs:182-215`), app tetap jalan.
- **Ini persis D-PLUGIN yuz-ui**, dan ia dijaga mesin (§7).

### Tingkat 2 — feature → module: BELUM berbentuk kernel

```text
feature folder hadir
        ▼
   ...tidak ada yang melihat...
        ▼
   seseorang harus MENULIS namanya di daftar
```

Bukti terukur (commit `21977e5`, menambah satu provider manga = **9 file**):

| Jenis sentuhan | Jumlah | Catatan |
| --- | --- | --- |
| file feature itu sendiri | 4 | inilah yang seharusnya |
| test feature | 1 | sah |
| registry sumber (daftar `new` eksplisit) | 1 | `MangaSourceRegistry.cs:110-156` |
| manifest build citizen (NuGet baru) | 1 | sah untuk dependensi baru |
| **manifest sumber proyek test (4 entri manual)** | 1 | **overhead enumerasi** |
| bump versi rilis | 1 | sah |

Biaya itu tidak lagi berskala di jalur Downloader: csproj test-nya memakai glob per
folder feature (`Module.Mangareader.Downloader.Tests.csproj:60-61`), jadi provider
baru tidak menuntut entri csproj. Yang **masih** manual: **80 entri
`Compile Include`** lintas tujuh proyek test, terbesar 26 di
`Module.Mangareader.Library.Tests` (nol wildcard). Di proyek-proyek itu setiap file
baru masih menuntut entri baru.

**Inilah jawaban atas keluhan owner** ("aku lihat banyak file berubah saat menambah
fitur, seharusnya cuma file fitur itu sendiri"): bukan saling import antar module —
itu terkunci mesin dan nol pelanggaran — melainkan **registrasi feature berbasis
enumerasi manual** di dalam citizen, yang tidak dijaga gate apa pun.

Rinci: `CITADEL-VIOLATIONS.md` B1, B2, B3, C3.

## 6. Kematangan tiap citizen

| Citizen | Isi nyata | Route | Gate mengompilasi? | Test? |
| --- | --- | --- | --- | --- |
| `mangareader` | penuh: 229 file, 45.348 baris, 4 feature + Reader + Library + History | `mangareader` | ya — 6 proyek test me-link sumbernya, 5 di antaranya di `Citadel.slnx` (`Engines.Tests` tidak) | 6 proyek test |
| `camoprof` | sedang: 27 file, 3.161 baris, 2 feature + Providers/Launcher/Runtime | `camoprof` | ya, lewat `Module.Camoprof.Tests` | 1 proyek test |
| `blank` | template: 3 file, 44 baris | `blank` | ya, `Citadel.Uia` mereferensikannya | dipakai sebagai citizen uji |
| `proxy` | sedang: 23 file cs/xaml (2.309 baris), `Features/{Pool, Sync, Webshare, Settings}` + `sharedLogic/` | `proxy` | ya lewat CI `Build citizens`; **tidak** di `Citadel.slnx` | 8 file di `tests/Module.Proxy.Tests`, tapi **tidak** ikut gate |
| `agentrouter` | sedang: 24 file cs/xaml (3.892 baris) + 4 py, `Features/{Claim, Proxy, Shortcuts}` + `sharedLogic/` | `agentrouter` | ya lewat CI `Build citizens`; **tidak** di `Citadel.slnx` | **tidak ada** proyek test C#; guard python tidak ikut gate |
| `yuzvid` | sedang: 26 file cs/xaml (6.051 baris) + 6 py, `Features/{Browser, Extraction, Queue, Runtime}` | `yuzvid` | **tidak sama sekali** — tidak di `Citadel.slnx`, tidak di langkah CI | 3 file test python; **tidak** ikut gate |
| `credenz` | **bukan citizen**: tanpa csproj, tanpa `module.json` | — | — | — |

### Soal `proxy`

27 file ter-track. `ProxyView.xaml.cs` 73 baris dan mengimpor empat namespace
feature (`:3-6`) — composition root, bukan code-behind kosong. Isinya
`Features/{Pool, Sync, Webshare, Settings}` + `sharedLogic/` (`ProxyPoolStore`,
`ProxySettings`, `ProxySettingsStore`, `AtomicTextFile`), dan
`tests/Module.Proxy.Tests` memuat 8 file test termasuk `ProxyLevelBGuardTests` —
proyek itu tidak ada di `Citadel.slnx`, jadi gate tidak menjalankannya.

Satu bagian plugin python-nya tidak ada di pohon kerja:
`module/proxy/Features/Pool/proxy_plugin/` hanya menyisakan `__pycache__`, nol file
`.py`. Sumbernya ada di `artifacts/recovery-ftf-proxy-2.1.3-20260907/proxy_plugin/`
(`__init__`, `plugin`, `pool`), dan `artifacts/` di-ignore `.gitignore:5` sehingga
nol file-nya ter-track. Jadi plugin pool yang dipakai saat ini berada di luar
version control — bisa dipulihkan, tapi butuh keputusan owner. → register D9.

### Soal `credenz`

Bukan citizen: tanpa `.csproj`, tanpa `module.json`, jadi `Reader.IsCitizenFolder`
menyatakan "bukan citizen" dan searcher melewatinya tanpa kegagalan
(`Reader.cs:37-38`, `Watcher.cs:477-485`). Isinya vault kredensial nyata
(`google/accounts/<id>/{identity.json,password.dat}`, `google/profiles/<id>/…`
±499 MB). **Aman di git:** `.gitignore:35-45` mengecualikannya berlapis dan hanya
2 file ter-track (`README.md`, `google/profiles/.gitkeep`). Tapi ia tinggal di
dalam folder yang didokumentasikan sebagai tempat screen drop-in → register D1.

## 7. Lapisan penegak: apa yang dijaga mesin

| Lapisan | Penegak | Sifat | Jangkauan |
| --- | --- | --- | --- |
| Graf dependensi antar proyek | `.agents/hooks/check-project-refs.mjs` (PostToolUse) | **memblokir** | hanya elemen `ProjectReference` pada csproj yang disunting |
| Isolasi assembly citizen | `Citizen.targets:142-151` `VerifyCitizenIsolation` | **build gagal** | assembly `Citadel.*` di folder citizen ter-deploy |
| Regresi C# | `.agents/hooks/gate-on-stop.mjs` (Stop) | melaporkan, tidak memblokir | hanya 17 proyek di `Citadel.slnx` — nol citizen |
| Kompilasi citizen | `.github/workflows/ci.yml` langkah `Build citizens` | **CI gagal** | lima dari enam citizen; `yuzvid` tidak ada di daftar |
| Route reserved/duplikat | `ModuleGate.cs:107-118` | runtime, dicatat & ditampilkan di Settings | route |
| Identitas route vs manifest | `Loader.cs:117-127` | runtime, folder gagal | route |
| Kontrak perilaku shared UI | `.agents/skills/citadel-shared-ui/SKILL.md` | **prosa** | — |
| Kontrak feature modularity | `.agents/skills/citadel-feature-modularity/SKILL.md` | **prosa** | — |

**Hasil pemeriksaan graf aktual vs diizinkan: identik, nol pelanggaran**
(`CITADEL-FLOWS-CROSS.md` §2.3). Yang dijaga prosa — dua skill kontrak itu —
adalah persis lapisan tempat gejala owner muncul.

## 8. Cara owner menavigasi: "kalau mau tahu X, buka Y"

| Pertanyaan | Buka |
| --- | --- |
| Bagaimana screen baru dikenali app? | `CITADEL-FLOWS-CROSS.md` §1 |
| Kenapa menambah fitur menyentuh banyak file? | `CITADEL-VIOLATIONS.md` B1–B3, C3 |
| Apa yang boleh di-import proyek mana? | `CITADEL-FLOWS-CROSS.md` §2.3 |
| Apa yang terjadi saat app dinyalakan? | `CITADEL-FLOWS-CROSS.md` §2.2 |
| Siapa pemilik komponen UI bersama? | `CITADEL-FLOWS-SETTING.md` (slice 3) |
| Bagaimana python dipanggil dari C#? | `CITADEL-FLOWS-SHAREDLOGIC.md` (slice 4) |
 | `CITADEL-FLOWS-CROSS.md` §1.7 untuk sisi deploy |
| Kenapa tema/layout bisa diubah user? | `CITADEL-FLOWS-CORE.md` §1.4 |
| Apa itu Lifetime dan kenapa ada di mana-mana? | `CITADEL-FLOWS-CORE.md` §1.2 |
| Apakah screen rusak bisa menjatuhkan app? | `CITADEL-FLOWS-CROSS.md` §1.6, §2.5 |
| Seberapa besar tiap bagian? | dokumen ini §2 |
| Bagian mana yang belum ada isinya? | dokumen ini §6 |
| Apa yang seharusnya kernel, apa yang plugin? | `CITADEL-KERNEL-MAP.md` |

## 9. Ringkasan diagnosis (sementara, akan diuji slice 3–8)

1. **Tingkat module ↔ shell sudah mencapai bentuk kernel yuz-ui.** Folder hadir =
   terdaftar; nol edit di luar `module/`; fault isolation tiga lapis; graf
   dependensi dikunci mesin dan bersih.
2. **Tingkat feature ↔ module belum.** Registrasi feature adalah daftar manual
   (registry + manifest test), tidak ada discovery, dan tidak ada penegak mesin.
   Inilah sumber "banyak file tersentuh".
3. **Kontrak citizen tidak tipis.** `Citadel.Contract` menarik seluruh
   `Citadel.Core` demi satu tipe (`Lifetime`), jadi tidak ada permukaan sempit
   yang bisa jadi satu-satunya pintu — dan hook justru mengesahkan tepi ini.
4. **Semua citizen berisi.** `proxy` dan `agentrouter` adalah citizen sedang dengan
   feature nyata. Dua catatan struktural yang masih terbuka: sumber `proxy_plugin`
   berada di luar git (`artifacts/`), dan satu folder vault kredensial 499 MB
   tinggal di dalam `module/`.
5. **Sejumlah klaim angka di dokumen/komentar sudah basi** (jumlah tipe kontrak,
   jumlah sub-screen Settings, "XAML tidak di-link"), semuanya salah ke arah yang
   sama: fitur masuk tanpa memperbarui klaim di sekitarnya.
