# PLAN SISA — metode tertulis untuk B2, sisa C14, D1, D6, gap 7, smoke

**Status: ARSIP METODE.** Steps 10-13 sudah dieksekusi dan checkpoint-nya tercatat
di `CITADEL-RESTRUCTURE-PLAN.md`; dokumen ini bukan runbook aktif. Smoke visual
yang masih memerlukan operator tetap mengikuti `SMOKE-CHECKLIST.md`.

Urutan eksekusi yang diusulkan (risiko rendah dulu): gap 7 → D6 → B2 → D1 →
sisa C14 → smoke sebagai pintu keluar keseluruhan.

---

## Step 10 — gap 7: penegakan batas ikut repo

Metode:
1. Edit `.gitignore`: ganti blok `.agents/*` + negasi tunggal sehingga
   `.agents/hooks/` dan `.agents/skills/citadel-feature-modularity/` ter-track
   (pertahankan negasi yang sudah ada untuk shared-ui).
2. `git add` kedua path itu + commit. Hook `check-project-refs.mjs` (dengan
   penguatan C8) dan `gate-on-stop.mjs` kini ter-version-control.
3. Verifikasi: `git ls-files .agents` menampilkan hooks + skill modularity; jalankan
   hook dengan payload sintetis (core baru ber-referensi = block) untuk konfirmasi
   perilaku tidak berubah setelah commit.
Rollback: revert commit; .gitignore kembali semula.

## Step 11 — D6: contract tipis (Lifetime pindah ke Contract)

Analisa: `Citadel.Contract` merujuk `Citadel.Core` HANYA untuk `Lifetime`
(`core/Citadel.Core/rpl/Lifetime.cs`). `Citadel.Core` sendiri memakai `Lifetime`
(`MainQueue.Post(Lifetime, ...)`), jadi arah referensi harus DIBALIK, bukan dihapus.

Metode:
1. Pindahkan `Lifetime.cs` ke `core/Citadel.Contract/` dengan **namespace tetap
   `Citadel.Core.Rpl`** (semua `using` di seluruh repo tetap valid; nol churn sumber).
2. `Citadel.Contract.csproj`: hapus ProjectReference ke Core (menjadi daun sejati).
3. `Citadel.Core.csproj`: tambah ProjectReference ke Contract.
4. Perbarui tabel ALLOWED di `.agents/hooks/check-project-refs.mjs`:
   `Citadel.Core: ["Citadel.Contract"]`, `Citadel.Contract: []`.
5. Verifikasi: build Shell + kelima citizen + suite penuh hijau; hook sintetis:
   csproj core baru ber-referensi = block; Contract.csproj tanpa referensi = lolos;
   Contract.csproj BILA suatu saat menambah referensi = block (itu tujuan D6).
Risiko: urutan build berubah (Core kini tergantung Contract); tidak ada perubahan
perilaku karena hanya pemindahan assembly untuk satu tipe.
Rollback: revert.

## Step 12 — B2: manifest test per-file menjadi wildcard

Pelajaran dari percobaan pertama (di-revert): tiga bentuk include harus ditangani
(dua baris Link, self-close, dan tiga baris DependentUpon), dan wildcard tidak boleh
menduplikasi include eksplisit yang tersisa.

Metode (satu proyek test per commit, mulai Downloader.Tests):
1. Untuk setiap root yang di-wildcard, hapus SEMUA include eksplisit di bawah root
   itu dalam ketiga bentuk (regex DOTALL yang menelan hingga `/>` ATAU
   `<DependentUpon>...</DependentUpon></Compile>`).
2. Tambahkan SATU pasangan per root: `Compile **\*.cs` + `Page **\*.xaml` dengan
   Link `%(RecursiveDir)%(Filename)%(Extension)`.
3. Pastikan tidak ada include eksplisit lain yang masih menunjuk file di dalam root
   (grep setelah transform = nol), supaya NETSDK1022 tidak muncul.
4. Verifikasi per proyek: `dotnet test <proyek>` hijau; lalu suite penuh.
5. Ulangi untuk Reader.Tests, Library.Tests, History.Tests, Camoprof.Tests,
   Archive.Tests masing-masing satu commit.
Rollback: revert per commit.

## Step 13 — D1: kernel keluar dari module/

Metode (satu commit per pemindahan agar rollback sempit):
1. `module/Citadel.Searcher` → `core/Citadel.Searcher`: perbarui ProjectReference di
   `core/Citadel.Shell/Citadel.Shell.csproj` dan `tests/Citadel.Uia/Citadel.Uia.csproj`,
   serta path di hook ALLOWED tidak berubah (nama proyek sama).
2. `module/Citizen.targets` → `core/Citizen.targets`: perbarui `<Import>` di kelima
   csproj citizen dan properti `CitadelRoot` di dalamnya (path relatif berubah satu
   level); perbarui komentar yang menyebut lokasi.
3. `module/sharedLogic` → `core/sharedLogic`? TIDAK. sharedLogic adalah payload
   runtime citizen (bukan kernel); ia tetap di module/ tetapi dipindah ke
   `module/sharedLogic` → tetap, dan yang berubah hanya dokumentasi peran.
   (Analisa: memindahkannya memutus jalur deploy sibling `module\` yang sengaja
   dibuat agar tidak terhitung sebagai citizen; risiko > manfaat.)
4. Verifikasi tiap commit: build kelima citizen + Shell + suite penuh;
   `Build-Release.ps1` dry-check bahwa jumlah direktori citizen tetap 5
   (sharedLogic bukan citizen karena tanpa csproj).
Rollback: revert per commit.

## Step 14 — sisa C14: pisahkan komposisi dari view (katalog wiring)

Analisa: tab adalah elemen XAML bernama (LibraryTab, HistoryTab, dst.), jadi
"feature mendaftar ke katalog" tidak bisa menggantikan XAML tanpa menulis ulang
seluruh view. Yang BISA dipisah adalah *wiring komposisi* yang kini berada di
konstruktor view.

Metode:
1. File BARU `module/mangareader/MangaReaderComposition.cs`: memindahkan seluruh
   badan konstruktor view (pembuatan queue/sources/lister/autoCover/catalogMirror
   dst.) menjadi satu method `Compose(...)` yang menerima aksesors tab dan
   mengembalikan objek pemegang referensi yang dibutuhkan view/dispose.
2. `MangaReaderView.xaml.cs` konstruktor menjadi: InitializeComponent + panggil
   composition + simpan referensi + daftarkan dispose. View tinggal host XAML +
   navigasi + lifecycle (target ±120 baris).
3. Ulangi pola yang sama untuk `camoprof/CamoprofComposition.cs` (parent camoprof
   meng-hardwire 9 subsistem; setelah dipisah, parent = host + navigasi).
4. Tidak ada perubahan interface feature, delegate contract, atau perilaku.
5. Verifikasi: suite penuh hijau + guard Level-B hijau + SMOKE CHECKLIST lulus penuh
   (step ini refactor perilaku-UI, jadi smoke wajib sebagai syarat keluar).
Rollback: revert.

## Step 15 — eksekusi smoke (pintu keluar keseluruhan)

1. Jalankan `docs/operations/smoke-checklist.md` poin 1–10 pada build hasil step 10–14,
   oleh owner atau sesi run terpisah.
2. Catat hasil per poin di `CITADEL-RESTRUCTURE-PLAN.md`.
3. Restructure dinyatakan SELESAI hanya bila: suite penuh hijau, kelima citizen +
   Shell build, dan smoke 10/10 lulus.
