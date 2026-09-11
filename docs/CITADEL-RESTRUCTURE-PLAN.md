# CITADEL-RESTRUCTURE-PLAN — target struktur + plan ber-phase

**Status: RENCANA. Belum satu baris kode pun diubah.** Ditulis 2026-09-11 atas
perintah owner, setelah fase dokumentasi selesai. Mengikuti pola
`yuz-ui/IMPLEMENTATION-PLAN.md`: tiap phase punya success criteria bertanda
**V** (terlihat mata) atau **T** (test/headless), dan gate keluar yang wajib hijau.

Keadaan **sekarang** tidak ditulis di sini — ia ada di `CITADEL-FLOWS-*.md` dan
`CITADEL-VIOLATIONS.md`. Dokumen ini hanya bentuk yang **diinginkan**.

Aturan main:

- Tiap phase dikerjakan **hanya bila owner memerintahkan phase itu eksplisit**.
- Satu phase hijau sebelum phase berikutnya mulai; tidak ada lompatan.
- Phase 0 wajib pertama: penjaga dipasang **sebelum** struktur dipindah, supaya
  kerapian yang dicapai tidak busuk lagi diam-diam.
- Reader, pyhost, dan discovery **tidak direstrukturisasi** — mereka sudah benar
  dan menjadi acuan (§7).

---

## 1. Target struktur repo

Prinsip penamaan: **folder menceritakan peran**, dan hanya ada empat peran di
level atas — mekanisme (kernel), gudang (shared), plugin (module), dan penunjang
(tests/tools/vault).

```text
citadel/
├─ core/                            KERNEL — mekanisme; tidak mendaftar ke mana pun
│   ├─ Citadel.Core/                fondasi bebas WPF: rpl · crl · tokens · Log · PowerSaving
│   ├─ Citadel.Contract/            kontrak citizen; SATU-SATUNYA pintu; daun sejati
│   │                               (Lifetime pindah ke sini dari Core/rpl — Phase 8)
│   ├─ Citadel.Shell/               composition root · ModuleGate · Router · LayoutApplier
│   │                               · SettingGate (BARU) · CoreGate (BARU)
│   │                               · ResidentShell · TrayHost · SingleInstance · AppUpdate
│   ├─ Citadel.Ui/                  chrome navigasi · pompa animasi · jembatan tema
│   ├─ Citadel.Searcher/            mesin discovery            ← PINDAH dari module/
│   ├─ Citadel.PyHost/              GATE 2: host python, protokol, registri sesi
│   │                               ← PINDAH dari module/sharedLogic/pyhost
│   ├─ Citadel.PyClient/            pustaka klien C#: transport NDJSON, bootstrap runtime,
│   │                               path vault                 ← PINDAH dari module/sharedLogic/cs
│   └─ Citizen.targets              kontrak build citizen      ← PINDAH dari module/
│
├─ setting/                         GUDANG + layar bawaan (nama DIPERTAHANKAN)
│   ├─ Components/                  14 kontrol bersama + perilaku universalnya
│   ├─ Screens/                     layar setting bawaan; mendaftar lewat SettingGate
│   ├─ LayoutEditor/                editor slot layout + preset Gallery
│   └─ SettingResources.xaml        style/token/template bersama
│
├─ module/                          HANYA citizen drop-in — SEMUA isinya boleh dihapus bersih
│   ├─ blank/                       template
│   ├─ mangareader/                 struktur internal: §3
│   ├─ camoprof/                    struktur internal: §4
│   └─ ftf/ · proxy/                menunggu keputusan owner (Phase 7)
│
├─ vault/                           kredensial — DI LUAR module/, tetap gitignored
│                                   ← PINDAH dari module/credenz (Phase 5)
├─ tests/                           11 proyek C# + suite python kini masuk gate (Phase 0)
└─ tools/ · .github/                rilis & CI
```

### Kenapa begini, dan kenapa tidak lebih jauh

| Keputusan | Alasan |
| --- | --- |
| `setting/` **tidak** diganti nama jadi `shared/` | nama ini sudah jadi rujukan di `SHARED-UI-BEHAVIOR.md`, skill `citadel-shared-ui`, dan seluruh XAML citizen (`xmlns:setting`). Mengganti nama = churn besar tanpa menambah kejelasan peran |
| Assembly name & namespace **tidak ikut pindah** untuk proyek core | folder ≠ namespace di .NET; `Citadel.Searcher` tetap `namespace Citadel.Searcher` walau foldernya pindah. Yang load-bearing adalah namespace `Citadel.Core.Modules` pada `IModule` (`IModule.cs:10-11`) — itu **tidak disentuh** |
| Folder citizen **tidak dipindah** | `Citizen.targets` menurunkan `RootNamespace`/`AssemblyName` dari nama folder citizen (`Citizen.targets:29-50`); memindah folder citizen = mengganti namespace seluruh isinya |
| `vault/` di root repo, bukan di luar repo | `CredenzPath.cs:27-37` walk-up mencari vault; memindah ke luar repo memutus alur dev. Gitignore berlapis tetap berlaku. Opsi "di luar repo" tetap terbuka bila owner menghendaki |
| `Citadel.PyHost` dan `Citadel.PyClient` dipisah | hari ini satu folder `sharedLogic` mencampur **gate** (host python) dengan **pustaka** (klien C#). Dua peran, dua folder — persis keluhan heksagonmu |

---

## 2. Target diagram gate (bahasa gambarmu)

Bulat = gate (mendengarkan). Heksagon = gudang (hanya dipakai). Kotak = anak
yang mendaftar. Panah anak → gate.

```text
                          FEATURE FEATURE                FEATURE FEATURE FEATURE FEATURE
                          (tray) (resident)              (Library) (History) (Downloader) ...
                             │  │                              │  │  │  │
                             ▼  ▼                              ▼  ▼  ▼  ▼
                        ( CORE GATE )◀──────────────▶( MANGAREADER GATE )   ← per citizen
                             ▲                              ▲
                             │                              │
                        ( SETTING GATE )                    │
                          ▲  ▲  ▲  ▲                        │
        (appearance)(layout)(gallery)(sidebar-groups)       │
                             │                              │
   ┌──────────────┐          │         ( MODULE GATE / group sidebar )
   │ GUDANG       │◀─────────┤                    ▲   ▲   ▲
   │ SHARED       │          │                    │   │   │
   │ COMPONENT    │◀─────────┼────────────────────┼───┼───┼────── citizen memakai gudang
   └──────────────┘          │                    │   │   │
                             │              MANGA READER  CAMOPROF  (FTF?)
   ┌──────────────┐          │                    │
   │ GUDANG       │◀─────────                    ▼
   │ PY CLIENT    │                         ( PYHOST HOST )   ← gate python, sudah ada
   └──────────────┘                            ▲   ▲   ▲
                                               │   │   └── mangareader_catalog      FEATURE python
                                               │   └────── mangareader_downloader   FEATURE python
                                               └────────── camoprof_add_profile     FEATURE python
```

Perbedaan dengan gambarmu, semuanya sudah dibahas dan kamu setujui arahnya:

1. **PYHOST HOST adalah gate**, bukan feature core — dan ia sudah ada, di python.
2. Panah gudang py client datang dari **citizen**, bukan dari module gate.
3. CORE GATE dan SETTING GATE **belum ada** hari ini; keduanya Phase 6.
4. MANGAREADER GATE / CAMOPROF GATE **belum ada** dalam bentuk ini; Reader sudah
   punya padanannya (`ReaderFeatureHost`) dan menjadi cetak biru.

---

## 3. Target struktur dalam citizen — cetak biru dari Reader

Pola yang harus dimiliki **setiap** citizen, diambil dari yang sudah terbukti di
`mangareader/Reader/`:

```text
module/<citizen>/
├─ <Citizen>Module.cs            adapter shell; 12–17 baris; tanpa pengetahuan feature
├─ <Citizen>Gate.cs              composition root TIPIS:
│                                  · membangun infrastruktur (context, hub, store)
│                                  · memuat <Citizen>FeatureCatalog
│                                  · menegakkan invarian bentuk, BUKAN isi
│                                  · TIDAK menyebut satu pun tipe feature konkret
├─ <Citizen>FeatureCatalog.cs    SATU-SATUNYA tempat nama feature disebut (1 baris per feature)
├─ <Citizen>Context.cs           container yang dihadapi feature (FM-4):
│                                  feature MENARIK kebutuhannya, bukan diantar parent
├─ <Citizen>View.xaml(.cs)       host tab/layar SAJA; navigasi lewat router tab,
│                                  bukan menyetel IsSelected dari code-behind
├─ Features/
│   └─ <Nama>/                   satu folder = satu feature
│       ├─ <Nama>Feature.cs      kontrak yang dilihat gate
│       ├─ [internal]            bebas; tidak boleh dilihat dari luar folder
│       └─ [<paket_python>/]     plugin pyhost milik feature ini (bila perlu)
├─ Components/                   combo component (komposisi primitif bersama)
└─ shareLogic/                   HANYA kernel bersama module (kontrak lintas feature);
│                                  file dengan satu konsumen pindah ke konsumen itu
└─ (tidak ada)                   parent tidak memegang logika feature apa pun
```

Aturan yang ditegakkan test (Phase 0), bukan niat:

- tidak ada tipe internal feature yang muncul di luar foldernya;
- parent/gate tidak menyebut tipe feature konkret di luar file katalog;
- feature ↔ feature hanya lewat hub/event/kontrak;
- menambah feature = folder baru + **satu baris** katalog (+ entri test bila ingin
  ter-cover).

### Contoh terapan: mangareader

```text
module/mangareader/
├─ MangaReaderModule.cs          tetap (12 baris)
├─ MangaReaderGate.cs            BARU — pengganti 412 baris MangaReaderView.xaml.cs
├─ MangaReaderFeatureCatalog.cs  BARU — 6 feature tingkat atas, 6 baris
├─ MangaReaderContext.cs         BARU — root library, queue, sources, index, history
├─ MangaReaderView.xaml(.cs)     menyusut jadi host tab
├─ Features/
│   ├─ Library/      (menyerap Grouping + UpdateChecker sebagai internalnya)
│   ├─ History/
│   ├─ Downloader/   (Sources di dalamnya jadi children yang mendaftar — §5)
│   ├─ Catalog/
│   ├─ CatalogMirror/
│   ├─ Reader/       TIDAK DIUBAH — sudah benar; jadi acuan
│   └─ CoverBuilder/
├─ Components/                   tetap
└─ shareLogic/                   menyusut: 3 file Reader-only pindah ke Features/Reader/
```

Logika yang hari ini nyasar di parent pindah ke pemiliknya:

| Logika | Pemilik baru |
| --- | --- |
| `CheckCatalogAvailability` (verdict ketersediaan lokal) | `Features/CatalogMirror/` |
| `ConfirmQueueTarget` (aturan tabrakan folder) | `Features/Downloader/` |
| `EnqueueUpdate` (translasi hasil queue) | `Features/Library/UpdateChecker/` |
| `ReadConfirmedMapping` (baca store index) | `Features/Downloader/` |
| pencatatan history saat reader terbuka | `Features/History/` (lewat event reader) |

### Contoh terapan: camoprof

```text
module/camoprof/
├─ CamoprofModule.cs             tetap
├─ CamoprofGate.cs               BARU — pengganti konstruksi 9 subsistem di CamoprofView
├─ CamoprofFeatureCatalog.cs     BARU — AddProfile, ProfileActions (+ kelak yang baru)
├─ CamoprofContext.cs            BARU — catalog profil, session coordinator, network, credential store
├─ CamoprofView.xaml(.cs)        host tab Launcher/Runtime SAJA
├─ Features/{AddProfile,ProfileActions}/   tetap, jadi anak katalog
├─ Providers/Google/ · Network/ · Launcher/ · Runtime/   tetap
└─ sharedLogic/                  BrowserSessionCoordinator pindah ke CamoprofContext;
                                 nama plugin jadi DATA milik feature, bukan string hardcoded
```

Tab "Editor" yang yatim: **dihapus atau diisi** — keputusan owner di Phase 4.

---

## 4. Target registrasi provider (children mendaftar)

Hari ini: `MangaSourceRegistry.CreateDefault` menulis `new` per provider
(`MangaSourceRegistry.cs:108-129`) + `ManualUrlProbeRegistry` sebagai titik kedua.

Target: registry menjadi **host**, provider menjadi **children yang mendaftar**.

```text
module/mangareader/shareLogic/Sources/
├─ MangaSourceContracts.cs       kontrak IMangaSource + facet (tetap)
└─ MangaSourceRegistry.cs        HOST: menemukan provider, menegakkan bentuk,
                                 menolak tabrakan id — seperti _Host di pyhost

module/mangareader/Features/Downloader/Sources/
└─ <Provider>/                   child: terdaftar karena ADA
    ├─ <P>Source.cs              ditandai [MangaSource] atau konvensi nama
    ├─ <P>FilterContribution.cs
    └─ <P>ManualUrlProbe.cs      opsional; facet ikut mendaftar sendiri
```

Mekanisme yang dipilih: **scan assembly** atas tipe yang mengimplementasikan
`IMangaSource` (padanan glob di .NET), dengan test penjamin:

> "provider terdaftar == folder provider yang ada" — child yang gagal mendaftar
> merobohkan `dotnet test`, bukan hilang diam-diam di runtime.

Ini persis test yang diminta D-CONTRACT yuz-ui, dan persis bentuk guard ARCH yang
sudah ada di python (`test_add_profile_architecture.py`).

Konsekuensi yang hilang sekaligus: edit `MangaSourceRegistry` per provider (B3),
edit `ManualUrlProbeRegistry` per provider (A10), dan — karena hanya ada satu
provider Comix setelah Phase 1 — kelas bug BUG-1 menjadi mustahil.

---

## 5. Phase dan success criteria

### Phase 0 — Penjaga dulu, struktur kemudian

**Isi:**
1. Test per citizen: "tepi internal yang diizinkan" (assert tidak ada tipe internal
   feature di luar foldernya; assert gate tidak menyebut tipe feature konkret).
2. CI menjalankan suite python `module/sharedLogic/tests/` (C9).
3. Gate Stop/CI mengompilasi `ftf`, `proxy`, `blank` (C6).
4. Hook `check-project-refs.mjs`: peringatkan `Compile Include` lintas folder dan
   proyek core/setting yang tidak dikenal (C1, C8).

**Success T:** gate merah bila seseorang menambah tepi terlarang; suite python jalan
di CI; ketiga citizen terkompilasi gate.
**Bukti penutup:** satu commit sengaja melanggar (tambah `using` lintas feature)
ditolak gate, lalu dibatalkan.
**Tidak menyentuh:** perilaku apa pun. Nol perubahan runtime.

### Phase 1 — BUG-1 dan satu Comix

**Isi:** satu `ComixContractException` di `shareLogic/Sources/`; satu provider Comix
dipakai Downloader **dan** Catalog; proses/client browser tetap dua (pemisahan itu
benar dan dipertahankan); hapus duplikat ±3.100 → ±1.600 baris.
**Success T:** test baru melempar 429/502/503 dari sumber Catalog ke
`CatalogMirrorSyncFeature` dan **assert backoff terjadi** — test yang tidak ada
hari ini dan yang akan merah sebelum perbaikan; suite Downloader + CatalogMirror
hijau sesudahnya.
**Success V:** tidak ada (headless).
**Gate keluar:** suite hijau + bug tidak bisa direproduksi lewat test baru.

### Phase 2 — Putus siklus Archive ↔ Rar

**Isi:** `IArchiveFormatProvider` di `shareLogic/Archive/`; `Features/Rar` mendaftar
sebagai penyedia format RAR; `ArchivePageReader` menjadi dispatcher murni.
**Success T:** test arah-dependensi satu arah (grep-based, meniru guard ARCH python);
suite Archive hijau.
**Tidak menyentuh:** perilaku ekstraksi; `Rar.exe` dan cara ia dipanggil.

### Phase 3 — Parent mangareader setipis Reader

**Isi:** `MangaReaderGate` + `MangaReaderFeatureCatalog` + `MangaReaderContext`;
`MangaReaderView` menyusut jadi host tab; navigasi tab lewat router tab; lima blok
logika pindah ke pemiliknya (§3).
**Success T:** test "gate tidak menyebut tipe feature konkret"; menambah satu feature
dummy = folder + satu baris katalog.
**Success V:** keenam tab berfungsi identik — screenshot sebelum/sesudah pada ukuran
normal dan minimum.
**Gate keluar:** suite mangareader hijau + screenshot disetujui owner.

### Phase 4 — camoprof: katalog + data plugin + tab yatim

**Isi:** `CamoprofGate` + `CamoprofFeatureCatalog` + `CamoprofContext`; nama plugin
pyhost jadi data milik feature; tab Editor dihapus atau diisi (keputusan owner).
**Success T/V:** sama seperti Phase 3, skala lebih kecil.

### Phase 5 — Keluarkan kernel dari `module/`

**Isi:** `module/Citadel.Searcher` → `core/`; `module/Citizen.targets` → `core/`;
`module/sharedLogic` dipecah → `core/Citadel.PyHost` + `core/Citadel.PyClient` +
`tests/` + dokumen; `module/credenz` → `vault/` (+ perbarui walk-up
`CredenzPath.cs:27-37`); `module/` tinggal `blank` + citizen.
**Success T:** build + seluruh suite hijau; `Build-Release.ps1` menghitung citizen
dengan benar; payload python tetap ter-deploy sebagai sibling `module\`.
**Success V:** app boot identik; sidebar dan discovery tidak berubah perilaku
(watcher tetap memantau `AppContext.BaseDirectory/module`).
**Risiko terbesar phase ini:** path deploy dan CI — bukan perilaku.

### Phase 6 — GATE 4 dan GATE 5

**Isi:** `SettingGate`: sub-screen setting mendaftar lewat katalog, menghapus
hardcode di tiga tempat (`ReservedRoutes`, `PopupRoute`, `BuiltInRoutes`) menjadi
satu sumber; `CoreGate`: tray/resident/update/sidebar-grouping mendaftar ke gate
shell alih-alih di-`new` oleh `App`.
**Success T:** menambah satu sub-screen setting = satu baris katalog; menambah satu
komponen shell = satu baris katalog.
**Success V:** Settings dan perilaku tray/close-vs-exit identik.

### Phase 7 — ftf/proxy: keputusan dan pemulihan

**Isi:** coba pulihkan sumber python dari `.pyc` dan
`artifacts/recovery-ftf-proxy-2.1.3-20260907`; bila pulih → jadikan citizen utuh
dengan pola §3; bila tidak → keputusan owner: pensiunkan (hapus folder + route) atau
biarkan kerang dengan catatan eksplisit.
**Success:** keputusan owner tercatat di dokumen; tidak ada `.pyc` yatim tersisa.

### Phase 8 — Contract tipis (keputusan owner)

**Isi:** `Lifetime` pindah ke `Citadel.Contract`; Contract menjadi daun sejati;
tabel `ALLOWED` hook berubah menjadi `"Citadel.Contract": []`; kebijakan citizen
boleh dipersempit bila owner menghendaki.
**Success T:** seluruh citizen tetap compile tanpa perubahan; hook menolak bila
Contract menambah referensi.
**Risiko:** `IModule` ditandai "Verbatim from v0 — do not alter"
(`IModule.cs:7`) dan namespace-nya sengaja `Citadel.Core.Modules`
(`:10-11`). Phase ini menyentuh wilayah itu — hanya atas keputusan owner.

---

## 6. Urutan dan ketergantungan

```text
P0 penjaga ─▶ P1 BUG-1+Comix ─▶ P2 siklus ─▶ P3 parent mangareader ─▶ P4 camoprof
                                                                        │
                          P8 contract tipis ◀─ P6 gate 4&5 ◀─ P5 kernel keluar ◀┘
                                                    │
                                                    └─ P7 ftf/proxy (paralel, mandiri)
```

P7 mandiri: bisa kapan saja setelah P0. P8 terakhir karena menyentuh wilayah
paling sensitif dan tidak bergantung pada yang lain.

---

## 7. Yang TIDAK berubah (daftar anti-jangkauan)

| Tidak disentuh | Alasan |
| --- | --- |
| `mangareader/Reader/` internal | sudah benar; menjadi cetak biru (§3) |
| protokol pyhost + mekanisme `install(host)` | sudah gate yang benar (B5) |
| discovery module (`Watcher`, `Loader`, `Reader`, `ModuleGate`) | sudah kernel yang bekerja |
| kepatuhan shared-UI di semua citizen XAML | sudah patuh penuh; nol duplikasi |
| graf dependensi antar proyek | sudah identik dengan yang diizinkan hook |
| namespace `Citadel.Core.Modules` pada `IModule` | load-bearing untuk kompatibilitas citizen |
| keamanan vault (gitignore berlapis, DPAPI) | sudah benar |
| pemisahan proses browser Downloader vs Catalog | keputusan yang benar; yang diduplikasi logika providernya, bukan prosesnya |

---

## 8. Biaya dan risiko lintas phase

| Risiko | Phase | Mitigasi |
| --- | --- | --- |
| churn namespace saat memindah isi `sharedLogic` mangareader | 3 | internal satu citizen; tidak menyentuh kontrak publik |
| path deploy python & perhitungan rilis | 5 | test rilis kering (`Build-Release.ps1` dry-run) sebelum merge |
| walk-up vault berubah perilaku dev | 5 | test `CredenzPath` untuk ketiga fallback |
| kompatibilitas citizen lama | 8 | namespace `IModule` tidak disentuh; citizen tidak perlu edit |
| screenshot parity tab | 3, 4, 6 | kriteria V wajib disetujui owner sebelum gate keluar |
| godaan merapikan yang tidak rusak | semua | §7 adalah daftar penolak; pelanggaran daftar ini = phase baru, bukan bagian phase berjalan |

---

## 9. Cara owner memverifikasi tanpa membaca kode

1. **Cocokkan tree dengan §1.** Setiap folder punya satu peran; folder dengan dua
   peran = phase belum selesai.
2. **Cocokkan diagram dengan §2.** Bulat mendengarkan, heksagon hanya dipakai,
   kotak mendaftar. Panah gudang→gate = pelanggaran yang terlihat dari gambar.
3. **Minta output gate per phase.** Phase yang klaimnya tidak disertai output gate
   hijau = belum selesai, apa pun katanya.
4. **Minta screenshot untuk kriteria V.** Phase 3, 4, 6 wajib punya sebelum/sesudah.
5. **Tanya satu angka:** "menambah feature X menyentuh berapa file?" Jawaban yang
   benar setelah phase terkait: **folder baru + satu baris katalog**.


## Aturan pelaksanaan (ditambahkan 2026-09-11, setelah insiden B2)

Latar belakang: langkah B2 (manifest test per-file menjadi wildcard) dikerjakan
tanpa metode tertulis lebih dulu. Eksekusinya menjadi rangkaian percobaan regex
di working tree, dua kali menghasilkan keadaan merah, sebelum di-revert. Itu
percobaan, bukan pelaksanaan rencana, dan tidak boleh terulang.

Aturan wajib untuk setiap langkah berikutnya:

1. Tulis METODE konkret sebelum menyentuh file: daftar file yang berubah,
   transformasi persis yang akan dilakukan, bentuk khusus yang harus ditangani
   (misalnya pasangan Page/Compile dan DependentUpon untuk proyek WPF), cara
   verifikasi, dan cara rollback.
2. Kalau saat eksekusi muncul sesuatu yang tidak ada di metode tertulis,
   BERHENTI. Jangan iterasi ad-hoc di working tree. Perbarui metode dulu, baru
   lanjut.
3. Satu langkah = satu diff terkendali = suite hijau = commit checkpoint.
   Tidak ada langkah yang menumpuk beberapa percobaan sekaligus.
4. Langkah yang bersifat optimasi (bukan perbaikan atas kerusakan nyata) boleh
   ditunda tanpa menghalangi langkah lain; penundaan dicatat, bukan dipaksakan.
5. Checkpoint adalah sumber kebenaran: 5f845aa (steps 1-4), 9bc33ac (step 5).
   Keadaan merah apa pun yang tidak terencana langsung di-revert ke checkpoint.


## Review pekerjaan + gap plan (2026-09-11)

### Keputusan owner
- ftf dan proxy DIBIARKAN sebagai placeholder. Tidak ada pemulihan .pyc dan tidak ada
  pensiun module. D9 tertutup sebagai keputusan, bukan sebagai pekerjaan.

### Verdict review (steps 1-6, checkpoint 5f845aa / 9bc33ac / a3962f1)
- Semua citizen (ftf, proxy, blank, camoprof, mangareader) dan Citadel.Shell
  COMPILE; suite penuh 9 proyek test hijau (929 test), termasuk 3 guard Level-B.
- Perubahan PERILAKU yang disengaja hanya SATU: BUG-1. Sebelumnya backoff
  429/502/503 pada sync CatalogMirror adalah kode mati (throttle langsung jatuh ke
  jalur gagal); sekarang ia retry dengan backoff. Ini perbaikan, bukan regresi.
- Seluruh perubahan lain behavior-identik:
  - Rar: mekanisme pindah file (RarArchiveEngine), logika sama, konsumen dipointkan.
  - Comix: 4 tipe murni-data disatukan; 3 tipe pembawa default rating sengaja tetap
    per-provider sehingga default browse kedua provider tidak berubah.
  - Handoffs: pemindahan verbatim; parent hanya komposisi+navigasi+lifecycle.
- Tidak ada perubahan pada: namespace feature, interface/delegate contract,
  perilaku shared-UI, discovery module, graf dependensi antar proyek.
- Kejujuran verifikasi: pembuktian lewat build + 929 test + 237 test Uia yang
  menjalankan shell in-process. TIDAK ada verifikasi live menjalankan app WPF;
  itu gap yang diakui (lihat gap 4).

### Gap plan yang teridentifikasi
1. Guard Level-B baru ada untuk mangareader. camoprof BELUM punya; tepi internalnya
   (parent meng-hardwire 9 subsistem, sharedLogic lokal meng-hardcode nama plugin)
   belum dijaga mesin. Perlu step: guard camoprof.
2. C6/C8 masih terbuka: ftf/proxy/blank tidak dikompilasi CI, dan hook tidak menjaga
   proyek core/setting baru yang belum masuk tabel ALLOWED. Meski ftf/proxy jadi
   placeholder, memasukkannya ke compile-gate tetap bernilai agar placeholder tidak
   rusak diam-diam.
3. B2 tertunda (manifest test per-file). Optimasi, bukan perbaikan.
4. Tidak ada langkah ACCEPTANCE live: plan belum mewajibkan smoke menjalankan app
   (buka tiap tab, sync, queue) setelah refactor. Test tidak membuktikan UI.
   Ditambahkan sebagai syarat keluar wajib untuk step refactor berikutnya.
5. Sisa C14 (katalog/gate seragam untuk tab mangareader dan camoprof) belum
   dijadwalkan sebagai step tersendiri; ia refactor interface besar.
6. D1 (kernel keluar dari module/) dan D6 (contract tipis) adalah keputusan owner,
   belum menjadi step eksekusi.


## Review steps 7-9 (2026-09-11)

- Step 7 (guard camoprof): checkpoint 50f9c63. Baseline scan: 1 tepi lintas-feature
  (ProfileActions->AddProfile, kontrak saja) dan 0 tipe bernama sama; keduanya jadi
  allow-list tertulis. Test 56 menjadi 58, hijau.
- Step 8 (C6/C8): C6 masuk CI (step Build citizens). C8 diperkuat di hook
  check-project-refs dan DIVERIFIKASI dengan payload sintetis: proyek core baru
  ber-referensi = block; module baru = lolos; Citadel.Shell nyata = lolos.
- Step 9: docs/SMOKE-CHECKLIST.md dibuat sebagai syarat keluar wajib step refactor.
- Audit diff a3962f1..1582068: NOL kode produksi. Hanya 1 file test baru, 1 workflow
  CI, dan docs. Suite 9/9 proyek hijau (931 test).
- Perilaku app tidak berubah sejak a3962f1 (yang sudah diverifikasi behavior-identik
  kecuali perbaikan BUG-1 yang disengaja).
- Smoke checklist BELUM dieksekusi (butuh live run). Restructure belum boleh
  dinyatakan selesai sebelum checklist lulus penuh.

### Temuan review baru (gap 7): penegakan batas tidak ter-version-control
.gitignore hanya melacak .agents/skills/citadel-shared-ui/SKILL.md. Akibatnya
hook check-project-refs.mjs dan gate-on-stop.mjs, serta skill
citadel-feature-modularity, hanya ada di mesin ini: clone baru atau agent di
environment lain TIDAK mendapatkan penegakan batas maupun kontrak modularity, dan
perubahan C8 pada hook belum masuk version control. Klaim "Level A machine-enforced"
karena itu benar hanya untuk harness lokal ini.
Rekomendasi (keputusan owner, karena mengubah .gitignore): un-ignore
.agents/hooks/ dan .agents/skills/citadel-feature-modularity/ agar penegakan dan
kontrak ikut repo.


## Status steps 10-13 (2026-09-11)

- Step 10 (gap 7): bde227f. Hook + skill modularity kini ter-version-control;
  hook sintetis pasca-commit tetap block untuk proyek core baru ber-referensi.
- Step 11 (D6): 8b6031e + e028fbe. Lifetime + Log pindah ke Citadel.Contract dengan
  namespace tetap; tepi dibalik Core->Contract; Contract = daun. Test invariant
  metadata assembly diperbarui ke graf baru (Core tepat satu referensi Citadel;
  Contract nol). Suite 9/9.
- Step 12 (B2): 907f071 + 97186da. Wildcard untuk Downloader.Tests dan Reader.Tests
  (proyek yang memang mengompilasi view). Library/History/Camoprof SENGAJA tetap
  kurasi: proyek test mereka non-WPF dan tidak mereferensikan Citadel.Setting,
  sehingga meng-glob view akan mengubah hakikat proyek tersebut. Keputusan ini
  tercatat, bukan kegagalan metode.
- Step 13 (D1): a313f06. Citadel.Searcher dan Citizen.targets pindah ke core/.
  Pelajaran tercatat: core/ dan module/ sama-sama satu level di bawah root, jadi
  kedalaman CitadelRoot tidak berubah; percobaan mengubahnya sempat merusak semua
  citizen dan segera dikembalikan dalam langkah yang sama.
- Step 14 BELUM dieksekusi: syarat keluarannya adalah SMOKE CHECKLIST lulus penuh,
  dan smoke hanya bisa dijalankan live (app WPF). Sesuai aturan pelaksanaan, step
  14 tidak dijalankan sebelum smoke tersedia.
- Step 15 = eksekusi smoke oleh owner (atau sesi run terpisah), lalu step 14,
  lalu smoke ulang sebagai pintu keluar keseluruhan.


## Smoke parsial oleh mesin (2026-09-11)

- App DI-LAUNCH dari build Debug terbaru (memuat steps 1-13): proses hidup stabil
  sekitar empat menit tanpa crash, lalu instance uji dimatikan bersih (PID uji
  12468; instance owner 14652 dibiarkan berjalan).
- Screenshot desktop menunjukkan window reader merender konten dengan benar
  (pada instance owner yang masih build lama).
- YANG TIDAK BISA diverifikasi mesin: poin 2-10 checklist (buka tiap tab, refresh
  Library, history, reader, sync start/stop, handoff queue, Settings + sub-screen,
  tray hide/restore/exit, resize). Semuanya butuh mata manusia.
- Konsekuensi sesuai Opsi A: owner menjalankan SMOKE CHECKLIST poin 1-10 terhadap
  build BARU (tutup instance lama 14652 dulu, lalu jalankan
  core/Citadel.Shell/bin/Debug/net10.0-windows/Citadel.Shell.exe atau build Release).
  Setelah owner melaporkan lulus, Step 14 dikerjakan, lalu smoke diulang sebagai
  pintu keluar keseluruhan.


## Smoke lewat workflow rilis yang benar (2026-09-11)

Koreksi atas percobaan smoke pertama: menjalankan exe Debug secara langsung BUKAN
workflow yang didukung. RELEASE.md menyatakan app membutuhkan published assemblies,
Components/, dan folder citizen di sebelahnya; karena itu smoke dijalankan lewat
tools/Build-Release.ps1 lalu exe di artifacts/publish/win-x64/.

Hasil mesin (screenshot citadel_smoke3.png):
- Shell staging hidup berdampingan dengan instalasi Velopack (identitas single-
  instance berbeda per path exe).
- Sidebar menampilkan grup ENTERTAINMENT (Manga Reader) dan AUTOMATION (CamoProf,
  FTF, Proxy) plus Blank dan Settings.
- Settings: "5 screens installed" dengan tabel route/order lengkap; PROBLEMS =
  "Nothing failed."; kartu CUSTOMISE (Appearance, Module layout, Sidebar groups,
  Gallery) dan SCREEN FOLDER ("Update modules") hadir; UPDATES menonaktifkan aksi
  karena build unpackaged, sesuai RELEASE.md.
- Ini memverifikasi poin 1, sebagian poin 2 (daftar tab), dan kehadiran poin 8
  SETELAH step 10-13, termasuk pemindahan kernel di step 13.

Catatan build: vpk packaging menolak karena Releases/ sudah memuat 2.2.6; itu
batasan versi/channel lokal, bukan kegagalan kode. Staging tree tetap lengkap dan
dipakai untuk smoke.

Sisa poin smoke yang butuh klik manusia: 3 (refresh Library), 4 (history setelah
reader), 5 (reader buka/tutup + zoom/dim/drawer), 6 (CatalogMirror start/stop),
7 (handoff Catalog ke Queue), 8 lanjutan (membuka keempat sub-screen), 9 (tray
hide/restore/exit), 10 (resize min/maks). Instance staging dibiarkan hidup agar
owner bisa langsung mengkliknya.


## Koreksi safety-net dan dampaknya ke plan (2026-09-11)

- Step 1 semula melabel "BUG-1" dan membuat sync retry throttle otomatis. Label itu
  salah: perilaku berhenti-pada-throttle adalah safety net untuk penyelesaian
  challenge manual lewat toggle headed browser provider.
- Commit 30dd644 mengembalikan safety net: fetch satu percobaan, throttle langsung
  berhenti; test mengunci perilaku itu (calls==1, state Error; Stop membangunkan
  polite delay).
- Dampak ke plan: langkah "deteksi block Cloudflare sebagai stop terminal" tetap
  bernilai sebagai LAPISAN TAMBAHAN (mengenali halaman block 403/challenge dan
  melaporkannya ke UI), tetapi BUKAN pengganti safety net ini dan tidak boleh
  memperkenalkan retry apa pun terhadap throttle.
- Status smoke: instance staging masih hidup untuk poin klik manusia; block IP
  owner berarti poin 6 (CatalogMirror sync) TIDAK BOLEH dijalankan melawan
  comix.ws sampai block luruh dan owner mengizinkan.
