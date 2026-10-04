# Yuzvid — Living Wiki

> **Terakhir diupdate: 2026-10-04 14:24 (+07:00)**
> — hapus dokumen lama yang menyesatkan (`flows-yuzvid.md`, `yuzvid-overview.md`,
> `docs/work/yuzvid-phase23/`); WIKI.md kini sumber kebenaran tunggal bentuk module.
> **Setiap kali file ini diubah, WAJIB update baris timestamp di atas** (format
> `YYYY-MM-DD HH:mm (+07:00)`).

> **WAJIB DIUPDATE setiap ada perubahan pada `module/yuzvid/`.**
> Kalau kamu (agent/ manusia) mengubah code, logic, flow, atau UI di module ini,
> update section yang relevan di file ini **dalam commit yang sama**. Wiki ini
> sumber kebenaran tunggal untuk bentuk module; kalau buntu antara wiki vs kode,
> **kode menang** — lalu benerin wiki-nya.
>
> Baseline terverifikasi: **Citadel v3.1.43** (tree), `4095c05` = v3.1.40 (pushed).
> Semua klaim di sini diverifikasi ke kode/disk, bukan ingatan.
> Deep dive + verifikasi: 2026-10-04.

---

## 0. Cara pakai wiki ini (buat agent berikutnya)

1. Baca §1 (apa ini) + §3 (peta file) dulu — 2 menit, sudah dapat bentuknya.
2. Butuh alur? §4 (3 alur utama). Butuh "kenapa begini"? §5 (model ancaman — **baca sebelum menyederhanakan apa pun**).
3. Mau ubah? §7 (matriks "ubah X → update apa"). Selesai ubah → update changelog §9.
4. Data lokal / path runtime → §6.

---

## 1. Modul ini apa

Citizen Citadel: **browser video tertanam** (tonton + ekstrak link + antre unduh
video hosting ala DoodStream/PlayMogo) dengan misi tunggal:

> **Situs target harus jalan normal seolah perilaku popup/redirect-iklan tidak
> pernah ada — tanpa memicu retaliasi anti-adblock situs.**

Route `yuzvid`, order 50, icon `E714`, entry `Module.Yuzvid.dll`,
komposisi root `Module.Yuzvid.YuzvidModule`.

**Origin:** port Citadel-native dari kit ekstraksi `ltx-quasar/urllib_lib`
(playmogo + DoodStream), lalu ditumbuhkan dengan downloader engine, UI jendela
ngambang, dan grup server V1–V4.

---

## 2. Stack (terverifikasi)

| Lapisan | Fakta |
|---|---|
| Runtime/UI | .NET 10 (`net10.0-windows`), WPF, **x64** (`win-x64`; loader x86 → crash `0x8007000B`) |
| Mesin browser | WebView2 SDK `1.0.4191.47` (`Module.Yuzvid.csproj:7`), profil terisolasi `WebView2ProfileV2` |
| Pola modul | Citizen: `YuzvidModule : IModule`, build via `core/Citizen.targets`, view **retained** (`IRetainedViewModule`) |
| Kontrak internal | `IYuzvidBrowserController` — parent bicara HANYA lewat ini |
| Pool proxy bersama | `module/sharedLogic/cs/*.cs` (dikompilasi ke dalam citizen, lihat `<Compile Include="..\sharedLogic\cs\**\*.cs" />`) |
| Plugin Python | pyhost — `yuzvid_downloader` (via `<PyhostPluginName>`), plugin memuat `install(host)` → daftar command `yuzvid.*` |
| Guard arsitektur | `module/yuzvid/tests/` (unittest stdlib, 17 test) — YUZ-1..3 + engine + extractor-groups |
| Observabilitas | `%LOCALAPPDATA%\Citadel\Yuzvid\popup-trace.log` (512KB + `.bak`; host+path, **tanpa query/token**) |
| Perintah | build: `dotnet build module/yuzvid/Module.Yuzvid.csproj -c Release` · test: `cd module\yuzvid; python -m pytest tests -q` · versi: `version.props` |

---

## 3. Peta kepemilikan — satu baris per file

```text
YuzvidModule.cs            composition root: rakit controller→setting→extract→queue→view, sekali per retained view
module.json                route/icon/entry manifest
layout.json                slot Setting UI (kosong saat ini)
Module.Yuzvid.csproj       deps (WebView2), RuntimeIdentifier win-x64, PyhostPluginName, deploy WebView2Loader
YuzvidView.xaml(.cs)       shell: 3 tab (Browser/Queue/Settings), toolbar, bookmark, riwayat URL. Logika fitur: NOL.

Features/Browser/
  YuzvidBrowserView.xaml(.cs)  host WebView2: init state-machine, resolver URL 3-kasus, guard cancel nav,
                               dedup in-flight, tap media (Source B), bridge JS (__yuzvid), ad-block network
  IYuzvidBrowserController.cs  KONTRAK PUBLIK browser: BrowserState, YuzvidDnsMode, BrowserRuntimeSnapshot,
                               event Navigated/StateChanged/ScriptMessageReceived, GetVideoRequestSnapshot, DownloadProxy
  YuzvidBrowserController.cs   pemilik view+proxy+pool: Activate (pin port), toggle proxy/DNS, snapshot Runtime, Dispose
  LocalProxyServer.cs          proxy localhost (HTTP CONNECT + SOCKS5+auth): browser tak pernah restart ganti proxy
  SilentPopupHostManager.cs    session popup hidden concurrent (timeout 20 dtk, idle 3 dtk, reserve-sebelum-await)
  RedirectBlockList.cs         subset EasyList (generik+$document, hormat @@) + baked fallback 2 host bukti-trace
  SecureDnsResolver.cs         DoH Cloudflare/Google/System di sisi C# (bypass hijack DNS ISP)
  YuzvidProxyPoolAdapter.cs    baca pool proxy bersama (pola cursor + quarantine ala MangaReader)
  PopupTrace.cs                tulis Debug + file (rotate 512KB), best-effort, tak pernah throw

Features/Extraction/
  VideoLinkExtractor.cs        regex murni HTML→VideoLink (port beku urllib_lib + provenance SHA256);
                               DefaultVideoHosts, ServerGroups, VersionGroups (V1..V4), anchor-label, DeriveTitle
  ExtractionFeature.cs         entry point fitur: ExtractAsync (merge A∪B), ShowWindow (buat jendela), ResolveStored
  ExtractionWindow.xaml(.cs)   jendela ngambang (feature-owned; Title="", caption OS = satu-satunya close)
  ExtractionGroupProjection.cs tabel flat: header grup tri-state + anak (natural sort, reconcile inkremental)
  ExtractionStore.cs           persist JSON per-halaman (snake_case, best-effort, tak pernah throw)

Features/Queue/
  QueueEngine.cs               pemilik transfer: 2 jalur (direct media vs engine pyhost), retry 2x, progress/cancel
  QueueDownloaderClient.cs     jembatan C#→pyhost: ResolveAsync/StartAsync/StatusAsync/StopAsync, SetOutputDir
  QueueSettingsStore.cs        persist folder simpan (queue-settings.json), fault-tolerant
  QueueView.xaml(.cs)          tabel antre + "Simpan ke:" + per-row open-folder
  yuzvid_downloader/           plugin pyhost (deploy flat ke sharedLogic/pyhost)
    plugin.py                    daftar 4 command yuzvid.*; unduhan di worker thread; resolve via asyncio.to_thread
    resolve.py                   playmogo /d/{hash} → /download/{token}/n/{id} → poll cloudatacdn → HEAD (camoufox)
    engine.py                    engine range multi-thread + resume (port doodlib; requests only, no browser)

Features/Runtime/
  YuzvidRuntimeView.xaml(.cs)  klinik diagnosa: cek runtime/SDK/browser/proxy, retry init. Wire(browser) dari root.

tests/
  test_yuzvid_architecture.py      YUZ-1/2/3 (parent boundary)
  test_yuzvid_downloader_engine.py split_to_ranges + PartState (8 test, no network)
  test_yuzvid_extractor_groups.py  V1..V4 map + ExtractionGroupProjection contract (6 test)
```

---

## 4. Tiga alur utama

### 4.1 Lahirnya module (composition root)
```
YuzvidModule.CreateView(lifetime)
  ├─ browser = new YuzvidBrowserController()
  ├─ browser.Activate()          ← START LocalProxyServer + PIN port SEBELUM WebView2 load
  ├─ lifetime.Add(browser.Dispose)
  ├─ settings = new YuzvidRuntimeView(); settings.Wire(browser)
  ├─ extraction = new ExtractionFeature(browser)
  ├─ engine = new QueueDownloaderClient(QueueSettingsStore.ResolveDownloadFolder()); lifetime.Add
  ├─ queue = new QueueEngine(() => browser.DownloadProxy, engine); lifetime.Add
  ├─ queueView = new QueueView(queue)
  └─ return new YuzvidView(browser, settings, extraction, queue, queueView)
```
**Urutan Activate() wajib:** WebView2 membaca `--proxy-server=127.0.0.1:PORT` saat load;
kalau port belum di-pin, browser jatuh ke mode direct. Fallback: proxy gagal → `SetLocalProxyPort(0)`.

### 4.2 Alur Extract (tombol "Extract" → jendela ngambang)
```
ExtractButton_Click (YuzvidView) → RunExtractionAsync()
  └─ ExtractionFeature.ExtractAsync()
       ├─ Sumber B dulu: GetVideoRequestSnapshot() → URI CDN bertoken (MENANG saat seri dedup)
       ├─ Sumber A: ExecuteScriptAsync("document.documentElement.outerHTML")
       │     ├─ DOM kosong           → skip ("belum selesai dimuat")
       │     ├─ DOM > 3.000.000 char → skip ("terlalu besar")
       │     └─ else → VideoLinkExtractor.ExtractFromHtml(html)
       ├─ merge + dedup (DedupKey = host+path, query dibuang)
       ├─ ExtractionStore.Save(result) → %LOCALAPPDATA%\...\extractions\<slug>.json  (failure → StoreKey null)
       └─ ExtractionResult (dgn StoreKey)
  └─ ExtractionFeature.ShowWindow(owner, result)   ← WINDOW DIBUAT OLEH FEATURE, bukan parent (guard YUZ-2)
       └─ ExtractionWindow.ShowResults → ExtractionGroupProjection.Update(links)
```
Event dari jendela → feature → parent: `CopyRequested`, `QueueRequested`,
`QueueSelectionRequested`, `RescanRequested`.

### 4.3 Alur Download (2 jalur, diputus per item)
```
QueueEngine.StartAsync(item)   [retry loop: MaxAttempts = 2]
  ├─ item.IsDirectMedia (.mp4/.m3u8/.webm)?
  │    └─ YA  → DownloadDirectAsync  : HttpClient GET (64KB buffer, timeout 30 mnt) lewat proxy browser
  └─ TIDAK (player page) → DownloadViaEngineAsync:
       1. _engine.ResolveAsync(url)    → pyhost "yuzvid.resolve"  → resolve.py (camoufox)
       2. _engine.StartAsync(cdn,...)  → pyhost "yuzvid.download.start" → worker thread engine.py
       3. loop: _engine.StatusAsync tiap 500ms → done / failed / stopped (progress% + kecepatan)
```
Python `plugin.py`: command `yuzvid.resolve`, `yuzvid.download.start`,
`yuzvid.download.status`, `yuzvid.download.stop`. Protokol v1 sekuensial tanpa
event → unduhan panjang di worker thread, C# polling. Resolve browser-sync
dijalankan via `asyncio.to_thread`.

---

## 5. Model ancaman — KENAPA desainnya rumit (baca sebelum menyederhanakan)

Situs target adalah **musuh aktif yang mendeteksi pemblokir**, bukan halaman pasif.
Detektornya terbukti dari rekayasa-balik DoodStream (parameter `adb=0|1` dan `ftor`
dikirim situs ke servernya sendiri). Retaliasinya: player di-pause, tembok
"matikan adblock", loop redirect.

| Detektor situs | Mekanisme kita yang melawannya | Kalau disederhanakan → akibat |
|---|---|---|
| Cek `window.open` null (`if (!p \|\| p.closed)`) | **Setiap** `window.open` dapat core WebView2 hidup (hidden), tanpa kecuali | drop/null → terdeteksi seketika → retaliasi. **Jangan pernah kembalikan drop.** |
| Flag skrip + impression pixel | Hidden session menjalankan chain penuh (UA, filter, tap, bridge sama dengan tab utama) | block rantai/session palsu → sesi ditandai server-side |
| Bait element/DOM | Block sub-resource hanya **7 domain** terbukti-aman; sisanya mengalir | block agresif → bait gagal render → ketahuan |
| Hijack same-tab (location/form) | Cancel `NavigationStarting` hanya non-gesture ke host terdaftar; gesture + same-host selalu lolos | cancel buta / biarkan → tab dibajak atau klik normal mati |
| Hijack DNS ISP (SafeSearch paksa) | DoH di C#, bukan flag Chromium | flag/do-nothing → SafeSearch ISP lolos |
| Ganti proxy = restart browser | LocalProxyServer di tengah (browser→localhost, toggle upstream) | env recreate → sesi mati tiap ganti proxy |

**Pemisahan A/B tap:** Sumber B (`GetVideoRequestSnapshot`) menang saat seri karena
URI-nya membawa token CDN; Sumber A (regex DOM) hanya pelengkap.

---

## 6. Data & path runtime

Semua di `%LOCALAPPDATA%\Citadel\Yuzvid\`:

| File/folder | Isi | Owner |
|---|---|---|
| `bookmarks.json` | daftar bookmark | `YuzvidView` |
| `url-history.json` | riwayat URL dropdown (max 50) | `YuzvidView` |
| `popup-trace.log` (+`.bak`) | trace navigasi/popup (host+path only) | `PopupTrace` |
| `easylist-hosts.txt` | cache EasyList `\|\|host^` (refresh mingguan) | `RedirectBlockList` |
| `extractions\<slug>.json` | hasil ekstraksi per-halaman | `ExtractionStore` |
| `queue-settings.json` | folder simpan unduhan | `QueueSettingsStore` |
| `Downloads\` | hasil unduhan default | `QueueEngine` |
| `WebView2ProfileV2\` | profil WebView2 terisolasi | `YuzvidBrowserView` |

Konstanta penting: `MaxDomChars=3_000_000` · `MaxTapEntries=200` ·
`MaxDisplayUrl=80` · `MaxHistoryItems=50` · `HardPopupTimeout=20s` ·
`PopupIdleGrace=3s` · `MaxAttempts=2` (queue) · `EngineThreads=4` ·
`MinPartSize=256KB` · timeouts pyhost: Resolve=90s, Start=30s, Status=10s.

---

## 7. Matriks "ubah X → update apa"

| Kalau kamu ubah… | Update di wiki ini |
|---|---|
| Tambah/hapus file di `Features/*` | §3 peta file + baris owner |
| Ubah alur extract/download/init | §4 alur terkait |
| Ubah host video / versi grup V1..V4 | §3 (VideoLinkExtractor) + test `test_yuzvid_extractor_groups.py` |
| Ubah `BlockedDomains` / policy popup / redirect | §5 model ancaman (+ vault #2 kalau doctrine berubah) |
| Tambah file/path persist baru | §6 tabel data lokal |
| Ubah konstanta (timeout, cap, threads) | §6 daftar konstanta |
| Tambah guard/test | §3 tests + §8 open-issues |
| Rilis versi baru | §9 changelog + header baseline |

---

## 8. Open issues (KNOWN — belum fix)

| # | Isu | Lokasi | Severity | Catatan |
|---|---|---|---|---|
| 1 | **V1/V2/V4 belum bisa diunduh** — hanya V3 (playmogo) yang resolve-nya proven; grup lain masuk antre lalu gagal di resolve | `VersionGroups` + `resolve.py` | Medium | port resolve doodlib spesifik playmogo |
| 2 | **Downloader Python tanpa proxy** — `plugin.py` baca `msg["proxy"]` tapi `QueueDownloaderClient.StartAsync` tidak mengirim field `proxy` → engine unduh direct (beda dgn jalur direct-media yg pakai proxy browser) | `QueueDownloaderClient.cs` ↔ `plugin.py` | Medium | jalur engine tdk lewat LocalProxyServer |
| 3 | **BlockedDomains match substring** (`uri.Contains`) bukan host-anchored → rawan false-positive | `YuzvidBrowserView.xaml.cs:~520` | Low | pertimbangkan host-suffix match |
| 4 | **Respons blok sintetis** `200 "Blocked" text/plain` — kandidat fingerprint detektor | `YuzvidBrowserView.xaml.cs` + popup manager | Low | riset terbuka (sangetods.net) |
| 5 | **ExtractionStore tanpa prune** — JSON lama menumpuk | `ExtractionStore.cs` | Low | belum ada kebijakan retensi |
| 6 | Kosmetik: tombol "Open" bookmark pakai glyph `E72B` (ikon Back) | `YuzvidView.xaml` | Info | ganti ke ikon open yang benar |

---

## 9. Changelog (ringkas — terbaru di atas)

- **2026-10-04 14:24** — Hapus dokumen lama yang menyesatkan: `docs/architecture/flows-yuzvid.md`
  (baseline 2.8.33), `docs/architecture/yuzvid-overview.md` (2.8.38), dan `docs/work/yuzvid-phase23/`
  (PLAN/REFACTOR_PLAN/TODO). Pointer mati di `docs/operations/handoff.md` + docstring
  `tests/test_yuzvid_architecture.py` dialihkan ke WIKI.md. Lihat §11.
- **2026-10-04** — Deep dive penuh + verifikasi ulang. Natural sort tabel
  extraction + fix chrome Opsi 1 masuk `4095c05` (v3.1.40). Working tree v3.1.43.
  17/17 test hijau. Wiki ini dibuat.
- **2026-10-04** — Downloader engine jadi plugin pyhost (`yuzvid_downloader`),
  E2E terbukti (resolve CDN cloudatacdn 73.4MB, ffprobe valid). QueueSettingsStore
  + ExtractionStore + jendela ngambang ExtractionWindow + grup V1..V4.
- **2026-09-16** — Port dasar dari `urllib_lib.py` (VideoLinkExtractor).

---

## 10. Provenance (jangan ubah tanpa memperbarui)

| Aset | Sumber | SHA/versi | Tanggal port |
|---|---|---|---|
| `VideoLinkExtractor.cs` | `LTX-QUASAR/dood/url_extract/urllib_lib.py` (627 baris) | SHA256 `DA64F48D…8BFD53` | 2026-09-16 |
| `engine.py` | `LTX-QUASAR/dood/dood_downloader/doodlib.py` (bagian unduh) | penuh di header file | 2026-10-04 |
| `resolve.py` | `doodlib.extract_cdn_url` (di-rewrite ke camoufox) | penuh di header file | 2026-10-04 |
| `SilentPopupHostManager.ContentVideoHosts` | **COPY** dari `VideoLinkExtractor.DefaultVideoHosts` | — | — |

**Aturan provenance:** host/aturan yang di-frozen TIDAK boleh diubah tanpa
memperbarui blok provenance di header file yang bersangkutan.

---

## 11. Dokumen yang DIHAPUS (jangan dibuat ulang)

Dokumen berikut **sudah dihapus** karena menyesatkan (baseline jauh di belakang
kode) dan digantikan oleh WIKI ini. Jangan hidupkan kembali; kalau butuh riwayat,
lihat git history.

| Dihapus | Alasan |
|---|---|
| `docs/architecture/flows-yuzvid.md` | baseline 2.8.33; mendeskripsikan `LinkDrawerView`/`ExtractionGroups` yang sudah tidak ada |
| `docs/architecture/yuzvid-overview.md` | baseline 2.8.38; tak menyebut downloader engine / ExtractionWindow / grup V1–V4 |
| `docs/work/yuzvid-phase23/PLAN.md` | rencana refactor yang sudah selesai (masuk `4095c05`) |
| `docs/work/yuzvid-phase23/REFACTOR_PLAN.md` | idem; R1–R4 sudah diterapkan |
| `docs/work/yuzvid-phase23/TODO.md` | idem |

**Bila butuh dokumen Yuzvid:** tulis/tambahkan **di sini** (`WIKI.md`), bukan bikin
file baru di `docs/`.
