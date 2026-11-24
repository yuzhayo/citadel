## Keadaan cakupan

Slice 1–8 (dokumentasi per wilayah) dan slice 9 (sintesis) selesai. Deliverable di
`docs/`:

| Dokumen | Isi |
| --- | --- |
| `CITADEL-STRUCTURE.md` | peta global: peran, skala, dua mekanisme registrasi, kematangan citizen, lapisan penegak, panduan navigasi owner |
| `CITADEL-FLOWS-CROSS.md` | §1 discovery module · §2 shell/registrasi/navigasi/lapisan penegak batas |
| `CITADEL-FLOWS-CORE.md` | Citadel.Core (rpl/crl/tokens/Log) · Citadel.Ui · periferal shell · jembatan ISettingHost |
| `CITADEL-FLOWS-SETTING.md` | gudang komponen bersama · persistensi preferensi · LayoutEditor/preset · layar setting · audit klaim |
| `CITADEL-FLOWS-SHAREDLOGIC.md` | jembatan pyhost · self-registration plugin python · flow AddProfile 20 langkah |
| `CITADEL-FLOWS-MANGAREADER.md` | Reader · Downloader · parent module · Library · History · CatalogMirror · shareLogic · **BUG-1** |
| `CITADEL-FLOWS-CITIZENS.md` | camoprof · proxy · agentrouter · blank · credenz · tests · tools/rilis |
| `CITADEL-VIOLATIONS.md` | register: bagian pertama (slice 1–2) + bagian kedua (slice 3–8) + diagnosis diperbarui |
| `CITADEL-KERNEL-MAP.md` | pemetaan kernel/plugin/children · tiga pola terbukti · urutan pelurusan |

Isi register: **1 BUG** (defek nyata) · **13 A** (klaim tertulis yang tidak lagi
benar) · **7 B** (biaya perubahan terukur, termasuk 1 entri positif) · **15 C**
(batas yang tidak dijaga mesin) · **26 D** (ketegangan struktur & navigasi).

### Fakta dasar repo

- **CatalogMirror sudah ter-commit** (`8a772b6`, disusul `cf04cb8`), bukan WIP.
- **`Citadel.slnx` berisi 17 proyek** (6 core/setting/searcher/shell + 11 test).
  **Tidak ada citizen di dalamnya** — konsekuensinya register C6.
- **`module/credenz` bukan citizen**: tanpa csproj, tanpa `module.json`;
  `Reader.IsCitizenFolder` melewatinya tanpa kegagalan.
- **`module/proxy`** 27 file ter-track: `Features/{Pool, Sync, Webshare, Settings}`
  + `sharedLogic/` (`ProxyPoolStore`, `ProxySettings`, `AtomicTextFile`),
  `ProxyView.xaml.cs` 66 baris. `tests/Module.Proxy.Tests` berisi 8 file test
  termasuk `ProxyLevelBGuardTests`, tapi **tidak ada di `Citadel.slnx`**.
  `Features/Pool/proxy_plugin/` hanya menyisakan `__pycache__` (0 file `.py`);
  sumbernya ada di `artifacts/recovery-ftf-proxy-2.1.3-20260907/proxy_plugin/`
  → D9.
- **`module/agentrouter`** 31 file ter-track, 3 feature (`Claim`, `Proxy`,
  `Shortcuts`), tanpa proyek test C#, guard python tidak ikut gate.
- **`module/mangareader/Features/` punya empat feature**: Catalog, CatalogMirror,
  Downloader, Rar. `Library/` punya dua sub-feature: Grouping, UpdateChecker.


---

## Cross-cutting flows

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Kontrak citizen publik (empat tipe) | core/Citadel.Contract | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.1, §1.9; register D6, A1, D7 |
| Pencarian & pemuatan module (manifest, watcher, failure) | core/Citadel.Searcher | SELESAI - `CITADEL-FLOWS-CROSS.md` §1 |
| Katalog & registrasi module ke shell | core/Citadel.Shell | SELESAI — `CITADEL-FLOWS-CROSS.md` §2.4 |
| Shell lifecycle & composition root | core/Citadel.Shell | SELESAI — `CITADEL-FLOWS-CROSS.md` §2.2; `CITADEL-FLOWS-CORE.md` §3.1 |
| Runtime inti (token, navigation, tray) | core/Citadel.Core | SELESAI — `CITADEL-FLOWS-CORE.md` §1, §3.2 |
| Layout, sidebar, navigasi antar tab/module | core/Citadel.Shell + setting/LayoutEditor | SELESAI dua sisi — shell: `CITADEL-FLOWS-CROSS.md` §2.5–§2.6, `CITADEL-FLOWS-CORE.md` §3.3; setting/LayoutEditor: `CITADEL-FLOWS-SETTING.md` §4 |
| Composition shared UI (setting/Components vs module Components) | setting/Components | SELESAI |
| Theme & resources (SettingResources.xaml) | setting/ | SELESAI |
| UI preference persistence (ui-preferences.json) | setting/Components | SELESAI |
| Window chrome & fluid desktop layout | setting/Components/WindowChrome | SELESAI |
| PyHost bridge (python browser plugin) | module/sharedLogic/pyhost | SELESAI |
| Setting host contract (ISettingHost) | setting/ISettingHost.cs | SELESAI |
| Helper cs bersama | module/sharedLogic/cs | SELESAI |

## core/ per proyek dan lapisan penegak (ditambahkan setelah slice 2)

| Flow | Pemilik | Status |
| --- | --- | --- |
| `rpl` — Lifetime, EventStream, Producer, Variable, Operators | core/Citadel.Core/rpl | SELESAI — `CITADEL-FLOWS-CORE.md` §1.2 |
| `crl` — MainQueue, Crl, Watchdog, PowerSaving | core/Citadel.Core/crl | SELESAI — §1.3 |
| `tokens` — Tokens, Defaults, Overrides, Guard, Theme, TokenPairs | core/Citadel.Core/tokens | SELESAI — §1.4 |
| Log (dua sink, ring, thread penulis) | core/Citadel.Core/Log.cs | SELESAI — §1.5 |
| Citadel.Ui: Sidebar, NavEntry, RailButton | core/Citadel.Ui/Controls | SELESAI — §2.1 |
| Citadel.Ui: Animation, AnimationManager, Easings | core/Citadel.Ui/Animations | SELESAI — §2.1 |
| Citadel.Ui: ThemeResources, Generic.xaml | core/Citadel.Ui/Theme(s) | SELESAI — §2.2 |
| Program + SingleInstanceHost (kepemilikan proses) | core/Citadel.Shell | SELESAI — §3.1 |
| ResidentShell + TrayHost (close vs exit) | core/Citadel.Shell | SELESAI — §3.2 |
| WindowBoundsPolicy + MainWindow (bounds, power saving) | core/Citadel.Shell | SELESAI — §3.2 |
| SidebarGroupingStore | core/Citadel.Shell | SELESAI — §3.3 |
| AppUpdateController + AppUpdateService (Velopack) | core/Citadel.Shell | SELESAI — §3.4 |
| SettingsWindow | core/Citadel.Shell | SELESAI — §3.5 |
| ISettingHost + ShellSettingHost (jembatan Setting↔Shell) | setting/ + core/Citadel.Shell | SELESAI — §4 |
| Hook `check-project-refs.mjs` (graf dependensi, memblokir) | .agents/hooks | SELESAI — `CITADEL-FLOWS-CROSS.md` §2.7 |
| Hook `gate-on-stop.mjs` (test saat turn berakhir, melaporkan) | .agents/hooks | SELESAI — §2.7 |
| `Citadel.slnx` (17 proyek; citizen tidak termasuk) | root | SELESAI — §2.7, register C6 |

## module/mangareader

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Library scan | mangareader/Library | SELESAI |
| Library grouping | mangareader/Library/Grouping | SELESAI |
| Library update checker | mangareader/Library/UpdateChecker | SELESAI |
| History | mangareader/History | SELESAI |
| Catalog (katalog online) | mangareader/Features/Catalog | SELESAI |
| CatalogMirror (katalog offline SQLite) | mangareader/Features/CatalogMirror | SELESAI |
| Downloader queue + worker + CBZ | mangareader/Features/Downloader | SELESAI |
| Rar/archive feature | mangareader/Features/Rar | SELESAI |
| Archive extraction | mangareader/shareLogic/Archive | SELESAI |
| Sources (Comix, Drake, Cucumber, dkk) | mangareader/shareLogic/Sources | SELESAI |
| Reader window & composition root | mangareader/Reader | SELESAI |
| Reader infrastructure (ReaderCore) | mangareader/Reader/ReaderCore | SELESAI |
| Reader features (ChapterLoading, Zoom, Dim, dkk) | mangareader/Reader/Features | SELESAI |
| CoverBuilder | mangareader/CoverBuilder | SELESAI |
| Navigasi tab & composition MangaReaderView | mangareader/MangaReaderView.xaml(.cs) | SELESAI |
| Components bersama module (MangaTitleCard dkk) | mangareader/Components | SELESAI |

## module/camoprof

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Providers (termasuk Google) | camoprof/Providers | SELESAI |
| Network / account health | camoprof/Network | SELESAI |
| Launcher | camoprof/Launcher | SELESAI |
| Runtime | camoprof/Runtime | SELESAI |
| Feature AddProfile | camoprof/Features/AddProfile | SELESAI |
| Feature ProfileActions | camoprof/Features/ProfileActions | SELESAI |
| sharedLogic milik module | camoprof/sharedLogic | SELESAI |
| View composition CamoprofView | camoprof/ | SELESAI |

## module/agentrouter

| Flow | Pemilik | Status |
| --- | --- | --- |
| Feature Claim | agentrouter/Features/Claim | ada — `IAgentRouterClaimService`, pyhost client |
| Feature Proxy | agentrouter/Features/Proxy | ada |
| Feature Shortcuts | agentrouter/Features/Shortcuts | ada — pointer ke profil CamoProf |
| sharedLogic (ShortcutCatalog, ProfileIdentity, credential store) | agentrouter/sharedLogic | ada |
| Guard arsitektur | agentrouter/tests | ada, python — **tidak** ikut gate |

Tidak ada proyek test C#. Citizen ini **belum punya flow document** — cakupan
dokumentasi untuknya masih terbuka.

## module/proxy

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Feature Pool | proxy/Features/Pool | SELESAI — `proxy_plugin/` hanya `__pycache__`; sumber di `artifacts/` |
| Feature Settings | proxy/Features/Settings | SELESAI |
| Feature Sync | proxy/Features/Sync | SELESAI |
| Feature Webshare | proxy/Features/Webshare | SELESAI — di luar cakupan `IMPLEMENTATION-PLAN.md:7` |
| sharedLogic (store/settings/atomic file) | proxy/sharedLogic | SELESAI |
| View composition ProxyView | proxy/ | SELESAI |

## module/credenz

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Google accounts | credenz/google/accounts | SELESAI |
| Google profiles | credenz/google/profiles | SELESAI |

## module/sharedLogic

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| PyHost client & session | sharedLogic/pyhost | SELESAI |
| PyHost providers (python) | sharedLogic/pyhost/providers | SELESAI |
| Reference & tests python | sharedLogic/reference, sharedLogic/tests | SELESAI |

## setting/

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| LayoutEditor (gallery/preset editor) | setting/LayoutEditor | SELESAI |
| Screens setting bawaan | setting/Screens | SELESAI |
| ISettingHost contract | setting/ISettingHost.cs | SELESAI |

## module/blank dan Citadel.Searcher

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Template module blank | module/blank | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.8 |
| Searcher: loader/watcher/reader/manifest/failure | core/Citadel.Searcher | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.1–§1.6 |
| Citizen.targets (kontrak build module) | core/Citizen.targets | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.7 |

## tests/ dan tools/

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Citadel.Core.Tests | tests/ | SELESAI |
| Citadel.Ui.Tests | tests/ | SELESAI |
| Citadel.Uia + Citizen + Citizen.Dependency | tests/ | SELESAI |
| Module.Camoprof.Tests | tests/ | SELESAI |
| Module.Mangareader.{Archive,Downloader,Engines,History,Library,Reader}.Tests | tests/ | SELESAI |
| Module.Proxy.Tests (8 file, termasuk Level-B guard) | tests/ | SELESAI — **tidak** ada di `Citadel.slnx` |
| Packaging & release (Build-Release.ps1, workflows) | tools/, .github/workflows | SELESAI |

## Yang tersisa di luar cakupan dokumentasi ini

Dicatat jujur supaya "tanpa tertinggal" tidak berarti "tanpa batas".

| Area | Keadaan |
| --- | --- |
| `docs/history/2026/` | plan/audit/spec/report terdahulu; dibaca sebagai **niat sejarah**, perilaku diverifikasi dari kode |
| `.agents/skills/` | hanya dua kontrak Citadel yang di-version-kan; skill generik lokal tidak termasuk repo |
| `docs/work/`, `docs/history/2026/hermes/` | plan aktif Add Profile dan satu arsip plan harness; tidak lagi menjadi folder root terpisah |
| Isi `artifacts/recovery-ftf-proxy-2.1.3-20260907` | 71 file, termasuk `proxy_plugin/{__init__,plugin,pool}.py` — sumber plugin python `module/proxy` yang tidak ada di git. `artifacts/` di-ignore `.gitignore:5` |
| `.qoder/settings.json` + `settings.local.json` | dibaca untuk menemukan dua hook; tidak dipetakan lebih jauh |
| Perilaku runtime nyata (app dijalankan) | **tidak ada verifikasi live/visual**. Seluruh dokumentasi berbasis kode + git, sesuai batas read-only pada brief |
| Isi `module/credenz` (identity.json, password.dat, profil browser) | **sengaja tidak dibuka**. Hanya nama, jumlah, dan siapa yang membaca/menulis path-nya yang dicatat |

## Cara melanjutkan bila session baru

1. Baca `governance/task-brief.md`.
2. Baca dokumen ini — status cakupan ada di sini.
3. Baca `architecture/structure.md` §9 untuk diagnosis, lalu
   `governance/violations.md` bagian "Ringkasan diagnosis (diperbarui)".
4. Bila owner memerintahkan perbaikan: mulai dari `architecture/kernel-map.md` §8
   (urutan berbasis kerusakan yang sudah terjadi), **bukan** dari urutan abjad register.
5. **BUG-1 belum bisa diklaim selesai.** Fix-nya sempat dibuat lalu di-revert di
   `30dd644` ("reverts my BUG-1 'fix'") untuk memulihkan safety net throttle.
   Keadaan kode 2026-11-22: `CatalogMirrorSyncFeature.cs` punya 7 `catch`, enam
   mem-publish `Failed(...)`, satu sengaja menelan dan didokumentasikan sebagai
   best-effort (`:427-430`). Rantai bukti asalnya di
   `architecture/flows-mangareader.md` §14.
