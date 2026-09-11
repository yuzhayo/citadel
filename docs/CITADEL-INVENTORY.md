## Keadaan 2026-09-11: seluruh slice SELESAI

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
| `CITADEL-FLOWS-CITIZENS.md` | camoprof · ftf · proxy · blank · credenz · tests · tools/rilis |
| `CITADEL-VIOLATIONS.md` | register: bagian pertama (slice 1–2) + bagian kedua (slice 3–8) + diagnosis diperbarui |
| `CITADEL-KERNEL-MAP.md` | pemetaan kernel/plugin/children · tiga pola terbukti · urutan pelurusan |

Isi register: **1 BUG** (defek nyata) · **13 A** (klaim tertulis yang tidak lagi
benar) · **7 B** (biaya perubahan terukur, termasuk 1 entri positif) · **15 C**
(batas yang tidak dijaga mesin) · **26 D** (ketegangan struktur & navigasi).

### Koreksi fakta terhadap inventaris awal

- **CatalogMirror BUKAN WIP.** Sudah ter-commit: `8a772b6 feat(mangareader): add
  independent offline catalog`, disusul `cf04cb8 test(mangareader): align catalog
  schema expectations`. `REPORT-catalog-mirror-handoff-2026-09-08.md` adalah keadaan
  per tanggal itu, bukan WIP yang harus dikejar.
- **Working tree bersih** kecuali `docs/`. HEAD: `7fb0b13 Persist shared UI preferences`.
- **`Citadel.slnx` berisi 17 proyek** (6 core/setting/searcher/shell + 11 test),
  bukan 18. **Tidak ada citizen di dalamnya** — konsekuensinya register C6.
- **`module/credenz` bukan citizen**: tanpa csproj, tanpa `module.json`;
  `Reader.IsCitizenFolder` melewatinya tanpa kegagalan.
- **`module/ftf` dan `module/proxy` kerang**: masing-masing hanya 6 file ter-track,
  folder feature kosong di git, hanya `.pyc` dan hantu `obj/` yang tersisa → D9.
- **`module/mangareader/Features/` punya empat feature**, bukan dua: Catalog,
  CatalogMirror, Downloader, Rar. `Library/` punya dua sub-feature: Grouping,
  UpdateChecker.


---

## Cross-cutting flows

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Kontrak citizen publik (empat tipe) | core/Citadel.Contract | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.1, §1.9; register D6, A1, D7 |
| Pencarian & pemuatan module (manifest, watcher, failure) | module/Citadel.Searcher | SELESAI — `CITADEL-FLOWS-CROSS.md` §1 |
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
| `Citadel.slnx` (18 proyek; citizen tidak termasuk) | root | SELESAI — §2.7, register C6 |

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

## module/ftf

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Backend | ftf/Backend | SELESAI |
| Feature FaucetRun | ftf/Features/FaucetRun | SELESAI |
| Feature GSuite | ftf/Features/GSuite | SELESAI |
| Feature Profiles | ftf/Features/Profiles | SELESAI |
| Feature Results | ftf/Features/Results | SELESAI |
| View composition FtfView | ftf/ | SELESAI |

## module/proxy

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Feature Pool | proxy/Features/Pool | SELESAI |
| Feature Settings | proxy/Features/Settings | SELESAI |
| Feature Sync | proxy/Features/Sync | SELESAI |
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
| Searcher: loader/watcher/reader/manifest/failure | module/Citadel.Searcher | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.1–§1.6 |
| Citizen.targets (kontrak build module) | module/Citizen.targets | SELESAI — `CITADEL-FLOWS-CROSS.md` §1.7; pelanggaran V3 |

## tests/ dan tools/

| Flow | Pemilik dugaan awal | Status |
| --- | --- | --- |
| Citadel.Core.Tests | tests/ | SELESAI |
| Citadel.Ui.Tests | tests/ | SELESAI |
| Citadel.Uia + Citizen + Citizen.Dependency | tests/ | SELESAI |
| Module.Camoprof.Tests | tests/ | SELESAI |
| Module.Mangareader.{Archive,Downloader,History,Library,Reader}.Tests | tests/ | SELESAI |
| Packaging & release (Build-Release.ps1, workflows) | tools/, .github/workflows | SELESAI |

## Yang tersisa di luar cakupan dokumentasi ini

Dicatat jujur supaya "tanpa tertinggal" tidak berarti "tanpa batas".

| Area | Keadaan |
| --- | --- |
| `.docs/` (19 PLAN/AUDIT/SPEC/REPORT) | dibaca sebagai **niat sejarah** sesuai brief; perilaku diverifikasi dari kode, bukan dipercaya dari plan. Tidak didokumentasikan ulang per file |
| `.agents/skills/` selain 2 skill citadel | 7 skill generik (api-and-interface-design, code-review-and-quality, git-workflow-and-versioning, incremental-implementation, mengquality, planning-and-task-breakdown, test-driven-development) — tidak spesifik citadel, tidak dipetakan |
| `.agents/bridge/` | catatan jembatan antar harness (`hermes-codex.md`, `qoder-codex.md`) — tidak dipetakan |
| `tasks/`, `.hermes/` | artefak perencanaan harness lain; `tasks/todo.md:30` menyebut credenz |
| Isi `artifacts/recovery-ftf-proxy-2.1.3-20260907` | **unverified** — hanya keberadaannya yang dicatat; relevan untuk memulihkan D9 |
| `.qoder/settings.json` + `settings.local.json` | dibaca untuk menemukan dua hook; tidak dipetakan lebih jauh |
| Perilaku runtime nyata (app dijalankan) | **tidak ada verifikasi live/visual**. Seluruh dokumentasi berbasis kode + git, sesuai batas read-only pada brief |
| Isi `module/credenz` (identity.json, password.dat, profil browser) | **sengaja tidak dibuka**. Hanya nama, jumlah, dan siapa yang membaca/menulis path-nya yang dicatat |

## Cara melanjutkan bila session baru

1. Baca `CITADEL-TASK-BRIEF.md`.
2. Baca dokumen ini — status cakupan ada di sini.
3. Baca `CITADEL-STRUCTURE.md` §9 untuk diagnosis, lalu `CITADEL-VIOLATIONS.md`
   bagian "Ringkasan diagnosis (diperbarui)".
4. Bila owner memerintahkan perbaikan: mulai dari `CITADEL-KERNEL-MAP.md` §8
   (urutan berbasis kerusakan yang sudah terjadi), **bukan** dari urutan abjad register.
5. BUG-1 adalah satu-satunya defek fungsional yang ditemukan; ia didokumentasikan
   dengan rantai bukti enam mata rantai di `CITADEL-FLOWS-MANGAREADER.md` §14 dan
   `CITADEL-VIOLATIONS.md`. Belum ada perbaikan yang dilakukan — fase ini read-only.
