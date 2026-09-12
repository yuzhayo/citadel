# Plan: core sidebar grouping

Status: IMPLEMENTED — automated verification passed; live user smoke pending

## Goal

Tambahkan grouping presentasi pada sidebar core: group dapat dibuat, diubah nama,
dihapus, dan diisi module dari Settings; parent group dapat expand/collapse langsung
di sidebar. Struktur dan state tersimpan otomatis.

## Kontrak terkunci

- Group hanya mengatur presentasi navigasi. Discovery, `module.json`, route,
  lifecycle, dan instance module tidak berubah.
- Satu route module maksimal berada dalam satu group. Module tidak diduplikasi.
- Group kosong valid. Menghapus group mengembalikan anggotanya ke `All modules`
  (ungrouped), bukan menghapus module.
- Sidebar hanya menyediakan expand/collapse dan navigasi. Create, rename, delete,
  serta membership dimiliki Settings.
- State expanded/collapsed dan konfigurasi group disimpan otomatis.
- Route yang sementara tidak terpasang boleh tetap tersimpan dan aktif kembali
  saat citizen dengan route yang sama ditemukan lagi.
- Settings selalu pinned dan tidak dapat dimasukkan ke group.

## Ownership dan boundary

- `Citadel.Shell`: persistence, validasi mutation, komposisi snapshot registry
  menjadi row sidebar, dan wiring.
- `Citadel.Ui`: model row presentasi serta interaksi group header/route; tidak
  mengetahui registry, filesystem, atau Settings.
- `Citadel.Setting`: editor group melalui `ISettingHost`; tidak mengetahui Shell,
  searcher, atau folder module.
- Tidak mengubah `ModuleDescriptor`, searcher, manifest citizen, FTF, Proxy,
  CamoProf, MangaReader, atau route/lifecycle module.

## Slice implementasi

### A. State dan persistence

1. Tambah snapshot group pada seam Settings.
2. Tambah store Shell dengan load toleran, mutation tervalidasi, dan atomic save.
3. Pastikan nama non-kosong, id stabil, dan assignment tunggal.

Acceptance: create/rename/delete/assign/toggle menghasilkan snapshot deterministik
dan bertahan setelah store dibuka ulang.

### B. Sidebar composition

1. Tambah row kind `Group` pada model navigasi UI.
2. Render group header dengan chevron dan child route berindentasi.
3. Klik group hanya toggle; klik child tetap menavigasi route asli.
4. Susun group sesuai urutan konfigurasi, child sesuai urutan registry, lalu
   module ungrouped sesuai urutan registry.

Acceptance: tidak ada route duplikat, collapse menyembunyikan child, dan Settings
tetap pinned.

### C. Settings editor

1. Tambah entry `Sidebar groups` pada Settings.
2. Editor menyediakan create, select, rename, delete, dan checkbox membership.
3. Refresh saat registry atau konfigurasi berubah; group kosong tetap dapat dibuat.

Acceptance: seluruh mutation melewati `ISettingHost`, dan delete mengembalikan
module ke ungrouped.

### D. Verifikasi integrasi

1. Tambah test terarah untuk store, sidebar toggle/navigation, dan Settings seam.
2. Jalankan test project yang terdampak serta Release build Shell.
3. Jalankan app untuk smoke visual: create group, assign FTF/Proxy atau module lain,
   collapse/expand, restart, delete group.

Visual smoke tetap dilaporkan terpisah dari hasil build/test.

## Definition of done

- Semua kontrak di atas terpenuhi tanpa perubahan citizen/discovery.
- Targeted tests dan Release build lulus.
- Tidak ada file build/temp baru yang ikut menjadi source change.
- App siap diuji pengguna; hasil visual belum disebut PASS sebelum dilihat live.

## Hasil implementasi

- Persistence/store, sidebar group row, Settings editor, dan composition wiring
  selesai dalam boundary yang ditetapkan.
- `Citadel.Ui.Tests`: 15/15 passed.
- `Citadel.Uia`: 235/235 passed.
- `Citadel.Shell` Release build: 0 warning, 0 error.
- Release output app launched for user smoke; visual acceptance remains pending.
