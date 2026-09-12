# CITADEL-TASK-BRIEF — penjangkar tugas dokumentasi (2026-09-11)

File ini adalah jangkar pemahaman. Bila context hilang (compaction) atau session
baru dimulai, BACA FILE INI DAHULU, lalu `CITADEL-INVENTORY.md`, lalu dokumen
konsep yuz-ui yang dirujuk di bawah. Jangan memulai dari ingatan.

## Tugas (kata owner)

- Hold yuz-ui; deep dive ke citadel.
- Struktur modular citadel seharusnya mengikuti konsep kernel yuz-ui (parent
  mendengar, children mendaftar, sibling saling asing, lintas folder hanya
  parent-ke-parent), tetapi agent yang membangunnya tetap membuat saling import.
- Deliverable: **dokumentasi detail tiap flow logic** + diagram flow gaya
  yuz-ui + register pelanggaran batas + pemetaan konsep kernel.
- Cakupan: **semuanya, tanpa ada yang tertinggal** (seluruh module dan flow).
- Read-only: tidak ada perubahan kode citadel selama fase dokumentasi.

## Standar dokumentasi (meniru yuz-ui)

- Satu node = satu pekerjaan; pemilik node disebut eksplisit.
- Diagram dua wujud: ASCII (terbaca editor mana pun) + mermaid (viewer).
- Setiap tepi import pelanggaran dicatat dengan `file:baris` sebagai bukti.
- Register pelanggaran berbentuk checklist terhadap aturan kontrak yang
  dilanggar, bukan keluhan; tiap pelanggaran boleh disertai catatan "bentuk
  lurusnya nanti".
- Bila ada keadaan working tree yang belum commit, didokumentasikan sebagaimana
  adanya dengan penanda **WIP**, tidak dianggap baseline dan tidak dibuang.
  **Keadaan terverifikasi 2026-09-11:** working tree bersih kecuali `docs/`;
  CatalogMirror sudah ter-commit (`8a772b6`), jadi laporan handoff
  `REPORT-catalog-mirror-handoff-2026-09-08.md` adalah keadaan per tanggal itu,
  bukan WIP yang harus dikejar sekarang.
- Dokumen harus bisa dinavigasi lewat nama oleh owner yang tidak membaca kode.

## Sumber konsep dan kontrak

- Konsep kernel/plugin/children: `C:\VSCODE\yuz-ui\README.md` (keputusan D-*),
  `C:\VSCODE\yuz-ui\DIAGRAM-LURUS.md` (gaya peta dan flow),
  `C:\VSCODE\yuz-ui\WADAH-SCREEN.md` (arsip cara owner berpikir).
- Kontrak yang DIMAKSUD citadel: `AGENTS.md` citadel, skill
  `citadel-feature-modularity`, skill `citadel-shared-ui`, serta
  `.docs/SHARED-UI-BEHAVIOR.md` dan `.docs/PLAN-ownership-shared-ui-2026-09-05.md`.
- Plan lama di `.docs/` adalah niat sejarah; perilaku sebenarnya harus diverifikasi
  dari kode, bukan dipercaya dari plan.

## Deliverable (lokasi: `docs/` di root citadel)

1. `CITADEL-STRUCTURE.md` — peta global: module, parent, registration point,
   ringkasan graf import vs kontrak.
2. `CITADEL-FLOWS-<module>.md` — satu file per module: flow per flow, node per
   pekerjaan, pemilik, tepi import, state bersama.
3. `CITADEL-FLOWS-CROSS.md` — flow lintas module: pencarian/pemuatan module,
   layout & navigasi, composition shared UI, pyhost bridge, setting host.
4. `CITADEL-VIOLATIONS.md` — register pelanggaran ber-bukti + bentuk lurusnya.
5. `CITADEL-KERNEL-MAP.md` — pemetaan: apa yang seharusnya kernel, plugin,
   children di citadel; tanpa perubahan kode.
6. `CITADEL-INVENTORY.md` — checklist cakupan berstatus; satu-satunya penjamin
   "tanpa tertinggal". Setiap flow selesai = status diperbarui saat itu juga.
7. `CITADEL-RESTRUCTURE-PLAN.md` — **ditambah atas perintah owner 2026-09-11**:
   target struktur + plan ber-phase dengan success criteria, mengikuti pola
   `yuz-ui/IMPLEMENTATION-PLAN.md`. Berisi bentuk yang DIINGINKAN; keadaan
   sekarang tetap tinggal di dokumen flow.

## Perluasan perintah owner (2026-09-11)

Fase dokumentasi (slice 1–9) SELESAI. Owner kemudian memerintahkan: "buat plan
dan target structure nya". Maka cakupan bertambah **perencanaan**, dan aturan
read-only terhadap kode tetap berlaku:

- Plan adalah dokumen untuk dibaca dan disetujui owner; ia tidak mengeksekusi
  dirinya sendiri.
- Tiap phase di plan hanya dikerjakan bila owner memerintahkan phase itu secara
  eksplisit, satu per satu.
- Bila pelaksanaan sebuah phase dimulai, brief ini diperbarui lebih dulu dengan
  status phase tersebut.

## Aturan kerja selama fase ini

- Write-through: temuan tiap slice langsung ditulis ke dokumen sebelum lanjut,
  sehingga kehilangan context tidak pernah menghilangkan temuan.
- Tidak ada perubahan kode; tidak ada refactor; tidak ada saran implementasi di
  luar catatan "bentuk lurusnya nanti" pada register pelanggaran.
- Bila owner memberi perintah baru yang bertentangan, brief ini diperbarui
  lebih dulu, baru pekerjaan dilanjutkan.

## Protokol resume pasca-compaction

1. Baca file ini.
2. Baca `CITADEL-INVENTORY.md`; lanjutkan dari status BELUM tertua.
3. Bila detail verbatim dibutuhkan (angka, baris, urutan koreksi), baca transkrip
   session lama: `C:\Users\YUZHA\.qoder\projects\C--VSCODE\18992c4d-3293-41d5-baab-e14e6b04658b.jsonl`
   (session yuz-ui + kelahiran tugas ini) secukupnya, jangan seluruhnya.
4. Tulis temuan baru ke dokumen sebelum melangkah lagi.
