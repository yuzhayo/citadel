# PLAN REMEDIASI — akses Comix pulih, app berjalan seperti sebelumnya

Ditulis 2026-09-11 setelah regresi safety-net (commit 30dd644) dan block Cloudflare
terhadap IP owner. Batas tegas: TIDAK ada circumvention WAF (spoofing fingerprint,
rotasi proxy untuk melewati block, penyaruan UA/header). Semua langkah di bawah
bersifat protektif, pemulihan lewat jalur sah, atau verifikasi.

## P1 — Proteksi agar ini tidak terulang (kode app)

1. Deteksi halaman block/challenge Cloudflare (status 403/503 + penanda halaman
   block) di Comix source dan CatalogMirror sync: begitu terdeteksi, berhenti
   TERMINAL, tidak ada retry apa pun, dan UI menampilkan pesan eksplisit
   "sumber memblokir akses otomatis; selesaikan manual lewat toggle headed browser
   atau tunggu block luruh".
2. Hard cap volume sync per sesi (mis. maksimum N page per run, default konservatif)
   sehingga satu run tidak bisa mem-page katalog penuh tanpa jeda manusia.
3. Auto-sync default OFF setelah block terdeteksi sekali; menyalakannya kembali
   adalah tindakan sadar owner lewat toggle yang sudah ada.
4. Test: block-detection berhenti terminal (calls==1), cap volume ditegakkan,
   dan toggle headed browser tetap menjadi satu-satunya jalur challenge.
Metode tertulis per item sebelum eksekusi; verifikasi suite + citizen build.

## P2 — Pemulihan akses Comix (jalur sah)

1. Hentikan SEMUA otomatisasi ke comix.ws sekarang (sync, downloader, probe).
   App sudah berhenti pada throttle setelah 30dd644; pastikan tidak ada run
   tersisa (matikan instance staging bila masih hidup).
2. Cek status block secara manual dari browser biasa (bukan otomatisasi):
   buka comix.ws/browse. Bila terbuka, block sudah luruh dan workflow manual
   (toggle headed browser, manusia menyelesaikan challenge) bisa dipakai kembali
   persis seperti sebelum refactor.
3. Bila masih block: email owner situs sesuai petunjuk halaman block, sertakan
   Ray ID a3977668ae1a5bbf, jelaskan bahwa akses otomatis sudah dihentikan dan
   tidak akan dijalankan lagi tanpa jeda manusiawi. Template surat disiapkan
   sebagai bagian step ini.
4. Setelah akses pulih: jalankan hanya mode headed manual untuk kebutuhan
   browsing; auto-sync tetap OFF sampai P1 selesai dan owner menyalakannya sadar.

## P3 — Verifikasi app berjalan seperti sebelumnya (selain Comix online)

1. Smoke poin yang TIDAK menyentuh comix.ws: Library scan lokal, Reader buka/tutup,
   History, CBZ lokal, CamoProf, Settings + sub-screen, tray, resize.
2. Instance staging (PID 19852) tersedia untuk klik manual owner; atau build
   Release baru bila diinginkan.
3. Catat hasil per poin di dokumen ini; app dinyatakan "berjalan seperti
   sebelumnya" bila seluruh poin non-Comix lulus dan Comix manual berfungsi
   setelah block luruh.

## Yang DITOLAK (tidak akan dikerjakan)
- Spoofing fingerprint / TLS fingerprint evasion.
- Rotasi atau penggantian proxy untuk melewati block.
- Penyaruan User-Agent/header agar tampak seperti manusia.
- Teknik apa pun yang tujuan satu-satunya adalah mengelabui Cloudflare.

Alasan penolakan bukan soal menghalangi owner: teknik-teknik itu memperburuk
reputasi IP, memperpanjang block, dan dapat memperluas block ke rentang ISP;
risikonya jatuh ke owner, bukan ke agen.
