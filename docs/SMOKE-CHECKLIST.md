# SMOKE CHECKLIST — acceptance live wajib

**Status: wajib sebagai syarat keluar setiap step refactor berikutnya.** Test tidak
membuktikan UI; checklist ini menutup celah itu (gap 4, review 2026-09-11).
Dijalankan oleh owner atau lewat sesi run terpisah; hasilnya dicatat di
`CITADEL-RESTRUCTURE-PLAN.md` sebagai bukti keluar step.

## Pra-syarat
- `dotnet build core/Citadel.Shell/Citadel.Shell.csproj` sukses.
- Kelima citizen sukses di-build (step CI "Build citizens").

## Checklist

1. **Start.** App start tanpa error; ikon tray muncul; window terbuka pada ukuran
   preferensi dan ter-clamp ke work area.
2. **Tab MangaReader.** Library, History, Downloader, Queue, Catalog, CatalogMirror,
   dan CoverBuilder dibuka satu per satu: tanpa exception, tanpa layout rusak,
   tanpa scrollbar ganda.
3. **Library.** Refresh berjalan dan selesai; kartu judul tampil; view Grid/List
   berpindah tanpa kehilangan state.
4. **History.** Membuka reader dari Library lalu menutupnya membuat entri history
   muncul di tab History.
5. **Reader.** Terbuka dari Library dengan chapter benar; zoom/dim/drawer berfungsi;
   ditutup bersih (tidak meninggalkan window yatim).
6. **CatalogMirror.** Start sync lalu Stop: state kembali resumable tanpa crash.
   Ini jalur yang dulu mati karena BUG-1 — wajib diamati bahwa Stop saat backoff
   membangunkan sync segera, bukan menunggu backoff habis.
7. **Catalog → Queue handoff.** Memilih chapter dan enqueue memindahkan ke tab Queue
   dengan job tercatat; folder target mengikuti aturan konfirmasi bila folder sudah
   ada (jalur `ConfirmQueueTarget`).
8. **Settings.** Settings dan keempat sub-screen (Appearance, Module layout,
   Gallery, Sidebar groups) terbuka; Gallery merender preset; perubahan appearance
   terlihat live.
9. **Tray-resident.** Close window = hide (app tetap di tray); open dari tray =
   restore; Exit dari tray = benar-benar keluar (proses berakhir).
10. **Resize.** Window di-resize ke ukuran minimum dan maksimum: tidak ada kontrol
    terpotong atau tertumpuk.

## Cara mencatat hasil
Tulis di `CITADEL-RESTRUCTURE-PLAN.md` bawah step terkait: tanggal, pelaksana,
poin 1–10 lulus/gagal, dan bukti (screenshot atau catatan perilaku). Step refactor
tidak dinyatakan selesai sebelum checklist ini lulus penuh.
