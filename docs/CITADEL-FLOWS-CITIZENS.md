# CITADEL-FLOWS-CITIZENS — camoprof, ftf, proxy, blank, credenz, tests, tools

Satu dokumen untuk citizen-citizen selain mangareader (yang punya dokumen sendiri),
plus vault kredensial, jaring test, dan jalur rilis. Ditulis 2026-09-11 dari kode.

| Bagian | Isi |
| --- | --- |
| §1 | `camoprof` — citizen sedang, pola parent eksplisit |
| §2 | `ftf` dan `proxy` — dua kerang kosong, dan pekerjaan python yang hilang |
| §3 | `blank` — template |
| §4 | `credenz` — vault kredensial, bukan citizen |
| §5 | `tests/` — jaring pengaman dan lubangnya |
| §6 | `tools/` dan jalur rilis |

---

## 1. camoprof — 26 file, 2.972 baris

### 1.1 Parent

`CamoprofModule.cs` 17 baris, adapter shell murni: `Route` (`:14`), `CreateView`
(`:16`), tanpa pengetahuan feature.

`CamoprofView.xaml.cs` **114 baris** = composition root.

- **Sembilan subsistem disebut dengan tipe konkret:** `GoogleCredentialStore` (`:32`),
  `ProfileCatalog` (`:33`), `BrowserSessionCoordinator` (`:34`), `NetworkMonitor`
  (`:35`), `GoogleAccountService` (`:36`), `AddProfileFeature` (`:37`),
  `ProfileActionsFeature` (`:38`), `LauncherView` (`:43`), `RuntimeView` (`:49`);
  plus `Lifetime` dari `Citadel.Core.Rpl` (`:3,:27`).
- **Tidak ada katalog/registri.** Urutan `new X()` eksplisit di `:32-49`; feature
  diantar lewat konstruktor ke `LauncherView` (`:43-48`).
- **Logika feature di parent? Tidak ada.** Hanya koordinasi: pilih tab mengaktifkan
  Runtime (`:71-84`), sinyal sibuk menonaktifkan panel Launcher/Editor (`:86-95`),
  disposal di luar thread UI (`:97-113`). Cocok dengan klaim komentarnya sendiri
  (`:14-17`).
- **Menjangkau internal? Tidak ada panggilan, tapi kopling tipe-konkret penuh.**
  Parent tidak pernah memanggil `AddProfileCoordinator`/`PyHostClient`/`Dialog`;
  namun ia meng-`using` kedua namespace feature (`:4-5`) dan mengonstruksi kedua
  fasad — **tidak ada interface di mana pun**, jadi tipe kontraknya adalah kelas
  konkretnya sendiri.
- **Pelanggaran FM-3:** parent meng-hardwire set feature. Diperparah oleh
  `sharedLogic/BrowserSessionCoordinator.cs:399` yang **meng-hardcode nama plugin**
  `"camoprof_add_profile"`, sehingga menghapus feature AddProfile juga menyentuh
  sharedLogic module.

**Biaya menambah SATU feature:**

| Tindakan | File |
| --- | --- |
| BUAT | `Features/<Baru>/*` (csproj auto-glob; tidak perlu edit csproj/module.json/layout.json) |
| **EDIT** | `CamoprofView.xaml.cs` — using (`:4-10`), konstruksi + wiring (`:32-49`) |
| **EDIT** bila menghadap Launcher | `Launcher/LauncherView.xaml.cs` — using (`:6-7`), field (`:21-22`), tanda tangan ctor (`:28-33`), handler |
| **EDIT** | `Launcher/LauncherView.xaml` — tombol/kolom baru |
| **EDIT** bila butuh perintah pyhost | `sharedLogic/BrowserSessionCoordinator.cs:399` (tambah nama plugin ke string hardcoded) |
| **BUNTU** bila butuh paket python kedua | `Module.Camoprof.csproj:19` hanya mendukung SATU `PyhostPluginName` → register B4 |

Minimum 2 file diedit; feature Launcher realistis 4–5 file.

### 1.2 Peta node

| Folder | Pekerjaan | Tipe kontrak |
| --- | --- | --- |
| root | adapter shell + composition root | `CamoprofModule` (IModule), `CamoprofView`; host tab Launcher/Runtime (`CamoprofView.xaml:10-27`) |
| `Providers/Google/` | state akun Google + penyimpanan kredensial DPAPI | `GoogleAccountService` (`:25`), `GoogleCredentialStore` (`:11`), record `GoogleAccountRecord.cs:3`, `GoogleAccountState.cs:3,19` |
| `Network/` | klasifikasi konektivitas | `NetworkMonitor` + event `SnapshotChanged` (`:20`), `NetworkSnapshot` (`NetworkState.cs:16`); internal `NetworkProbe.cs:6`, `NetworkPolicy.cs:3` |
| `Launcher/` | UI tabel profil | `LauncherView` (internal, `:14`), VM baris `LauncherProfileRow.cs:8` |
| `Runtime/` | UI setup venv/browser python | `RuntimeView` + `SetupBusyChanged` (`:21`) |
| `Features/AddProfile/` | pairing/enrollment Google | fasad `AddProfileFeature.cs:15`, record kontrak `AddProfileContract.cs:3,24,37` + policy `:43`; paket python `camoprof_add_profile/` |
| `Features/ProfileActions/` | hapus + policy cek-Google | `ProfileActionsFeature.cs:25`, record outcome `:16` |
| `sharedLogic/` (lokal module) | proses pyhost + registri sesi; batas folder profil | `BrowserSessionCoordinator.cs:24` + `SessionChanged` (`:54`), `ProfileCatalog.cs:12`, `ProfileEntry.cs:3` |

Flow AddProfile end-to-end (20 langkah) ada di `CITADEL-FLOWS-SHAREDLOGIC.md` §4 —
ditulis di sana karena sebagian besar rantainya adalah jembatan pyhost.

**Model sesi:** SATU proses pyhost per lifetime module-view
(`BrowserSessionCoordinator.cs:33,374-402`); sesi per profil dalam registri
(`:28-29`); semua mutasi diserialisasi satu gerbang (`:30`). AddProfile mengklaim
sesi headed untuk profilnya dan menutupnya di akhir alur; `GoogleAccountService`
membuka/menutup sesi sementara sendiri (`:36-47,59-62,143-153`).

### 1.3 Tepi

- **ProfileActions → AddProfile: kontrak-saja, tanpa panggilan.** Memakai record
  `AddProfileRequest` (`ProfileActionsFeature.cs:1,18,71,77`) dan mengembalikannya,
  sehingga Launcher yang merutekan ke `AddProfileFeature`
  (`LauncherView.xaml.cs:169-172`). Arah sebaliknya: tidak ada.
- Event yang nyata: `RuntimeView → CamoprofView` (`SetupBusyChanged`,
  `RuntimeView.xaml.cs:21` / `CamoprofView.xaml.cs:54`);
  `BrowserSessionCoordinator → LauncherView` (`SessionChanged`, `:54` /
  `LauncherView.xaml.cs:42`); `NetworkMonitor → LauncherView` (`:20` / `:43`).
- **Tidak ada bypass penyimpanan.** Neither feature menyentuh folder profil
  langsung: hapus lewat `ProfileCatalog` (`ProfileActionsFeature.cs:52`); file
  kredensial ditulis hanya lewat `GoogleCredentialStore`
  (`AddProfileCoordinator.cs:51-54`). Catatan: **direktori** profil browser dibuat
  oleh python (`pyhost.py:261`), bukan katalog — katalog hanya memindai/menghapus.
- Sisanya konkret: `CamoprofView → sembilan subsistem` (`:32-49`);
  `LauncherView → AddProfileFeature` (`:21,95,219`), `→ AddProfileDialog`
  (konkret+xaml, `:217`), `→ ProfileActionsFeature` (`:22,154,205`),
  `→ ProfileCatalog` (`:56`), `→ BrowserSessionCoordinator` (`:113,118,135,139`);
  `AddProfileCoordinator → PyHostClient` (`:43`), `→ BrowserSessionCoordinator`
  (`:49-50,266-277`), `→ GoogleCredentialStore` (`:51-54`);
  `ProfileActions → ProfileCatalog` (`:52`), `BrowserSessionCoordinator` (`:51`),
  `GoogleCredentialStore` (`:53`), `GoogleAccountService` (`:75`);
  `GoogleAccountService → NetworkMonitor` (`:162`), `GoogleCredentialStore`
  (`:54,80,108`), `BrowserSessionCoordinator` (`:39-113`);
  `ProfileCatalog → GoogleCredentialStore` (`:19,77`).

### 1.4 Kepatuhan shared-UI: penuh

Keempat file XAML mendeklarasikan `xmlns:setting` (`CamoprofView.xaml:4`,
`LauncherView.xaml:4`, `RuntimeView.xaml:4`, `AddProfileDialog.xaml:5`).
**Nol** definisi `<Style>`/`ControlTemplate` lokal dan **nol** kontrol kustom di
seluruh module (grep lintas `camoprof/**/*.xaml`; hanya ada rujukan
`Style="{DynamicResource SettingCardStyle}"` di `LauncherView.xaml:57`,
`RuntimeView.xaml:31`).

| File | Penilaian |
| --- | --- |
| `CamoprofView.xaml` | **reuse**: `SettingTabs` (`:10`), `UiPreference.Key` (`:11`), `SettingViewport` (`:20`); host composition-only (`:15-17,23-25`) |
| `Launcher/LauncherView.xaml` | **reuse/compose**: `SettingViewport` (`:9`), `SettingActionCard` (`:16`), `SettingButton` (`:20,47,84,98,113,119`), `SettingToggle` (`:24`), `SettingTable` + `InteractiveColumns` (`:59-128`), `SettingTableActions` (`:111`), `SettingCardStyle` (`:57`), `UiPreference` (`:25,60`). DataTemplate sel memakai TextBlock/SettingButton polos — konten, bukan chrome baru |
| `Runtime/RuntimeView.xaml` | **reuse**: `SettingViewport` (`:9`), `SettingButton` (`:21,64`), `SettingCardStyle` (`:31`) |
| `Features/AddProfile/AddProfileDialog.xaml(.cs)` | **combo sah**: root-nya ADALAH `setting:SettingDialog` (`:1`), di-subclass (`AddProfileDialog.xaml.cs:20`), isi hanya TextBlock + SettingButton (`:37`). Launcher juga memakai ulang `SettingDialog.Confirm` (`LauncherView.xaml.cs:186-191`). Tidak menambah rendering rendah baru |

Tidak ditemukan duplikasi.

### 1.5 sharedLogic lokal module

| File | Baris | Pekerjaan |
| --- | --- | --- |
| `BrowserSessionCoordinator.cs` | 406 | memiliki satu proses PyHost + registri profile→session, serialisasi mutasi, menerjemahkan BROWSER_GONE jadi pembersihan registri |
| `ProfileCatalog.cs` | 102 | scan/hapus tervalidasi atas root folder profil |
| `ProfileEntry.cs` | 7 | record baris bersama |

**Pemilik tunggal yang nyata:** `BrowserSessionCoordinator` adalah satu-satunya file
yang mereferensikan `PyHost` (`:33,339,398`); Launcher/Runtime tidak memuat logika
sesi, hanya memanggil.

**Duplikasi di dalamnya:** perintah Google bertipe (`InspectGoogleAsync :122`,
`NavigateAsync :152`, `RelogGoogleAsync :184`) hidup berdampingan dengan dispatcher
generik (`:225,:264`) — dua pola untuk satu pekerjaan (perutean perintah wire).

### 1.6 Anomali

| Temuan | Bukti |
| --- | --- |
| **File entri plugin ganda.** `camoprof_add_profile/plugin.py` identik byte-per-byte dengan `__init__.py` (35 baris masing-masing). pyhost memuat paket lewat `__import__` (`pyhost.py:166`), yaitu `__init__.py`; jadi `plugin.py` **mati** — dua pemilik untuk satu pekerjaan. (Di sisi mangareader pola yang sama dipakai, dan di sana `Citizen.targets:200-213` memang men-deploy `plugin.py` sebagai `__init__.py`.) | diff: IDENTICAL |
| **sharedLogic lokal menyebut nama feature.** Komentar `BrowserSessionCoordinator.cs:396-397` mengklaim "the shared pyhost core stays feature-free" — benar untuk `module/sharedLogic`, tapi file "shared" lokal ini meng-hardcode `"camoprof_add_profile"` (`:399`), sehingga menghapus/mengganti nama feature mengedit sharedLogic. Bertentangan dengan FM-3 | |
| **Tab yatim.** `layout.json:4` mendeklarasikan slot `EditorPanel`; `CamoprofView.xaml:19-21` merender tab "Editor" yang hanya berisi `SettingViewport` kosong; `CamoprofView.xaml.cs:94` men-toggle-nya. Tidak ada kode atau feature yang memiliki isi Editor | |
| **Kebijakan id profil tinggal di lapisan UI.** `LauncherView` mencetak identitas profil `"p_"+Guid` (`LauncherView.xaml.cs:89`) — penamaan domain dimiliki view, bukan feature atau katalog | |
| **Artefak build dari feature yang sudah dihapus.** `camoprof/obj/Debug/.../Providers/Google/Enrollment/GoogleEnrollmentDialog.baml/.g.cs` (dan Release) ada tanpa sumber yang cocok — sebuah dialog Enrollment pernah ada di bawah Providers, kini hilang dari sumber | hanya bukti; `obj/` adalah keluaran build |
| Cacat format | `BrowserSessionCoordinator.cs:300` — spasi liar pada `CloseAsync(        string profile,` |
| Klaim komentar yang **cocok** dengan kode | `AddProfileFeature.cs:7-13` ("Launcher … never opens a browser session … for this flow") dan `AddProfilePyHostClient.cs:10-15` ("ONLY C# class that touches the wire protocol for Add Profile") |

---

## 2. ftf dan proxy — dua kerang, dan pekerjaan python yang hilang

### 2.1 Fakta dasar (terverifikasi `git ls-files`)

Masing-masing **hanya 6 file ter-track**:

| | `module/ftf/` | `module/proxy/` |
| --- | --- | --- |
| entry | `FtfModule.cs:8-17` — `IModule`, `Route => "ftf"`, `new FtfView()` (`:16`) | `ProxyModule.cs:8-16` — `Route => "proxy"`, `new ProxyView()` (`:15`) |
| manifest | `module.json:4-7` route `ftf`, order 30, entry `Module.FTF.dll`, type `Module.FTF.FtfModule` | `module.json:4-7` route `proxy`, order 40 |
| csproj | `Module.FTF.csproj:3` hanya `<Import Project="..\Citizen.targets" />` | `Module.Proxy.csproj:3` sama |
| view | `FtfView.xaml:10-34` — `setting:SettingTabs` + 3 `TabItem` placeholder (Launcher/Editor/Runtime), masing-masing `setting:SettingViewport` + TextBlock literal | `ProxyView.xaml:10-34` — **komposisi identik byte-per-byte**, komentar sama (`:9`), `UiPreference.Key="proxy.workspace.tab"` (`:11`), placeholder `:15,22,29` |
| code-behind | 9 baris, hanya `InitializeComponent` (`FtfView.xaml.cs:6-9`) | 9 baris (`ProxyView.xaml.cs:6-9`) |
| layout | `layout.json:3-5` tiga slot visibility yang cocok dengan `x:Name` (`FtfView.xaml:13,20,27`) | `layout.json:3-5` tiga slot sama |

**Folder feature-nya kosong di git.** `module/ftf/Backend/` nol file;
`Features/{FaucetRun, GSuite, Profiles, Results}/` tanpa `.cs/.xaml/.py`.
`module/proxy/Features/{Pool, Settings, Sync}/` sama.

### 2.2 Jejak pekerjaan yang tidak pernah masuk git

Yang tersisa di disk hanya cache:

```text
module/ftf/Features/FaucetRun/.pytest_cache/
module/ftf/Features/FaucetRun/ftf_plugin/__pycache__/adapter.cpython-312.pyc
module/ftf/Features/FaucetRun/ftf_plugin/__pycache__/pipeline.cpython-312.pyc
module/ftf/Features/FaucetRun/ftf_plugin/tests/__pycache__/test_pipeline.cpython-312-pytest-8.4.1.pyc
module/proxy/Features/Pool/.pytest_cache/
module/proxy/Features/Pool/proxy_plugin/__pycache__/pool.cpython-312.pyc
module/proxy/Features/Pool/proxy_plugin/tests/__pycache__/test_pool.cpython-312-pytest-8.4.1.pyc
```

Ditambah **hantu terkompilasi** di `obj/`: `obj/Debug/.../Features/GSuite/GSuiteView.g.cs`,
`SettingsView.g.cs`, `SyncView.g.cs` — artinya view-view itu **pernah** dikompilasi
di mesin ini.

Verifikasi git: `.py` sumbernya **tidak ada di disk dan tidak pernah ada di riwayat**
(`git log --diff-filter=D -- module/ftf module/proxy` kosong). `.gitignore:52-54`
mengabaikan `__pycache__/`, `*.pyc`, `*.pyo`; `.pytest_cache/` mengabaikan dirinya
sendiri lewat `.gitignore` yang dibuat pytest. Karena itu `git status` tampak bersih.

Ada juga `artifacts/recovery-ftf-proxy-2.1.3-20260907` — bukti bahwa sumber
ftf/proxy pernah dibangun-ulih/dipulihkan di luar git pada suatu titik (isinya
unverified).

**Artinya untuk owner:** ada pekerjaan python untuk FTF faucet-pipeline dan Proxy
pool yang pernah dijalankan dan diuji di mesin ini, lalu hilang. Yang tersisa hanya
cache terkompilasi dan hantu `obj/`. Bukan kebocoran rahasia, tapi ini **pekerjaan
yang hilang dan tidak terlihat dari `git status`**. → register D9.

### 2.3 Analisis parent (untuk keduanya identik)

| Pertanyaan | Jawaban |
| --- | --- |
| Subsistem disebut tipe konkret | **0** |
| Katalog/registri feature | **tidak ada** |
| Urutan `new X()` | tidak ada (feature belum ada) |
| Logika feature di parent | tidak ada |
| Biaya menambah feature | EDIT `*View.xaml` (tambah TabItem/isi) + EDIT `layout.json` (tambah slot) + BUAT `Features/<Nama>/`. **csproj tidak perlu diedit** (SDK globbing + `Citizen.targets:198-201` auto-deploy `Features\*\<PyhostPluginName>\*.py`) |

Menambah feature di sini **melanggar FM-3** ("adding a feature touches only that
feature's folder", `SKILL.md:299-300`) — tapi hanya karena katalog belum ada; dengan
nol feature, tidak ada pelanggaran FM-1/2/4/5 yang bisa diamati.

### 2.4 Kepatuhan shared-UI: penuh

Keduanya **reuse**: namespace `setting:` (`FtfView.xaml:4`, `ProxyView.xaml:4`),
`SettingTabs` (`:10`), `SettingViewport` (`:14,21,28`), `UiPreference.Key` (`:11`).
Nol duplikasi template/style lokal. Composition-only.

### 2.5 Risiko gate

Klaim "tidak ada gate yang mengompilasi mereka" — **terverifikasi**:

- Tidak ada di `Citadel.slnx` (17 proyek terdaftar).
- CI hanya menjalankan `dotnet test Citadel.slnx`
  (`.github/workflows/ci.yml:29,33`); hook `gate-on-stop.mjs:34` juga.
- Yang **pertama kali** mengompilasi ftf/proxy adalah skrip rilis:
  `tools/Build-Release.ps1:59-65` menemukan `module/*/Module.*.csproj`, `:71-80`
  membangun masing-masing.

Jadi ftf/proxy yang rusak bisa duduk hijau di `main` dan baru gagal **di dalam job
rilis**. → register C6.

---

## 3. blank — template, dan bukti bahwa janji plug-and-play nyata

Enam file, 44 baris: `Module.Blank.csproj`, `module.json`, `layout.json`,
`BlankModule.cs`, `BlankView.xaml(.cs)`. Rinci di `CITADEL-FLOWS-CROSS.md` §1.8.

Ia juga dipakai sebagai **citizen uji nyata**: `tests/Citadel.Uia.csproj:45`
mereferensikan `Module.Blank.csproj` (tanpa keluaran assembly), jadi test
discovery memuatnya lewat loader, persis seperti app. Ini satu-satunya citizen yang
direferensikan proyek test.

---

## 4. credenz — vault kredensial, bukan citizen

**Di disk:** 499 MB. `google/accounts/` = 6 direktori identitas (mis.
`p_2fdf0d0a…`), masing-masing berisi tepat `identity.json` + `password.dat`
(nama saja; isi tidak dibuka). `google/profiles/` = 6 direktori profil browser +
`.gitkeep`.

**Ter-track git:** hanya `module/credenz/README.md` dan
`module/credenz/google/profiles/.gitkeep`. Perlindungannya berlapis di
`.gitignore:35-45` (negasi tingkat-demi-tingkat, komentar di `:36-39`).

**Klaim README** (`module/credenz/README.md`): vault baca/tulis terpusat untuk
identitas masuk dan hasil keluar (`:3-5`); tata letak `google/profiles` = rumah
profil persisten camoufox ber-kunci profile ID, `google/accounts` = `identity.json`
+ `password.dat` DPAPI CurrentUser (`:9-11`); isi di-gitignore, hanya README +
`.gitkeep` di-commit (`:18-20`); vault dev = folder ini, terpasang =
`%LocalAppData%\Citadel\Credenz`, C# yang meresolusi dan menyerahkan path absolut ke
Python lewat `CITADEL_CREDENZ` (`:21-25`); password tidak pernah di JSON,
`password.dat` dilindungi DPAPI, didekripsi hanya untuk relog CamoProf (`:26-28`).

**Definisi path:** `module/sharedLogic/cs/CredenzPath.cs:16-49` — urutan resolusi:
env `CITADEL_CREDENZ` (`:20-25`) → walk-up ≤8 induk mencari `module/credenz` yang
dapat ditulis (`:27-37`) → `%LocalAppData%\Citadel\Credenz` (`:39-42`).
`ProfilesRoot()` (`:45-46`), `GoogleAccountsRoot()` (`:48-49`).

| Peran | Siapa | Bukti |
| --- | --- | --- |
| penulis `identity.json` | camoprof | `GoogleCredentialStore.cs:86-90` (tulis atomik) |
| penulis `password.dat` | camoprof | `GoogleCredentialStore.cs:135,145-152` (`ProtectedData.Protect` DPAPI), root dari `CredenzPath.GoogleAccountsRoot()` (`:18`) |
| penulis data profil browser | python | `pyhost.py:193,267` menyetel camoufox `user_data_dir` di bawah `<CITADEL_CREDENZ>/google/profiles` |
| pembaca/penghapus | camoprof | `ProfileCatalog.cs:18` (`CredenzPath.ProfilesRoot()`), hapus direktori profil `:45-47` |
| penyerah path ke python | mangareader + camoprof | `CatalogBrowserClient.cs:396`, `DownloaderPyHostClient.cs:409`; `PyHost.cs:62,79` menyuntik env |
| penjaga bila vault tak ada | python | `pyhost.py:174-185` (`STARTUP_NO_CREDENZ`) |

**Apakah ikut terhitung rilis? TIDAK — dan itu benar.** Discovery citizen di
`Build-Release.ps1:59-65` memindai tiap subdirektori `module\` mencari
`Module.*.csproj`; `credenz` tidak punya, jadi ia bukan proyek citizen. Pemeriksaan
invarian di `:90-92` membandingkan **direktori ter-deploy** di `publish\module\`
dengan jumlah csproj — `credenz` tidak pernah di-deploy ke sana (deploy hanya lewat
`Citizen.targets:104-111`). Jadi `credenz` tak terlihat oleh kedua sisi penghitungan;
sama halnya `sharedLogic`, yang memang sengaja ditempatkan sebagai sibling
`module\` pada keluaran (`Citizen.targets:87-94`). Pertanyaan terbuka di
`CITADEL-STRUCTURE.md` §6 **terjawab: aman**.

**Tidak ada kecocokan "credenz" di ftf/proxy.**

---

## 5. tests/ — 11 proyek, semuanya di `Citadel.slnx`

| Proyek | Mencakup | Cara mengakses kode |
| --- | --- | --- |
| `Citadel.Core.Tests` | runtime Core (12 file test: tokens, gate lifetime/event/main-queue, watchdog, power saving, log) | ProjectReference ×1 (`:15`) |
| `Citadel.Ui.Tests` | Citadel.Ui (animasi, layout sidebar, theme resources; STA) | ProjectReference ×2 (`:16-17`) |
| `Citadel.Uia` | shell terbangun end-to-end: router, layout applier, preset, fluid layout, searcher (discovery/loader/idle-cost/ownership/reader/bridge), layar setting, perilaku komponen bersama, sidebar grouping, app update, module gate, single-instance | ProjectReference ×9 termasuk Shell + Searcher + Setting (`:21-26`), plus referensi tanpa-keluaran ke `module/blank` dan `Citadel.Uia.Citizen` (`:45-52`) |
| `Citadel.Uia.Citizen` | **fixture**, bukan proyek test: citizen palsu (assembly `Module.Fixture`) | referensi Core/Contract `Private=false` + dependensi privat (`:29-41`) |
| `Citadel.Uia.Citizen.Dependency` | DLL managed privat milik citizen palsu (`Citizen.PrivateDependency`) | csproj fixture kosong (`:13-17`) |
| `Module.Camoprof.Tests` | camoprof: wildcard sharedLogic/cs + ProfileCatalog/BrowserSessionCoordinator + Google credential store + feature AddProfile | `Compile Include` ×10 (1 wildcard) (`:29-38`) |
| `Module.Mangareader.Archive.Tests` | shareLogic/Archive + feature Rar | ×3 (`:21,23,25`) |
| `Module.Mangareader.Downloader.Tests` | Features/Downloader (ManualUrl, FilterSearch/Comix, Catalog, source index) + satu file Components | ×66 + ProjectReference Setting (`:21`) |
| `Module.Mangareader.History.Tests` | store/feature History + shareLogic view-mode | ×8 (`:22-36`) |
| `Module.Mangareader.Library.Tests` | store Library, grouping, update checker, comparer shareLogic | ×16 (`:21-51`) |
| `Module.Mangareader.Reader.Tests` | hub ReaderCore, chapter loading/navigation, chrome/dim/drawer/autoscroll | ×46 + ProjectReference Setting (`:16`) |

**UIA = UI Automation.** "drives the built shell and pins contract and gate behavior
a screenshot cannot; real-window AutomationId evidence can additionally come from an
external UI Automation inspector" (`Citadel.Uia.csproj:4-6`). Praktiknya test
menjalankan shell in-process lewat `ShellHarness.cs`.

**Pasangan Citizen / Citizen.Dependency — dugaan owner benar.**
`Citadel.Uia.Citizen` "exists only for tests, deliberately does NOT import
Citizen.targets" sehingga tidak pernah muncul di app nyata; ia menyediakan
(a) route kedua yang berbeda dan (b) dependensi managed privat untuk membuktikan
`AssemblyDependencyResolver` bekerja di ALC terisolasi
(`Citadel.Uia.Citizen.csproj:3-18`). Referensinya di Uia hanya untuk urutan build,
`ReferenceOutputAssembly=false` — "tests load these through the loader, the way the
app does" (`Citadel.Uia.csproj:29-52`). Dependency-nya harus proyek lokal karena
kandidat NuGet mana pun sudah ada di default context dan tidak membuktikan apa pun
(`Citadel.Uia.Citizen.Dependency.csproj:3-12`).

### Yang TIDAK punya cakupan sama sekali

| Area | Keadaan |
| --- | --- |
| `module/ftf/`, `module/proxy/` | tanpa proyek test, tidak di slnx, CI tidak pernah mengompilasi — hanya `Build-Release.ps1:71-80` yang melakukannya, saat rilis |
| `module/blank/` | tanpa unit test; hanya dipakai sebagai artefak ter-deploy oleh Citadel.Uia (`:32-34`) |
| mangareader `CoverBuilder/` | tanpa test |
| mangareader `Features/Catalog/` (klien browser Runtime) | tanpa test |
| `MangaReaderModule.cs`, `MangaReaderView.xaml(.cs)`, `MangaTitleCard.xaml(.cs)` | tanpa test — **parent module tidak teruji** |
| sebagian besar `Components/` | hanya `MangaMultiSelectFilter.xaml.cs` yang di-link (`Downloader.Tests:41`) |
| camoprof `Launcher/`, `Network/`, `Runtime/`, `Providers/Google` di luar 2 file yang di-link | tanpa test |
| `sharedLogic/pyhost` | suite python **ada** (`module/sharedLogic/tests/test_pyhost.py`) tapi **tidak ada workflow CI yang menjalankannya** |
| `tools/` | skrip tanpa test |
| `Citadel.Searcher` | hanya tercakup lewat Uia, tanpa unit test tersendiri |

→ register C6, C9, C15.

---

## 6. tools/ dan jalur rilis

| File | Pekerjaan |
| --- | --- |
| `tools/Build-Release.ps1` | mengorkestrasi seluruh build installer |
| `tools/Get-ProjectVersion.ps1` | membaca `<CitadelVersion>` dari `version.props` lewat regex (`:7-14`) |
| `tools/Get-ReleaseVersion.ps1` | menghitung semver berikutnya dari `version.props` vs tag `v*` terbaru, bump patch/minor/major (`:10-33`) |
| `tools/enrollment_live_smoke.py` | smoke live enrollment camoprof terhadap `CITADEL_CREDENZ` sementara (`:54-55`) |
| `.github/workflows/ci.yml` | restore + test slnx pada push/PR (`:27-33`) |
| `.github/workflows/release.yml` | rilis manual |

### Flow rilis

```text
[1] dispatch manual + pilihan bump; branch main diwajibkan   release.yml:3-11, 27-30
[2] wajib ada check `build` hijau untuk commit yang persis ini release.yml:40-51
[3] versi = Get-ReleaseVersion.ps1
      max(version.props CitadelVersion=2.2.6, tag terbaru + bump)
                                                            release.yml:53-58; version.props:5-10
[4] catatan rilis dari git log prevTag..HEAD → artifacts/release-notes.md
                                                            release.yml:60-74
[5] dotnet vpk download github → menarik rilis Velopack sebelumnya ke Releases/
      untuk pembuatan delta                                  release.yml:76-90
[6] Build-Release.ps1
      validasi semver                                         :13-15
      bersihkan + buat ulang artifacts/publish/win-x64        :29-35
      dotnet publish Citadel.Shell self-contained win-x64     :38-53
      temukan citizen = setiap module\*\Module.*.csproj       :58-65
      bangun masing-masing -p:CitizenRuntimeRoot=<publish>\module\   :70-80
         ← override ini mengalihkan target deploy Citizen.targets:85
           (default: bin Shell) ke pohon publish
      assert exe + Components/ ada                            :82-88
      assert jumlah direktori citizen ter-deploy == jumlah csproj    :90-93
      assert tidak ada Citadel.*.dll bersama di dalam folder citizen :95-108
      dotnet tool restore                                     :110
      vpk pack → Releases/                                    :113-135
      verifikasi *-Setup.exe + ringkasan JSON dengan SHA-256   :137-149
[7] vpk upload github --publish → GitHub Release + tag        release.yml:99-111
      ringkasan langkah dengan hash installer                 :113-125
```

**Peran tiap file konfigurasi:**

- `global.json:3-5` — pin SDK 10.0.400, `rollForward: latestPatch` (CI cocok,
  `ci.yml:25`).
- `version.props` — satu sumber kebenaran versi (komentar `:3`).
- `Releases/` — direktori feed Velopack (indeks RELEASES + `.nupkg` full/delta).
- `artifacts/` — scratch (keluaran publish + catatan rilis + sisa diagnostik).

**Velopack** dipakai untuk kemasan installer/delta + auto-update dalam app.
Di-pin **dua kali**: CLI `vpk 1.2.0` (`.config/dotnet-tools.json`) dan library
`Velopack 1.2.0` (`core/Citadel.Shell/Citadel.Shell.csproj:19`) — dan csproj itu
sendiri memberi peringatan "Keep this version identical to
.config/dotnet-tools.json" (`:18`). Satu kebijakan versi, dua tempat: pola yang sama
dengan register D7.

Feed/repo update dipin di `core/Citadel.Shell/AppUpdateService.cs:32`
(`RepositoryUrl = "https://github.com/yuzhayo/citadel"`, dikonsumsi `GithubSource`
`:44-48`); URL repo yang sama dipakai `vpk download/upload` di
`release.yml:85,105`.

**Perhatikan:** langkah [6] memeriksa dua invarian yang juga dijaga
`VerifyCitizenIsolation` saat build citizen (`Citizen.targets:142-151`) — jumlah
direktori citizen dan absence of shared DLL. Jadi ada **dua** penegak untuk
invarian rilis, di dua tempat, seperti pola D7.

---

## 7. Rujukan register dari dokumen ini

B4 (satu `PyhostPluginName` per citizen) ·
C6 (ftf/proxy/blank tidak terkompilasi gate) ·
C9 (suite python tidak masuk gate) ·
C15 (parent module `MangaReaderView` dan sejumlah area tanpa cakupan test) ·
D9 (ftf/proxy kerang + pekerjaan python yang hilang dari git) ·
D23 (`BrowserSessionCoordinator.cs:399` meng-hardcode nama plugin feature di folder
"shared" lokal) ·
D24 (`plugin.py` identik dengan `__init__.py` di camoprof — file mati) ·
D25 (tab Editor yatim di camoprof; id profil dicetak di lapisan UI) ·
D26 (versi Velopack dipin dua tempat; invarian rilis ditegakkan dua tempat).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.
