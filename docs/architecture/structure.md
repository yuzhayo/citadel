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
(`module/blank/Module.Blank.csproj:9-11`, `module/Citizen.targets:14-16`).

## 2. Skala repo (terukur 2026-09-11, tanpa `bin/`, `obj/`, `artifacts/`)

| Wilayah | file cs/xaml | baris | file py | Peran |
| --- | --- | --- | --- | --- |
| `module/mangareader` | 183 | 36.703 | 4 | citizen penuh |
| `tests` | 108 | 22.896 | 1 | jaring pengaman |
| `setting` | 46 | 5.698 | 0 | gudang komponen bersama + layar setting |
| `core/Citadel.Shell` | 19 | 3.471 | 0 | composition root + shell |
| `module/camoprof` | 26 | 2.972 | 6 | citizen sedang |
| `core/Citadel.Core` | 16 | 1.959 | 0 | fondasi bebas WPF |
| `module/Citadel.Searcher` | 5 | 1.358 | 0 | mesin discovery |
| `module/sharedLogic` | 3 | 1.238 | 11 | payload bersama (cs + python) |
| `core/Citadel.Ui` | 12 | 1.172 | 0 | chrome navigasi milik core |
| `module/ftf` | 3 | 61 | 0 | **kerang kosong** |
| `module/proxy` | 3 | 61 | 0 | **kerang kosong** |
| `module/blank` | 3 | 44 | 0 | template |
| `core/Citadel.Contract` | 5 | 133 | 0 | kontrak citizen |
| `module/credenz` | 0 | 0 | 0 | **bukan citizen** — vault kredensial ±499 MB |
| `tools` | 0 | 0 | 1 | rilis |

Total repo: 432 file cs/xaml + 65 file py.

**Yang langsung terbaca dari angka ini:** 63% baris kode produksi citadel ada di
satu citizen (`mangareader`). `ftf` dan `proxy` masing-masing 61 baris — mereka
ada sebagai screen terdaftar, tapi tidak punya isi (§6).

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
│   module/Citadel.Searcher   discovery ← TINGGAL DI FOLDER PLUGIN      │
│   module/Citizen.targets    kontrak build citizen ← JUGA DI module/   │
└───────────────────────────────┬───────────────────────────────────────┘
                                │  folder hadir = terdaftar
                                ▼
┌───────────────────────────────────────────────────────────────────────┐
│ PLUGIN  (screen drop-in; semuanya boleh dihapus bersih)               │
│                                                                       │
│   module/blank         template 44 baris                              │
│   module/mangareader   36.703 baris, 183 file                         │
│   module/camoprof      2.972 baris, 26 file                           │
│   module/ftf           61 baris  — kerang                             │
│   module/proxy         61 baris  — kerang                             │
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
│   tests/              11 proyek test                                   │
│   tools/ · Releases/ · artifacts/ · .github/    kemasan & rilis        │
└───────────────────────────────────────────────────────────────────────┘
```

Catatan penting: **kernel citadel tinggal di dua rumah.** Empat proyek di `core/`,
tapi mesin discovery (`Citadel.Searcher`) dan kontrak build (`Citizen.targets`)
tinggal di dalam `module/` — folder yang didokumentasikan sebagai tempat plugin.
Alasannya tertulis dan masuk akal ("shared by every screen, owned by none",
`module/Citadel.Searcher/Citadel.Searcher.csproj:4-6`), tapi akibatnya `module/`
tidak bisa dibaca sebagai "semuanya boleh dihapus" (register D1).

## 4. Pohon struktur dengan peran per folder

```text
citadel/
├─ AGENTS.md                  kontrak agent: skills first, code second
├─ Citadel.slnx               18 proyek — CITIZEN TIDAK TERMASUK
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
│   ├─ Citadel.Searcher/      Watcher (satu-satunya wajah publik) · Reader ·
│   │                         Loader · ModuleManifest · ModuleFailure
│   ├─ Citizen.targets        referensi + deploy + verifikasi isolasi citizen
│   ├─ blank/                 TEMPLATE: 6 file, semuanya yang dibutuhkan screen
│   ├─ mangareader/           Components · CoverBuilder · History · Library
│   │                         (+Grouping, UpdateChecker) · Reader (+ReaderCore,
│   │                         Features) · Features/{Catalog, CatalogMirror,
│   │                         Downloader, Rar} · shareLogic/{Sources, Archive}
│   ├─ camoprof/              Providers(+Google) · Network · Launcher · Runtime ·
│   │                         Features/{AddProfile, ProfileActions} · sharedLogic
│   ├─ ftf/                   FtfModule/FtfView + folder feature KOSONG
│   ├─ proxy/                 ProxyModule/ProxyView + folder feature KOSONG
│   ├─ sharedLogic/           cs/ (di-compile ke citizen) · pyhost/ (+providers) ·
│   │                         reference/ · tests/
│   └─ credenz/               VAULT: google/accounts, google/profiles (gitignored)
│
├─ tests/                     Citadel.{Core,Ui,Uia,Uia.Citizen,
│                             Uia.Citizen.Dependency}.Tests +
│                             Module.Mangareader.{Archive,Downloader,History,
│                             Library,Reader}.Tests + Module.Camoprof.Tests
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
| registry sumber (daftar `new` eksplisit) | 1 | `MangaSourceRegistry.cs:93-105` |
| manifest build citizen (NuGet baru) | 1 | sah untuk dependensi baru |
| **manifest sumber proyek test (4 entri manual)** | 1 | **overhead enumerasi** |
| bump versi rilis | 1 | sah |

Dan biaya itu berskala: **149 entri `Compile Include` manual** lintas enam proyek
test, 66 di antaranya di satu csproj. Setiap file feature baru menuntut entri baru.

**Inilah jawaban atas keluhan owner** ("aku lihat banyak file berubah saat menambah
fitur, seharusnya cuma file fitur itu sendiri"): bukan saling import antar module —
itu terkunci mesin dan nol pelanggaran — melainkan **registrasi feature berbasis
enumerasi manual** di dalam citizen, yang tidak dijaga gate apa pun.

Rinci: `CITADEL-VIOLATIONS.md` B1, B2, B3, C3.

## 6. Kematangan tiap citizen

| Citizen | Isi nyata | Route | Gate mengompilasi? | Test? |
| --- | --- | --- | --- | --- |
| `mangareader` | penuh: 183 file, 36.703 baris, 4 feature + Reader + Library + History | `mangareader` | ya, lewat 5 proyek test yang me-link sumbernya | 5 proyek test |
| `camoprof` | sedang: 26 file, 2.972 baris, 2 feature + Providers/Launcher/Runtime | `camoprof` | ya, lewat 1 proyek test | 1 proyek test |
| `blank` | template: 6 file, 44 baris | `blank` | ya, `Citadel.Uia` mereferensikannya | dipakai sebagai citizen uji |
| `ftf` | **kerang**: 3 file cs/xaml (61 baris), folder `Backend`, `Features/{FaucetRun, GSuite, Profiles, Results}` **kosong di git** | `ftf` | **tidak** | **tidak ada** |
| `proxy` | **kerang**: 3 file cs/xaml (61 baris), folder `Features/{Pool, Settings, Sync}` **kosong di git** | `proxy` | **tidak** | **tidak ada** |
| `credenz` | **bukan citizen**: tanpa csproj, tanpa `module.json` | — | — | — |

### Soal `ftf` dan `proxy` — fakta yang perlu owner tahu

Yang ter-track git untuk masing-masing hanya 6 file: `*Module.cs`, `*View.xaml`,
`*View.xaml.cs`, `*.csproj`, `layout.json`, `module.json` (diverifikasi
`git ls-files`, 2026-09-11). Isi code-behind-nya 9 baris dengan komentar
"feature behavior belongs in feature folders" (`FtfView.xaml.cs:5-9`,
`ProxyView.xaml.cs:5-9`) — niatnya benar, foldernya belum ada isinya.

Tapi di disk ada **sisa kerja python yang tidak pernah masuk git**:

```text
module/ftf/Features/FaucetRun/ftf_plugin/__pycache__/adapter.cpython-312.pyc
module/ftf/Features/FaucetRun/ftf_plugin/__pycache__/pipeline.cpython-312.pyc
module/ftf/Features/FaucetRun/ftf_plugin/tests/__pycache__/test_pipeline.cpython-312-pytest-8.4.1.pyc
module/proxy/Features/Pool/proxy_plugin/__pycache__/pool.cpython-312.pyc
module/proxy/Features/Pool/proxy_plugin/tests/__pycache__/test_pool.cpython-312-pytest-8.4.1.pyc
```

`.pyc` ada, `.py` sumbernya **tidak ada di disk dan tidak pernah ada di riwayat git**
(`git log --diff-filter=D -- module/ftf module/proxy` kosong). `.gitignore:52-54`
mengabaikan `__pycache__/` dan `*.pyc`, dan `.pytest_cache/` mengabaikan dirinya
sendiri lewat `.gitignore` yang dibuat pytest — itulah sebabnya `git status`
tampak bersih.

Artinya: ada kerja python untuk FTF faucet-pipeline dan Proxy pool yang pernah
dijalankan dan diuji di mesin ini, lalu sumbernya hilang; yang tersisa hanya
cache terkompilasi. **Ini bukan kebocoran rahasia**, tapi ini pekerjaan yang
hilang dan tidak terlihat dari `git status`. → register D9.

`layout.json` ftf mendeklarasikan slot `LauncherPanel`, `EditorPanel`,
`RuntimePanel` (`module/ftf/layout.json:1-7`) yang namanya tidak berhubungan dengan
folder feature-nya (`Backend`, `FaucetRun`, `GSuite`, `Profiles`, `Results`) — jejak
bahwa layout ditulis untuk rencana yang berbeda dari folder yang akhirnya dibuat.

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
| Regresi C# | `.agents/hooks/gate-on-stop.mjs` (Stop) | melaporkan, tidak memblokir | hanya 18 proyek di `Citadel.slnx` |
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
4. **Dua citizen berupa kerang** dengan sisa kerja python yang hilang dari git,
   dan satu folder vault kredensial 499 MB tinggal di dalam `module/`.
5. **Sejumlah klaim angka di dokumen/komentar sudah basi** (jumlah tipe kontrak,
   jumlah sub-screen Settings, "XAML tidak di-link"), semuanya salah ke arah yang
   sama: fitur masuk tanpa memperbarui klaim di sekitarnya.
