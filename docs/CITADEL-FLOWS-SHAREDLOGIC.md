# CITADEL-FLOWS-SHAREDLOGIC — jembatan PyHost dan payload bersama

Ditulis 2026-09-11 dari kode. Klaim bertanda `file:baris`.

**Temuan utama, dan ini penting untuk seluruh tugas:** lapisan python citadel
**sudah menjalankan pola self-registration yang persis seperti D-CONTRACT yuz-ui** —
plugin mendaftar lewat kehadiran paket, host hanya mendengarkan, tabrakan nama
dideteksi keras, dan plugin yang gagal tidak menjatuhkan host. Pola yang hilang di
lapisan feature C# (register C3, B3) **sudah ada dan terbukti di lapisan python**.

---

## 1. Apa isi `module/sharedLogic/`

Bukan citizen: tanpa `module.json`, tanpa `.csproj`
(`module/sharedLogic/README.md:3-5`). Ia payload bersama dengan tiga wujud:

```text
module/sharedLogic/
├─ cs/                 SUMBER C# — di-COMPILE KE DALAM tiap citizen
│   ├─ PyHost.cs         595 baris · klien NDJSON-over-stdio, memiliki SATU proses python
│   ├─ RuntimeSetup.cs   578 baris · bootstrap runtime python idempoten
│   └─ CredenzPath.cs     65 baris · resolusi folder vault kredensial
│   semuanya `namespace CitadelBridge` (PyHost.cs:7, CredenzPath.cs:3, RuntimeSetup.cs:9)
│
├─ pyhost/             PAYLOAD PYTHON — di-DEPLOY utuh sebagai sibling module\
│   ├─ pyhost.py         442 baris · server NDJSON: protokol, registri sesi, lifecycle
│   ├─ README.md         352 baris · kontrak PROTOCOL v1
│   └─ providers/
│       ├─ __init__.py         PyhostError (error terstruktur di wire) + log stderr
│       └─ google/
│           ├─ __init__.py     primitif Google bersama (URL, deteksi email, dsb.)
│           ├─ inspection.py   isi `google.inspect`
│           └─ relogin.py      isi `google.relogin`, guard headed-only
│
├─ reference/          DOKUMEN HIDUP, tidak dieksekusi (README.md:13-14)
│   ├─ human_login.py    login manual ke profil persisten
│   └─ launch_home.py    membuka kembali home yang sudah login
│
├─ tests/              suite unittest stdlib, dijalankan manual
│   ├─ test_pyhost.py                  803 baris · protokol & lifecycle
│   ├─ test_add_profile_plugin.py      592 baris · perilaku plugin CAMOPROF
│   ├─ test_add_profile_invariants.py  245 baris · INV-1..5
│   └─ test_add_profile_architecture.py 140 baris · ARCH-1..4, guard grep-sumber
│
├─ README.md · requirements.txt  (camoufox==0.5.5, playwright>=1.51,<1.52)
```

---

## 2. Mekanisme self-registration plugin python (inti temuan)

```text
C# spawn proses python
│  PyHost.Start(venvPython, pyhostScript, credenzRoot, pluginName)
│  env CITADEL_CREDENZ  (PyHost.cs:79)
│  env CITADEL_PYHOST_PLUGINS  (PyHost.cs:80-83)   ← daftar paket, dipisah koma
▼
pyhost.py _main → _Host.__init__            (pyhost.py:411-412, 83-90)
▼
_Host._load_feature_plugins                 (pyhost.py:155-171)
│  untuk tiap nama paket:
│    __import__(nama)
│    module.install(self)          ← PAKET MENDAFTARKAN DIRI; host tidak tahu isinya
│  plugin yang GAGAL → dilog, host TETAP HIDUP  (pyhost.py:169-171)
▼
install(host) milik plugin
│  host.register_commands(owner, {perintah: handler})   (pyhost.py:92-101)
│     TABRAKAN NAMA → error keras COMMAND_COLLISION      (pyhost.py:97-99)
│  opsional host.add_lifecycle_hook(hook)                (pyhost.py:103-106)
│     dipanggil dari _drop_session (:219-223) dan retire_session (:134-138)
│  handler berbentuk async (host, msg) -> dict
▼
API host yang terlihat plugin            (arah dependensi: plugin → core saja)
   host.get_session (:108) · host.set_primary_page (:116) · host.retire_session (:125)
```

**Tidak ada base class.** Sebuah plugin adalah "paket python yang bisa diimpor
dan punya `install(host)` di level modul":
`camoprof_add_profile/plugin.py:28-35`, `mangareader_downloader/plugin.py:25-28`.
`plugin.py` juga di-deploy sebagai `__init__.py` sehingga mengimpor paket
menjalankan registrasi (`Citizen.targets:200-213`).

Bandingkan langsung dengan yuz-ui:

| yuz-ui D-CONTRACT | pyhost citadel |
| --- | --- |
| child terdaftar karena file-nya ADA | plugin terdaftar karena paketnya ada di `CITADEL_PYHOST_PLUGINS` |
| parent mendengarkan, tidak menyebut nama child | `_Host` tidak tahu isi plugin; ia hanya memanggil `install` |
| sibling saling asing | arah dependensi plugin → core saja; antar plugin tidak ada |
| menambah child = menambah file | menambah plugin = menambah folder + satu nama env |
| fault isolation | plugin gagal dilog, host tetap hidup (`pyhost.py:169-171`) |
| tabrakan id terdeteksi | `COMMAND_COLLISION` keras (`pyhost.py:97-99`) |

Satu perbedaan penting: daftar paket masih **ditulis tangan** di csproj citizen
(`<PyhostPluginName>`) dan di-deploy oleh `Citizen.targets`, jadi "kehadiran folder"
belum sepenuhnya otomatis seperti glob yuz-ui — dan `Citizen.targets` hanya mendukung
**satu** nama paket per citizen (register B4).

### Klaim "shared core stays feature-free" — TERBUKTI untuk kode, tidak untuk dokumen

- **Kode:** grep `camoprof|mangareader|comix|downloader` (case-insensitive) atas
  seluruh `sharedLogic/pyhost/**/*.py` → **nol kecocokan**. Klaim
  `PyHost.cs:56-57` ("the shared core stays feature-free") dan komentar
  `Citizen.targets:194` **benar**.
- **Dokumen:** `pyhost/README.md:65` menyebut "CamoProf uses a temporary headless
  session…", dan `:154-277` memuat spesifikasi lengkap perintah
  `camoprof.add_profile.*` **di dalam kontrak bersama**. Sementara itu perintah
  hidup milik mangareader (`downloader.open/api/fetch/close`,
  `mangareader_downloader/browser.py:268,405,487,545`) **tidak muncul sama sekali**
  di README bersama. → kepemilikan dokumen protokol asimetris, register D15.
- **Test:** tiga dari empat suite di `sharedLogic/tests/` adalah guard feature
  Add-Profile milik camoprof; `test_add_profile_plugin.py:25-26` menaruh
  `..\camoprof\Features\AddProfile` di `sys.path` dan `:31` menyetel
  `CITADEL_PYHOST_PLUGINS=camoprof_add_profile`. Jadi folder `tests/` bersama punya
  dua pemilik. → register C10.
- **Perbatasan:** `providers/google/__init__.py:3-4` menyebut `enrollment.py`
  (modul plugin camoprof) di docstring-nya — tanpa nama citizen, tapi ini rujukan
  lintas batas.

---

## 3. Sisi C#: tiga file, tiga pekerjaan, dan satu bahaya yang sudah ditangani

| File | Baris | Pekerjaan | Static mutable? |
| --- | --- | --- | --- |
| `cs/PyHost.cs` | 595 | transport NDJSON + korelasi id (`_pending` :36) + wrapper bertipe Google/sesi + tangga kill proses (`Dispose` :291, `Abort` taskkill-tree :352) + pembaca baris terbatas (:430-489) | tidak; hanya konstanta `TimeSpan` immutable (:32-33) |
| `cs/RuntimeSetup.cs` | 578 | probe status + unduh/ekstrak CPython + jalankan proses + buat venv + pip + camoufox fetch | **ya**: `private static readonly HttpClient Http = new();` (:36) |
| `cs/CredenzPath.cs` | 65 | resolusi folder vault: env `CITADEL_CREDENZ` → walk-up ke `module\credenz` (:27-37) → `%LocalAppData%\Citadel\Credenz` (:39-42) | tidak; kelas statis tanpa field |

### Bahaya salinan-per-assembly, dan cara citadel menanganinya

Karena `cs/` di-include sebagai **sumber** (`Module.Camoprof.csproj:10`,
`Module.Mangareader.csproj:10`), tiap citizen punya **salinan tipenya sendiri** di
assembly-nya sendiri. Konsekuensinya: state statis tidak bisa dibagi antar citizen.

Kode ini **tahu** dan merancangnya secara eksplisit: `RuntimeSetup.cs:158-163`
mencatat bahwa "a static semaphore would be per-assembly and useless", lalu
`RunAsync` dijaga **file lock lintas proses** `.setup.lock` (`:172-176`). Jadi
koordinasi bootstrap python antar citizen dilakukan lewat filesystem, bukan lewat
state proses — keputusan yang benar given mekanisme `Compile Include`.

Ini bukti konkret untuk register C1: tepi berbasis sumber punya konsekuensi nyata
yang harus dirancang ulang di setiap tempat yang butuh state bersama.

### Wrapper PyHost di dua citizen: pekerjaan berbeda, bukan duplikat

| Aspek | `camoprof/Features/AddProfile/AddProfilePyHostClient.cs` | `mangareader/Features/Downloader/DownloaderPyHostClient.cs` |
| --- | --- | --- |
| Bentuk | adapter bertipe **stateless** (:17-33) | **pemilik host penuh** (:26) |
| `PyHost`? | tidak memegang; lifetime diserahkan ke `BrowserSessionCoordinator` yang diinjeksi (:26) | `IDisposable`, `PyHost? _host` (:45) |
| Timer | tidak ada | idle-release `Timer` (:44, :58, :414-439) |
| Spawn | tidak; dilakukan koordinator | lazy `EnsureHostCoreAsync` (:386-412) |
| Ekstra | seam test berupa `Func` (:21-24) | `AbortSession` (:225), containment path staging `ResolveContained` (:240-252), kelas timeout sendiri (:29-38) |
| Namespace perintah | `camoprof.add_profile.*` (:51-55) | `downloader.open/api/fetch/close` (:133, :158, :181, :296) |

Analog kepemilikan host di camoprof adalah `sharedLogic/BrowserSessionCoordinator.cs:24`
(registri + gerbang operasi), dan `AddProfilePyHostClient` duduk di atasnya.
**Verdict: dua pekerjaan berbeda, bukan duplikasi.**

### Browser/session host: per-proses, tidak pernah dibagi

Satu instance `_Host` per proses python (`pyhost.py:412`), "one process per owning
view" (`pyhost/README.md:4`), registri sesi adalah state instance (`pyhost.py:85`).
Setiap wrapper C# men-spawn host-nya sendiri: camoprof
`BrowserSessionCoordinator.cs:398`, mangareader Downloader
`DownloaderPyHostClient.cs:409`, mangareader Catalog
`CatalogBrowserClient.cs:396`. Dan `Module.Mangareader.csproj:48-50` menegaskan dua
paket mangareader "never share a browser/session host". Browser-nya Camoufox
persistent context per profil di bawah credenz (`pyhost.py:263-274`).

---

## 4. Flow end-to-end: Add Profile di camoprof

```text
[1]  LauncherView.xaml.cs:87   AddProfileButton_Click
[2]  LauncherView.xaml.cs:95   RunAddProfileAsync → :211-217 bangun dialog + request
[3]  AddProfileFeature.cs:24-28   satu-satunya pintu yang dilihat Launcher
[4]  AddProfileCoordinator.cs:62  ExecuteAsync → :84 RunCoreAsync
[5]  AddProfileCoordinator.cs:92  EnsureHeadedSessionAsync
[6]  AddProfileCoordinator.cs:272 sessions.OpenAsync(profileId,"about:blank",headless:false)
[7]  BrowserSessionCoordinator.cs:83-104 OpenAsync → :374 EnsureHost()
[8]  BrowserSessionCoordinator.cs:398  PyHost.Start(VenvPython, DeployedPyhostScript,
                                        CredenzPath.Resolve(), "camoprof_add_profile")
        ← spawn pertama; sesudahnya host dipakai ulang
[9]  PyHost.cs:59-92  Start → python -u pyhost.py (:76-77),
                      env CITADEL_CREDENZ (:79), CITADEL_PYHOST_PLUGINS (:82)
[10] pyhost.py:411-412 _main → _Host.__init__ :83-90
[11] pyhost.py:155-171 _load_feature_plugins → import camoprof_add_profile → install(self)
[12] camoprof_add_profile/plugin.py:28-35 install →
        register camoprof.add_profile.start/status/finish/cancel (:14-19)
        + lifecycle hook enrollment.disarm_for_session (:23-25)
[13] pyhost.py:252-309 cmd_session_open → :265-274 AsyncCamoufox(persistent_context,
        user_data_dir=<credenz>\google\profiles\<name>) → :283 context, :286 page
        respons {"session":"s1",...}; C# mencocokkan id (PyHost.cs:491-542);
        koordinator mencatat profile→s1 (BrowserSessionCoordinator.cs:103-111)
[14] AddProfileCoordinator.cs:96 StartAsync →
        AddProfilePyHostClient.cs:40-56 bangun camoprof.add_profile.start →
        BrowserSessionCoordinator.cs:225-245 SendSessionCommandAsync menyuntik `session` →
        PyHost.cs:177/:245 tulis NDJSON
[15] pyhost.py:388-399 _handle → commands.py:13-16 cmd_start → enrollment.py:69 start
        :103 ctx.new_page() · pasang listener password JS (password_capture.py:79;
        kesamaan host accounts.google.com ditegakkan di JS :32 dan Python :60)
        :115 host.set_primary_page · :306-311 page.goto(SIGNIN_URL) di latar
[16] poll: AddProfileCoordinator.cs:99-119 StatusAsync → enrollment.py:129 status
        → _advance (:212-244) mesin keadaan; user mengetik password SEKALI di halaman
        Google → password_observed (:241)
[17] selesai: AddProfileCoordinator.cs:174 FinishAsync → enrollment.py:138-160 finish
        mengembalikan {email,password} TEPAT SEKALI, guard satu-pakai
        ENROLLMENT_CONSUMED (:150) → :293 host.retire_session
        (drop registri sinkron, tutup browser di latar; pyhost.py:125-153)
[18] AddProfileCoordinator.cs:202 SaveCredentialAsync →
        Providers/Google/GoogleCredentialStore.cs:94 SaveAsync (DPAPI;
        root = CredenzPath.GoogleAccountsRoot(), :18)
[19] finally: AddProfileCoordinator.cs:79-80 cancel + tutup registri lokal
[20] hasil kembali ke LauncherView.xaml.cs:236
```

Yang layak diperhatikan owner: password **tidak pernah** ditulis oleh python ke
disk; ia dikembalikan sekali lewat protokol, lalu C# menyimpannya terenkripsi DPAPI
di vault (`GoogleCredentialStore.cs:94`). Guard satu-pakai di python
(`ENROLLMENT_CONSUMED`, `enrollment.py:150`) mencegah pengambilan kedua.

---

## 5. Konsumen `CitadelBridge` (13 file)

| Citizen | Jumlah | File |
| --- | --- | --- |
| camoprof | 8 | `Launcher/LauncherView.xaml.cs:5`, `Runtime/RuntimeView.xaml.cs:4`, `Providers/Google/GoogleCredentialStore.cs:7`, `sharedLogic/BrowserSessionCoordinator.cs:3`, `sharedLogic/ProfileCatalog.cs:3`, `Features/AddProfile/AddProfileCoordinator.cs:2`, `Features/AddProfile/AddProfilePyHostClient.cs:2` (+1 pemakaian ctor) |
| mangareader | 4 | `Features/Downloader/DownloaderPyHostClient.cs:3`, `Features/Downloader/Sources/Comix/ComixSource.cs:6`, `Features/Catalog/Runtime/CatalogBrowserClient.cs:3`, `Features/Catalog/Sources/Comix/CatalogComixSource.cs:6` |
| tests | 1 | `tests/Module.Camoprof.Tests/AddProfileCoordinatorTests.cs:3` |

`ftf`, `proxy`, dan `blank` **tidak** memakai `sharedLogic/cs` — cocok dengan
temuan bahwa keduanya kerang (§STRUCTURE 6) dan bahwa hanya 2 dari 6 citizen yang
me-link `cs/`.

---

## 6. Test python: ada, tapi tidak masuk gate mana pun

- Suite memakai **unittest stdlib**, bukan pytest (`test_pyhost.py:3-4` memuat
  perintah jalannya), dijalankan manual dengan venv runtime.
- `.github/workflows/ci.yml:33-35` hanya menjalankan `dotnet test Citadel.slnx`.
  Hook `gate-on-stop.mjs:34` juga hanya itu.
- Suite python hanya muncul sebagai gate di plan lama
  (`.docs/PLAN-camoprof.md:330` G0b; `.docs/PLAN-ownership-shared-ui-2026-09-05.md:226`).

Jadi seluruh jembatan python — termasuk protokol yang mengangkut kredensial —
**tidak diuji oleh gate otomatis mana pun**. → register C9.

---

## 7. Duplikasi dan drift yang ditemukan

| Temuan | Bukti |
| --- | --- |
| Logger stderr ditulis dua kali; import-nya mati | `pyhost.py:27` mengimpor `log as _log` dari `providers`, lalu `pyhost.py:37-39` mendefinisikan `_log` identik yang menaungi import. Badan yang sama ada di `providers/__init__.py:19-21` |
| README `sharedLogic/` drift | `:15` menyebut cs/ sebagai "(PyHost client, RuntimeSetup)" — `CredenzPath.cs` tidak disebut; pohonnya (`:7-18`) tidak memuat `pyhost/providers/` dan `tests/`; `:30-31` mengatakan payload deploy adalah "pyhost/pyhost.py + requirements.txt" padahal `Citizen.targets:174` men-deploy `pyhost\**\*.py` (seluruh pohon termasuk providers). Bandingkan `Module.Camoprof.csproj:6` yang menyebut ketiganya |
| Catatan "v2 belum diimplementasi" untuk kemampuan yang sudah rilis | `pyhost/README.md:338-343` mencadangkan browser-context fetch untuk manga downloader sebagai "v2 — not implemented… do not build ahead", padahal sudah terkirim sebagai plugin v1 (`browser.py:405,487`; `DownloaderPyHostClient.cs:168-201`) |
| Aturan path dilanggar oleh skrip referensi | `CredenzPath.cs:6-8` dan `pyhost/README.md:33-34` mewajibkan "Python never computes a path itself", tapi `reference/human_login.py:22-25` menghitung root credenz dengan walk-up 4 tingkat. Termitigasi: reference didokumentasikan "not executed" (`README.md:13-14`) |
| Deploy payload bersama punya pemilik kedua | `Citizen.targets:196-214` mendukung satu `PyhostPluginName`; mangareader menambah `DeployMangaReaderCatalogPyhostPlugin` sendiri (`Module.Mangareader.csproj:51-65`) yang menduplikasi logika `MakeDir` + dua `Copy` + `plugin.py`→`__init__.py` → register B4 |

---

## 8. Yang sudah cocok dengan konsep kernel yuz-ui

| Konsep yuz-ui | Padanan di pyhost | Bukti |
| --- | --- | --- |
| Child mendaftar lewat kehadiran | paket plugin diimpor dari daftar env, `install(host)` | `pyhost.py:155-171` |
| Parent mendengarkan, tak menyebut nama child | `_Host` tidak tahu isi plugin | `pyhost.py:92-106` |
| Sibling saling asing | arah dependensi plugin → core saja | `pyhost/README.md` PROTOCOL v1 |
| Fault isolation | plugin gagal dilog, host hidup | `pyhost.py:169-171` |
| Id unik, tabrakan ditolak keras | `COMMAND_COLLISION` | `pyhost.py:97-99` |
| Domain tidak menyentuh platform | python tidak menghitung path; C# yang memberi | `CredenzPath.cs:6-8`, `PyHost.cs:79` |
| State bersama lewat mekanisme yang sadar batas | file lock lintas proses, bukan semaphore statis | `RuntimeSetup.cs:158-176` |
| Kontrak tertulis sebagai sumber kebenaran | "if code and this file disagree, the file wins" | `pyhost/README.md:6-7` |

---

## 9. Rujukan register dari slice ini

A7 (catatan "v2 not implemented" untuk kemampuan yang sudah rilis) ·
A8 (drift `sharedLogic/README.md`) ·
B4 (satu `PyhostPluginName` per citizen → target duplikat) ·
C1 (tepi `Compile Include` dan konsekuensi salinan-per-assembly) ·
C9 (suite test python tidak masuk gate) ·
C10 (`sharedLogic/tests/` berisi guard feature camoprof) ·
D10 (`PyHost.cs` memegang beberapa pekerjaan, termasuk wrapper khusus Google di
transport "generik") ·
D11 (`RuntimeSetup.cs` memegang beberapa pekerjaan) ·
D14 (`_log` ditulis dua kali, import mati) ·
D15 (dokumen protokol asimetris: camoprof lengkap, mangareader tidak ada).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.
