# PLAN GAP — metode tertulis untuk gap 1, 2, 4

**Status: ARSIP METODE.** Steps 7-9 sudah dilanjutkan oleh checkpoint yang tercatat
di `CITADEL-RESTRUCTURE-PLAN.md`. Checklist aktif ada di `SMOKE-CHECKLIST.md`;
khusus throttle/challenge, aturan terkini adalah satu percobaan lalu berhenti,
tanpa retry otomatis.

Baseline terukur (scan 2026-09-11):
- camoprof cross-feature: SATU tepi — `ProfileActions -> AddProfile`
  (`Features/ProfileActions/ProfileActionsFeature.cs`), dan ia memakai **kontrak**
  (`AddProfileRequest`), bukan internal. Ini allow-list awal.
- camoprof shareLogic -> feature: TIDAK ADA.

## Step 7 — guard Level-B untuk camoprof

Metode:
1. File BARU `tests/Module.Camoprof.Tests/CamoprofLevelBGuardTests.cs`, meniru
   `LevelBGuardTests` mangareader (walk-up ke root via `Citadel.slnx`, scan
   `module/camoprof/**/*.cs`, skip bin/obj).
2. Assertion:
   a. tepi `Features/A -> Features/B` di luar allow-list
      `{ProfileActions->AddProfile}` = gagal;
   b. `shareLogic/ -> Features/*` = gagal;
   c. tipe publik bernama sama di dua folder `Features/*` = gagal (baseline saat
      eksekusi discan dulu; bila ada yang sah, masuk allow-list dengan alasan
      tertulis, bukan diam-diam).
3. Assertion 3 mangareader (parent hanya kontrak) TIDAK dipaksakan di camoprof
   karena parent camoprof masih meng-hardwire 9 subsistem (sisa C14); dicatat sebagai
   debt, sama seperti mangareader.
4. Tidak ada perubahan csproj: guard membaca sumber dari disk, dan file test baru
   otomatis ter-include oleh SDK di proyek testnya.
Verifikasi: `dotnet test tests/Module.Camoprof.Tests` hijau (56 + 3 baru); suite
penuh hijau. Rollback: revert satu commit.

## Step 8 — C6/C8 (lubang gate)

Metode C6 (citizen tidak dikompilasi CI):
1. Baca `.github/workflows/ci.yml` dulu; tambahkan SATU step setelah test:
   `dotnet build` untuk kelima csproj citizen
   (ftf, proxy, blank, camoprof, mangareader) dengan `-v q --nologo`.
2. Tidak mengubah slnx (citizen sengaja di luar slnx).
Verifikasi: jalankan perintah yang sama lokal (sudah OK untuk kelima citizen);
step CI gagal bila ada citizen rusak. Rollback: revert.

Metode C8 (proyek core/setting baru tidak dijaga hook):
1. Di `.agents/hooks/check-project-refs.mjs`, ganti perilaku
   `if (!allowed) return;` menjadi: bila path di bawah `core/` atau `setting/` dan
   nama proyek TIDAK ada di tabel ALLOWED, perlakukan `allowed = []` (setiap
   ProjectReference = block) alih-alih skip. Aturan `module/` dan pengecualian
   `tests/` tidak berubah.
2. Verifikasi TANPA mengubah repo: jalankan script hook dengan payload stdin
   sintetis (JSON tool_input.file_path menunjuk csproj sementara di core/) dan
   konfirmasi keluaran `decision: block` untuk referensi tak diizinkan, serta
   lolos untuk graf yang sah. Tidak ada file repo yang disentuh saat verifikasi.
Rollback: revert.

## Step 9 — acceptance live (gap 4)

Checklist wajib sebagai syarat keluar setiap step refactor berikutnya
(disimpan di `docs/SMOKE-CHECKLIST.md`):
1. App start tanpa error; tray muncul; window terbuka.
2. Tiap tab MangaReader dibuka: Library, History, Downloader, Queue, Catalog,
   CatalogMirror, CoverBuilder — tanpa exception dan tanpa layout rusak.
3. Library refresh berjalan dan selesai; History mencatat setelah reader dibuka.
4. Reader dibuka dari Library dan ditutup bersih.
5. CatalogMirror: Start sync lalu Stop — state kembali resumable tanpa crash
   (ini jalur yang dulu mati karena BUG-1).
6. Settings + keempat sub-screen terbuka; Gallery merender preset.
7. Close window = hide (tray-resident); open dari tray = restore; Exit = benar-benar
   keluar.
Eksekusi checklist oleh owner atau lewat sesi run terpisah; hasilnya dicatat di
plan sebagai bukti keluar step.

## Yang TIDAK dijadwalkan di dokumen ini
- Gap 3 (B2): tertunda sadar; butuh metode khusus bentuk DependentUpon.
- Gap 5 (sisa C14, katalog/gate seragam): refactor interface besar; perlu analisa
  tersendiri sebelum metode.
- Gap 6 (D1 kernel keluar module/, D6 contract tipis): keputusan owner.
