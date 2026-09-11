# CITADEL-KERNEL-MAP — apa yang seharusnya kernel, plugin, dan children

Pemetaan konsep kernel yuz-ui ke citadel. **Tanpa perubahan kode.** Ditulis
2026-09-11 setelah slice 1–8, jadi setiap pemetaan didukung bukti `file:baris` dari
dokumen flow.

Sumber konsep: `C:\VSCODE\yuz-ui\README.md` (D-CONTRACT, D-PLUGIN),
`C:\VSCODE\yuz-ui\DIAGRAM-LURUS.md` §1.

---

## 1. Kesimpulan satu paragraf

Citadel **sudah punya kernel**, dan kernel itu bekerja: `Citadel.Core` adalah daun
sejati tanpa satu pun referensi, `Citadel.Shell` adalah composition root yang
mendengarkan, dan `Citadel.Searcher` menemukan plugin lewat kehadiran folder. Yang
belum berbentuk kernel adalah **satu tingkat di bawahnya**: hubungan feature↔module
di dalam tiap citizen. Di tingkat itu tidak ada discovery, tidak ada katalog yang
seragam, dan tidak ada penegak mesin — dan di situlah BUG-1 serta gejala "banyak file
tersentuh" berasal.

Kabar baiknya: **pola yang dibutuhkan sudah terbukti tiga kali di repo ini** (§6).
Perbaikan bukan merintis, melainkan memindahkan.

---

## 2. Tiga tingkat, tiga keadaan

```text
TINGKAT 1 — app ↔ module            SUDAH berbentuk kernel   ✅ dijaga mesin
   kernel: Core + Contract + Shell + Searcher + Citizen.targets
   plugin: blank · mangareader · camoprof · ftf · proxy
   bukti:  CITADEL-FLOWS-CROSS.md §1, §2.3, §2.4
   penjaga: check-project-refs.mjs (memblokir) · VerifyCitizenIsolation (build gagal)

TINGKAT 2 — module ↔ feature        BELUM berbentuk kernel   ❌ hanya prosa
   parent: MangaReaderView (412 baris) · CamoprofView (114 baris) · ReaderWindow (165 baris)
   bukti:  CITADEL-FLOWS-MANGAREADER.md §10 · CITADEL-FLOWS-CITIZENS.md §1.1
   penjaga: TIDAK ADA (register C3)
   pengecualian yang membuktikan bisa: Reader/ (§6.1)

TINGKAT 3 — feature ↔ feature       CAMPURAN                 ❌ tidak dijaga
   baik:  nol tepi feature→feature di Reader (semua lewat 4 hub)
          nol provider mereferensikan provider lain di Downloader
   buruk: siklus shareLogic/Archive ↔ Features/Rar (C13)
          CatalogMirror → Downloader.Sources.Comix (C14) → BUG-1
          parent menjangkau 10 internal CatalogMirror (C14)
```

---

## 3. Pemetaan konsep

| Konsep yuz-ui | Padanan citadel hari ini | Keadaan |
| --- | --- | --- |
| `core/` = kernel | `core/Citadel.Core` + `Citadel.Contract` + `Citadel.Shell` | **ada**, tapi tersebar di tiga proyek dan Contract tidak tipis (D6) |
| kernel boot tanpa plugin | shell start + window tampil + Settings lengkap **sebelum** searcher jalan; nol citizen = app utuh | **terbukti** (`App.xaml.cs:29-32,125-133`) |
| `contracts.ts` satu-satunya permukaan plugin | `Citadel.Contract` — tapi ia menarik seluruh `Citadel.Core` | **ada tapi bocor** (D6): citizen punya akses sah ke 16 tipe Core |
| `plugins/*/index.ts` glob-by-convention | folder `module/<screen>/` + `module.json` | **terbukti** (`Reader.cs:37-38`, `Watcher.cs:477-485`) |
| parent mendengarkan, tidak mencari | `IModuleGate` ("Core listens; it never searches") | **terbukti** (`IModuleGate.cs:3-5`, `ModuleGate.cs:20-23`) |
| tambah/hapus plugin = tambah/hapus folder, nol edit core | salin `module/blank`, `dotnet build`; citizen bahkan tidak masuk `.slnx` | **terbukti** (`Module.Blank.csproj:9-11`, `Citizen.targets:14-16`) |
| sibling saling asing | citizen ↔ citizen: nol tepi. feature ↔ feature: **tidak dijaga** | setengah (C3, C13, C14) |
| fault isolation | **tiga lapis**: pump searcher · `CreateView` dibungkus Router · animasi navigasi | **terbukti** (`Watcher.cs:370-375`, `Router.cs:182-215,347-351`) |
| ESLint memaksa matriks import | `check-project-refs.mjs` — **hanya tingkat `ProjectReference`** | sebagian (C1, C2, C8) |
| satu file = satu pekerjaan | dilanggar luas di tingkat module (D22, D20, D17, D12) | sebagian |
| children mendaftar dengan ada | **hanya di lapisan python**: `install(host)` + `register_commands` | terbukti di pyhost, tidak di C# (B5) |
| deploy matrix sebagai acceptance | citizen di luar `.slnx` + `Build-Release.ps1` membangun tiap citizen | **ada**, tapi gate Stop tidak menutupi `ftf`/`proxy`/`blank` (C6) |

---

## 4. Apa yang seharusnya kernel, plugin, children

### 4.1 Kernel (mekanisme — tidak boleh tahu isi plugin)

| Sekarang tinggal di | Seharusnya dibaca sebagai |
| --- | --- |
| `core/Citadel.Core` | fondasi: `rpl` (kepemilikan/reaktif), `crl` (threading), `tokens` (tema+layout), `Log`, `PowerSaving` |
| `core/Citadel.Contract` | kontrak citizen — **seharusnya daun sejati**, kini menarik Core (D6) |
| `core/Citadel.Shell` | composition root, gate, router, layout applier, resident/tray, update |
| `core/Citadel.Ui` | chrome navigasi milik kernel: sidebar, rail button, pompa animasi, jembatan tema |
| **`module/Citadel.Searcher`** | **kernel** — mesin discovery. Kini tinggal di folder plugin (D1) |
| **`module/Citizen.targets`** | **kernel** — kontrak build plugin. Kini tinggal di folder plugin (D1) |
| **`module/sharedLogic/pyhost/`** | **kernel runtime kedua** — host plugin python. Kini tinggal di folder plugin, dan di-deploy sebagai sibling `module\` justru supaya tidak terlihat sebagai citizen (`Citizen.targets:87-94`) |
| `setting/` | **bukan kernel**: gudang bersama. Ia setara `plugins/display` dalam hal "dipakai semua", tapi ia tidak menemukan atau memuat apa pun |

**Catatan penting:** tiga bagian kernel tinggal di dalam `module/`, folder yang
didokumentasikan sebagai tempat plugin. Itulah sebabnya `module/` tidak bisa dibaca
sebagai "semuanya boleh dihapus" (D1). Dalam istilah yuz-ui: `core/` dan `plugins/`
harus **saudara**, bukan induk-anak.

### 4.2 Plugin (screen drop-in)

`module/blank`, `module/mangareader`, `module/camoprof`, `module/ftf`,
`module/proxy`. Semuanya boleh dihapus bersih — **kecuali** bahwa menghapus
camoprof/mangareader juga menuntut bersih-bersih di `module/sharedLogic/tests/`
(C10) dan, untuk camoprof, di `sharedLogic/BrowserSessionCoordinator.cs:399` (D23).

`module/credenz` **bukan plugin**: tanpa csproj, tanpa `module.json`, dan
`Reader.IsCitizenFolder` melewatinya tanpa kegagalan. Ia vault data (D1).

### 4.3 Children (yang seharusnya mendaftar ke parent-nya)

Citadel punya **empat** populasi children, dan hanya satu yang sudah benar-benar
mendaftar:

| Populasi children | Parent-nya | Keadaan sekarang |
| --- | --- | --- |
| citizen | shell (`IModuleGate`) | **mendaftar lewat kehadiran folder** ✅ |
| plugin python | `_Host` pyhost | **mendaftar lewat `install(host)`** ✅ (`pyhost.py:155-171`) |
| feature Reader | `ReaderFeatureHost` | **katalog eksplisit, 1 baris, host menegakkan bentuk** ◐ (`ReaderDefaultFeatureCatalog.cs:12-29`) |
| provider manga | `MangaSourceRegistry` | **daftar `new` eksplisit, 2 tempat, plus registri probe kedua** ❌ (`MangaSourceRegistry.cs:108-129`, `ManualUrlProbeRegistry.cs:10-28`) |
| feature tingkat module | `MangaReaderView` / `CamoprofView` | **tidak ada katalog sama sekali**; parent meng-hardwire di konstruktor ❌ (`MangaReaderView.xaml.cs:36-186`, `CamoprofView.xaml.cs:32-49`) |
| format arsip (RAR/ZIP) | `ArchivePageReader` | **siklus**: dispatcher meng-import feature, feature meng-import dispatcher ❌ (C13) |

---

## 5. Bentuk kontrak yang seharusnya (pemetaan, bukan rencana kerja)

yuz-ui punya satu file `contracts.ts` yang tidak meng-import apa pun, dan satu aturan
ESLint: plugin hanya boleh meng-import file itu. Padanan citadel:

| yuz-ui | citadel hari ini | celah |
| --- | --- | --- |
| `contracts.ts` (daun, nol import) | `Citadel.Contract` (menarik `Citadel.Core`) | D6 — `Lifetime` tinggal di Core (`rpl/Lifetime.cs:19`), jadi kontrak tidak bisa tipis tanpa memindahkannya |
| ESLint: plugin hanya import contracts | hook: citizen hanya boleh ProjectReference Core/Contract/Setting | C1/C2 — `Compile Include` dan baseline `.targets` tidak terlihat; `using` tingkat namespace tidak dijaga sama sekali |
| ESLint: sibling children saling asing | tidak ada padanan | C3 — inilah celah yang menghasilkan BUG-1 |
| test "child terdaftar == file di `children/`" | tidak ada padanan di C#; **ada** di python sebagai guard ARCH berbasis grep sumber | C9 — guard python itu pun tidak masuk gate |

**Bentuk lurusnya, dinyatakan sebagai pemetaan:** celah C3 adalah satu-satunya yang
menghasilkan kerusakan nyata sejauh ini (BUG-1). Padanan terdekat yang sudah terbukti
di repo ini adalah `test_add_profile_architecture.py` (ARCH-1..4, guard grep-sumber)
dan `check-project-refs.mjs`. Keduanya pola yang sama: **daftar tepi yang diizinkan,
ditegakkan otomatis, gagal keras.**

---

## 6. Tiga pola di dalam citadel yang sudah membuktikan bentuk kernel

Ini bagian terpenting dokumen ini: perbaikan tidak perlu merintis apa pun.

### 6.1 `mangareader/Reader/` — katalog + host yang menegakkan bentuk

```text
ReaderWindow.xaml.cs (165 baris)   → menyebut NOL tipe feature konkret
        │                            hanya infrastruktur: state, 4 hub, hosts, context
        ▼
ReaderDefaultFeatureCatalog.cs:12-29   12 × .Add(nama, factory)
        ▼
ReaderFeatureHost.cs:27-40  attach  → :31-35 key/name cocok
                            :42-47 tepat satu IReaderDrawerContributionHost
                            :49-63 kartu drawer diagregasi OTOMATIS
                            :72-84 visual di-mount OTOMATIS
                            :92-97 start · :104-105 dispose urutan terbalik
        ▼
feature saling bicara HANYA lewat 4 hub → NOL tepi feature→feature
```

**Hasil terukur:** menambah feature drawer-card atau visual = **nol edit host**.
Menambah feature apa pun = folder baru + **satu baris** katalog.

Ini persis D-CONTRACT yuz-ui, kecuali satu hal: daftarnya masih ditulis tangan
(satu baris), bukan glob. Dan itu **cukup** — satu baris di satu tempat adalah biaya
yang owner sebut "few files only".

Vonis FM di Reader: FM-1 patuh, FM-2 patuh, FM-3 patuh, FM-4 campuran (8 dari 12
feature masih menerima state konkret lewat closure katalog), FM-5 sebagian besar
patuh. Rinci: `CITADEL-FLOWS-MANGAREADER.md` §1.2.

### 6.2 `sharedLogic/pyhost/` — self-registration sejati, lengkap dengan deteksi tabrakan

```text
CITADEL_PYHOST_PLUGINS (daftar paket, diset C# saat spawn — PyHost.cs:80-83)
        ▼
_Host._load_feature_plugins (pyhost.py:155-171)
   __import__(nama) → module.install(self)
   plugin GAGAL → dilog, host TETAP HIDUP (:169-171)
        ▼
install(host) milik plugin
   host.register_commands(owner, {perintah: handler})   (:92-101)
   tabrakan nama → COMMAND_COLLISION, keras              (:97-99)
   host.add_lifecycle_hook(hook) opsional                (:103-106)
        ▼
arah dependensi: plugin → core SAJA
shared core feature-free: NOL kecocokan nama citizen di seluruh pyhost/**/*.py
```

Ini **glob-by-convention versi python**: host tidak tahu isi plugin, plugin tidak
tahu plugin lain, dan kegagalan satu plugin tidak menjatuhkan host — tiga sifat yang
persis diminta D-PLUGIN.

### 6.3 `Citadel.Searcher` — kehadiran folder sebagai registrasi

```text
folder module/<screen>/ + module.json  →  Watcher melihat  →  Loader memuat di ALC sendiri
                                       →  IModuleGate.Register  →  sidebar + router tahu
hapus folder  →  Unregister otomatis (Watcher.cs:629-660)  →  contender dipromosikan (:666-682)
folder rusak  →  retry ≤4×, failure per stage, pump tetap hidup
```

Nol edit di luar `module/`. Bahkan `.slnx` tidak disentuh. Ini bentuk paling murni
dari "tambah plugin = tambah folder" di seluruh repo.

### 6.4 Apa yang membedakan ketiganya dari sisi Downloader/parent module

| Sifat | Reader | pyhost | Searcher | Downloader/parent |
| --- | --- | --- | --- | --- |
| ada satu tempat pendaftaran | ya (katalog) | ya (env + `install`) | ya (folder) | **dua** (registry + probe registry) atau **nol** (parent hardwire) |
| parent menegakkan bentuk, bukan isi | ya (`ReaderFeatureHost:31-47`) | ya (`COMMAND_COLLISION`) | ya (validasi manifest + route) | tidak |
| agregasi otomatis | ya (drawer card, visual) | ya (perintah per owner) | ya (folder) | tidak |
| fault isolation | ya (dispose terbalik) | ya (plugin gagal → host hidup) | ya (tiga lapis) | tidak ada di tingkat feature |
| biaya tambah satu anak | 1 baris | 1 folder + 1 nama env | 1 folder | 2–4 file + manifest test |

---

## 7. fault isolation: perbandingan langsung

| Lapis | yuz-ui | citadel | Bukti citadel |
| --- | --- | --- | --- |
| discovery/boot | kernel boot tanpa plugin | shell tampil + Settings lengkap sebelum searcher jalan | `App.xaml.cs:29-32,125-133` |
| plugin rusak saat start | error plugin tidak mematikan kernel | folder gagal → failure per stage, retry ≤4×, pump tetap hidup | `Watcher.cs:370-375,693-715` |
| plugin rusak saat tick | loop kernel terus jalan | `CreateView` dibungkus: gagal → lifetime mati, citizen di-unregister, mendarat di Settings | `Router.cs:182-215` |
| plugin rusak saat render | — | animasi navigasi gagal → transisi diselesaikan paksa | `Router.cs:347-351` |
| plugin runtime kedua | — | plugin python gagal → dilog, host hidup | `pyhost.py:169-171` |

Citadel **lebih dalam** dari yuz-ui di sini: ia punya lapisan isolasi untuk proses
python terpisah, yang tidak ada padanannya di yuz-ui.

Yang **belum** ada di citadel: isolasi di tingkat feature dalam satu citizen. Feature
yang melempar saat Attach tidak ditangkap siapa pun — `ReaderFeatureHost` menegakkan
bentuk (`:31-47`) tapi tidak membungkus kegagalan per feature.

---

## 8. Urutan pelurusan yang disarankan (pemetaan prioritas, bukan rencana kerja)

Diurutkan menurut **kerusakan yang sudah terjadi**, bukan menurut kerapian. Semua
tanpa perubahan kode sampai owner memerintahkan.

| # | Sasaran | Kenapa di urutan ini | Entri register |
| --- | --- | --- | --- |
| 1 | **BUG-1** — `catch` mati di CatalogMirrorSyncFeature | defek nyata yang sedang berjalan: sync ±90 ribu judul tanpa backoff throttling | BUG-1 |
| 2 | **C12** — dua provider Comix | akar BUG-1; ±3.100 baris duplikat; selama dua `ComixContractException` ada, bug sejenis akan terulang | C12 |
| 3 | **C3** — tidak ada penegak tepi internal citizen | satu-satunya celah yang sudah terbukti menghasilkan kerusakan; dua pola penegak sudah ada di repo (hook csproj, guard ARCH python) | C3, C9 |
| 4 | **C13** — siklus `shareLogic/Archive ↔ Features/Rar` | saling import literal; pola children-mendaftar sudah terbukti di Reader dan pyhost | C13 |
| 5 | **B2** — 149 entri manifest test | sumber terbesar "banyak file tersentuh"; pola wildcard sudah dipakai di repo yang sama | B2, B1 |
| 6 | **C14 + D22** — parent mangareader menjangkau internal | 412 baris, enam pekerjaan; bentuk targetnya sudah ada 300 baris lebih tipis di module yang sama (`ReaderWindow`) | C14, D22, B7 |
| 7 | **D9** — pekerjaan python ftf/proxy yang hilang | keputusan owner: lanjut atau pensiun; `.pyc` dan folder recovery masih ada dan mungkin bisa dipulihkan | D9 |
| 8 | **D6** — Contract tidak tipis | menyentuh `IModule` yang ditandai "Verbatim from v0 — do not alter"; keputusan owner, bukan refactor | D6 |
| 9 | **C6, C8** — lubang gate | `ftf`/`proxy`/`blank` tak terkompilasi gate; proyek baru tak dijaga hook | C6, C8 |
| 10 | **D1** — `module/` memegang empat peran | murni navigasi; tidak ada perilaku yang berubah | D1 |
| 11 | A1–A13, D2–D26 | akurasi dokumen dan satu-file-satu-pekerjaan; murah, tapi tidak mendesak | — |

---

## 9. Yang TIDAK perlu diluruskan

Sama pentingnya dengan daftar di atas:

| Sudah benar | Bukti |
| --- | --- |
| Graf dependensi antar proyek | aktual = diizinkan hook, persis (`CITADEL-FLOWS-CROSS.md` §2.3) |
| Citizen tidak menyentuh `Citadel.Ui`/`Shell` | nol `using Citadel.Ui` di `module/`; nol `assembly=Citadel.Ui` di XAML module |
| Citizen tidak menyentuh citizen | hanya satu `ProjectReference` di seluruh `module/`: Searcher→Contract |
| `setting/` tetap universal | nol nama module di seluruh sumber `setting/`; nol kunci style ganda (26 kunci, masing-masing sekali) |
| `Citadel.Core` daun sejati | csproj 9 baris, nol referensi, nol `System.Windows` |
| shared pyhost feature-free | nol kecocokan nama citizen di seluruh `pyhost/**/*.py` |
| Kredensial aman di git | `.gitignore:35-45` berlapis; 2 file ter-track dari 499 MB |
| Isolasi assembly citizen | stream-load + hook `Resolving` null + `VerifyCitizenIsolation` gagal build |
| Bahaya salinan-per-assembly sudah dirancang | `RuntimeSetup.cs:158-176` memakai file lock lintas proses, bukan semaphore statis |
| Reader sebagai referensi | klaim `SKILL.md:262-297` **terbukti** untuk FM-1, FM-2, FM-3 |

---

## 10. Satu kalimat untuk owner

Konsep kernel yang kamu inginkan untuk citadel **sudah berjalan di citadel** — untuk
module. Yang belum adalah menerapkannya satu tingkat lebih dalam, ke feature, dan
repo ini sudah punya tiga contoh yang terbukti (Reader, pyhost, Searcher) sehingga
tidak ada yang perlu ditemukan dari nol.
