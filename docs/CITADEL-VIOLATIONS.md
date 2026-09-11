# CITADEL-VIOLATIONS — register penyimpangan batas

Tumbuh per slice bersama `CITADEL-FLOWS-*.md`. Setiap entri wajib punya bukti
`file:baris` dan catatan "bentuk lurusnya nanti". Tidak ada perubahan kode selama
fase dokumentasi.

## Cara membaca register ini

Dugaan awal owner: "agent yang build tetap membuat saling import". Setelah dua
slice, ternyata **pernyataan itu benar hanya di satu lapisan**, dan lapisan itu
bukan yang dikira. Register ini karena itu dibagi empat kategori jujur, bukan
satu daftar "pelanggaran":

| Kategori | Arti | Kenapa dipisah |
| --- | --- | --- |
| **A** | Klaim tertulis di repo yang **sudah tidak benar** | objektif: dokumen/komentar menyatakan X, kode menyatakan bukan-X |
| **B** | **Biaya perubahan terukur** | tidak ada aturan yang dilanggar, tapi inilah gejala yang owner rasakan ("banyak file tersentuh saat tambah fitur") |
| **C** | Batas yang **tidak dijaga mesin** | aturannya ada (prosa atau hook), tapi ada tepi yang lolos dari semua penegak |
| **D** | **Ketegangan struktur & navigasi** | tidak melanggar kontrak tertulis, tapi merusak cara owner membaca proyek lewat nama |

Yang **tidak** masuk register ada di bagian akhir (§5) dan sama pentingnya:
sejumlah besar batas citadel justru **terkunci mesin dan nol pelanggaran**.

## Sumber aturan yang dipakai mengadili

| Kode | Bunyi | Sumber |
| --- | --- | --- |
| **FM-1** | Feature = folder berkontrak; parent hanya tahu kontraknya ada, tak pernah referensi internal | `.agents/skills/citadel-feature-modularity/SKILL.md` Rule 1 |
| **FM-2** | Komunikasi lewat event/hub, bukan panggilan langsung antar feature | Rule 2 |
| **FM-3** | Parent tak pernah cek "apakah feature X ada"; feature mendaftar sendiri | Rule 3 |
| **FM-4** | State bersama lewat container/context, bukan field parent per feature | Rule 4 |
| **FM-5** | Sebelum menambah logika ke parent: koordinasi atau logika feature? | Rule 5 |
| **SU-1..5** | Kepemilikan shared UI, larangan menyalin template, parent composition-only, approval primitive baru, satu pemilik behavior pair | `.agents/skills/citadel-shared-ui/SKILL.md` |
| **CT-1** | Kontrak citizen publik = **empat** tipe | `.docs/README.md:15` |
| **CT-2** | Contract tidak boleh tumbuh tanpa keputusan | `IModuleGate.cs:10-12`, `LayoutDeclaration.cs:17-19` |
| **CT-3** | Searcher mendeklarasikan Contract saja; reachable ≠ diizinkan | `Citadel.Searcher.csproj:7-15` |
| **CT-4** | Core tak boleh belajar screen berasal dari disk | `ModuleDescriptor.cs:3-8`, `ModuleManifest.cs:4-6` |
| **CT-5** | Citizen boleh Core/Contract/Setting; **tak pernah** Shell/Ui | `Citizen.targets:68-77` |
| **CT-6** | Citizen tak boleh deploy salinan assembly Citadel bersama | `Citizen.targets:132-151`, `Loader.cs:40-52` |
| **GR-1** | Graf dependensi dikunci hook; "the boundary does not move" | `.agents/hooks/check-project-refs.mjs:15-32,65-66` |
| **ST-1** | Satu folder = satu peran; satu file = satu pekerjaan | `C:\VSCODE\yuz-ui\README.md` D-STRUCT |

## Indeks

| ID | Ringkas | Kat | Berat | Status |
| --- | --- | --- | --- | --- |
| A1 | Kontrak diklaim empat tipe, nyata lima | A | ringan | TERCATAT |
| A2 | "three sub-screens" & "four names", nyata 4 sub-screen & 5 reserved route | A | ringan | TERCATAT |
| A3 | "Screen XAML is not linked", nyata 5 `Page Include` XAML | A | ringan | TERCATAT |
| A4 | "pairs enforced at launch", nyata Guard hanya memperingatkan | A | ringan | TERCATAT |
| B1 | Tambah 1 provider = 9 file, 2 di antaranya manifest build | B | **berat** | TERCATAT |
| B2 | 149 entri `Compile Include` manual lintas 6 proyek test | B | **berat** | TERCATAT |
| B3 | Registry sumber = daftar `new` eksplisit per provider | B | sedang | TERCATAT |
| B4 | `Citizen.targets` hanya dukung satu plugin pyhost; citizen butuh dua → target duplikat | B | sedang | TERCATAT |
| C1 | `Compile Include` lintas folder tak terlihat hook | C | sedang | TERCATAT |
| C2 | Baseline referensi citizen hidup di `.targets`, tak terlihat hook | C | ringan | TERCATAT |
| C3 | FM-1/FM-2/FM-3 tak dijaga mesin mana pun | C | **berat** | TERCATAT |
| C4 | `Rpl` ↔ `Crl` saling import (siklus namespace) | C | sedang | TERCATAT |
| C5 | Tepi runtime Ui→Setting tak ada di graf compile-time | C | ringan | TERCATAT |
| C6 | Gate Stop tidak mengompilasi `ftf`, `proxy`, `blank` | C | sedang | TERCATAT |
| C7 | State kegagalan update dibangun di dua lapisan | C | ringan | TERCATAT |
| C8 | Hook meloloskan proyek yang belum ada di tabel `ALLOWED` | C | sedang | TERCATAT |
| D1 | `module/` memegang empat peran, termasuk vault kredensial 499 MB | D | sedang | TERCATAT |
| D2 | `PowerSaving.cs` di root proyek, namespace-nya `.Crl` | D | ringan | TERCATAT |
| D3 | `Tokens.cs` memuat empat tipe publik | D | ringan | TERCATAT |
| D4 | `Overrides.cs` memegang tiga pekerjaan | D | ringan | TERCATAT |
| D5 | `SingleInstanceHost.cs` dua tipe; `ShellSettingHost.cs` tiga peran | D | ringan | TERCATAT |
| D6 | Contract tidak tipis: menarik seluruh Core | D | **berat** | TERCATAT |
| D7 | Kebijakan "assembly bersama" ditulis tiga kali, tiga mekanisme | D | sedang | TERCATAT |
| D8 | Dua window berdisiplin ukuran berbeda | D | ringan | TERCATAT |

Berat = seberapa jauh penyimpangan dari bentuk yang dimaksud, bukan seberapa
susah memperbaikinya.

---

# A — Klaim tertulis yang sudah tidak benar

## A1 — Kontrak citizen diklaim empat tipe, kenyataannya lima

**Aturan:** CT-1, CT-2.

Klaim: `.docs/README.md:15` "the four types in `core/Citadel.Contract`";
`LayoutDeclaration.cs:8-9` "One of the contract's four public types";
`LayoutDeclaration.cs:17-19` "the contract is exactly four public types, and a
fifth would grow it"; `IModuleGate.cs:10-12` "Returning a result here would grow a
locked contract".

Kenyataan (lima tipe publik, diverifikasi 2026-09-11):

| Tipe | Bukti |
| --- | --- |
| `IModule` | `IModule.cs:13` |
| `IModuleGate` | `IModuleGate.cs:20` |
| `ModuleDescriptor` | `ModuleDescriptor.cs:9` |
| `LayoutDeclaration` | `LayoutDeclaration.cs:21` |
| `IContentHeaderActionProvider` | `IContentHeaderActionProvider.cs:10` |

**Yang membuatnya lebih dari sekadar angka basi:** `LayoutDeclaration.cs:17-19`
memakai angka "empat" sebagai **alasan desain** — slot kind dibiarkan string, bukan
enum, justru supaya tidak menambah tipe kelima. Tipe kelima kemudian ditambahkan
lewat jalur lain, dan alasan desain itu kini menunjuk angka yang salah.
Penambahannya sendiri aditif dan hati-hati ("IModule remains unchanged; views that
do not implement this interface keep the normal title-only header",
`IContentHeaderActionProvider.cs:6-8`); yang tidak dilakukan adalah memperbarui
klaim.

**Akibat:** `.docs/README.md` adalah sumber kebenaran yang dibaca agent. Agent
berikutnya yang diminta "jaga kontrak tetap empat tipe" akan menolak perubahan sah,
atau menambah tipe keenam tanpa merasa melanggar karena angkanya sudah tidak
dipercaya.

**Bentuk lurusnya nanti:** (1) perbarui angka di tiga tempat dan catat
`IContentHeaderActionProvider` sebagai tipe kelima yang disetujui; **atau** (2)
ganti klaim berbasis angka dengan klaim berbasis daftar, supaya menambah tipe tidak
otomatis membuat dokumen salah. Pilihan (2) lebih tahan lama.

---

## A2 — "three sub-screens" dan "four names", nyata empat sub-screen dan lima reserved route

**Aturan:** CT-1 keluarga yang sama (klaim angka), plus keakuratan dokumen yang
dipakai agent.

| Klaim | Bukti | Kenyataan |
| --- | --- | --- |
| "One reusable, modeless editor window for Settings' **three** sub-screens" | `SettingsWindow.xaml.cs:12` | `PopupRoute` memetakan **empat**: Appearance, Layout, Gallery, SidebarGroups (`ShellSettingHost.cs:217-233`) |
| "Its **three** reserved sub-screens ... none of the **four** names passes through the module gate" | `App.xaml.cs:170-172` | dua angka berbeda dalam satu komentar; `ReservedRoutes` berisi **lima**: `settings`, `settings/appearance`, `settings/layout`, `settings/gallery`, `settings/sidebar-groups` (`ModuleGate.cs:46-53`) |

**Pola yang terlihat:** kedua angka salah ke arah yang sama — keduanya benar
**sebelum** `SidebarGroups` ditambahkan. Jadi ini jejak fitur yang masuk tanpa
memperbarui klaim di sekitarnya, persis seperti A1 dan seperti B1 (fitur masuk
dengan biaya manifest).

**Bentuk lurusnya nanti:** hitung dari sumbernya. `ReservedRoutes` sudah satu
daftar di satu tempat; komentar bisa merujuk daftar itu alih-alih menyalin angkanya.

---

## A3 — "Screen XAML is not linked", nyata lima `Page Include` XAML

**Aturan:** keakuratan alasan tertulis yang dipakai agent berikutnya untuk memutuskan.

Klaim: `tests/Module.Mangareader.Downloader.Tests/…csproj:23-30` — "Screen XAML is
not linked, so these tests cover contracts, persistence, publication and decoding
rather than rendering. The Comix filter panel is the one control that must come
along, because the single registry entry constructs it."

Kenyataan: **lima** XAML di-link, bukan satu — `ComixFilterPanel.xaml` (:33),
`MangaMultiSelectFilter.xaml` (:38), `DrakeScansFilterPanel.xaml`,
`CucumberMangaFilterPanel.xaml`, dan satu lagi pada jalur FilterSearch. Setiap
provider baru menambah satu, karena setiap provider membawa panel filternya sendiri
(lihat B1: commit `21977e5` menambah `CucumberMangaFilterPanel.xaml` sebagai
`Page Include`).

**Akibat:** pengecualian yang tadinya "satu kontrol" sekarang aturan de-facto
"setiap panel filter provider ikut di-link". Agent yang membaca komentar itu akan
menyimpulkan XAML tidak perlu di-link, lalu test-nya gagal konstruksi.

**Bentuk lurusnya nanti:** tulis aturannya sebagaimana adanya — "XAML screen
tidak di-link; XAML **kontribusi filter provider** di-link karena registry
mengkonstruksinya" — atau better, hapus pengecualian dengan membuat registry tidak
mengkonstruksi UI (lihat B3).

---

## A4 — "pairs enforced at launch", nyata Guard hanya memperingatkan

**Aturan:** keakuratan klaim perilaku.

Klaim: `tokens/TokenPairs.cs:11` — "pairs enforced at launch".
Kenyataan: `Guard` **memperingatkan** soal kontras, tidak memblokir
(`Guard.cs:155-164`, putusan `GuardVerdict.Warned` di `Guard.cs:12-18`), dan ini
memang keputusan yang tercatat di tempat lain: "Upstream silently adjusts
(EnsureContrast); Citadel warns instead" (`TokenPairs.cs:5-8`).

Jadi dua komentar di file yang sama saling melunakkan: yang satu bilang "enforced",
yang lain bilang "warns". "Enforced" di sini berarti "diperiksa", bukan "ditolak".

**Bentuk lurusnya nanti:** satu kata. Ganti "enforced" menjadi "checked" supaya
tidak menjanjikan pemblokiran yang tidak terjadi.

---

# B — Biaya perubahan terukur (gejala yang owner rasakan)

Tidak ada kontrak tertulis yang dilanggar di kategori ini. Yang diukur adalah
**berapa file yang harus disentuh untuk menambah satu hal** — persis keluhan owner:
*"I see a lot of modified files when I add feature, should be few files only,
that's the feature file itself."*

## B1 — Menambah satu provider menyentuh 9 file

Bukti dari riwayat git, bukan perkiraan. Commit `21977e5`
`feat(mangareader): add Cucumber Manga provider`, 9 file:

| # | File | Peran | Penilaian |
| --- | --- | --- | --- |
| 1-4 | `Sources/CucumberManga/{HtmlParser,Source}.cs`, `FilterSearch/CucumberManga/{FilterPanel.xaml,FilterContribution.cs}` | feature itu sendiri | **sah** — inilah yang seharusnya |
| 5 | `Sources/MangaSourceRegistry.cs` | tambah satu `new MangaSourceRegistration(...)` | sah menurut FM-3 (catalog pattern), tapi daftar manual → B3 |
| 6 | `module/mangareader/Module.Mangareader.csproj` | tambah `PackageReference AngleSharp` + tulis ulang komentar | sah untuk dependensi NuGet baru |
| 7 | `tests/Module.Mangareader.Downloader.Tests.csproj` | tambah **4 entri** `Compile/Page Include` + `AngleSharp` | **overhead enumerasi** → B2 |
| 8 | `CucumberMangaSourceTests.cs` | test | **sah** |
| 9 | `version.props` | bump rilis | sah |

Jadi dari 9 file: 5 sah (4 feature + 1 test), 1 sah karena dependensi baru, 1 sah
karena rilis, **2 adalah biaya registrasi manual**. Bila provider tidak butuh
NuGet baru, biayanya tetap 8 file dengan 2 di antaranya manifest.

Skala commit fitur lain untuk perbandingan:

| Commit | Judul | File |
| --- | --- | --- |
| `8a772b6` | add independent offline catalog | 55 |
| `7fb0b13` | Persist shared UI preferences | 21 |
| `bd186b5` | add Drake provider and refine filters | 17 |
| `21977e5` | add Cucumber Manga provider | 9 |
| `ff87f8f` | require active Comix session for queue | 8 |
| `96c4030` | persist detected profile identities | 8 |
| `d549e83` | simplify shared navigation and table handles | 6 |
| `172f193` | run queue jobs in parallel | 3 |
| `f132622` | replace downloader browser label with info icon | 2 |

## B2 — 149 entri `Compile Include` manual lintas enam proyek test

Proyek test tidak mereferensikan citizen (citizen tidak ada di `.slnx`), jadi
mereka **me-link sumbernya file per file**:

| Proyek test | entri `Compile Include` | wildcard |
| --- | --- | --- |
| `Module.Mangareader.Downloader.Tests` | 66 | 2 |
| `Module.Mangareader.Reader.Tests` | 46 | 0 |
| `Module.Mangareader.Library.Tests` | 16 | 0 |
| `Module.Camoprof.Tests` | 10 | 1 |
| `Module.Mangareader.History.Tests` | 8 | 0 |
| `Module.Mangareader.Archive.Tests` | 3 | 1 |
| **total** | **149** | **4** |

Setiap file feature baru = satu entri baru di satu atau lebih csproj test. Inilah
mesin utama di balik B1 #7.

**Pola yang benar sudah ada di repo yang sama:** tepi sumber yang memakai wildcard
tidak menuntut edit csproj saat file bertambah —
`module/sharedLogic/cs/**\*.cs` di-link wildcard oleh `Module.Camoprof.csproj:10`,
`Module.Mangareader.csproj:10`, dan entri terakhir `Downloader.Tests.csproj`;
demikian juga `mangareader/shareLogic/Archive/*.cs`
(`Module.Mangareader.Archive.Tests.csproj:21`). Jadi wildcard terbukti bisa
dipakai; ia hanya tidak dipakai untuk folder feature.

**Kenapa tidak wildcard dari awal?** Alasannya tertulis dan sah sebagian: test
ingin mengambil sumber murni Downloader **tanpa** XAML screen
(`Downloader.Tests.csproj:23-30`). Wildcard folder akan menyeret XAML. Tapi
akibatnya adalah manifest 66 baris yang harus dirawat tangan, dan A3 menunjukkan
pengecualian XAML-nya sendiri sudah bocor.

**Bentuk lurusnya nanti:** pisahkan sumber murni dari sumber ber-XAML **di disk**
(misalnya `Features/Downloader/Sources/**` = murni, panel filter dipindah ke satu
folder UI), lalu wildcard per folder. Atau: buat citizen bisa direferensikan
proyek test tanpa masuk `.slnx` (ProjectReference dari test ke citizen sudah
dikecualikan hook, `check-project-refs.mjs:41-42`), sehingga 149 entri hilang
semua. Pilihan kedua menghapus B2 dan A3 sekaligus, dan tidak melanggar GR-1
karena `tests/` memang bebas.

## B3 — Registry sumber adalah daftar `new` eksplisit per provider

`Features/Downloader/Sources/MangaSourceRegistry.cs:93-105`: factory
menulis `var comix = new Comix.ComixSource(client);` lalu
`var cucumberManga = new CucumberManga.CucumberMangaSource();` dan menyusun daftar
`MangaSourceRegistration` satu entri per provider, lengkap dengan lambda yang
mengkonstruksi `FilterContribution` masing-masing.

**Ini sah menurut kontrak:** FM-3 justru menyarankan catalog pattern
(`SKILL.md:102-114`). Jadi bukan pelanggaran. Tapi inilah satu-satunya bagian
parent yang **wajib** diedit tiap provider bertambah, dan ia tumbuh linier.

**Bandingkan dengan yuz-ui:** di sana child mendaftar karena file-nya ADA
(glob-by-convention, `D-CONTRACT`), jadi menambah child = menambah file, nol edit
parent. Citadel sudah mencapai bentuk itu di **tingkat module→shell** (§5), tapi
belum di **tingkat provider→feature** dan **feature→module**.

**Bentuk lurusnya nanti:** discovery berbasis refleksi/wildcard di dalam citizen
(misalnya setiap `IMangaSource` di assembly citizen terdaftar otomatis), sehingga
menambah provider = menambah folder. Ini perubahan di dalam satu citizen, tidak
menyentuh kontrak publik, dan tidak melanggar GR-1.

## B4 — `Citizen.targets` hanya mendukung satu plugin pyhost per citizen

`Citizen.targets:196-214` men-deploy paket python dari
`Features\*\$(PyhostPluginName)\*.py` — **satu** nama paket per citizen, diset via
`<PyhostPluginName>` (`Module.Camoprof.csproj`, `Module.Mangareader.csproj`).

MangaReader punya **dua** pemilik runtime python (Downloader dan Catalog), jadi ia
menulis target keduanya sendiri: `DeployMangaReaderCatalogPyhostPlugin`
(`Module.Mangareader.csproj`, `AfterTargets="DeployPyhostPlugins"`), yang
menduplikasi logika `DeployPyhostPlugins` — `MakeDir`, dua `Copy` dengan
`DestinationFiles`, `plugin.py` sebagai `__init__.py`.

Komentarnya menjelaskan maksudnya dengan baik ("Catalog is a second top-level
runtime owner... The two packages never share a browser/session host"), jadi ini
keputusan sadar. Biaya strukturalnya: pekerjaan "deploy paket pyhost milik
feature" sekarang punya **dua pemilik** (ST-1), dan perbaikan apa pun pada logika
deploy harus ditulis dua kali.

**Bentuk lurusnya nanti:** buat `PyhostPluginName` menerima **daftar** (item
MSBuild, bukan property), sehingga `DeployPyhostPlugins` mengulang sendiri dan
citizen tidak perlu menulis target kedua.

---

# C — Batas yang tidak dijaga mesin

## C1 — `Compile Include` lintas folder tidak terlihat hook graf

`check-project-refs.mjs:48-52` hanya mencocokkan `<ProjectReference ...>`, dan
:8-9 menyatakan itu sengaja. Akibatnya tepi berbasis sumber sepenuhnya di luar
graf:

| Tepi | Bukti |
| --- | --- |
| `module/sharedLogic/cs/**\*.cs` → dikompilasi ke dalam camoprof | `Module.Camoprof.csproj:10` |
| `module/sharedLogic/cs/**\*.cs` → dikompilasi ke dalam mangareader | `Module.Mangareader.csproj:10` |
| 149 entri sumber citizen → proyek test | lihat B2 |

Konsekuensi yang layak diketahui owner: karena `sharedLogic/cs` di-include sebagai
**sumber**, tiap citizen mendapat **salinan tipenya sendiri** di assembly-nya
masing-masing. Itu cocok dengan model isolasi ALC (`Loader.cs:45-52`), tapi artinya
`sharedLogic/cs` tidak bisa memegang state bersama antar citizen — dan hanya 2 dari
6 citizen yang memakainya.

**Bentuk lurusnya nanti:** perluas hook agar juga melaporkan `Compile Include`
yang menunjuk keluar folder proyek. Tidak perlu memblokir (polanya sah), cukup
terlihat — sekarang tepi ini tidak terlihat di mana pun kecuali dengan membaca
setiap csproj.

## C2 — Baseline referensi citizen tidak pernah diaudit

Citizen mendeklarasikan **nol** `ProjectReference` di csproj-nya sendiri; Core,
Contract, dan Setting datang dari `<Import Project="..\Citizen.targets" />`
(`Citizen.targets:74-77`). Hook membaca csproj yang disunting, jadi untuk citizen
ia melihat daftar kosong dan selalu lolos.

Aturan `MODULE_ALLOWED` (`check-project-refs.mjs:32,45`) tetap efektif menangkap
citizen yang **menambah** referensi terlarang di csproj-nya sendiri — itu kasus
yang realistis. Yang tidak pernah diperiksa adalah baseline-nya: kalau suatu hari
`Citizen.targets` diubah memberi citizen akses ke `Citadel.Ui`, tidak ada hook yang
menyala; yang menyala hanya `VerifyCitizenIsolation` saat build, dan itu pun karena
alasan berbeda (identitas assembly, `Citizen.targets:142-151`).

**Bentuk lurusnya nanti:** hook juga memeriksa `Citizen.targets` terhadap
`MODULE_ALLOWED`, sehingga perubahan baseline citizen adalah perubahan yang
terlihat.

## C3 — FM-1, FM-2, FM-3 tidak dijaga mesin mana pun

Ini temuan terpenting kategori C. Kontrak feature-modularity adalah **prosa di
SKILL.md**. Tidak ada analyzer, tidak ada hook, tidak ada test yang menegaskan
"feature tidak meng-import feature lain" atau "parent tidak tahu internal feature".

Yang dijaga mesin hanya tingkat **proyek** (C1/GR-1). Di dalam satu citizen — tempat
sebagian besar kode hidup — tidak ada penegak apa pun. Bukti bahwa tepi internal
itu padat: `using Module.Mangareader.*` muncul lintas sub-folder dalam jumlah besar
(`.Components` 21+ kali, `.Sources` 30+ kali, `.ShareLogic` 25+ kali,
`.ReaderCore` 10+ kali, terverifikasi 2026-09-11), dan semuanya sah secara compiler
karena satu assembly.

Sebagian besar mungkin sah (feature memakai kontrak bersama module). Tapi **tidak
ada cara membedakan yang sah dari yang tidak tanpa membaca tiap file** — dan itu
persis yang tidak bisa dilakukan owner. Slice 5 memeriksa ini per feature.

**Bentuk lurusnya nanti:** satu test per citizen yang menegaskan daftar tepi
internal yang diizinkan (misalnya "tidak ada tipe di `Features/X/` yang di-import
`Features/Y/`"), sehingga penyimpangan merobohkan test alih-alih lolos diam-diam.
Ini analog persis dengan `eslint.config.ts` yuz-ui yang melarang import `./` di
dalam `children/` (D-CONTRACT).

## C4 — `Rpl` dan `Crl` saling import

Satu-satunya **siklus import literal** yang ditemukan sejauh ini, dan ia ada di
fondasi:

- `rpl/Operators.cs:1` → `using Citadel.Core.Crl;`
- `crl/Crl.cs:1` → `using Citadel.Core.Rpl;`
- `crl/MainQueue.cs:2` → `using Citadel.Core.Rpl;`

Sah secara compiler karena keduanya di dalam `Citadel.Core`. Penyebabnya nyata dan
bukan kelalaian: `Operators.BatchOnMain(MainQueue)` butuh `MainQueue`
(`Operators.cs:30-40`), sementara `MainQueue.Post(Lifetime, Action)` butuh
`Lifetime` (`MainQueue.cs:43-57`).

**Kenapa tetap dicatat:** inilah bentuk "saling import" yang owner keluhkan, dan ia
tidak terdeteksi gate apa pun karena hook hanya bekerja di tingkat proyek (C1).
Menunjukkan bahwa penegakan di tingkat proyek saja tidak cukup.

**Bentuk lurusnya nanti:** pindahkan `BatchOnMain` keluar dari `rpl/` (ia memang
"Citadel invention — no rpl precedent", `Operators.cs:28-30`) ke `crl/`, sehingga
`crl → rpl` satu arah.

## C5 — Tepi runtime Ui → Setting tidak ada di graf compile-time

`Citadel.Ui` hanya boleh mereferensikan `Citadel.Core`
(`check-project-refs.mjs:18`, nyata di `Citadel.Ui.csproj:8`). Tapi
`ThemeResources.xaml:138-147` memakai `Style="{DynamicResource SettingScrollViewerStyle}"`
— resource milik `Citadel.Setting` — dengan komentar jujur bahwa assembly-nya tidak
direferensikan dan style "resolved at runtime from the merged application
dictionaries", serta degradasi anggun bila tidak ada.

Ini **keputusan yang masuk akal** (menghindari referensi balik Ui→Setting), tapi
konsekuensinya: graf yang dikunci hook bukan graf yang sebenarnya dijalankan. Merge
terjadi di `App.xaml:14-16`, dan tidak ada penegak bahwa merge itu tetap ada. Bila
suatu hari `App.xaml` berhenti me-merge dictionary Setting, sidebar kehilangan
scrollbar auto-fade secara diam-diam — tidak ada build atau test yang merah.

**Bentuk lurusnya nanti:** satu test UIA yang menegaskan scrollbar sidebar memakai
style bersama, bukan default. Murah, dan mengubah kegagalan diam-diam menjadi
kegagalan yang terlihat.

## C6 — Gate Stop tidak mengompilasi `ftf`, `proxy`, dan `blank`

`gate-on-stop.mjs:34` menjalankan `dotnet test Citadel.slnx`. `Citadel.slnx:1-19`
berisi 18 proyek: 6 core/setting/searcher/shell + 12 test. **Tidak ada citizen.**

| Citizen | Terkompilasi gate Stop? | Lewat mana |
| --- | --- | --- |
| `mangareader` | sebagian | 5 proyek test me-link 139 entrinya (B2) |
| `camoprof` | sebagian | `Module.Camoprof.Tests` me-link 10 entri |
| `ftf` | **tidak** | tidak ada di slnx, tidak ada test project-nya |
| `proxy` | **tidak** | tidak ada di slnx, tidak ada test project-nya |
| `blank` | ya | `Citadel.Uia.csproj:45` mereferensikan `Module.Blank.csproj` |

Jadi tiga citizen bisa rusak oleh suntingan agent dan gate Stop tetap hijau.
Catatan: ini konsekuensi **sengaja** dari keputusan "citizen tidak masuk slnx"
(`Module.Blank.csproj:9-11`) yang justru membuat "tambah screen tanpa edit apa pun
di luar module/" menjadi benar. Trade-off-nya nyata dan belum tercatat di mana pun.

**Bentuk lurusnya nanti:** gate Stop (atau `Build-Release.ps1`) menambahkan satu
langkah `dotnet build` untuk setiap citizen yang ada di `module/`, tanpa
memasukkannya ke `.slnx`. Menjaga janji plug-and-play sekaligus menutup lubang ini.

## C7 — State kegagalan update dibangun di dua lapisan

`AppUpdateService.cs:124-133` menyusun `Snapshot with { Status = $"Update check
failed: {ErrorText(exception)}", CanCheck, CanInstall, Busy = false }`.
`AppUpdateController.cs:162-175` menyusun bentuk yang hampir identik:
`current with { Status = $"{prefix}: {message}", CanCheck = current.Supported,
CanInstall = current.Available, Busy = false }`.

Pemisahan service/controller sendiri **sah dan beralasan** (service = batas
containment Velopack, "Velopack and the GitHub source never leak into Setting,
Core, or Contract", `AppUpdateService.cs:9-10`; controller = state machine
thread-affine, `AppUpdateController.cs:7-9`). Yang ganda hanya pekerjaan "ubah
exception menjadi state kegagalan", dengan dua cara mengekstrak pesan dan dua
sumber kebenaran untuk `CanCheck`/`CanInstall`.

**Bentuk lurusnya nanti:** satu factory `AppUpdateState.Failed(prefix, exception, current)`
dipakai kedua lapisan.

## C8 — Hook meloloskan proyek yang belum ada di tabel `ALLOWED`

`check-project-refs.mjs:46` — `if (!allowed) return; // project not in the graph
yet — nothing to assert`. Proyek baru (nama apa pun di luar enam entri `ALLOWED`
dan di luar `module/` atau `tests/`) **tidak dijaga sama sekali** sampai seseorang
menambahkannya ke tabel.

Jadi graf terkunci untuk proyek yang sudah dikenal, dan terbuka untuk yang baru.
Ini lubang masuk: pelanggaran masa depan paling mungkin datang dari proyek yang
belum ada di tabel.

**Bentuk lurusnya nanti:** balik default-nya — proyek di `core/` atau `setting/`
yang tidak dikenal dianggap tidak boleh mereferensikan apa pun, sehingga menambah
proyek baru memaksa keputusan sadar di tabel.

---

# D — Ketegangan struktur & navigasi

Owner membaca proyek lewat nama folder dan nama file. Kategori ini mencatat tempat
nama tidak lagi menceritakan isi.

## D1 — `module/` memegang empat peran

`.docs/README.md:11` mendefinisikan `module/` sebagai "screen discovery plus
independently deployable citizen screens" — satu folder, dua pekerjaan, dan
kenyataannya empat:

| Isi `module/` | Peran | Citizen? | Boleh dihapus? |
| --- | --- | --- | --- |
| `blank`, `camoprof`, `ftf`, `mangareader`, `proxy` | screen drop-in | ya | ya, bersih |
| `Citadel.Searcher` | infrastruktur discovery | tidak (tanpa `module.json`) | **tidak** — app runtuh |
| `sharedLogic` | payload runtime bersama (cs + python) | tidak | **tidak** |
| `Citizen.targets` | kontrak build bersama | bukan folder | **tidak** |
| `credenz` | **vault kredensial 499 MB** (identity.json, password.dat, profil browser) | tidak | bukan kode |

Alasan keberadaan searcher di sana tertulis dan masuk akal: "Searching is module
logic, not core logic, so it lives at module/ root: shared by every screen, owned
by none" (`Citadel.Searcher.csproj:4-6`).

**Soal `credenz`, satu hal yang harus dikatakan jelas:** tidak ada kebocoran.
`.gitignore:35-45` mengecualikannya dengan hati-hati dan hanya dua file yang
ter-track git (`module/credenz/README.md`, `google/profiles/.gitkeep`, diverifikasi
2026-09-11). Tapi 499 MB kredensial hidup dan profil browser nyata berada **di
dalam folder yang didokumentasikan sebagai tempat screen drop-in**. Konsekuensi
praktisnya: `Build-Release.ps1` "counts `module\*` directories against citizen
projects" (`Citizen.targets:90-91`) — perlu diverifikasi di slice 8 apakah
`credenz` ikut terhitung.

**Bentuk lurusnya nanti:** pindahkan infrastruktur (`Citadel.Searcher`,
`Citizen.targets`, `sharedLogic`) ke folder bernama perannya (`platform/` atau
`host/`), dan vault ke luar `module/` sama sekali (misalnya `%LOCALAPPDATA%`
atau folder `vault/` di root). Hasilnya `module/` berarti persis "screen drop-in,
semuanya boleh dihapus".

## D2 — `PowerSaving.cs` berada di root proyek, namespace-nya `Citadel.Core.Crl`

`PowerSaving.cs:1` mendeklarasikan `namespace Citadel.Core.Crl;` tapi file-nya ada
di `core/Citadel.Core/PowerSaving.cs`, bukan di `core/Citadel.Core/crl/`.
Namespace-nya beralasan ("Lives in Citadel.Core.Crl — not its own namespace —
because that is where compatible citizens look for it", `:5-7`), tapi **lokasi
file-nya tidak dijelaskan**.

Bagi owner yang menavigasi lewat nama, `crl/` berisi tiga file sementara perilaku
keempat (`PowerSaving`) ada di luar folder itu — dan `Citadel.Core.Crl` sebagai
namespace berisi empat tipe dari dua folder.

**Bentuk lurusnya nanti:** pindahkan file ke `crl/`. Nol perubahan kode, nol
perubahan namespace, murni navigasi.

## D3 — `Tokens.cs` memuat empat tipe publik

`tokens/Tokens.cs` memuat `TokenKind` (:6), `TokenValue` (:12), `Tokens` (:90),
`TokenCommitResult` (:486). Nama file menjanjikan satu pekerjaan; isinya nilai
token, store hidup, dan hasil commit.

**Bentuk lurusnya nanti:** `TokenValue.cs` (Kind + Value + CommitResult) dan
`Tokens.cs` (store). Atau terima apa adanya dan catat — ini yang paling ringan di
kategori D.

## D4 — `Overrides.cs` memegang tiga pekerjaan

(1) kebijakan path store termasuk penanda portable (`Overrides.cs:24-34`);
(2) simpan/muat `ui.json` sparse secara atomik (`:44`, `:137`);
(3) **validasi properti layout** `IsValidLayoutProperty` (`:246-274`) yang dipanggil
`Tokens.CommitLayout` (`Tokens.cs:319`).

Pekerjaan ketiga adalah kebijakan layout, bukan persistensi, dan ia hidup di file
persistensi. Bandingkan dengan `LayoutApplier.cs:115-129` yang memiliki kebijakan
kind layout di sisi shell — jadi kebijakan layout sekarang punya dua pemilik di dua
proyek (`Guard`/`Overrides` di Core, `LayoutApplier` di Shell).

**Bentuk lurusnya nanti:** pindahkan `IsValidLayoutProperty` ke `tokens/` sebagai
pemilik kebijakan layout, atau satukan dengan `Guard` yang sudah menjadi pemilik
kebijakan validasi token.

## D5 — Dua file dengan lebih dari satu tipe/peran

- `SingleInstanceHost.cs` memuat `SingleInstanceHost` (:40) **dan**
  `NativeWindowActivation` (:450-478, aktivasi foreground Win32), plus
  `InstanceLaunchKind`/`InstanceLaunch` (:12-33). Berhubungan, tapi Win32
  activation adalah pekerjaan tersendiri yang bisa berdiri sebagai file.
- `ShellSettingHost.cs` memegang tiga peran: jembatan `ISettingHost`, pemilik
  `SettingsWindow` (`:151-171`), dan pabrik `AppUpdateController` (`:59-62`).
  Ketiganya berdekatan dengan "mengisi seam", tapi nama file hanya menyebut yang
  pertama.

**Bentuk lurusnya nanti:** `NativeWindowActivation.cs` terpisah; dan bila peran
pemilik window/factory update tumbuh, pisahkan — saat ini masih bisa diterima.

## D6 — Contract tidak tipis: ia menarik seluruh Core

Ini ketegangan paling berat di kategori D, dan **disahkan oleh hook**:
`check-project-refs.mjs:17` secara eksplisit mengizinkan
`"Citadel.Contract": ["Citadel.Core"]`. Jadi bukan pelanggaran GR-1 — grafnya
memang begitu dirancang.

Bukti rantai: `Citadel.Contract.csproj:16` mereferensikan Core; `IModule.cs:2`
`using Citadel.Core.Rpl;`; `IModule.cs:17` `CreateView(Lifetime lifetime)`;
penyebabnya tunggal — `Lifetime` didefinisikan di `core/Citadel.Core/rpl/Lifetime.cs:19`.

Pengakuan bahwa ini bocor: `Citadel.Searcher.csproj:7-11` — "Contract references
Core transitively, which means Core's types are *reachable* here — that is not
permission to use them." Penegakannya **komentar**, dan `Watcher.cs:23-27`
mengulanginya ("**No Citadel.Core.** This project declares Contract only").

**Tiga akibat:**

1. Tidak ada permukaan tipis yang bisa jadi satu-satunya pintu. Di yuz-ui
   permukaan itu `contracts.ts` dan ia tidak meng-import apa pun. Di citadel,
   "contract" memberi setiap citizen akses sah ke seluruh `Citadel.Core`
   (`rpl` lima tipe, `crl` tiga, `tokens` enam, `Log`, `PowerSaving`).
2. Niat yang tercatat di `Citadel.Searcher.csproj:9-11` — mengganti searcher
   dengan package feed atau test double tanpa menyeret logging Core — tidak
   tercapai.
3. `CT-5` memberi citizen "Core + Contract + Setting". Karena Contract sudah
   membawa Core, menyebut Core di daftar itu redundan; yang sebenarnya terjadi
   adalah citizen **memang** memakai Core secara luas (terverifikasi: setiap
   citizen memakai `Citadel.Core.Rpl`). Jadi ini bukan penyimpangan, tapi
   konsekuensi desain yang membuat batas "contract-only" tidak mungkin dicapai
   tanpa memindahkan `Lifetime`.

**Bentuk lurusnya nanti:** pindahkan `Lifetime` (atau abstraksi minimal yang
dibutuhkan `CreateView`) ke `Citadel.Contract`, sehingga Contract jadi daun sejati.
Risiko yang harus diakui owner: `IModule.cs:7` menulis "Verbatim from v0 — do not
alter", dan namespace-nya sengaja `Citadel.Core.Modules` walau assembly-nya
`Citadel.Contract` (`IModule.cs:10-11`) supaya citizen lama tetap compile.
Memindahkan `Lifetime` menyentuh wilayah sesensitif itu — ini keputusan owner,
bukan refactor diam-diam.

**Catatan pembanding:** `Citadel.Core` sendiri **sudah** daun sejati
(`Citadel.Core.csproj:1-9`, nol referensi). Jadi bentuk yang dituju sudah terbukti
mungkin di repo yang sama; yang belum adalah membuat Contract setipis Core.

## D7 — Kebijakan "assembly Citadel bersama" ditulis tiga kali

| # | Tempat | Mekanisme | Isi |
| --- | --- | --- | --- |
| 1 | `Loader.cs:45-52` | daftar nama eksplisit di runtime, dipakai hook `Resolving` mengembalikan null (`Loader.cs:211-216`) | Core, Contract, Setting, Ui, Shell |
| 2 | `Citizen.targets:144` | MSBuild item list untuk `VerifyCitizenIsolation` | Core, Contract, Setting, Ui, Shell |
| 3 | `Citizen.targets:74-76` | `ProjectReference` dengan `Private="false"` | Core, Contract, Setting |
| 4 | `Citizen.targets:127-128` | **aturan prefix** `Citadel.*` | apa pun berawalan `Citadel.` |

Menambah satu assembly Citadel bersama berarti menyentuh tiga daftar dengan tiga
semantik. Daftar #4 otomatis benar karena berbasis prefix; #1 dan #2 tidak.

Akibat bila terlewat: #2 terlewat → citizen men-deploy salinan privat → dua
identitas tipe → cast `IModule` gagal saat load dengan pesan yang menyalahkan
citizen (`Loader.cs:280-287`). #1 terlewat → assembly dimuat dua kali di dua
context → kegagalan sama, muncul lebih lambat. Keduanya persis yang ingin dicegah
`VerifyCitizenIsolation` ("Fail here instead, where the cause is obvious",
`Citizen.targets:132-141`), tapi penegaknya sendiri punya salinan kebijakan yang
bisa basi.

**Bentuk lurusnya nanti:** satu sumber kebenaran. Aturan prefix sudah terbukti
dipakai di `Citizen.targets:128`, jadi `Loader.SharedAssemblies` bisa diganti
`name.Name.StartsWith("Citadel.", Ordinal)` dan `VerifyCitizenIsolation` bisa
enumerasi `$(CitizenRuntimeFolder)Citadel.*.dll`. Catatan jujur: prefix mengikat
kebijakan ke konvensi penamaan; kalau kelak ada `Citadel.*` yang boleh privat per
citizen, aturan prefix jadi terlalu luas.

## D8 — Dua window berdisiplin ukuran berbeda

`MainWindow.xaml:12-17` melarang literal ukuran ("No size literals..."), dan
larangan itu ditegakkan di `MainWindow.xaml.cs:130-135`. `SettingsWindow.xaml:7-10`
meng-hardcode 760/720/640/520.

Larangan itu memang khusus MainWindow (window utama mengikuti token; window editor
modeless punya ukuran tetap yang masuk akal), jadi ini **bukan pelanggaran**.
Dicatat karena owner membaca `SHARED-UI-BEHAVIOR.md` yang berbicara tentang fluid
layout untuk app, dan dua window ini berperilaku berbeda tanpa ada dokumen yang
menjelaskan kenapa.

**Bentuk lurusnya nanti:** satu kalimat di `SHARED-UI-BEHAVIOR.md` yang menyatakan
ukuran tetap SettingsWindow adalah keputusan, bukan kelalaian.

---

## 5. Yang BUKAN penyimpangan (sama pentingnya untuk dibaca)

Dugaan awal owner adalah citadel penuh saling import. Dua slice pertama **tidak**
mendukung itu di lapisan antar-proyek. Yang terverifikasi bersih:

| Batas | Dijaga oleh | Bukti nyata |
| --- | --- | --- |
| Graf dependensi antar proyek | hook `PostToolUse`, **memblokir** | graf aktual = graf `ALLOWED`, persis, di keenam proyek (§CROSS 2.3) |
| Citizen tak boleh Shell/Ui | `Citizen.targets:68-77` + `VerifyCitizenIsolation:142-151` | **nol** `using Citadel.Ui` di seluruh `module/`; nol `assembly=Citadel.Ui` di XAML module |
| Citizen tak boleh citizen | `MODULE_ALLOWED` (`check-project-refs.mjs:32`) | hanya **satu** `ProjectReference` di seluruh `module/`: Searcher→Contract |
| Identitas assembly tunggal | stream-load + hook `Resolving` null untuk assembly bersama | `Loader.cs:45-52`, `211-216`; pesan khusus `Loader.cs:278-287` |
| Core tak tahu screen dari disk | `ModuleDescriptor` tanpa asal-usul; `ModuleManifest` internal | `ModuleDescriptor.cs:3-8`, `ModuleManifest.cs:4-6`, `ModuleGate.cs:20-23` |
| Setting tak tahu Shell/searcher | `ISettingHost` sebagai seam | `ISettingHost.cs:38-41`, `ShellSettingHost.cs:14-17`, `Citadel.Setting.csproj:5-7,14-15` |
| `Citadel.Core` daun sejati | csproj 9 baris, nol referensi | `Citadel.Core.csproj:1-9` |
| Tambah screen = tambah folder | citizen di luar `.slnx`, deploy ke output Shell | `Module.Blank.csproj:9-11`, `Citizen.targets:14-16` |
| Fault isolation | tiga lapis | pump `Watcher.cs:370-375` · `CreateView` dibungkus `Router.cs:182-215` · animasi `Router.cs:347-351` |
| Kredensial tidak masuk git | `.gitignore:35-45` | 2 file ter-track dari 499 MB |

**Diagnosis ini diperbarui setelah slice 1-8 selesai; lihat "Ringkasan diagnosis (diperbarui)" di akhir dokumen.**

---

# Bagian kedua — temuan slice 3 sampai 8

Ditambahkan 2026-09-11 setelah `setting/`, `sharedLogic/`, `mangareader`,
`camoprof`, `ftf`/`proxy`, `credenz`, `tests/`, dan `tools/` dipetakan.

## Indeks bagian kedua

| ID | Ringkas | Kat | Berat |
| --- | --- | --- | --- |
| **BUG-1** | `catch` backpressure CatalogMirror tidak pernah cocok — retry 429/502/503 kode mati | **defek** | **kritis** |
| A5 | `SettingList`/`SettingCombo` disebut sebagai kontrol; yang ada hanya style | A | ringan |
| A6 | Artefak publish basi: 10 preset termasuk `Toolbar.presets.json` yatim | A | ringan |
| A7 | "v2 — not implemented, do not build ahead" untuk kemampuan yang sudah rilis | A | ringan |
| A8 | `sharedLogic/README.md` drift: kurang `CredenzPath`, `providers/`, `tests/`; salah cakupan deploy | A | ringan |
| A9 | Lima "behavior pair" ternyata pasangan dictionary+class, bukan code-behind x:Class | A | ringan |
| A10 | "satu-satunya titik registrasi" vs `ManualUrlProbeRegistry` + csproj test | A | sedang |
| A11 | Parent mangareader: "never interprets provider behavior" dibantah kodenya sendiri | A | sedang |
| A12 | "Catalog Mirror composes its own snapshot store" — parent yang mengonstruksi | A | sedang |
| A13 | "the queue's own answer is returned as-is" — parent menjumlah ulang pencacah | A | sedang |
| B4 | `Citizen.targets` hanya dukung satu plugin pyhost; mangareader menulis target duplikat | B | sedang |
| B5 | **Positif:** lapisan python sudah self-registration sejati | B | — |
| B6 | Biaya tambah feature berbeda jauh: Reader nol edit host, Downloader 2–4 | B | **berat** |
| B7 | Biaya tambah tab module mangareader = 2 file parent | B | sedang |
| C9 | Suite test python tidak masuk gate apa pun | C | **berat** |
| C10 | `sharedLogic/tests/` berisi guard feature camoprof — folder bersama, dua pemilik | C | sedang |
| C11 | `MangaMultiSelectFilter` abu-abu terhadap SU-4 | C | ringan |
| C12 | Dua provider Comix, ±3.100 baris duplikat, dua `ComixContractException` | C | **kritis** |
| C13 | Siklus import `shareLogic/Archive` ↔ `Features/Rar` | C | **berat** |
| C14 | Parent mangareader menjangkau 10 internal CatalogMirror + static Downloader | C | **berat** |
| C15 | Area tanpa cakupan test, termasuk parent module | C | sedang |
| D9 | `ftf`/`proxy` kerang; pekerjaan python hilang dari git, hanya `.pyc` tersisa | D | **berat** |
| D10 | `PyHost.cs` 595 baris, beberapa pekerjaan termasuk wrapper khusus Google | D | sedang |
| D11 | `RuntimeSetup.cs` 578 baris, beberapa pekerjaan | D | ringan |
| D12 | `AppearanceScreen.cs` 629 baris, `GalleryScreen.cs` 456 baris | D | ringan |
| D13 | `ISettingHost.cs` empat tipe publik / tiga concern; `UiPreference.cs` dua tipe | D | ringan |
| D14 | `_log` ditulis dua kali di `pyhost.py`; import-nya mati | D | ringan |
| D15 | Dokumen protokol pyhost asimetris: camoprof lengkap, mangareader tidak ada | D | sedang |
| D16 | Kebijakan kind layout punya dua pemilik di dua proyek | D | ringan |
| D17 | `FrameContentHost.cs` dua kelas; `ReaderFeatureContract.cs` ±15 tipe; hub di luar `ReaderCore/` | D | sedang |
| D18 | Logika feature Overlay tinggal di root Reader | D | sedang |
| D19 | Dua sanitizer nama file dengan aturan berbeda; cek magic-byte PNG ganda | D | ringan |
| D20 | `CoverBuilderView.xaml.cs` 352 baris; `DownloadQueueStore.cs` rumah helper JSON bersama | D | ringan |
| D21 | Tiga file Reader-only (±424 baris) tinggal di `shareLogic/` | D | ringan |
| D22 | `MangaReaderView.xaml.cs` 412 baris, enam pekerjaan | D | **berat** |
| D23 | `BrowserSessionCoordinator.cs:399` meng-hardcode nama plugin feature | D | sedang |
| D24 | `plugin.py` identik byte-per-byte dengan `__init__.py` di camoprof — file mati | D | ringan |
| D25 | Tab Editor yatim di camoprof; id profil dicetak di lapisan UI | D | ringan |
| D26 | Versi Velopack dipin dua tempat; invarian rilis ditegakkan dua tempat | D | ringan |

---

## BUG-1 — `catch` backpressure CatalogMirror tidak pernah cocok

- [x] **Terverifikasi baris per baris oleh dokumenter sendiri, 2026-09-11**

Ini **defek nyata**, bukan penyimpangan struktur. Ia dicatat di puncak bagian ini
dengan sengaja.

**Rantai bukti (enam mata rantai, semuanya terverifikasi langsung):**

1. Ada **dua** tipe `ComixContractException`, keduanya
   `sealed : InvalidOperationException`, nama identik, namespace berbeda:
   - `module/mangareader/Features/Downloader/Sources/Comix/ComixSource.cs:195`
     (namespace `Module.Mangareader.Features.Downloader.Sources.Comix`, dideklarasikan `:9`)
   - `module/mangareader/Features/Catalog/Sources/Comix/CatalogComixSource.cs:196`
     (namespace `Module.Mangareader.Features.Catalog.Sources.Comix`, dideklarasikan `:10`)
2. Sumber snapshot yang benar-benar dipakai CatalogMirror adalah `CatalogComixSource`:
   `Features/Catalog/CatalogSourceDirectory.cs:18` `_sources = [new CatalogComixSource(browser)]`,
   dengan `using …Features.Catalog.Sources.Comix` di `:2`.
3. `CatalogComixSource` melempar exception dari namespace-nya **sendiri**, dengan
   `HttpStatus` terisi: `CatalogComixSource.cs:198` (`public int? HttpStatus { get; set; }`),
   lemparan di `:619, 628, 664, 805, 861, 1104, 1114, 1128`;
   `GetSnapshotPageAsync` di `:568`, dan ia bahkan punya
   `catch (ComixContractException …) when (HttpStatus is 401 or 403)` di `:597`.
4. Tapi `Features/CatalogMirror/CatalogMirrorSyncFeature.cs:4` meng-import namespace
   **Downloader**: `using Module.Mangareader.Features.Downloader.Sources.Comix;`
5. Sehingga `catch (ComixContractException exception) when (exception.HttpStatus is
   429 or 502 or 503)` di `:416-418` mengikat ke tipe **Downloader**.
6. Kedua tipe `sealed` dan tidak saling menurunkan → exception bertipe Catalog
   **tidak mungkin** tertangkap oleh catch bertipe Downloader.

**Akibat yang bisa diamati:** retry backpressure bertipe untuk HTTP 429/502/503 —
backoff eksponensial `wait × 2^attempt`, cap 30 detik, plus jitter (`:419-425`) —
adalah **kode mati** pada sync CatalogMirror. Exception penyedia jatuh ke handler
generik (`:268`), sehingga sinkronisasi katalog ±90 ribu judul **tidak melakukan
backoff bertipe** saat provider melakukan throttling.

**Komentarnya justru menjelaskan niat yang benar:** "Typed backpressure only: v1
serves one source, so its contract exception is the only throttle signal. Never
message-sniffed; a second source would bring its own typed signal here"
(`CatalogMirrorSyncFeature.cs:413-415`). Niatnya tepat; `using`-nya salah arah.

**Klaim dokumen yang ikut gugur:**
`.docs/REPORT-catalog-mirror-handoff-2026-09-08.md:47` mencatat "backoff typed
429/502/503 (2 dtk ×2ⁿ, cap 30 dtk, maks 4 percobaan)" sebagai keputusan terkunci
yang terverifikasi. Mekanisme itu ada di kode tetapi tidak pernah aktif untuk sumber
yang benar.

**Kenapa tidak terdeteksi gate apa pun:**

- Compiler senang: tipe itu ada dan punya `HttpStatus`.
- Test hijau: tidak ada test yang melempar 429 dari sumber Catalog ke sync
  (`tests/Module.Mangareader.Downloader.Tests` me-link `CatalogMirrorSyncFeature.cs`
  tapi tidak mensimulasikan throttle lintas-namespace).
- Review sulit: kedua tipe bernama **identik**, jadi membaca `catch
  (ComixContractException)` terlihat benar kecuali pembaca memeriksa `using` di baris 4.

**Akar penyebabnya dua entri register lain:** **C12** (dua provider Comix yang
diduplikasi, masing-masing dengan exception bernama sama) **ditambah C3** (tidak ada
penegak untuk import lintas feature). Ini contoh konkret kenapa C3 mahal: satu
`using` yang salah arah menghasilkan perilaku retry yang hilang diam-diam di fitur
terbaru dan terbesar.

**Bentuk lurusnya nanti:** satu `ComixContractException` di tempat netral
(`shareLogic/Sources/`, tempat `MangaSourceContracts.cs` dan
`CatalogSnapshotContracts.cs` sudah tinggal) dipakai kedua provider; atau — lebih
baik, dan sekaligus menghapus C12 — satu provider Comix, bukan dua.

---

## A5 — `SettingList` / `SettingCombo` disebut sebagai kontrol; yang ada hanya style

`.docs/SHARED-UI-BEHAVIOR.md:64` menulis "All scroll surfaces use
`SettingScrollViewerStyle`: … `SettingList` / `SettingCombo` dropdowns". Tidak ada
kontrol bernama itu. Yang ada adalah **style** `SettingListStyle`
(`setting/SettingResources.xaml:176`) dan `SettingComboBoxStyle` (`:59`), dan
template keduanya memang memakai `SettingScrollViewerStyle` (`:197`, `:126`).

Jadi substansinya benar, namanya salah jenis. Bagi owner yang mencari folder
`SettingList` di `setting/Components/`, folder itu tidak ada.

**Bentuk lurusnya nanti:** ganti nama di dokumen menjadi `SettingListStyle` /
`SettingComboBoxStyle`, atau tambahkan kolom "bentuk: kontrol | style" pada tabel
kepemilikan.

## A6 — Artefak publish basi bertentangan dengan klaim preset

Klaim `SHARED-UI-BEHAVIOR.md:28-30` — hanya Button, Field, Toggle, Slider, Table
yang punya `.presets.json` — **benar untuk sumber**: tepat lima file ada, dan daftar
kontrol Gallery cocok (`GalleryScreen.cs:26`).

Tapi `artifacts/publish/release-1.1.0-smoke/Components/` berisi **10** preset,
termasuk `ActionCard`, `PasswordField`, `TableActions`, `Tabs`, dan
`Toolbar.presets.json` — preset untuk kontrol yang **tidak ada lagi di sumber mana
pun**. `artifacts/publish/win-x64/Components/` berisi 5 yang benar.

**Akibat:** dua pohon artefak di repo saling bertentangan, dan salah satunya menyebut
kontrol yang tidak eksis. Siapa pun yang memeriksa artefak untuk menjawab "kontrol
apa saja yang ada" mendapat jawaban salah.

**Bentuk lurusnya nanti:** `artifacts/` adalah keluaran; ia seharusnya tidak
menyimpan snapshot rilis lama yang bisa dibaca sebagai kebenaran. Bersihkan, atau
beri penanda tanggal/status di dalamnya.

## A7 — "v2 — not implemented, do not build ahead" untuk kemampuan yang sudah rilis

`module/sharedLogic/pyhost/README.md:338-343` mencadangkan browser-context fetch
untuk manga downloader sebagai "v2 — not implemented… do not build ahead". Kemampuan
itu **sudah terkirim** sebagai plugin v1: `mangareader_downloader/browser.py:405,487`
dan `DownloaderPyHostClient.cs:168-201`.

Dokumen ini menyatakan dirinya sumber kebenaran — "if code and this file disagree,
the file wins" (`pyhost/README.md:6-7`) — jadi kontradiksi ini berbahaya secara
khusus: agent yang mematuhinya akan menolak membangun sesuatu yang sudah ada.

**Bentuk lurusnya nanti:** perbarui §v2 menjadi catatan bahwa kemampuan sudah
terkirim sebagai plugin v1 milik citizen, dan pindahkan aturannya: "the file wins"
hanya berlaku untuk protokol inti, bukan untuk kemampuan milik plugin.

## A8 — `sharedLogic/README.md` drift

Tiga ketidakcocokan dalam satu file 35 baris:

- `:15` menyebut `cs/` sebagai "(PyHost client, RuntimeSetup)" — `CredenzPath.cs`
  tidak disebut, padahal ia pemilik resolusi vault.
- pohonnya (`:7-18`) tidak memuat `pyhost/providers/` dan `tests/`.
- `:30-31` mengatakan payload deploy adalah "pyhost/pyhost.py + requirements.txt",
  padahal `Citizen.targets:174` men-deploy `pyhost\**\*.py` — seluruh pohon termasuk
  `providers/`.

Bandingkan `Module.Camoprof.csproj:6` yang menyebut ketiga komponen C# dengan benar.

## A9 — Lima "behavior pair" bukan pasangan code-behind

`SHARED-UI-BEHAVIOR.md:213-215` mengajarkan "presentation in `.xaml`, behavior in
`.xaml.cs`". Untuk `SettingTableActions`, `SettingTabs`, `SettingViewport`,
`SettingActionCard`, dan `SettingDialog`, `.xaml`-nya adalah `ResourceDictionary`
**tanpa `x:Class`** (`Tabs.xaml:1`, dst.) dan kelasnya memuatnya saat runtime lewat
`SharedComponentResources.Load` (`Tabs.xaml.cs:11`, `Viewport.xaml.cs:30`,
`ActionCard.xaml.cs:17`, `Dialog.xaml.cs:12`, `TableActions.xaml.cs:26`).

Pola ini sah dan konsisten, tapi berbeda dari yang dibayangkan pembaca dokumen. Yang
benar-benar pasangan x:Class: `Button`, `Field`, `PasswordField`, `Toggle`, `Slider`,
`Table`, `ScrollBar`, `Drawer`, `WindowChrome`.

## A10 — "satu-satunya titik registrasi" padahal ada dua, plus manifest test

`MangaSourceRegistry.cs:104-107` mengklaim "The one explicit registration point…
the Downloader parent and Catalog composition are never edited". Benar untuk parent
dan Catalog. Tapi:

- `ManualUrl/ManualUrlProbeRegistry.cs:10-28` adalah titik registrasi per-provider
  **kedua**, dan ia menyebut tiga tipe provider konkret (Comix + DrakeScans +
  DynamicManual, `:2-3,14-25`). DrakeScans membutuhkannya; CucumberManga tidak.
- csproj test menuntut edit per file (`Downloader.Tests.csproj:93-119` untuk
  Cucumber/DrakeScans).

Jadi "satu titik" hanya benar bila lingkupnya dipersempit ke registry sumber.

## A11 — Parent mangareader: "never interprets provider behavior" dibantah kodenya

Komentar `MangaReaderView.xaml.cs:75-77`: "The root only translates record shapes
and forwards results: it never interprets provider behavior and owns no
update-checking rule."

Dibantah oleh file yang sama: parent **mengonstruksi** `UpdateMatcher` dan
`UpdateCheckerFeature` (`:79-88`), dan mengimplementasi `ReadConfirmedMapping`
(`:329-342`) yang membaca store internal Downloader `index.Load().Mappings` (`:333`).

## A12 — "Catalog Mirror composes its own snapshot store" — parent yang mengonstruksi

Komentar `MangaReaderView.xaml.cs:93-95` menyatakan CatalogMirror mengomposisi
dirinya lewat proyeksi netral plus snapshot store-nya sendiri. Kenyataannya parent,
bukan CatalogMirror, yang mengonstruksi setiap internalnya (`:101-169`), termasuk
`catalogStore.Database` (`:114`).

## A13 — "the queue's own answer is returned as-is" — parent menjumlah ulang

Komentar `MangaReaderView.xaml.cs:216-218`: "the queue's own answer is returned
as-is … never reinterpreted here." `EnqueueUpdate` (`:349-379`) **menjumlah ulang**
dua pencacah skip milik queue (`:367-370`).

Catatan: `UpdateCheckerFeature.cs:35-37` ("Update Checker never calls a queue
itself — the composition root supplies the route") **akurat**; yang meleset adalah
klaim bahwa rute itu tidak menafsirkan ulang.

---

## B4 — `Citizen.targets` hanya mendukung satu plugin pyhost per citizen

`Citizen.targets:196-214` men-deploy paket python dari
`Features\*\$(PyhostPluginName)\*.py` — **satu** nama paket per citizen, diset lewat
property `<PyhostPluginName>` (`Module.Camoprof.csproj`, `Module.Mangareader.csproj`).

MangaReader punya **dua** pemilik runtime python (Downloader dan Catalog), jadi ia
menulis target keduanya sendiri: `DeployMangaReaderCatalogPyhostPlugin`
(`Module.Mangareader.csproj:51-65`, `AfterTargets="DeployPyhostPlugins"`), yang
menduplikasi logika `MakeDir` + dua `Copy` dengan `DestinationFiles` + aturan
`plugin.py` → `__init__.py`.

Komentarnya menjelaskan maksudnya dengan baik ("Catalog is a second top-level
runtime owner… The two packages never share a browser/session host"), dan
pemisahan itu **terverifikasi** di sisi C# (`CITADEL-FLOWS-MANGAREADER.md` §8).
Biaya strukturalnya: pekerjaan "deploy paket pyhost milik feature" kini punya **dua
pemilik** (ST-1), dan camoprof terbentur batas yang sama bila butuh plugin kedua
(`Module.Camoprof.csproj:19`).

**Bentuk lurusnya nanti:** buat `PyhostPluginName` menerima **daftar** (item MSBuild,
bukan property), sehingga `DeployPyhostPlugins` mengulang sendiri dan tidak ada
citizen yang perlu menulis target kedua.

## B5 — POSITIF: lapisan python sudah self-registration sejati

Dicatat di kategori B karena ini **temuan pembanding**, bukan penyimpangan.

`module/sharedLogic/pyhost/` sudah menjalankan pola yang persis diminta owner, dan
yang hilang di lapisan feature C#:

| Sifat yuz-ui D-CONTRACT | Nyata di pyhost | Bukti |
| --- | --- | --- |
| child mendaftar karena ADA | paket diimpor dari daftar env, `install(host)` dipanggil | `pyhost.py:155-171` |
| parent mendengarkan, tak menyebut nama child | `_Host` tidak tahu isi plugin; hanya `register_commands` | `pyhost.py:92-106` |
| tabrakan id ditolak keras | `COMMAND_COLLISION` | `pyhost.py:97-99` |
| fault isolation | plugin gagal dilog, **host tetap hidup** | `pyhost.py:169-171` |
| sibling saling asing | arah dependensi plugin → core saja | `pyhost/README.md` PROTOCOL v1 |
| shared core feature-free | **terbukti**: nol kecocokan nama citizen di seluruh `pyhost/**/*.py` | grep 2026-09-11 |

Satu-satunya yang belum otomatis: daftar paket masih ditulis tangan di csproj
(`<PyhostPluginName>`) dan dibatasi satu per citizen (B4).

**Artinya untuk perbaikan nanti:** pola yang dibutuhkan lapisan feature C# **sudah
pernah dibangun dan terbukti di repo yang sama**, di lapisan python. Ia bukan ide
baru yang harus dirintis — ia pola yang harus dipindah.

## B6 — Biaya menambah feature berbeda jauh di dalam satu citizen

| Tempat | Biaya menambah satu feature |
| --- | --- |
| `mangareader/Reader/` | folder baru + **1 baris** di `ReaderDefaultFeatureCatalog.cs:17-29`; feature drawer-card/visual = **nol edit host** (`ReaderFeatureHost.cs:49-63,72-84`) |
| `mangareader/Features/Downloader/` (provider) | 2–5 file baru + **2 tempat** di `MangaSourceRegistry.cs:108-129` + mungkin `ManualUrlProbeRegistry.cs:10-28` + entri manual csproj test + `version.props` |
| `mangareader/` (tab tingkat atas) | **2 file parent** wajib diedit (§B7) |
| `camoprof/` | minimum **2 file** diedit; feature Launcher realistis **4–5 file** |
| `ftf`/`proxy` | `*View.xaml` + `layout.json` + folder baru (belum ada feature untuk dibandingkan) |

Reader membuktikan bentuk yang diinginkan owner **sudah mungkin** di codebase ini.
Perbedaannya bukan teknologi — ia ada atau tidak adanya katalog + host yang
menegakkan bentuk.

## B7 — Menambah tab module mangareader = dua file parent

`MangaReaderView.xaml` (tambah xmlns + `<TabItem>` + view, di `:14-43`) **dan**
`MangaReaderView.xaml.cs` (konstruksi/wiring di konstruktor `:36-186` + teardown di
`DisposeView` `:381-411`). Navigasi tab memakai 6 `<TabItem>` hardcoded dan
`TabItem.IsSelected` programatik (`:204`, `:209`, `:246`), bukan router.

Melanggar FM-3 ("adding/removing a feature touches only that feature's folder",
`SKILL.md:299-300`).

---

## C9 — Suite test python tidak masuk gate apa pun

Suite di `module/sharedLogic/tests/` (unittest stdlib: `test_pyhost.py` 803 baris,
`test_add_profile_plugin.py` 592, `test_add_profile_invariants.py` 245,
`test_add_profile_architecture.py` 140) **tidak dijalankan oleh gate otomatis mana
pun**:

- `.github/workflows/ci.yml:33-35` hanya `dotnet test Citadel.slnx`.
- `.agents/hooks/gate-on-stop.mjs:34` juga hanya itu.
- Suite python hanya muncul sebagai gate di plan lama
  (`.docs/PLAN-camoprof.md:330` G0b; `.docs/PLAN-ownership-shared-ui-2026-09-05.md:226`).

Yang tidak teruji karenanya: protokol NDJSON, lifecycle sesi, plugin loading,
**dan seluruh jalur yang mengangkut kredensial**. Perhatikan bahwa
`test_add_profile_architecture.py` berisi guard ARCH-1..4 berbasis grep-sumber —
persis jenis penegak yang hilang di lapisan C# (C3), dan ia sudah ada di lapisan
python.

**Bentuk lurusnya nanti:** satu langkah CI yang menjalankan suite python dengan venv
runtime, atau minimal memindahkan guard ARCH ke hook agent agar berlaku juga untuk
C#.

## C10 — Folder `tests/` bersama berisi guard feature satu citizen

Tiga dari empat suite di `module/sharedLogic/tests/` adalah guard feature
Add-Profile milik camoprof: `test_add_profile_plugin.py:25-26` menaruh
`..\camoprof\Features\AddProfile` di `sys.path`, dan `:31` menyetel
`CITADEL_PYHOST_PLUGINS=camoprof_add_profile`.

Sementara `module/sharedLogic/README.md:1` mengklaim tidak ada screen yang memiliki
folder ini. Jadi folder bersama punya dua pemilik, dan menghapus feature camoprof
berarti membersihkan folder shared.

Ini cermin python dari D23 (nama feature di-hardcode di sharedLogic lokal camoprof).

## C11 — `MangaMultiSelectFilter` abu-abu terhadap SU-4

Dicatat sebagai **abu-abu, bukan pelanggaran tegas**.

Fakta: `Components/MangaMultiSelectFilter.xaml` memakai `setting:` (`:4`) +
`setting:SettingButton` (`:10`), dan mengimplementasikan kontrak bersama
`IUiPreferenceControl` (`.xaml.cs:49`; interface di
`setting/Components/UiPreference.cs:13`). Tapi Popup/CheckBox/chevron-nya adalah WPF
polos (`xaml:16-46`, `cs:76-84`) — **satu-satunya komponen module yang menambah
perilaku interaksi rendah baru**.

Tidak ada primitif multi-select di `setting/Components/`, jadi **tidak ada yang
disalin** (SU-2 aman). Ia dipakai empat layar (`CatalogMirrorView.xaml:165-185`,
`ComixFilterPanel.xaml:40-90`, `CucumberMangaFilterPanel.xaml:18-24`,
`DrakeScansFilterPanel.xaml:7-10`), jadi peran reusable-nya nyata, bukan
hardcoding satu layar.

SU-4 mengecualikan combo component yang "introduces no new low-level rendering or
interaction behavior". Komponen ini menambah perilaku popup — jadi secara hurufiah
ia di luar pengecualian, tapi secara semangat (reusable, tidak menyalin, tidak
mengganti perilaku bersama) ia dekat dengannya.

**Bentuk lurusnya nanti:** keputusan owner, dua pilihan — (a) terima sebagai combo
dan catat pengecualiannya di `SHARED-UI-BEHAVIOR.md`; atau (b) angkat menjadi
primitif bersama `setting/Components/SettingMultiSelect`, karena sudah dipakai empat
layar dan `SHARED-UI-BEHAVIOR.md:212-216` sendiri berkata "if a genuinely reusable
pattern is needed by more than one screen, add it there".

## C12 — Dua provider Comix, ±3.100 baris duplikat

| Aspek | `Features/Downloader/Sources/Comix/ComixSource.cs` | `Features/Catalog/Sources/Comix/CatalogComixSource.cs` |
| --- | --- | --- |
| Ukuran | **1.606 baris** | **1.537 baris** |
| Kontrak sendiri | `ComixContract` (`:26`) | `ComixContract` sendiri (`:27`) |
| Exception sendiri | `ComixContractException` (`:195`) | `ComixContractException` (`:196`) |
| `ICatalogSnapshotSource` | logika snapshot paralel (`:514,752`) | logika paralel (`:514,683`) |
| Decoder halaman | `Comix/ComixPageDecoder.cs` | `Catalog/Sources/Comix/CatalogComixPageDecoder.cs` |

Satu pekerjaan (berbicara dengan penyedia Comix), dua pemilik, ±3.100 baris.

**Ini bukan sekadar duplikasi kosmetik — ia sudah menghasilkan BUG-1.** Dua tipe
exception bernama identik di dua namespace membuat satu `using` yang salah arah
menjadi tak terlihat oleh compiler, test, dan review.

Pemisahannya disengaja dan terdokumentasi: `CatalogSourceDirectory.cs:8-9`
menyatakan tidak meng-impor registri/kontribusi/context/browser Downloader, dan
`Module.Mangareader.csproj:48-50` menyatakan kedua paket python "never share a
browser/session host". **Pemisahan runtime-nya benar dan terverifikasi**; yang
diduplikasi adalah **logika penyedia**, bukan prosesnya.

**Bentuk lurusnya nanti:** pisahkan dua sumbu yang sekarang tercampur —
(1) logika penyedia Comix (kontrak, parse, transform, snapshot) jadi **satu** pemilik
di `shareLogic/Sources/`; (2) isolasi runtime (klien browser, proses pyhost, root
staging) tetap **dua**, karena itu memang keputusan yang benar. Hasilnya duplikasi
±3.100 baris hilang, pemisahan proses tetap, dan BUG-1 tidak mungkin terjadi lagi
karena hanya ada satu `ComixContractException`.

## C13 — Siklus import `shareLogic/Archive` ↔ `Features/Rar`

`shareLogic/Archive/ArchivePageReader.cs:3` meng-import `Features.Rar`, sementara
`Features/Rar/RarArchiveFeature.cs:3,80,90` meng-import `shareLogic`. Folder
"bersama" bergantung pada satu feature, dan feature itu bergantung balik ke folder
bersama.

Ini **saling import literal di dalam satu citizen** — persis bentuk yang dikeluhkan
owner — dan tidak terlihat gate mana pun (C3): satu assembly, jadi compiler tidak
peduli.

Konteks pekerjaannya: ekstraksi arsip memang terbelah dua pemilik
(`Features/Rar/RarArchiveFeature.cs:13`memiliki pemanggilan proses RAR: extract
`ReadPages :41-71`, tulis-ulang sampul `WriteCover :73-117`, spawn `Rar.exe` arg
`x/a/t` `:54-56,95-98,129-160`; `shareLogic/Archive/ArchivePageReader.cs:34-53`
memiliki ekstraksi ZIP + dispatch format; plus `CoverArchiveWriter.cs:22`,
`ArchiveReplacementTransaction.cs:49`, `ArchiveLockCoordinator.cs:15-32`).

**Bentuk lurusnya nanti:** satu pemilik untuk "buka arsip apa pun". `ArchivePageReader`
menjadi dispatcher yang **bertanya** ke penyedia format terdaftar (RAR, ZIP) lewat
kontrak, sehingga arah dependensi jadi `Features/Rar → shareLogic/Archive` saja.
Ini persis pola children-mendaftar-ke-parent yang sudah terbukti di Reader dan di
pyhost.

## C14 — Parent mangareader menjangkau internal feature

`MangaReaderView.xaml.cs` mengonstruksi ±10 tipe internal CatalogMirror
(`:101-169`), termasuk `catalogStore.Database` (`:114`) — objek database diteruskan
ke feature lain (`new CatalogGenreStore(catalogStore.Database)`, `:112-114`).
Plus static milik Downloader: `ListerFeature.IsLocallyAvailable` (`:129`) dan
`DownloadQueueFeature.SanitizeFolder` (`:148`), dan store internal
`index.Load().Mappings` (`:333`).

Melanggar FM-1 (parent hanya tahu kontrak) dan FM-4 (state bersama seharusnya lewat
context, bukan diantar parent dari internal feature ke feature lain).

**Pembanding yang membuktikan ini bisa dihindari:** `ReaderWindow.xaml.cs` (165
baris) menyebut **nol** tipe feature konkret; keduabelas feature hanya muncul di
`ReaderDefaultFeatureCatalog.cs:18-29`.

## C15 — Area tanpa cakupan test

| Area | Keadaan |
| --- | --- |
| `module/ftf/`, `module/proxy/` | tanpa proyek test, tidak di `.slnx`, CI tidak pernah mengompilasi; pertama kali terkompilasi di `Build-Release.ps1:71-80` |
| `module/blank/` | tanpa unit test; hanya dipakai sebagai artefak ter-deploy oleh `Citadel.Uia` (`:32-34`) |
| **`MangaReaderView.xaml(.cs)` — parent module 412 baris** | **tanpa test**, padahal ia memuat logika availability, aturan tabrakan folder, dan translasi queue (§A11–A13) |
| `MangaReaderModule.cs`, `MangaTitleCard.xaml(.cs)` | tanpa test |
| mangareader `CoverBuilder/` | tanpa test |
| mangareader `Features/Catalog/` (klien browser Runtime) | tanpa test |
| sebagian besar `Components/` | hanya `MangaMultiSelectFilter.xaml.cs` yang di-link (`Downloader.Tests:41`) |
| camoprof `Launcher/`, `Network/`, `Runtime/`, `Providers/Google` di luar 2 file | tanpa test |
| `module/sharedLogic/pyhost` | suite ada, tidak masuk gate (C9) |
| `tools/` | skrip tanpa test |
| `Citadel.Searcher` | hanya lewat Uia, tanpa unit test tersendiri |

Catatan: ketiadaan test pada parent module adalah alasan BUG-1 dan A11–A13 bisa
bertahan — tidak ada yang menguji klaim yang ditulis parent tentang dirinya sendiri.

---

## D9 — `ftf` dan `proxy` kerang, dengan pekerjaan python yang hilang dari git

Masing-masing **hanya 6 file ter-track** (`git ls-files`): `*Module.cs`,
`*View.xaml`, `*View.xaml.cs`, `*.csproj`, `layout.json`, `module.json`. Code-behind
9 baris, hanya `InitializeComponent` (`FtfView.xaml.cs:6-9`, `ProxyView.xaml.cs:6-9`),
dengan komentar "feature behavior belongs in feature folders" (`:5`) — niatnya benar,
foldernya kosong.

Folder feature **kosong di git**: `ftf/Backend/`, `ftf/Features/{FaucetRun, GSuite,
Profiles, Results}/`, `proxy/Features/{Pool, Settings, Sync}/`.

Yang tersisa di disk hanya jejak:

```text
ftf/Features/FaucetRun/ftf_plugin/__pycache__/adapter.cpython-312.pyc
ftf/Features/FaucetRun/ftf_plugin/__pycache__/pipeline.cpython-312.pyc
ftf/Features/FaucetRun/ftf_plugin/tests/__pycache__/test_pipeline.cpython-312-pytest-8.4.1.pyc
proxy/Features/Pool/proxy_plugin/__pycache__/pool.cpython-312.pyc
proxy/Features/Pool/proxy_plugin/tests/__pycache__/test_pool.cpython-312-pytest-8.4.1.pyc
ftf/Features/FaucetRun/.pytest_cache/   proxy/Features/Pool/.pytest_cache/
obj/Debug/.../Features/GSuite/GSuiteView.g.cs · SettingsView.g.cs · SyncView.g.cs
artifacts/recovery-ftf-proxy-2.1.3-20260907
```

`.py` sumbernya **tidak ada di disk dan tidak pernah ada di riwayat git**
(`git log --diff-filter=D -- module/ftf module/proxy` kosong). `.gitignore:52-54`
mengabaikan `__pycache__/`, `*.pyc`, `*.pyo`; `.pytest_cache/` mengabaikan dirinya
sendiri lewat `.gitignore` buatan pytest. Karena itu **`git status` tampak bersih**.

**Yang perlu owner ketahui:** ada kerja python untuk FTF faucet-pipeline (`adapter`,
`pipeline`, `test_pipeline`) dan Proxy pool (`pool`, `test_pool`) yang pernah
dijalankan dan diuji di mesin ini, lalu sumbernya hilang. `.pyc` bisa
di-dekompilasi sebagian, dan `artifacts/recovery-ftf-proxy-2.1.3-20260907`
menunjukkan pernah ada upaya pemulihan. Ini bukan kebocoran rahasia — ini **pekerjaan
yang hilang tanpa jejak di version control**.

**Bentuk lurusnya nanti:** (1) putuskan apakah ftf/proxy dilanjutkan atau
dipensiunkan; (2) bila dilanjutkan, periksa `.pyc` dan folder recovery **sebelum**
menulis ulang, karena kemungkinan besar isinya bisa dipulihkan; (3) tambahkan aturan
`.gitignore` yang tidak mengabaikan `*.py` di dalam `module/` — sumber python citizen
adalah kode produksi, dan pola plugin pyhost (`Citizen.targets:196-214`) memang
mengharapkan `Features/*/<paket>/*.py` ter-commit.

## D10 — `PyHost.cs` memegang beberapa pekerjaan

595 baris dalam satu file: transport NDJSON + korelasi id (`_pending` :36) ·
**wrapper bertipe khusus Google/sesi** (`InspectGoogleAsync :117`,
`NavigateSessionAsync :127`, `RelogGoogleAsync :142`, `VerifySessionAsync :159`) ·
tangga kill proses (`Dispose :291`, `Abort` taskkill-tree `:352`) · pembaca baris
terbatas (`:430-489`).

Yang paling layak dicatat: **pengetahuan khusus Google tinggal di transport yang
menyebut dirinya generik.** Ini disahkan oleh guard arsitektur
(`module/sharedLogic/tests/test_add_profile_architecture.py:10-11`, ARCH-3), jadi ia
keputusan sadar — tapi konsekuensinya `sharedLogic/cs` bukan transport netral, dan
setiap citizen yang me-link-nya (camoprof, mangareader) ikut membawa pengetahuan
Google. Mangareader tidak memakai fungsi Google itu.

**Bentuk lurusnya nanti:** pindahkan wrapper bertipe Google ke camoprof (satu-satunya
pemakai), biarkan `PyHost.cs` menjadi transport + `SendAsync` generik (`:177`).

## D11 — `RuntimeSetup.cs` memegang beberapa pekerjaan

578 baris: probe status (`CheckStatusAsync :94`) + unduh/ekstrak CPython terverifikasi
SHA-256 (`:31-34, :311`) + jalankan proses (`RunAsync :164`, helper `:493-577`) +
buat venv (`:204`) + pip (`:221`) + camoufox fetch (`:236`).

Satu hal yang **patut dipuji** dan dicatat sebagai pembanding: file ini secara
eksplisit merancang mengatasi bahaya salinan-per-assembly. `:158-163` mencatat bahwa
"a static semaphore would be per-assembly and useless", lalu `RunAsync` dijaga
**file lock lintas proses** `.setup.lock` (`:172-176`). Satu-satunya static mutable
adalah `HttpClient` per salinan (`:36`). Ini bukti bahwa konsekuensi C1 dipahami dan
ditangani dengan benar di satu tempat.

## D12 — Layar setting yang sangat besar

`setting/Screens/AppearanceScreen.cs` 629 baris memegang: editor token (`:34-91`) +
lifecycle drag-preview (`:165-200`) + render guard-issue (`:514-540`) + komputasi
batas lintas-metrik (`:549-607`) + aturan parse warna (`:403-422`).
`GalleryScreen.cs` 456 baris memegang layar + CRUD preset + konfirmasi hapus.

**Bukan pelanggaran SU:** kontrak memang mengizinkan layar memiliki data, command,
dan logikanya sendiri. Tapi empat pekerjaan yang bisa dipisahkan tinggal di satu
file, dan nama file hanya menyebut yang pertama.

## D13 — File dengan beberapa tipe publik di `setting/`

- `ISettingHost.cs`: empat tipe publik — `ScreenFailure` (`:10`), `AppUpdateState`
  (`:16`), `SidebarGroup` (`:28`), `ISettingHost` (`:44`) — dan interface-nya
  merentang tiga concern: registri module, status update app, dan CRUD grup sidebar
  (`:71-81`).
- `UiPreference.cs`: `IUiPreferenceControl` (`:13`) + kelas attached-property
  `UiPreference` (`:26`).
- `ScrollBar.xaml.cs`: `SettingScrollBar : ResourceDictionary` (`:12`) +
  `ScrollBarAutoFade` attached (`:21`) — yang ini **didokumentasikan sebagai satu
  unit** (`SHARED-UI-BEHAVIOR.md:34-36`), jadi sah.

Pola yang sama dengan D3 (`Tokens.cs` empat tipe publik) dan D5.

## D14 — `_log` ditulis dua kali, import-nya mati

`module/sharedLogic/pyhost/pyhost.py:27` mengimpor `log as _log` dari `providers`,
lalu `:37-39` **mendefinisikan ulang `_log` yang identik**, menaungi import. Badan
yang sama ada di `providers/__init__.py:19-21`. Jadi ada dua pemilik untuk satu
pekerjaan dan satu import mati.

## D15 — Dokumen protokol pyhost asimetris

`pyhost/README.md` adalah kontrak PROTOCOL v1 dan menyatakan dirinya menang bila
berselisih dengan kode (`:6-7`). Ia memuat spesifikasi lengkap perintah
`camoprof.add_profile.*` (`:154-277`) dan menyebut CamoProf di `:65`, tapi perintah
hidup milik mangareader (`downloader.open/api/fetch/close`,
`mangareader_downloader/browser.py:268,405,487,545`) **tidak muncul sama sekali**
(nol kecocokan grep).

Jadi dokumen "bersama" sebenarnya mendokumentasikan satu citizen. Pembaca yang
percaya "the file wins" akan menyimpulkan perintah downloader tidak sah.

**Bentuk lurusnya nanti:** README bersama hanya memuat protokol inti + cara plugin
mendaftar; spesifikasi perintah per plugin pindah ke README milik paket plugin itu.

## D16 — Kebijakan kind layout punya dua pemilik di dua proyek

Kosakata slot (position/size/visibility) dan validasinya hidup di
`setting/LayoutEditor/DeclarationReader.cs:8,19-21`, sementara penerapannya dan
penolakan kind tak dikenal hidup di `core/Citadel.Shell/LayoutApplier.cs:30-32,115-129`.
Keduanya konsisten hari ini, tapi tidak ada yang merujuk yang lain, dan tidak ada
test yang menegaskan keduanya setuju.

Ditambah `Overrides.IsValidLayoutProperty` (`tokens/Overrides.cs:246-274`) yang
dipanggil `Tokens.CommitLayout` (`Tokens.cs:319`) — jadi **tiga** tempat menyentuh
kebijakan properti layout.

## D17 — Struktur Reader menyimpang dari bentuk yang dijanjikan skill-nya

- `FrameContentHost.cs` memuat dua kelas: adapter viewport (`:15-111`) +
  `ReaderStatusHost` (`:114-190`).
- `ReaderFeatureContract.cs` 280 baris memuat ±15 tipe, termasuk **dua hub konkret**
  (`:70`, `:113`). `SKILL.md:270` menempatkan "hubs" di `ReaderCore/`, tapi 2 dari 4
  hub tinggal di root `Reader/`.
- `ReaderPreferencesStore.cs` = store + 3 record + `IReaderPreferencesFileIO` +
  implementasi (`:9-23`, `:357-382`).
- Skill menyebut `ReaderWindow.cs`; yang ada pasangan WPF `ReaderWindow.xaml` (178)
  + `.xaml.cs` (165) — drift nama di `SKILL.md:269`.

## D18 — Logika feature Overlay tinggal di root Reader

`Reader/ReaderViewportNavigator.cs:8` ("Coalesced 90%-viewport **Overlay**
navigation") dan `ReaderViewportStepPolicy.cs` berada di root `Reader/`, padahal
satu-satunya konsumen adalah feature Overlay (`ReaderOverlay.xaml.cs:8,22`).
Namanya sendiri menyebut feature pemiliknya.

Ditambah: XAML parent memiliki template render permukaan/halaman milik
ChapterLoading termasuk transform zoom (`ReaderWindow.xaml:48-97`, `:55-58`).

## D19 — Duplikasi kecil di Downloader

- Cek magic-byte PNG ditulis dua kali: `PageTransport.cs:324-327` dan
  `AutoCoverFeature.cs:224-227`.
- **Dua sanitizer nama file dengan aturan berbeda** untuk pekerjaan mirip:
  `DownloadQueueFeature.SanitizeFolder :1091` vs `ChapterDownloadPipeline.Sanitize :491`.
  Yang kedua lebih berbahaya: dua aturan berbeda untuk nama folder yang sama bisa
  menghasilkan path yang tidak cocok antara penjadwal dan pipeline.

## D20 — File besar dengan pekerjaan campuran di mangareader

- `CoverBuilderView.xaml.cs` 352 baris: orkestrasi batal/sibuk (`:262-281`),
  pemuatan preview (`:289-340`), penyusunan teks status (`:192-208`, `:230-243`).
- `DownloadQueueStore.cs` juga menjadi rumah helper JSON generik `DownloaderJson`
  (`:39`) yang dipakai `DownloadSourceIndex.cs:43` **dan** parent
  `MangaReaderView.xaml.cs:49` — helper bersama tinggal di dalam satu feature.
- `MangaSourceRegistry.cs` memuat 2 interface UI + record registrasi + registri +
  factory (`:11,25,40,53,108`).
- `CucumberMangaSource.cs` memuat kontrak, exception, enum, options, record query,
  dan source (`:11-116`); `ComixSource.cs` serupa (`:26-513`).

## D21 — Tiga file Reader-only tinggal di `shareLogic/`

| File | Baris | Satu-satunya konsumen |
| --- | --- | --- |
| `CbzChapterLoader.cs` | 296 | `Reader/CbzReaderChapterLoader.cs` |
| `ChapterSurfaceModel.cs` | 65 | Reader (5 file, semuanya di `Reader/`) |
| `ChapterLoadingContracts.cs` | 63 | Reader |

±424 baris kontrak chapter-loading milik Reader tinggal di folder "bersama".
Selebihnya `shareLogic/` adalah kernel bersama yang sah (12 file lain punya 2–27
konsumen lintas folder).

## D22 — `MangaReaderView.xaml.cs`: 412 baris, enam pekerjaan

Komposisi (`:36-186`) + logika ketersediaan lokal (`:116-137`) + aturan tabrakan
folder (`:139-160`) + translasi hasil queue (`:349-379`) + navigasi tab
(`:204,209,246`) + disposal (`:381-411`).

Ini file yang paling jauh dari "satu file = satu pekerjaan", dan ia adalah pintu
masuk citizen terbesar. Bandingkan `ReaderWindow.xaml.cs` (165 baris, satu
pekerjaan) di module yang sama.

## D23 — Nama feature di-hardcode di folder "shared" lokal camoprof

`module/camoprof/sharedLogic/BrowserSessionCoordinator.cs:399` meng-hardcode
`"camoprof_add_profile"`, sementara komentarnya di `:396-397` mengklaim "the shared
pyhost core stays feature-free". Klaim itu benar untuk `module/sharedLogic`, tapi
file **lokal** ini justru menyebut nama feature.

Akibat: menghapus atau mengganti nama feature AddProfile mengharuskan edit
sharedLogic — bertentangan dengan FM-3.

## D24 — `plugin.py` identik dengan `__init__.py` di camoprof (file mati)

`module/camoprof/Features/AddProfile/camoprof_add_profile/plugin.py` identik
byte-per-byte dengan `__init__.py` (35 baris masing-masing). pyhost memuat paket
lewat `__import__` (`pyhost.py:166`), yaitu `__init__.py`, sehingga `plugin.py`
tidak pernah dieksekusi.

Catatan pembanding: `Citizen.targets:200-213` memang men-deploy `plugin.py`
**sebagai** `__init__.py`, jadi di sumber cukup ada satu file. Dua file identik di
sumber = dua pemilik untuk satu pekerjaan, dan risiko keduanya menyimpang tanpa
terdeteksi.

## D25 — Tab yatim dan kebijakan domain di lapisan UI (camoprof)

- `layout.json:4` mendeklarasikan slot `EditorPanel`; `CamoprofView.xaml:19-21`
  merender tab "Editor" berisi hanya `SettingViewport` kosong;
  `CamoprofView.xaml.cs:94` men-toggle-nya. Tidak ada kode atau feature yang
  memiliki isinya — slot UI mati yang tetap terlihat user.
- `LauncherView.xaml.cs:89` mencetak identitas profil `"p_" + Guid` — penamaan
  domain dimiliki **view**, bukan feature atau `ProfileCatalog`.

## D26 — Satu kebijakan, dua tempat (versi Velopack dan invarian rilis)

- Versi Velopack dipin **dua kali**: CLI `vpk 1.2.0` (`.config/dotnet-tools.json`)
  dan library `Velopack 1.2.0` (`core/Citadel.Shell/Citadel.Shell.csproj:19`), dan
  csproj itu sendiri memberi peringatan "Keep this version identical to
  .config/dotnet-tools.json" (`:18`) — peringatan manual untuk masalah yang bisa
  ditegakkan mesin.
- Invarian rilis ditegakkan **dua kali**: `Build-Release.ps1:90-93` (jumlah
  direktori citizen) dan `:95-108` (tidak ada `Citadel.*.dll` bersama), sementara
  `VerifyCitizenIsolation` (`Citizen.targets:142-151`) sudah memeriksa hal kedua
  saat build citizen.

Pola yang sama dengan D7: kebijakan benar, tapi punya lebih dari satu pemilik, jadi
bisa basi tidak serempak.

---

## Ringkasan diagnosis (diperbarui setelah slice 1–8)

Dugaan awal owner — "agent yang build tetap membuat saling import" — **teruji dan
hasilnya lebih spesifik dari dugaan**:

1. **Di tingkat antar-proyek, tidak ada saling import.** Graf dependensi dikunci
   hook, graf aktual identik dengan yang diizinkan, nol `using Citadel.Ui` di
   module, nol citizen mereferensikan citizen lain, dan nol nama module di dalam
   `setting/`. Lapisan ini **bersih dan dijaga mesin**.
2. **Di tingkat antar-feature dalam satu citizen, ada saling import** — dan di sinilah
   semuanya tidak dijaga. Tiga bentuk nyata: siklus
   `shareLogic/Archive ↔ Features/Rar` (C13), import lintas feature
   `CatalogMirror → Downloader.Sources.Comix` (C14/§14 mangareader), dan parent yang
   menjangkau internal feature (C14).
3. **Saling import itu sudah menimbulkan kerusakan nyata**, bukan hanya kerapian:
   BUG-1 membuat retry throttling CatalogMirror mati total, dan akar penyebabnya
   adalah dua tipe exception bernama identik hasil duplikasi provider (C12).
4. **Gejala "banyak file tersentuh" punya tiga sumber berbeda**, bukan satu:
   enumerasi manual di manifest test (B2, 149 entri), registrasi `new` eksplisit di
   registry (B3), dan parent yang meng-hardwire set feature (B7, camoprof §1.1).
5. **Bentuk yang diinginkan owner sudah terbukti di tiga tempat dalam repo ini**:
   `Reader/` (katalog + host + nol edit untuk feature visual/drawer),
   `pyhost` python (self-registration + deteksi tabrakan + fault isolation), dan
   module discovery (folder hadir = terdaftar). Jadi perbaikan bukan merintis pola
   baru, melainkan **memindahkan pola yang sudah ada** ke lapisan yang belum
   memakainya.


---

## Status perbaikan (2026-09-11, phase 1-4)

- **BUG-1 — DIPERBAIKI.** `CatalogMirrorSyncFeature.cs` tidak lagi meng-import
  namespace Downloader; catch mengikat tipe bersama yang benar-benar dilempar
  sumber Catalog. Regression test: `ContractExceptionIsTheSingleSharedModuleType`.
  Backoff 429/502/503 kini hidup (test sync yang ada menjadi bermakna karena fake
  source dan provider nyata kini melempar tipe yang sama).
- **C12 — SEBAGIAN.** `ComixContractException` disatukan di
  `shareLogic/Sources/MangaSourceContracts.cs`; definisi lokal di `ComixSource.cs`
  dan `CatalogComixSource.cs` dihapus. Tujuh tipe vocabulary Comix masih duplikat
  lintas Catalog/Downloader — terenumerasi di
  `LevelBGuardTests.AcceptedDuplicateTypeNames` sebagai debt; C12 penuh =
  memindahkannya ke shared.
- **C13 — DIPERBAIKI.** Mekanisme Rar.exe pindah ke
  `shareLogic/Archive/RarArchiveEngine.cs`; `Features/Rar/RarArchiveFeature.cs`
  dihapus; `ArchivePageReader` dan `ArchiveReplacementTransaction` tidak lagi
  meng-import feature. Siklus putus; arah dependensi feature→shared.
- **C3 — SEBAGIAN.** Guard test Level-B ada untuk mangareader
  (`Architecture/LevelBGuardTests.cs`). camoprof belum punya; assertion 3 (parent
  hanya kontrak) sengaja belum ditegakkan karena C14/D22 belum diperbaiki.
- **Belum disentuh:** A1-A13 selain yang disebut, B1-B7, C1-C2, C4-C15 selain yang
  disebut, D1-D26.
