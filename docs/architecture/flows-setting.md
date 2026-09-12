# CITADEL-FLOWS-SETTING — gudang komponen bersama dan layar setting

Ditulis 2026-09-11 dari kode. Klaim bertanda `file:baris`.
Kontrak yang mengadili: `.agents/skills/citadel-shared-ui/SKILL.md` (SU-1..SU-5)
dan `docs/contracts/shared-ui-behavior.md`.

**Temuan utama:** `setting/` adalah bagian citadel yang **paling patuh**. Nol nama
module di dalamnya, nol kunci style ganda, dan tabel kepemilikan di
`SHARED-UI-BEHAVIOR.md` cocok 13/13 dengan file yang ada. Penyimpangannya berupa
dokumen yang kurang lengkap dan beberapa file yang memegang lebih dari satu
pekerjaan — bukan perebutan kepemilikan.

---

## 1. Batas kepemilikan

```text
setting/  MEMILIKI                          module/  MEMILIKI
─────────────────────────────────────       ─────────────────────────────
kontrol reusable (Setting*)                 semantik feature
style · token · template universal          data domain
perilaku universal (hover/focus/disabled,   command & routing
  auto-fade scrollbar, chrome window,       SUSUNAN (arrangement) kontrol
  snap slider, ukuran kolom tabel)          visual yang bentuknya bagian dari
ui-preference persistence                     feature (cover card, overlay)
layout declaration editor                   combo component (komposisi primitif
preset Gallery                                bersama, tanpa perilaku rendah baru)
```

Bukti batasnya ditegakkan dari dua arah:

- `setting/Citadel.Setting.csproj:14-15` hanya mereferensikan `Citadel.Core` dan
  `Citadel.Contract`; komentarnya menyebut "not Shell, not the searcher, not
  module/" (`:5-7`).
- **Nol nama module di dalam `setting/`.** Grep `Manga|Camo|Comix|Faucet|Proxy|Downloader|Chapter`
  atas seluruh sumber `setting/`: tidak ada satu pun kecocokan kode. Satu-satunya
  yang terdekat adalah komentar "e.g. ReaderWindow" di
  `Components/NativeWindowChromeBehavior.cs:9` — menyebut konsumen masa depan,
  tanpa kopling kode.
- **Nol kunci style ganda.** Grep `x:Key="Setting` repo-wide (tanpa
  bin/obj/artifacts): 26 kecocokan, **semuanya di dalam `setting/`**, dan setiap
  kunci didefinisikan tepat sekali (`SettingResources.xaml:16-24,29,59,146,176,207,213,219`;
  `Dialog.xaml:5`; `ActionCard.xaml:5`; `ScrollBar.xaml:10,84`; `Viewport.xaml:5`;
  `TableActions.xaml:5`; `Tabs.xaml:5,45`).
- **Tidak ditemukan jalur behavior kedua di konsumen** (pemeriksaan tertarget):
  tidak ada `TargetType="{x:Type ScrollBar}"` di luar `ScrollBar.xaml:10,17` dan
  `Table.xaml:46` (yang `BasedOn` style bersama); `ReaderChromeController.cs:8`
  meng-instantiate `SettingWindowChrome` (adapter, bukan reimplementasi);
  `ReaderInputRouter.cs:166-167` memeriksa tipe `SettingWindowChrome`/`SettingDrawer`
  untuk kebijakan input; `MangaMultiSelectFilter` mengimplementasikan kontrak
  `IUiPreferenceControl` yang memang disediakan. Sapuan penuh setiap view module
  belum dilakukan → sebagian **unverified**.

---

## 2. Peta Components/

| Komponen | Tipe publik | Pasangan file | Pekerjaan |
| --- | --- | --- | --- |
| `SettingButton` | `: Button` (`Button.xaml.cs:6`) | x:Class sejati | tombol aksi, screen-blind |
| `SettingField` | `: Control` (`Field.xaml.cs:7`) | x:Class sejati | input teks + placeholder |
| `SettingPasswordField` | `: UserControl` (`PasswordField.xaml.cs:7`) | x:Class sejati | input password; password **bukan** DP |
| `SettingToggle` | `: ToggleButton` (`Toggle.xaml.cs:6`) | x:Class sejati | saklar dua keadaan |
| `SettingSlider` | `: Slider` (`Slider.xaml.cs:7`) | x:Class sejati | menambah DP `Step` snapping (`:10-16`) |
| `SettingTable` | `: UserControl` (`Table.xaml.cs:17`) | x:Class sejati, 345 baris | pembungkus DataGrid screen-blind: API `Columns`/`Rows` string + `InteractiveColumns` + tangkapan preferensi |
| `SettingTableActions` | `: Control` (`TableActions.xaml.cs:10`) | **dictionary + class** | aksi sel primer/sekunder |
| `SettingTabs` | `: TabControl` (`Tabs.xaml.cs:7`) | **dictionary + class** (`Tabs.xaml:1` tanpa x:Class, dimuat `:11`) | grup tab |
| `SettingViewport` | `SettingViewportMode` enum + `: ContentControl` (`Viewport.xaml.cs:6,16`) | **dictionary + class** (`:30`) | akar screen; mode `Contained`/`Document` |
| `SettingActionCard` | `: ContentControl` (`ActionCard.xaml.cs:7`) | **dictionary + class** (`:17`) | kartu permukaan isi penuh |
| `SettingDialog` | `: Window` (`Dialog.xaml.cs:7`, tidak sealed) | **dictionary + class** (`:12`) | chrome modal + konfirmasi |
| `SettingScrollBar` | `: ResourceDictionary` (`ScrollBar.xaml.cs:12`) **+** `ScrollBarAutoFade` attached (`:21`) | x:Class sejati (dictionary) | auto-fade scrollbar |
| `SettingDrawer` | `: ContentControl` (`Drawer.xaml.cs:13`) | pasangan sejati | panel geser kiri, `WidthFraction` |
| `SettingWindowChrome` | `: UserControl` (`WindowChrome.xaml.cs:16`) | pasangan sejati, 411 baris | chrome window auto-fade + kebijakan resize bersama |

Bukan kontrol, tapi penghuni `Components/`:

| File | Tipe | Pekerjaan |
| --- | --- | --- |
| `NativeWindowChromeBehavior.cs` | statis (`:14-21`) | fallback DWM dark caption |
| `UiPreference.cs` | `IUiPreferenceControl` (`:13`) + `UiPreference` attached (`:26`) | opt-in persistensi pilihan UI |
| `UiPreferenceStore.cs` | `internal` (`:12`) | store JSON atomik |
| `PresetStore.cs` | `Preset` record (`:9`) + `PresetStore` (`:29`) | baca/tulis `<Name>.presets.json`; menolak preset bernama route (`:19-21`) |
| `PresetApplier.cs` | statis (`:15`) | menerapkan nilai preset per id ke kontrol |
| `SharedComponentResources.cs` | `internal` (`:5-12`) | pemuat dictionary lewat pack-URI |

**Lima "behavior pair" sebenarnya pasangan dictionary+class, bukan pasangan
code-behind x:Class** (`TableActions`, `Tabs`, `Viewport`, `ActionCard`, `Dialog`).
`.xaml`-nya adalah `ResourceDictionary` tanpa `x:Class`, dan kelasnya memuatnya saat
runtime lewat `SharedComponentResources.Load`. Pola ini sah dan konsisten, tapi
berbeda dari yang dibayangkan pembaca `SHARED-UI-BEHAVIOR.md:213-215`
("presentation in `.xaml`, behavior in `.xaml.cs`") → register A9.

---

## 3. Persistensi preferensi UI

```text
XAML konsumen
│  setting:UiPreference.Key="kunci.stabil.unik"
▼
UiPreference (attached DependencyProperty)              UiPreference.cs:28-33
│  saat Key diset → pasang Loaded/Unloaded + event perubahan per tipe  (:88-111)
│  restore saat load (:51-86) · simpan saat berubah & saat unload (:136-171)
│  dijaga flag `Restoring` + state di ConditionalWeakTable (:35, 173-178)
▼
UiPreferenceStore (internal)                            UiPreferenceStore.cs:12
│  path: %LOCALAPPDATA%\Citadel\ui-preferences.json      (:27-30)
│  tulis atomik: file tmp → File.Move(overwrite)         (:52-60)
│  rusak/kebesaran/tak terbaca → fallback diam ke nilai XAML/default (:62-67, 85-98)
│  batas: file 256 KB, nilai 64 KB                       (:15-16)
▼
tipe yang opt-in bawaan (switch Restore/Save)            UiPreference.cs:62-80, 161-169
   SettingTable · Selector · ToggleButton · SettingField · IUiPreferenceControl
```

`IUiPreferenceControl` nyata (`UiPreference.cs:13-20`) dan **satu-satunya**
implementer produksi adalah `module/mangareader/Components/MangaMultiSelectFilter.xaml.cs:49`
(implementasi event eksplisit di `:95`) — persis seperti klaim
`SHARED-UI-BEHAVIOR.md:155-157`.

Konsumen yang memakai `setting:UiPreference.Key` (terverifikasi 2026-09-11):
`module/proxy/ProxyView.xaml:11`, `module/ftf/FtfView.xaml:11`,
`module/camoprof/CamoprofView.xaml:11`, `Launcher/LauncherView.xaml:25,60`, dan di
mangareader: `MangaReaderView.xaml:15`, `LibraryView.xaml:59,101`,
`HistoryView.xaml:103`, `UpdateCheckerDialog.xaml:71`, `CatalogScreen.xaml:35,71,237`,
`DownloadListScreen.xaml:58`, `CatalogMirrorView.xaml:57-351` (13 kunci),
`CucumberMangaFilterPanel.xaml` (8 kunci), `ComixFilterPanel.xaml` (9 kunci);
plus di dalam setting sendiri: `Screens/SettingsScreen.cs:45`.

Catatan: `proxy` dan `ftf` — dua citizen kerang (§STRUCTURE 6) — tetap memakai
kunci preferensi di view-nya, jadi mekanisme ini hidup bahkan tanpa feature.

---

## 4. LayoutEditor dan preset Gallery

`setting/LayoutEditor/` menghasilkan editor untuk route `settings/layout`. Yang
diedit adalah **deklarasi layout screen**, bukan visual komponen:

| File | Tipe | Pekerjaan |
| --- | --- | --- |
| `DeclarationReader.cs` | `SlotKind`, `SlotModel`, `NumberEdit`, `DeclarationReader` (`:8,11,20,37`) | deklarasi + override → model editor, plus validasi nilai |
| `EditorBuilder.cs` | `LayoutEditResult`, `EditorBuilder` (`:12,23`) | membangun satu kontrol per slot (`:29-54`), commit override sparse ber-kunci route lewat token store Core (`:26-28`) |

Kosakata slot lengkapnya tiga: position, size, visibility (`DeclarationReader.cs:8,19-21`)
— cocok dengan yang ditegakkan `LayoutApplier.cs:30-32,115-129` di sisi shell.
Jadi kebijakan kind layout punya **dua pemilik di dua proyek**: validasi nilai di
`setting/LayoutEditor`, penerapan di `core/Citadel.Shell/LayoutApplier`. Keduanya
konsisten hari ini, tapi tidak ada satu pun yang merujuk yang lain.

**Preset Gallery.** Klaim `SHARED-UI-BEHAVIOR.md:28-30` — "Only the Gallery-editable
primitives (Button, Field, Toggle, Slider, and Table) have `.presets.json` files" —
**benar untuk sumber**: tepat lima file ada
(`Components/{Button,Field,Toggle,Slider,Table}.presets.json`), dan daftar kontrol
Gallery cocok (`GalleryScreen.cs:26`). Tidak ada preset untuk Drawer, WindowChrome,
Dialog, Tabs, Viewport, ActionCard, TableActions, PasswordField, ScrollBar.

Preset disimpan di sebelah kontrol pada folder output (`PresetStore.cs:49-53`,
`Citadel.Setting.csproj:22-29`), Gallery menulis balik (`GalleryScreen.cs:15-19`),
dan sebuah preset tidak boleh bernama route (`PresetStore.cs:19-21,31`).

**Tapi artefak publish-nya basi:** `artifacts/publish/release-1.1.0-smoke/Components/`
berisi **10** preset termasuk `ActionCard`, `PasswordField`, `TableActions`, `Tabs`,
dan `Toolbar.presets.json` — preset untuk kontrol yang **tidak ada lagi di sumber
mana pun**. `artifacts/publish/win-x64/Components/` berisi 5 yang benar.
→ register A6.

---

## 5. Screens

| Layar | Route | Sumber data | Pekerjaan |
| --- | --- | --- | --- |
| `SettingScreen.cs` | (abstrak) | — | akar `SettingViewport` mode Document, merge `SettingResources`, helper Section/Body/Card/Stack/Row/Action, registrasi namescope `LayoutSlot` (`:16-97`) |
| `SettingsScreen.cs` | `settings` | **`ISettingHost`** injeksi konstruktor (`:38-41`), refresh di `host.Changed` (`:90-91`) | tabel screen terpasang, daftar kegagalan, rediscovery, aksi update, tautan ke 4 sub-route (`:22-92`) |
| `SidebarGroupsScreen.cs` | (sub) | `ISettingHost` (`:20`) | edit grup sidebar milik Shell (`:9-57`) |
| `ModuleLayoutScreen.cs` | `settings/layout` | `ISettingHost` + `Tokens` + `LayoutDeclaration` sendiri (`:34-38`) | pemilih route + editor slot hasil generate (`:12-66`) |
| `GalleryScreen.cs` | `settings/gallery` | `ISettingHost` (lookup pemakaian) + `presetDirectory` + `PresetStore` (`:43-47,143`) | peramban/editor/preview preset + hapus dengan konfirmasi (`:11-85`) |
| `AppearanceScreen.cs` | `settings/appearance` | **bukan** `ISettingHost` — `Tokens` langsung (`:58-61`) | editor token tema/metrik/warna (`:34-91`) |
| `SettingsLayout.cs` | — | — | `LayoutDeclaration` bawaan untuk `settings` sendiri (`:10-21`) |

Dua catatan:

- **Cara layar mendapat data tidak seragam.** Lima layar lewat `ISettingHost`;
  `AppearanceScreen` menyuntik `Tokens` langsung. Keduanya masuk akal (Appearance
  memang editor token), tapi pembaca yang mencari "bagaimana layar setting mendapat
  data" menemukan dua jawaban.
- **Layar setting bukan composition-only, dan itu benar.** Kontrak SU mengatakan
  "Screen/module code may own domain data, commands, routing, and arrangement of
  shared controls" — layar bawaan app memang memiliki logikanya sendiri. Yang layak
  dicatat adalah **ukurannya**: `AppearanceScreen.cs` 629 baris memegang editor
  token + lifecycle drag-preview (`:165-200`) + render guard-issue (`:514-540`) +
  komputasi batas lintas-metrik (`:549-607`) + aturan parse warna (`:403-422`);
  `GalleryScreen.cs` 456 baris memegang layar + CRUD preset. → register D12.

---

## 6. Tepi dependensi

```text
setting/ ──▶ Citadel.Core, Citadel.Contract        (csproj:14-15)  SAJA
   ▲
   ├── core/Citadel.Shell        (Citadel.Shell.csproj:30; dipakai App.xaml.cs:9-10,
   │                              ShellSettingHost.cs:5-6, MainWindow.xaml.cs:10-11,
   │                              SettingsWindow.xaml.cs:6, AppUpdateController.cs:2,
   │                              AppUpdateService.cs:2, SidebarGroupingStore.cs:5;
   │                              App.xaml:6,16 me-merge SettingResources)
   ├── SETIAP citizen            (lewat Citizen.targets:76, Private="false";
   │                              aturan dinyatakan :68, penjaga salinan privat :144-150)
   └── tests                     (Citadel.Uia.csproj:24,
                                  Module.Mangareader.Reader.Tests.csproj:16,
                                  Module.Mangareader.Downloader.Tests.csproj:21)

core/Citadel.Ui ──✗ TIDAK mereferensikan setting; satu-satunya tepi adalah
                    DynamicResource runtime yang sudah diketahui
                    (Ui/Theme/ThemeResources.xaml:147)
```

Grep repo-wide `(DynamicResource|StaticResource) Setting` **tidak menemukan tepi
XAML runtime lain** dari proyek tanpa referensi compile-time: semua kecocokan lain
ada di dalam `setting/` sendiri atau di file module yang memang memegang referensi
dari `Citizen.targets`. Jadi C5 adalah satu-satunya tepi runtime tersembunyi.

**Internals tidak bocor.** Yang `internal` di setting: `SharedComponentResources`,
`UiPreferenceStore`, `UiPreference.OverrideStoreForTesting` (`UiPreference.cs:44`),
`PresetApplier.ApplyValues` (`PresetApplier.cs:35`). Satu-satunya pemakai luar
adalah `tests/Citadel.Uia/SharedComponentBehaviorTests.cs:256`
(`OverrideStoreForTesting`), sah lewat `InternalsVisibleTo Citadel.Uia`
(`Citadel.Setting.csproj:19`). **Tidak ada** kode Shell atau module yang menyentuh
tipe internal setting.

---

## 7. Akurasi `SHARED-UI-BEHAVIOR.md` (audit klaim)

| Klaim | Hasil |
| --- | --- |
| 13 baris tabel kepemilikan | **13/13 ada**, nol entri basi |
| Hanya 5 kontrol punya `.presets.json` | **benar untuk sumber**; artefak publish basi (A6) |
| ScrollBar auto-fade dimiliki `Components/ScrollBar.xaml(.cs)` | benar; kedua tipe publik didokumentasikan bersama (`:34-36`) |
| `SettingList` / `SettingCombo` dropdown memakai `SettingScrollViewerStyle` (`:64`) | **kontrol bernama itu tidak ada**; yang ada style `SettingListStyle` (`SettingResources.xaml:176`) dan `SettingComboBoxStyle` (`:59`), dan template keduanya memang memakai `SettingScrollViewerStyle` (`:197`, `:126`) → A5 |
| Drawer & WindowChrome | terdokumentasi di bagian prosa terpisah (`:71-94`, `:96-113`), **tidak masuk tabel kepemilikan** |

Jadi tabelnya tidak salah, tapi **tidak lengkap**: tiga penghuni `Components/`
(Drawer, WindowChrome, NativeWindowChromeBehavior) punya bagian prosa sendiri dan
tidak muncul di tabel yang disebut sebagai daftar kepemilikan.

---

## 8. Yang sudah cocok dengan konsep kernel yuz-ui

| Konsep yuz-ui | Padanan di `setting/` | Bukti |
| --- | --- | --- |
| Satu pemilik per perilaku | setiap kunci style didefinisikan tepat sekali; nol duplikat | 26 kunci, semua di setting, §1 |
| Konsumen tidak menulis ulang perilaku | `BasedOn` style bersama; adapter, bukan reimplementasi | `Table.xaml:46`, `ReaderChromeController.cs:8` |
| Kontrak sempit sebagai pintu | `ISettingHost`: Setting mendeklarasikan bentuk, Shell mengisi | `ISettingHost.cs:38-41` |
| Opt-in eksplisit, bukan otomatis | `UiPreference.Key` — "the shared behavior never guesses a screen or persists every control automatically" | `SHARED-UI-BEHAVIOR.md:145-148`, `UiPreference.cs:28-33` |
| Gagal lunak, tidak menjatuhkan | store rusak → fallback ke nilai XAML/default tanpa memblokir layar | `UiPreferenceStore.cs:62-67,85-98` |

---

## 9. Rujukan register dari slice ini

A5 (`SettingList`/`SettingCombo` tidak ada sebagai kontrol) ·
A6 (artefak publish basi + `Toolbar.presets.json` yatim) ·
A9 (lima "behavior pair" adalah pasangan dictionary+class) ·
C5 (tepi runtime Ui→Setting, sudah dicatat slice 2) ·
D12 (`AppearanceScreen.cs` 629 baris, `GalleryScreen.cs` 456 baris) ·
D13 (`ISettingHost.cs` empat tipe publik / tiga concern; `UiPreference.cs` dua tipe) ·
D16 (kebijakan kind layout punya dua pemilik di dua proyek).

Detail dan bentuk lurusnya: `CITADEL-VIOLATIONS.md`.
