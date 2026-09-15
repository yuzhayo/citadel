# Yuzvid Phase 2–4 — task breakdown (v2, post-review)

> Baseline: 2.8.16.
> Setiap task ≤5 file. Stop di checkpoint. Blocker #0 sudah selesai.
> Guard = verifikasi **lokal** (bukan CI) sampai kamu putuskan upgrade CI.
> Nama metode/prop mengacu baseline audit — **jangan tebak**.

## BLOCKER (SEBELUM APA-APA)
- [x] **#0** Keputusan: kontrak guard **A (cuma feature baru)** vs **B (bersihin dulu)** + gigi guard **gigi-1 (lokal)** vs **gigi-2 (CI)**. Rekomendasi: A + gigi-1 dulu. Tanpa ini, #0b gak punya definisi cakupan yang jujur.
      - **TERJAWAB 2026-09-15 via `REFACTOR_PLAN.md` R1–R4 (v2.8.16): B + gigi-1.**
        Parent dilepas dari `LocalProxyServer`/`YuzvidProxyPoolAdapter`/`YuzvidRuntimeView`;
        guard `module/yuzvid/tests/test_yuzvid_architecture.py` hijau 3/3 dan build Yuzvid 0 error.

## Fase 0 — fondasi (guard, scope ditentukan oleh #0)
- [x] **T0.1** Guard `module/yuzvid/tests/test_yuzvid_architecture.py`
      - Cakupan B: parent tidak menyebut tipe internal Feature dan tidak menginstansiasi tipe Feature;
        composition root wajib melakukan satu cutover + cleanup lifetime.
      - Verify 2026-09-16: guard hijau 3/3; parser juga mendeteksi `public partial class`.
      - Tetap lokal dari `module/yuzvid/tests`; bukan gate CI.

## Fase 2 — JS bridge (fail-fast)
- [ ] **T2.1** Wire `core.WebMessageReceived` → `ScriptMessageReceived?.Invoke(e.WebMessageAsJson)`
      - File: `Features/Browser/YuzvidBrowserView.xaml.cs` (di `InitWebView2Async`, setelah Core siap)
      - Acceptance: **warning CS0067 hilang** saat build lokal.
      - Verify: `dotnet build module/yuzvid` → 0 error + 0 CS0067; smoke JS→C#: panggil `__yuzvid.post({t:1})` → string `{"t":1}` kebaca (status bar / Debug).
      - Dep: T0.1 (guard fiksif punya definisi).
- [ ] **T2.2** Upgrade payload `AddScriptToExecuteOnDocumentCreatedAsync`: `__yuzvid.post(obj)` + `__yuzvid.onCommand`
      - File: `Features/Browser/YuzvidBrowserView.xaml.cs`
      - Acceptance: smoke 2 arah: `await ExecuteScriptAsync("2+3")` → `"5"`; `__yuzvid.post(...)` → C# nangkap;
        C# memanggil `__yuzvid.onCommand({type:'ping'})` dan menerima balasan `post(...)` melalui `WebMessageReceived`.
      - Verify: smoke manual dua arah.
      - Dep: T2.1

> **CHECKPOINT:** JS↔C# hidup dua arah. Kalau belum, jangan lanjut.

## Fase 3 — ekstraksi link
- [ ] **T3.1** Freeze provenance + port `DEFAULT_VIDEO_HOSTS` → `Features/Extraction/VideoLinkExtractor`
      - Sumber pola (DI LUAR repo citadel): `C:\VSCODE\LTX-QUASAR\dood\url_extract\urllib_lib.py`.
      - Acceptance: input HTML string → `List<VideoLink>` benar + identity dedup by `origin + path`;
        setiap item mempertahankan URI lengkap terbaru, termasuk query/token.
      - Verify: cek terarah fixture host dood/voe.
      - Dep: T2.1 (kanal; punya path sumber nyata).
- [ ] **T3.2** Source A: DOM statik `ExecuteScriptAsync("document.documentElement.outerHTML")` + **JSON decode eksplisit** + batas ukuran
      - Acceptance: halaman ≤ batas → link muncul; halaman > batas → status "halaman terlalu besar, lewati" (UI responsif).
      - Verify: smoke normal + smoke halaman raksasa (status benar, freeze 0).
      - Dep: T3.1
- [ ] **T3.3** Source B: tap dinamis — perluas `OnAdBlockResourceRequested` catat URI `.mp4`/`.m3u8`/cdn video
      - File: `Features/Browser/YuzvidBrowserView.xaml.cs` (ubah handler yang **sudah ada**)
      - Risk **High**: jangan sampai nge-block video.
      - Acceptance: play video → URI cdn kedetect; ad tetap di-block; Browser hanya memberi
        `ExtractionFeature` snapshot immutable, bukan akses ke buffer internal.
      - Verify: smoke buka halaman video, cek buffer terisi + ad ke-block.
      - Dep: T2.1
- [ ] **T3.4** `LinkDrawerView` + `ExtractionFeature` (kontrak `ExtractAsync`) + slot parent + tombol "re-scan"
      - File: folder `Features/Extraction/` (baru) + slot XAML parent
      - Acceptance: Extract → drawer gabungan A+B, dedup, halaman kosong→ status "no links".
      - Verify: smoke 3 host beda + guard T0.1 tetap hijau (parent tak nyentuh internal Extraction).
      - Dep: T3.1, T3.2, T3.3

> **CHECKPOINT:** end-to-end extract→drawer jalan sebelum sentuh download.

## Fase 3.5 — rapuin parent (PERLU IZIN, nyentuh kopling)
- [ ] **T3.5** Pindah logika Extract dari `YuzvidView.xaml.cs` → parent cuma raise `ExtractRequested`
      - Acceptance: parent tipis, guard lolos, perilaku sama.
      - Dep: T3.4 + izin kamu

## Fase 4 — Queue + download
- [ ] **T4.1** `QueueEngine` (opsi 4a: HttpClient+stream, reuse `YuzvidProxyPoolAdapter`)
      - Acceptance: 1 link → file jadi.
      - Verify: smoke unduh 1 file.
      - Dep: T3.4
- [ ] **T4.2** Bind `QueueTable.ItemsSource` → `ObservableCollection<QueueItem>` + progress + retry
      - Acceptance: baris Idle→Downloading→Done; Failed → retry host lain; Cancel jalan.
      - Dep: T4.1

## E2E
- [ ] Browser buka 1 host video → Extract → drawer → pilih 1 → Queue → file jadi.
- [ ] Guard T0.1 hijau. Build `dotnet build module/yuzvid` 0 error, 0 CS0067.

## Terbuka (UNVERIFIABLE sekarang, dites pas live)
- Token/CDN kedaluwarsa → butuh re-scan / play ulang.
- Host ber-captcha → Source B baru nyala setelah interaksi.
- Multi-tab paralel belum diuji.
- Smoke visual DNS/proxy (review #6) belum tercatat → jadwalkan verifikasi live terpisah.
