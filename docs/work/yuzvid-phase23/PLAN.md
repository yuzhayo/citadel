# Yuzvid Phase 2 & 3 — implementasi plan (puzzle-block)

> Status: **Phase 0 selesai; Phase 2–4 belum dimulai.** Baseline aktif: **2.8.16**.
> Revisi v2 setelah review — enam temuan diverifikasi ke disk, semuanya valid.
> Prinsip: fitur = adapter tipis yang merangkai blok yang sudah ada. Menara dilarang.

---

## 0. Realita CI & batas yang WAJIB dibaca dulu (revisi v2)

Aku cek `​.github/workflows/ci.yml` langsung. Yang beneran terjadi:

- CI cuma dua langkah: `dotnet test Citadel.slnx` + loop `dotnet build module/<warga>`.
- Daftar warga yang di-build: **ftf, proxy, blank, camoprof, mangareader**. → **yuzvid TIDAK ADA.**
- `Citadel.slnx` juga **tidak memuat yuzvid** (dicek: `yuzvid` tidak ketemu).
- CI **tidak menjalankan pytest / python test sama sekali**. Guard python `sharedLogic/tests/*`
  (punya camoprof) pun **bukan** gate CI — itu disiplin lokal/manual.

**Konsekuensi jujur (ini membatalkan framing plan v1):**
1. Klaim v1 "guard bikin build gagal" **SALAH**. Python guard tak menyentuh CI.
2. Lebih parah: **yuzvid saat ini tak pernah di-compile CI.** Bug kompilasi yuzvid bisa
   merge diam-diam. Ini gap terpisah dari Phase 2/3, tapi ngaruh ke cara kita bikin "teeth".

Jadi "teeth" buat guard bukan ngarang — harus **dibeli** lewat salah satu opsi di §1.

### Koreksi asumsi teknis lama (tetap berlaku dari v1)
| asumsi v1 | realita (di disk) |
|---|---|
| DoH via flag Chromium | **SALAH** — DoH manual di `SecureDnsResolver` jalan di `LocalProxyServer.ConnectDirectAsync`; browser set `--proxy-server` saja |
| `url_extractor.py` = sniffer | **SALAH** — page-fetch + regex host video, unwrap shortlink, ikut chain dood, pagination |
| DNS/proxy "selesai & solid" | **DITURUNKAN** → *"implemented, live verification pending"*. Browser **selalu** lewat local proxy; belum ada smoke visual tercatat |

---

## 1. Blocker #1 & #2 — guard & kontrak fitur (**SELESAI** sebelum Phase 2)

> **Keputusan/hasil, 2026-09-15:** **B + gigi-1**. Ownership Browser/proxy/runtime sudah
> dipindahkan dari parent ke composition root + `IYuzvidBrowserController`; guard lokal ada di
> `module/yuzvid/tests/test_yuzvid_architecture.py` dan lulus 3/3. Build `module/yuzvid` lulus
> 0 error. Guard tetap check lokal, bukan gate CI.

### 1a. Kontrak guard vs baseline — konflik nyata (review #2)
`citadel-feature-modularity` melarang parent pegang **tipe internal** feature. Tapi baseline
`YuzvidView.xaml.cs` sebelumnya langsung `new LocalProxyServer()`, `new YuzvidProxyPoolAdapter()`,
`new YuzvidRuntimeView()`. Ini adalah audit historis; kondisi tersebut sudah dihapus lewat opsi B.
Pilihan berikut dipertahankan sebagai konteks keputusan, bukan pekerjaan tersisa:

- **A (tidak dipilih):** guard **cuma mengawal feature BARU** (`Extraction/`).
  Usage konkret Browser yang sudah ada di-`grandfather` lewat daftar pengecualian yang
  di-dokumentasiin, bukan whitelist kosong. Guard tetap punya arti: mencegah **pelanggaran
  baru**, sambil jujur bilang utang lama belum dibayar.
- **B (dipilih dan selesai):** ekstrak kontrak `IYuzvidBrowserController` buat Browser,
  parent ngomong ke interface — **baru** pasang guard penuh. Mahal, nyentuh banyak file,
  risiko regresi. Kalau kamu mau ini, itu proyek sendiri sebelum Phase 2.

### 1b. Gimana guard punya "gigi" (review #1)
Python guard tak ke-CI. Supaya klaim "build gagal" jujur, pilih salah satu:

- **Opsi gigi-1 (dipilih):** taruh guard sebagai **check lokal wajib** yang
  aku jalankan tiap task, bukan klaim CI. Di plan ditulis jelas: *"guard = verifikasi lokal,
  bukan gate CI."* Nggak bohong, nggak butuh ubah CI.
- **Opsi gigi-2 (bikin CI nyata):** tambah `dotnet build module/yuzvid/...` ke loop "Build
  citizens" + satu step `pytest module/yuzvid/tests` di ci.yml. **Ini ngubah infra CI repo** →
  butuh izin kamu eksplisit (di luar scope Phase 2/3). Bonusnya: nutup gap "yuzvid tak ke-build".

> Tidak ada keputusan terbuka di §1. Upgrade ke gigi-2 tetap pilihan infra CI terpisah.

---

## 2. Provenance sumber regex (review #3 — path ditetapkan)

Yang aku port **bukan** di repo citadel. Lokasi sebenarnya:
```
C:\VSCODE\LTX-QUASAR\dood\url_extract\urllib_lib.py   (DEFAULT_VIDEO_HOSTS + regex)
C:\VSCODE\LTX-QUASAR\dood\url_extract\url_extractor.py (CLI tipis di atasnya)
```
Aturan main biar nggak ngulang kesalahan v1:
- Citadel **tidak boleh** punya dependensi runtime ke `LTX-QUASAR` (proyek lain, citizen lain
  belum tentu ada). Jadi **pattern-nya DISALIN ke dalam C#**, bukan diimpor/dipanggil.
- Sebelum port, **freeze sumbernya**: catat sha256/isi `DEFAULT_VIDEO_HOSTS` yang dipakai
  (biar nanti bisa dibuktikan daftar host apa yang masuk). Aku ambil daftarnya sekarang kalau
  kamu OK — kalau host listnya mau kamu kurasi sendiri, itu keputusan kamu, aku tinggalin TODO.
- Yang di-"proven" cuma **daftar host + pola dedup by path**. Logika `ouo.io` unwrap &
  pagination **tidak** ikut Phase 3 (butuh navigasi multi-halaman → masuk scope sendiri nanti).

---

## 3. Kepemilikan fitur (parent = router bodoh)

| kemampuan | owner folder | catatan |
|---|---|---|
| JS bridge | `Features/Browser/` | sudah di `YuzvidBrowserView` |
| Ekstraksi link + hasil | **`Features/Extraction/` (BARU)** | mandiri, gak nyampur Browser |
| Link drawer | view di `Extraction/`, parent cuma sedia slot | shared-ui: fitur punya composition sendiri |
| Queue + download | **`Features/Queue/`** (upgrade placeholder) | Fase 4, gak dicampur Phase 2/3 |

Tombol Extract sekarang di toolbar parent (`YuzvidView.xaml.cs`). Ini bau Rule-4.
Fase 3.5 mindahin → parent cuma raise `ExtractRequested`. **Nyentuh kopling parent → perlu izin.**

---

## 4. Peta blok

**Dipakai ulang, TIDAK diubah:**
- `YuzvidBrowserView`: state machine, `EnsureInitializedAsync`/`InitWebView2Async`, error overlay
- `cv2.AddScriptToExecuteOnDocumentCreatedAsync` — sudah jalan, tinggal diisi payload
- `AddWebResourceRequestedFilter("*", All)` + `WebResourceRequested` + `OnAdBlockResourceRequested`
  → handler ini **sudah baca `e.Request.Uri`**, tinggal dilebarkan jadi tap (lihat §6)
- `LocalProxyServer`, `SecureDnsResolver` — implemented (verifikasi live pending)
- `YuzvidProxyPoolAdapter`, `setting:` controls

**DITULIS BARU:**
1. `Browser` — handler `WebMessageReceived` (kecil)
2. `Extraction/VideoLinkExtractor.cs` — regex + dedup (inti Phase 3)
3. `Extraction/ExtractionFeature.cs` — kontrak tipis
4. `Extraction/LinkDrawerView.xaml(.cs)` — UI daftar link
5. `Queue/QueueEngine.cs` + view — download (Fase 4)
6. `tests/test_yuzvid_architecture.py` — guard (**selesai**, check lokal per §1a)

---

## 5. Phase 2 — JS bridge (target: bukti JS↔C# 2 arah jalan)

```text
page JS ─postMessage({type})→ cv2.WebMessageReceived (HARUS di-wire)
                                    → raise ScriptMessageReceived(json)
C#      ─ExecuteScriptAsync("...")→ page JS → balas nilai (Task<string>)
```

**Kerjaan:**
- Di `InitWebView2Async`, setelah `Core` siap:
  `core.WebMessageReceived += (s,e) => ScriptMessageReceived?.Invoke(this, e.WebMessageAsJson);`
  → **menghapus warning CS0067** (event akhirnya kepakai).
- Upgrade payload `AddScriptToExecuteOnDocumentCreatedAsync`: tambah
  `__yuzvid.post(obj)` (bungkus `chrome.webview.postMessage(JSON.stringify(obj))`) +
  `__yuzvid.onCommand = fn` buat dipanggil C#.
- **Belum** inject DOM floating button — Fase 2 = buktiin kanal. Button = Fase 2b opsional.

**Acceptance:**
- [ ] `dotnet build module/yuzvid` → **CS0067 hilang**.
- [ ] Smoke JS→C#: `__yuzvid.post({hello:1})` → string `{"hello":1}` kebaca di status bar/Debug.
- [ ] Smoke C#→JS dasar: `await ExecuteScriptAsync("2+3")` → `"5"`.
- [ ] Smoke command bridge: C# memanggil `__yuzvid.onCommand({type:'ping'})`; handler page
  membalas lewat `__yuzvid.post(...)`, lalu balasan terlihat melalui `WebMessageReceived`.
  Ini membuktikan kontrak command yang dideklarasikan, bukan hanya eksekusi JavaScript umum.

**Verifikasi:** build lokal + smoke manual (tak ada harness WebView2; jujur: manual).

---

## 6. Phase 3 — ekstraksi link + drawer (target: link muncul, kebaca)

Dua sumber link, dipakai **dua-duanya** (saling menutupi), karena `url_extractor` = page-fetch+regex:

**Sumber A — statik (padanan url_extractor):**
- `await ExecuteScriptAsync("document.documentElement.outerHTML")` **mengembalikan JSON string**
  (bukan HTML mentah) → **WAJIB decode eksplisit** (`JsonSerializer.Deserialize<string>` dulu)
  baru di-regex. **Jangan** treat hasilnya sebagai teks polos. (review #5)
- **Batas ukuran:** sebelum regex, cek panjang string. Kalau > ambang (mis. 2–4 MB) → **status
  "halaman terlalu besar, lewati scan statik"**, jangan regex string raksasa (bikin UI freeze).
- Pola host di-port dari `DEFAULT_VIDEO_HOSTS` §2 (disalin ke C#, bukan diimpor).

**Sumber B — dinamis (tap di filter yang sudah ada):**
- Perluas `OnAdBlockResourceRequested`: **selain block ad, catat** URI video
  (`.mp4`, `.m3u8`, host cdn dood). Ini yang **bisa langsung di-download** (URL CDN bertoken).
- Buffer dibatasi (mis. N entri terakhir) — bukan list tak tumbuh.
- Browser mengekspos buffer ini hanya sebagai snapshot publik yang immutable (mis.
  `IVideoRequestSnapshot` / `GetVideoRequestsSnapshot()`); `ExtractionFeature` tidak boleh
  membaca field internal Browser atau mengubah buffer.

**Gabung:** A ∪ B → identity dedup = `origin + path` (query diabaikan), tetapi setiap
`VideoLink` **tetap menyimpan URI lengkap terbaru**, termasuk query/token CDN. Dengan begitu
baris duplikat menyatu tanpa membuang URL yang masih bisa diunduh.

```text
klik Extract → ExtractionFeature.ExtractAsync(currentUrl, core)
   ├─ A: dump DOM → decode → (cek batas) → regex hosts
   ├─ B: baca buffer tap WebResourceRequested
   └─ List<VideoLink> → parent buka LinkDrawerView → centang → copy (F3) / Queue (F4)
```

Kenapa bersih: tanpa subprocess Python, tanpa Playwright (WebView2 udah renderer), regex port sekali.

**Acceptance:**
- [ ] 1 halaman dood/playmogo → Extract → drawer tampilkan ≥1 `.mp4`/`.m3u8`.
- [ ] Source B: URL cdn sama kedetect 2x → dedup (1 baris).
- [ ] Halaman tanpa video → drawer kosong, crash 0, status "no links".
- [ ] Halaman raksasa → status "terlalu besar", UI tetap responsif (regresi freeze 0).
- [ ] Tak ada tipe internal `Extraction/` direference dari parent (guard §1a cakupan baru lolos).

**Risiko (jujur):** host yang baru ngasih cdn URL **setelah** klik play/captcha → Source B
kosong sebelum interaksi → **UNVERIFIABLE tanpa interaksi user**. Drawer perlu tombol **"re-scan"**.

---

## 7. Phase 4 — Queue + download (TERPISAH, gak dicampur Phase 2/3)

Blok download yuzvid **belum ada**. Opsi:
- **4a (rekomendasi):** `QueueEngine` = `HttpClient` + stream ke file, upstream **reuse
  `YuzvidProxyPoolAdapter`** (pola MangaReader). Tanpa dep Python.
- **4b:** shell ke `dood/dood_downloader`. Cepet tapi nambah coupling runtime luar.

`QueueTable` kolom Title/Item/Status/Progress/Detail **sudah ada** di XAML → tinggal bind
`ObservableCollection<QueueItem>`, UI gak dirombak.

Acceptance: [ ] 1 link → file jadi, progress Idle→Downloading→Done. [ ] Gagal proxy → retry host
lain (reuse quarantine). [ ] Cancel jalan.

---

## 8. Urutan (bottom-up, high-risk duluan)

| # | Task | Owner | Risk | Cek | Gate? |
|---|---|---|---|---|---|
| 0 | Putuskan kontrak (**A/B**) + gigi guard (**gigi-1/2**) — **keputusan kamu** | — | — | keputusan tercatat | **BLOCKER** |
| 0b | Guard `test_yuzvid_architecture.py` sesuai cakupan §1a | tests | Low | sengaja langgar→merah | pre-req |
| 1 | Wire `WebMessageReceived`→event + payload `post/onCommand` | Browser | Med (fail-fast) | CS0067 hilang + smoke 2 arah | — |
| 2 | Freeze daftar host + `VideoLinkExtractor` (regex+dedup, **decode JSON+bate size**) | Extraction | Med | cek terarah fixture | — |
| 3 | Tap dinamis (Source B) di `OnAdBlockResourceRequested` | Browser↔Extraction | **High** | cdn .mp4 kebaca, ad tetap block | — |
| 4 | `LinkDrawerView` + `ExtractionFeature.ExtractAsync` + slot parent | Extraction+parent | Low | drawer keisi, halaman kosong aman | — |
| 5 | Pindah logika Extract keluar parent (raise event) — **PERLU IZIN** | parent | Med | parent tipis, guard lolos | — |
| 6 | `QueueEngine` (4a) + bind `QueueTable` | Queue | High | file jadi, retry | Fase 4 |

Checkpoint: **setelah #1** kanal terbukti (fondasi drawer & re-scan). **setelah #4**
extract→drawer end-to-end sebelum sentuh download.

---

## 9. Terbuka / UNVERIFIABLE (dites saat live)
- Link cdn bertoken bisa kedaluwarsa menit → perlu "re-scan" / play ulang sebelum unduh.
- Host ber-captcha → Source B nyala setelah interaksi.
- Multi-tab paralel: WebView2 per-tab environment terpisah, belum diuji stabil barengan.
- **Verifikasi live DNS/proxy** (review #6): status "implemented" saat ini; smoke visual
  (cek IP keluar lewat proxy, cek DoH resolve) belum tercatat → jadwalkan sebelum klaim "solid".

## 10. Yang TIDAK diubah scope ini
- Flag Chromium DNS (memang tidak dipakai; DoH di C# — sudah benar).
- Daftar `DEFAULT_VIDEO_HOSTS` dikurangi/dikurasi — **keputusan kamu** kalau mau diedit.
- `ouo.io` unwrap & pagination — di luar Phase 3 (navigasi multi-halaman = scope sendiri).
