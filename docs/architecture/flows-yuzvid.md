# CITADEL-FLOWS-YUZVID — browser + extraction + queue dalam satu citizen

Ditulis 2026-09-16 dari kode aktif (baseline 2.8.33). Klaim bertanda `file:baris`.
Kontrak yang mengadili: `.agents/skills/citadel-feature-modularity/SKILL.md`
(FM-1..FM-5) dan `.agents/skills/citadel-shared-ui/SKILL.md` (SU-1..SU-5).

**Temuan utama:** yuzvid adalah citizen yang **sudah patuh modularity** — parent
(`YuzvidView`) hanya mengenal kontrak publik, tiga feature (Browser, Extraction,
Queue) + Runtime dirakit di composition root. Guard lokal
(`module/yuzvid/tests/test_yuzvid_architecture.py`, YUZ-1..YUZ-3) menjaga batas
ini dan lulus 3/3. Satu-satunya pen pen pen-penyimpangan historis (parent
memegang `LocalProxyServer`/`YuzvidProxyPoolAdapter`/`YuzvidRuntimeView`
langsung) sudah dibersihkan lewat `docs/work/yuzvid-phase23/REFACTOR_PLAN.md`
R1–R4.

Peta file (semua path relatif ke `module/yuzvid/`):

```text
YuzvidModule.cs                        ← composition root (36 baris)
YuzvidView.xaml(.cs)                   ← parent shell: tabs, toolbar, popups (503 + ~453 baris)
Features/Browser/
  IYuzvidBrowserController.cs          ← kontrak publik (BrowserState, YuzvidDnsMode, snapshot, interface)
  YuzvidBrowserController.cs           ← pemilik view + proxy + pool (170 baris)
  YuzvidBrowserView.xaml(.cs)          ← host WebView2 + guard navigasi + tap (565 baris)
  LocalProxyServer.cs                  ← proxy localhost + DoH (408 baris)
  SecureDnsResolver.cs                 ← DoH Cloudflare/Google/System (61 baris)
  YuzvidProxyPoolAdapter.cs            ← cursor pool health-aware (139 baris)
  SilentPopupHostManager.cs            ← popup/headless/floating sessions (~530 baris)
  RedirectBlockList.cs                 ← EasyList subset + baked fallback
  PopupTrace.cs                        ← trace file + Debug (R0 diagnosis)
Features/Extraction/
  VideoLinkExtractor.cs                ← port regex urllib_lib (provenance di header)
  ExtractionFeature.cs                 ← gabung Source A∪B (86 baris)
  LinkDrawerView.xaml(.cs)             ← daftar link feature-owned
Features/Queue/
  QueueEngine.cs                       ← transfer HttpClient via proxy lokal (244 baris)
  QueueView.xaml(.cs)                  ← tabel + tombol aksi per baris
Features/Runtime/
  YuzvidRuntimeView.xaml(.cs)          ← diagnostik lazy-mount (149 baris)
tests/test_yuzvid_architecture.py      ← guard YUZ-1..YUZ-3
```

---

## 1. Lifecycle modul — composition root tipis, retained view

`YuzvidModule.cs:22-35` (`CreateView`): bangun controller → `Activate()` tepat
sekali → daftarkan `Dispose` ke retained lifetime → bangun settings view + Wire
→ bangun extraction → bangun queue (+`Dispose`) → bangun queue view → serahkan
semuanya ke `new YuzvidView(...)`. Router meng-cache view (`Router.cs:56`,
`:371-386`) sehingga `CreateView` jalan sekali per route; detach/attach
memanggil `OnViewDetached/Attached` (`YuzvidView.xaml.cs:489-502`) yang hanya
menutup popup transient — WebView2 utama tetap warm.

### 1.1 Vonis per aturan FM

| Aturan | Vonis | Bukti |
|---|---|---|
| **FM-1** parent tak tahu internal feature | **PATUH** | `YuzvidView.xaml.cs` tidak menyebut satu pun tipe `internal` di `Features/` (YUZ-1 hijau); `new` feature hanya di `YuzvidModule.cs` (YUZ-2 hijau) |
| **FM-2** komunikasi lewat event/kontrak | **PATUH** | 6 event controller (`YuzvidBrowserController.cs:75-80`); drawer→parent via event `CopyRequested/QueueRequested/RescanRequested`; queue dipanggil sebagai kontrak publik |
| **FM-3** tanpa cek "apakah feature X ada" | **PATUH** | tidak ada branch feature-detection di parent; semua feature selalu dirakit di root |
| **FM-4** shared state via wadah | **PATUH longgar** | tidak ada service container; sebagai gantinya injeksi konstruktor eksplisit dari root (5 argumen `YuzvidView`). Bisa diperdebatkan, tapi dependensi eksplisit dan satu arah |
| **FM-5** logika di folder feature | **PATUH** | proxy/DNS/bridge/tap di Browser; regex/dedup di Extraction; transfer di Queue; parent hanya teruskan perintah + tampilkan status |

---

## 2. Browser init — state machine eksplisit + trace milestone

`YuzvidBrowserView.xaml.cs:215-302` (`InitWebView2Async`), dipicu sekali via
`OnLoaded` (`:189-192`) dengan deduplikasi `_initializationTask` (`:199-213`):

```text
OnLoaded → EnsureInitializedAsync → InitWebView2Async
  State=Initializing, trace "init start"
  → env (proxy localhost:port, profil WebView2ProfileV2) → trace "env-ok"
  → EnsureCoreWebView2Async → trace "core-ok"
  → simpan environment → buat popup manager → UA → filter "*" →
    WebResourceRequested → NewWindowRequested → WebMessageReceived →
    bridge script → NavigationStarting/Completed
  → State=Ready, trace "init-ready" → jalankan _pendingUrl (flag self-nav)
```

Kegagalan: `catch` → `State=Failed` + overlay error + trace `failed: msg`
(`:239-246`); core null → Failed + `failed: no-core` (`:250-257`).
`RetryInitAsync` (`:197`) memanggil ulang rantai yang sama; tombol Retry di
error overlay (`:436-438`) dan di tab Runtime memakainya.

---

## 3. Navigasi — resolver, guard cancel, event ke toolbar

### 3.1 Resolver alamat (`:380-423`, `TryNormalizeUrl`, 3 kasus)

| Input | Hasil |
|---|---|
| mengandung `://` + http/https + host | URL langsung, else error "URL tidak valid…" |
| tanpa spasi, mirip domain (ada `.`) | prepend `https://`, validasi ulang |
| teks biasa / sisanya | Google search `?q=` + `EscapeDataString` |

`Navigate` (`:127-156`): set flag `_expectSelfNav` lalu `core.Navigate`;
saat Initializing → antre `_pendingUrl` + event Queued; saat Failed → event
Failed. `GoBack/GoForward/Refresh/Stop` (`:158-180`) no-op aman saat belum Ready.

### 3.2 Guard cancel navigasi dokumen (`:304-348`)

`Browser_NavigationStarting` → `ShouldCancelDocumentNav`. Diizinkan berurutan:
non-dokumen (Reload/BackOrForward) → navigasi milik sendiri (flag, sekali
pakai) → **gesture eksplisit user (`IsUserInitiated`)** → pindah same-host.
Sisanya ke host terdaftar → `e.Cancel = true`, id dicatat
(`_cancelledNavIds`), trace `nav-cancel`, tab diam total (URL tak berubah,
tanpa halaman blank).

Trade-off tercatat di kode (`:318-328`): redirect JS dari click handler lolos
(ikut gesture kliknya); hijack timer/onload mati. Completed untuk id yang
dibatalkan dibuat sunyi (`cancelled-by-guard`, `:352-358) — tidak ada status
"Navigasi gagal" palsu.

### 3.3 Daftar redirector (`RedirectBlockList.cs`)

Bukan daftar tangan: subset EasyList (`\|\|host^` generik + `$document`,
hormati `@@` sekelasnya; rule `$script`-only dkk diabaikan agar tak
over-block) + baked fallback 2 host bukti-trace. Fetch mingguan + cache
`%LOCALAPPDATA%\Citadel\Yuzvid\easylist-hosts.txt`, offline pakai cache/baked.
Fungsi murni, fail-open.

---

## 4. Proxy lokal + DNS — tanpa restart browser

Browser selalu ke `127.0.0.1:PORT` (`:225`). `LocalProxyServer.cs`:
CONNECT (HTTPS) vs plain-HTTP (`:101-202`), upstream SOCKS5+auth RFC 1929
(`:227-309`) atau HTTP CONNECT+Basic (`:313-345`), else direct
(`ConnectDirectAsync`, `:206-225`) yang resolve via `SecureDnsResolver`
(DoH Cloudflare/Google JSON API, System = Windows DNS). Relay dua arah
(`:349-357`). Header CONNECT browser dikonsumsi dulu agar TLS tak korup.

`SetProxyEnabled` (`YuzvidBrowserController.cs:108-136`): flag adapter dulu,
OFF → upstream null, ON → acquire+pasang, gagal pool → self-heal direct +
`Notice` di snapshot. Exception tak pernah keluar ke parent.
`SetDnsMode` (`:138-148`): terjemah enum publik→internal. Toggle toolbar
(`YuzvidView.xaml.cs:167-186`) baca snapshot untuk teks status + koreksi
visual saat pool gagal. DNS picker (`:195-205`) compose teks dari enum publik.

---

## 5. Popup — konten ke tab utama, iklan ke hidden (Fase 1)

`OnNewWindowRequested` → manager, kecuali pre-init (drop sunyi agar tab tak
pernah dibajak). Gateway: host konten → navigate tab utama (dedup vs tab
yang sudah di sana / sedang ke sana — `maintab-dedup-skip` di trace);
sisanya hidden. Tidak ada floating window, tidak ada toggle popup.

| Request | Hasil |
|---|---|
| Host konten (host tab aktif + daftar video beku) | tab utama navigasi (single visible tab) |
| Selain itu (iklan) | hidden session: jalan penuh (kamuflase utuh), tutup silent |
| Manager belum siap / detach / shutdown / init gagal | handled, tanpa assignment |

- **Headless** (`PopupSession`): window tak terlihat (Opacity 0, off-screen,
  tanpa taskbar/fokus), hard timeout 20 dtk + idle grace 3 dtk
  (`DispatcherTimer`, thread UI saja), tutup silent.
- Nested request kembali ke gateway yang sama; reservasi GUID **sebelum**
  await pertama (paralel aman); cek detached/shutdown/id setelah tiap await
  (orphan di-dispose, tanpa assignment).
- Detach (`CloseTransientHosts`) → tutup semua + tolak baru; attach
  (`ResumeTransientHosts`) → terima baru, lama tetap tutup. Dispose controller:
  `view.Shutdown()` blocking dulu, baru proxy.

---

## 6. Bridge JS↔C# — dua arah, kontrak-terusan

Inject `BridgeScript` (`:446-451`): `__yuzvid.post(obj)` → `postMessage`, dan
`__yuzvid.onCommand` default echo untuk smoke ping. JS→C#: `WebMessageReceived`
(`:280-281`) → event view → teruskan controller (`:34,80`) → kontrak
(`IYuzvidBrowserController.cs:67-71`). C#→JS: `ExecuteScriptAsync`
(`:88-94`, throw bila belum Ready) → kontrak (`:81-85`). Dipakai Extraction
untuk dump DOM (T3.2). Warning CS0067 historis sudah hilang (event dipakai).

---

## 7. Ekstraksi — A∪B, B menang seri, drawer milik feature

`ExtractionFeature.ExtractAsync` (`ExtractionFeature.cs:38-85`):
1. **Source B dulu**: snapshot tap → `TryBuildFromUri` → kumpulkan (URI CDN
   bertoken menang seri dedup).
2. **Source A**: `ExecuteScriptAsync(outerHTML)` → **decode JSON eksplisit**
   → tolak bila kosong/>3MB (`MaxDomChars`) dengan status → regex
   `VideoLinkExtractor.ExtractFromHtml`.
3. Gabung dedup by `origin+path` (query diabaikan), URI lengkap disimpan.

`VideoLinkExtractor.cs`: port `urllib_lib.py` (provenance SHA di header file):
host list beku, grup server + fallback label-kedua-dari-belakang, pola
`https?://…`, buang junk akhir, slug/episode/title, `IsDirectMediaUrl`
(mp4/m3u8/webm). Tap inline Browser (`IsLikelyMediaUri`, `:498-504`) sengaja
duplikat 3 baris agar hot path tanpa coupling feature→feature (terdokumentasi).

Parent (`YuzvidView.xaml.cs:140-165`): tombol Extract → `RunExtractionAsync`
→ drawer `ShowResults` + buka popup; Copy → clipboard; Queue → enqueue +
pindah tab 1; Re-scan → ulangi. Parent tak punya logika ekstraksi (T3.5,
guard YUZ-1 hijau).

---

## 8. Queue — unduh lewat proxy yang sama dengan browsing

`QueueEngine.cs`: `Enqueue(VideoLink)` → `QueueItem` (Title/Item/Status/
Progress/Detail, `INotifyPropertyChanged`). `StartAsync`: lewati bila
Mengunduh/Selesai, 2x attempt + jeda 2 dtk, cancel via CTS
(`Antre/Mengunduh→Batal`), `ClearCompleted` (Selesai/Batal),
`Remove` (=cancel+lepas). `DownloadOnceAsync` (`:173-205`): `HttpClient` per
unduhan dengan proxy dari `controller.DownloadProxy` (framework-typed, tanpa
bocor internal), UA Chrome, stream 64KB + persen/MB, `FileMode.CreateNew`,
nama disanitasi + anti-tabrakan `(2)`. Folder
`%LOCALAPPDATA%\Citadel\Yuzvid\Downloads`. `Dispose` = cancel semua.

`QueueView`: tabel `SettingTable` kolom Title/Item/Status/Progress/Detail +
kolom aksi per baris (Start/Stop/✕ — `SelectedItem` tak ada di
`SettingTable`, maka aksi per baris) + tombol Clear completed/Start all/
Stop All/Remove failed + ringkasan hitungan. Dirakit module, dipasang parent
(pola R3 yang sama dengan settings view).

---

## 9. Tab lain + persistence + trace

- **Bookmark**: bintang ☆/★ (`\uE734`/`\uE735`) di kiri URL; klik = simpan
  (no-op bila sudah) + buka daftar; hapus per baris; JSON
  `%LOCALAPPDATA%\Citadel\Yuzvid\bookmarks.json`, korup → mulai fresh.
- **History URL**: dropdown 80-char truncate + dedup + cap 50, persist
  `url-history.json` (format sama). Tanpa ini dropdown kosong tiap restart.
- **Settings tab**: lazy-mount sekali (`WorkspaceTabs_SelectionChanged`,
  `YuzvidView.xaml.cs:293-302`); `YuzvidRuntimeView.Wire(kontrak)` —
  refresh pertama di `Loaded`, baca snapshot + `RetryInitAsync` via kontrak.
- **Gear popup**: `BrowserSettingsPopup` (`StaysOpen=True` karena ComboBox di
  dalamnya mati bila auto-close) berisi Proxy/Popup/DNS + keterangan per
  baris; persist via `UiPreference.Key`.
- **Trace** (`popup-trace.log`, maks 512KB + `.bak`, hanya host — tanpa token):
  `init start/env-ok/core-ok/ready/failed`, `nav-start id/user/host`,
  `nav-cancel`, `nav-done ok/err/cancelled-by-guard`, `popup-req
  req/user/host/kind`, `popup … accepted/initialized/navigation-complete/
  closed(reason)`, `redirect-block`, `easylist refreshed/failed`.

---

## 10. Yang belum terbukti / batas jujur

1. **Smoke visual**: build + guard + trace membuktikan alur, bukan pixels.
   Klik→drawer→queue→file, floating window terlihat, dan retensi pindah
   module butuh mata manusia (tercatat UNVERIFIABLE di TODO E2E).
2. **Link CDN bertoken kedaluwarsa** dalam hitungan menit — tombol re-scan
   adalah mitigasi, bukan solusi.
3. **HLS tersegmentasi** (`.m3u8` multi-`.ts`) hanya tersimpan sebagai file
   playlist, belum dirangkai.
4. **Quarantine proxy saat unduh gagal** belum tersambung (Queue tak menjangkau
   adapter; butuh mediasi controller bila diminta).
5. **Captcha/anti-bot host video** di luar scope mekanisme ini.
6. EasyList offline > 7 hari → baked fallback 2 host; mismatch semantik ABP
   penuh (domain=/third-party) fail-open (allow) secara sadar.
