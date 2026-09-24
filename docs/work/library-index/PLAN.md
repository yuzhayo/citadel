# Plan: Library Index + lazy chapter loading (revisi)

Status: plan ready — belum diimplementasi.
Sumber: plan awal operator + review terhadap kode (`Library/LibraryScanner.cs`,
`Library/LibraryView.xaml.cs`, `shareLogic/MangaTitleCardModel.cs`,
`shareLogic/MangaReaderEvents.cs`, `Library/LibraryScanPersistence.cs`).
Label R-01..R-05 = perbaikan dari review, mengikat seperti isi plan lainnya.

## Target behavior

- Saat MangaReader start: baca index lokal, lalu tampilkan title + cover dari index.
- Tidak ada scan semua chapter saat startup.
- Klik cover/title: baru scan folder title tersebut dan tampilkan chapter asli.
- Tombol Scan: reconcile index dengan filesystem.
- Perubahan title saat module aktif: watcher memperbarui hanya title terdampak.
- Perubahan saat module mati: startup tetap cepat dari index, lalu reconciliation
  dangkal berjalan setelah grid tampil.

Hasil akhir: startup Library skalanya mengikuti jumlah title/index entry,
bukan jumlah semua archive chapter.

### 1. Pisahkan data index dan data title penuh

Tambahkan model khusus index, terpisah dari `MangaTitle`:

- `LibraryIndexDocument`
  - schema version
  - normalized library root
  - timestamp pembaruan
  - daftar entry

- `LibraryIndexEntry`
  - title/folder name
  - absolute folder path
  - `AddedUtc`
  - jumlah chapter
  - first/latest chapter label
  - fingerprint folder (`LastWriteTimeUtc`)
  - fingerprint `cover.*`
  - path thumbnail cover cache

Index tidak menyimpan daftar `ChapterInfo`. Jadi membaca 3.000 entry hanya
membaca satu file kecil, bukan 600.000 chapter.

### 2. Storage index yang aman

Buat `LibraryIndexStore` di area data lokal MangaReader, bukan di folder
manga user. Ikuti filosofi `LibraryScanPersistence`: yang gagal/cancel tidak
pernah merusak yang tersimpan.

Aturan:

- satu index per normalized root path (nama file diturunkan dari root yang
  dinormalisasi — satu root, satu file, tidak tertukar);
- tulis melalui file `.tmp`, lalu replace atomik;
- dokumen rusak, schema lama, atau root tidak cocok → diabaikan dan dibangun ulang;
- index lama tetap dipakai bila reconciliation baru gagal/cancel, agar Library
  tidak tiba-tiba kosong;
- cache thumbnail cover juga berada di storage lokal dan dibersihkan bila
  title hilang.

### 3. Pecah `LibraryScanner`

`LibraryScanner` saat ini selalu membuat `MangaTitle` beserta semua
`ChapterInfo` (`LibraryScanner.cs` L19–76). Dipecah menjadi dua jalur:

- `ReindexTitleAsync(folder)`
  - baca archive langsung dalam satu folder title;
  - hitung chapter dan first/latest label;
  - buat/perbarui thumbnail cover;
  - hasilnya hanya `LibraryIndexEntry`.

- `LoadTitleAsync(indexEntry)`
  - dipanggil hanya saat user membuka title;
  - baca archive folder title tersebut;
  - menghasilkan `MangaTitle` lengkap untuk `ChapterSelector`, Reader,
    Update Checker, dan Cover Builder.

Tidak ada lagi `ScanAsync()` yang membuat chapter list seluruh library pada
startup. (`ScanAsync` lama dihapus saat increment 3 selesai — satu pemilik,
satu pemanggil (`LibraryView`), jadi tidak perlu masa kompatibilitas.)

**R-01 — `AddedUtc` tidak boleh ditulis ulang saat reindex.**
Scanner hari ini memakai folder creation time sebagai `AddedUtc`
(`LibraryScanner.cs` L67–72). `ReindexTitleAsync` wajib mewarisi `AddedUtc`
dari entry lama bila title sudah ada di index; hanya title benar-benar baru
yang mendapat nilai baru. Kalau aturan ini dilanggar, sort "recently added"
hancur permanen dan tidak bisa dipulihkan. Test khusus di §10.

**R-02 — "archive pertama" harus deterministik.**
"Ambil first page dari archive pertama" didefinisikan sebagai: archive
urutan pertama menurut `NaturalStringComparer.OrdinalIgnoreCase` yang sama
dengan urutan chapter — bukan urutan enumerasi filesystem (tidak stabil
antar run). Kalau tidak, cover bisa gonta-ganti tiap reindex. Test khusus
di §10.

### 4. Cover tanpa membuka archive saat startup

Grid harus memakai cover cache dari index.

Saat reindex sebuah title:

1. Prioritas `cover.*` di folder title (difingerprint; berubah → reindex cover).
2. Jika tidak ada, ambil first page dari archive pertama (definisi R-02)
   satu kali, buat thumbnail cache.
3. Simpan path thumbnail di index.

Saat startup, card memuat bitmap dari thumbnail cache saja. Tidak ada
`MangaCoverLoader` yang membuka archive untuk ribuan title.

### 5. Startup Library

Ubah `LibraryView_Loaded` (`LibraryView.xaml.cs` L114–136):

1. Restore root.
2. Baca `LibraryIndexStore`.
3. Langsung isi `_cards` dari `LibraryIndexEntry` (butuh R-03 di bawah —
   `_cards` hari ini menampung card berbasis `MangaTitle` penuh).
4. Tampilkan status misalnya `3,000 titles indexed`.
5. Mulai background shallow reconciliation (primer — lihat R-04), lalu watcher.

Jika index belum ada:

- tampilkan state `Building library index…`;
- jalankan initial index build di background;
- setelah selesai, simpan index dan isi grid.

Initial build memang tetap perlu membaca folder sekali; itu biaya satu kali,
bukan biaya setiap start module.

**R-03 — Card model harus di-remodel, bukan gratis.**
`_cards` adalah `ObservableCollection<MangaTitleCardModel>` dan
`MangaTitleCardModel` membungkus `MangaTitle` penuh (`card.Manga.Title`
dipakai grouping di `LibraryView.xaml.cs` L390). "Isi `_cards` dari entry"
berarti: card menjadi entry-backed (Title, AddedUtc, chapter count, cover
thumbnail dari `LibraryIndexEntry`) + slot model penuh yang hanya terisi
setelah `LoadTitleAsync`. Grouping/filter/sort (§8) di-pointing ke field
entry, bukan `MangaTitle.Chapters`. Tanpa ini §5 dan §7 tidak bisa jalan.

### 6. Reconciliation dan watcher

Tambahkan `LibraryIndexCoordinator` sebagai pemilik update index.

**R-04 — Urutan keandalan dibalik: reconcile dulu, watcher bonus.**
Koleksi manga sering tinggal di network drive/USB, tempat
`FileSystemWatcher` tidak bisa diandalkan (event hilang/diduplikat).
Maka yang primer adalah **shallow reconciliation setiap startup**
(enumerasi folder title + metadata folder saja, tanpa membuka chapter —
murah), dan watcher hanya bonus saat module aktif. Jangan merancang
dengan asumsi watcher selalu benar.

- `FileSystemWatcher` hanya memantau root library (top-level saja),
  bukan seluruh archive.
- Event create/change/delete/rename di-debounce.
- Event dipetakan ke folder title tingkat pertama.
- Hanya entry title itu yang di-reindex atau dihapus.
- Saat startup, background shallow reconciliation hanya enumerasi folder
  title dan membaca metadata folder; tidak membuka chapter.
- Folder yang fingerprint-nya berubah saja di-reindex penuh.
- Tombol Scan menjalankan reconciliation eksplit seluruh root dengan
  aturan sama, bukan memaksa reload semua title ke UI.

Watcher dibuang saat Library/module dispose agar tidak ada event atau scan
setelah view ditutup. Ikuti pola cancellation yang sudah ada
(`_scanCancellation` di `LibraryView`): reconcile baru membatalkan yang
lama; hasil basi tidak pernah menyentuh `_cards` maupun index tersimpan
(cek `ReferenceEquals` seperti `ScanLibraryAsync` L191).

### 7. Lazy open title

Klik card tidak lagi langsung memanggil `ChapterSelector.ShowTitle(card)`.

Flow baru:

`Card click → disable card/loading state → LoadTitleAsync(entry) →
update entry bila berubah → buat/isi model lengkap →
ChapterSelector.ShowTitle(...)`

- Hanya satu folder title yang dibaca. Double-click saat load berjalan
  tidak memicu load ganda (in-flight guard per title).
- Jika folder hilang atau archive rusak, error hanya tampil pada title
  itu; index/grid lain tetap hidup.

### 8. Integrasi consumer (langkah paling berisiko — ditaruh belakangan)

Saat ini `TitlesChanged` (`shareLogic/MangaReaderEvents.cs`,
`LibraryView.xaml.cs` L342–343) mengirim seluruh `MangaTitle`, yang akan
memaksa semua chapter tetap dimuat. Kontrak ini harus diubah — dan ini
blast radius terbesar di seluruh plan.

**R-05 — Cutover kontrak di increment 5, setelah grid lazy jalan.**
Konsumen yang terdampak (terverifikasi di kode):

- `MangaReaderView.LibraryTab_TitlesChanged` (`MangaReaderView.xaml.cs` L81)
- History (`History/HistoryView.xaml.cs`) — resolve hanya title/chapter
  yang benar-benar ada pada history, lewat lazy loader berdasarkan
  `TitleFolderPath`
- Cover Builder (`CoverBuilder/CoverBuilderView.xaml.cs`) — picker memakai
  `LibraryIndexEntry`; saat bake cover baru, load title terpilih saja
- Update Checker (`Library/UpdateChecker/`) dan Reader — selalu menerima
  `MangaTitle` hasil lazy load dari title yang sedang dibuka
- Grouping/filter/sort (`Library/Grouping/`, `LibraryView` L390) — memakai
  field index (`Title`, `AddedUtc`, chapter count), tanpa perlu
  `MangaTitle.Chapters`

Aturan migrasi: payload event berubah dari daftar `MangaTitle` penuh ke
daftar `LibraryIndexEntry`; tidak ada masa dual-publish (satu pemilik
event, konsumennya terdaftar di atas — diubah serentak dalam satu
increment agar tidak ada setengah migrasi). Increment 5 tidak mulai
sebelum increment 3 hijau.

### 9. Status UI

- Startup: `Loaded 3,000 indexed titles`.
- Reconcile: `Checking library changes…`
- Title sedang dibuka: `Loading chapters…`
- Scan manual: `Updating library index…`
- Index awal kosong: `Building library index…`

### 10. Test yang diperlukan

Unit test fokus pada kontrak, bukan UI penuh:

- index root mismatch/corrupt → rebuild aman;
- index startup tidak memanggil load chapter semua title;
- satu folder berubah → hanya entry itu yang di-reindex;
- delete/rename title → entry dan cover cache dibuang;
- click satu title → hanya folder itu yang dibaca;
- cancel/failure tidak merusak index lama (index tersimpan + `_cards`
  tidak tersentuh hasil basi);
- watcher debounce tidak menggandakan reindex;
- History/Cover Builder tidak memaksa eager chapter load;
- **R-01:** reindex title lama mempertahankan `AddedUtc`;
- **R-02:** cover dari archive natural-sort pertama, stabil antar reindex;
- **R-03:** card dari entry menampilkan Title/count/cover tanpa `MangaTitle`.

## Increment eksekusi (vertikal, masing-masing hijau sebelum lanjut)

1. **Index model + store + atomic write + test** (§1–2). Berhenti di sini
   = fondasi tersimpan tanpa mengubah perilaku apa pun.
2. **Scanner split + `ReindexTitleAsync`** (§3 + R-01/R-02). `ScanAsync`
   lama masih hidup; belum dipakai siapa-siapa yang baru.
3. **Startup-from-index + lazy open + card remodel** (§5, §7, R-03).
   `ScanAsync` lama dihapus di sini. Grid jalan tanpa full scan.
4. **Watcher + coordinator + tombol Scan** (§6 + R-04).
5. **Migrasi consumer + cutover kontrak** (§8 + R-05). Paling berisiko,
   ditaruh setelah grid terbukti jalan.
6. **Cover cache end-to-end** (§4; bisa paralel setelah increment 2).

Satu increment tidak mewajibkan commit maupun full suite — yang wajib:
check perilaku increment itu hijau sebelum increment dependennya mulai.
Check gagal = diagnosis dulu, bukan menumpuk perubahan di atasnya.

## Peta file

| Area | File baru | File diubah |
|---|---|---|
| Index model/store | `Library/LibraryIndexDocument.cs`, `Library/LibraryIndexStore.cs` | — |
| Scanner | `Library/LibraryTitleLoader.cs` (`ReindexTitleAsync` + `LoadTitleAsync`) | `Library/LibraryScanner.cs` (dihapus di increment 3) |
| Coordinator | `Library/LibraryIndexCoordinator.cs` | — |
| View/startup | — | `Library/LibraryView.xaml.cs` (`LibraryView_Loaded`, `ScanLibraryAsync`, `_cards`) |
| Card model | — | `shareLogic/MangaTitleCardModel.cs` (entry-backed + slot model penuh) |
| Kontrak event | — | `shareLogic/MangaReaderEvents.cs`, `MangaReaderView.xaml.cs`, `History/`, `CoverBuilder/`, `Library/UpdateChecker/`, `Library/Grouping/` |
| Test | `tests/.../LibraryIndex*.cs` (store, reindex, lazy, watcher-debounce, R-01/R-02/R-03) | — |

Tidak ada perubahan Router/Coordinator Shell, tidak ada primitif UI baru,
tidak ada dependency baru.
