# Yuzvid — panduan agen baru (stack + kenapa desainnya rumit)

> Baca ini dulu sebelum menyentuh `module/yuzvid/`. Detail tiap alur ada di
> `flows-yuzvid.md` (dokumen ini peta + alasannya, sana kompasnya).
> Baseline: 2.8.38. Semua klaim di sini diverifikasi ke kode/disk, bukan ingatan.

## 1. Modul ini apa

Citizen Citadel: browser video tertanam (tonton + ekstrak link + antre unduh
video hosting semacam DoodStream/PlayMogo) dengan misi tunggal: **situs berjalan
normal seolah perilaku popup/redirect-iklan tidak pernah ada** — tanpa memicu
retaliasi anti-adblock situs.

## 2. Stack yang dipakai

| Lapisan | Fakta (terverifikasi) |
|---|---|
| Runtime/UI | .NET 10 (`net10.0-windows`), WPF, x64 (`win-x64`; loader x86 crash `0x8007000B`) |
| Mesin browser | WebView2 SDK `1.0.4191.47` (`Module.Yuzvid.csproj:7`), profil terisolasi `WebView2ProfileV2` |
| Pola modul | Citizen Citadel: `YuzvidModule : IModule`, route `"yuzvid"`, build via `core/Citizen.targets`, view retained (selamat dari navigasi Shell) |
| Kontrak internal | `IYuzvidBrowserController` — parent bicara HANYA lewat ini |
| Pool proxy bersama | `module/sharedLogic/cs/*.cs` (dikompilasi ke dalam citizen): `proxy.txt` + health + origins, pola cursor + quarantine ala MangaReader |
| Guard arsitektur | `module/yuzvid/tests/test_yuzvid_architecture.py` (YUZ-1..YUZ-3, unittest stdlib) — parent dilarang impor/instansiasi tipe internal `Features/` |
| Observabilitas | `%LOCALAPPDATA%\Citadel\Yuzvid\popup-trace.log` (512KB + `.bak`; host+path, **tanpa query/token**) |
| Data lokal | `bookmarks.json`, `url-history.json`, `easylist-hosts.txt`, folder `Downloads/` — semua di `%LOCALAPPDATA%\Citadel\Yuzvid\` |
| Perintah | build modul: `dotnet build module/yuzvid/Module.Yuzvid.csproj -c Release`; guard: `python -m unittest module.yuzvid.tests.test_yuzvid_architecture`; versi: `version.props` (`CitadelVersion`, naik tiap rilis) |

## 3. Peta kepemilikan (satu baris per file)

```text
YuzvidModule.cs            composition root: rakit controller→setting→extract→queue→view, sekali per retained view
YuzvidView.xaml(.cs)       shell: tabs, toolbar, popup drawer/bookmark/settings, history+bookmark persist. Logika fitur: NOL.
Features/Browser/
  YuzvidBrowserView        host WebView2: init state-machine, resolver URL 3-kasus, guard cancel, tap media, bridge JS
  YuzvidBrowserController  pemilik view+proxy+pool: toggle, snapshot Runtime, teardown berurutan
  LocalProxyServer         proxy localhost (HTTP CONNECT + SOCKS5+auth): browser tak pernah restart ganti proxy
  SecureDnsResolver        DoH Cloudflare/Google/System di sisi C# (bypass hijack DNS ISP)
  SilentPopupHostManager   session popup hidden concurrent (timeout 20 dtk, idle 3 dtk, reserve-sebelum-await)
  RedirectBlockList        subset EasyList (generik+$document, hormat @@) + baked fallback 2 host bukti-trace, refresh mingguan
  PopupTrace               tulis Debug + file, best-effort, tak pernah throw
Features/Extraction/      regex host video (port beku urllib_lib + provenance SHA) + gabung DOM∪tap + drawer
Features/Queue/           unduh HttpClient via proxy yang SAMA dengan browsing + retry 2x + progress/cancel
Features/Runtime/         diagnostik lazy-mount (runtime/SDK/browser/proxy), retry init
```

## 4. Kenapa desainnya rumit — model ancaman (baca ini sebelum menyederhanakan apa pun)

Situs target adalah **musuh aktif yang mendeteksi pemblokir**, bukan halaman pasif.
Detektornya (terbukti dari rekayasa balik DoodStream: parameter `adb=0|1` dan
`ftor` dikirim situs ke servernya sendiri) dan retaliasinya (player di-pause,
tembok "matikan adblock", loop redirect):

| Detektor situs | Mekanisme kita yang melawannya | Kalau disederhanakan jadi… | Akibat |
|---|---|---|---|
| Cek `window.open` null (`if (!p \|\| p.closed)`) | **Setiap** `window.open` dapat core WebView2 hidup (hidden), tanpa kecuali di operasi normal | drop/null | Terdeteksi seketika → retaliasi. **Jangan pernah kembalikan drop.** |
| Flag skrip + impression pixel | Hidden session menjalankan chain penuh (UA, filter, tap, bridge sama dengan tab utama); pixel tercatat | block rantai / session palsu | Sesi ditandai server-side |
| Bait element/DOM | Block sub-resource hanya 7 domain terbukti-aman; sisanya mengalir | block agresif | Bait gagal render → ketahuan |
| Hijack same-tab (location/form) | Cancel di `NavigationStarting` hanya untuk non-gesture ke host terdaftar; gesture + same-host selalu lolos | cancel buta / biarkan | Tab dibajak, atau klik normal mati |
| Hijack DNS ISP (SafeSearch paksa) | DoH di C#, bukan flag Chromium | flag/do-nothing | SafeSearch ISP lolos |
| Ganti proxy = restart browser | LocalProxyServer di tengah (browser→localhost, toggle upstream) | env recreate | Sesi mati tiap ganti proxy |

**Invarian yang tidak boleh dilanggar perubahan apa pun:** dalam operasi normal,
`window.open` tidak pernah return null; tab utama hanya pindah oleh navigasi
pengguna (address bar/bookmark/back/forward/default halaman sendiri).

## 5. Trade-off yang dicatat jujur (jangan "diperbaiki" tanpa baca ini)

1. **Klik-ke-iklan ikut gesture → lolos guard.** Hanya timer/onload yang mati.
   Harga sadar: satu Back. Alternatif (daftar gesture-vs-iklan) butuh klasifikasi
   yang tidak ada sinyalnya — pernah dicoba via blocklist ganancias, hasilnya
   halaman normal ikut mati. Jangan ulangi.
2. **Duplikat navigasi URL-identik dibatalkan yang datang belakangan**
   (`dup-cancel`). Aman karena tujuannya sama; reload/back/forward dikecualikan
   via `NavigationKind`.
3. **`.m3u8` tersegmentasi** hanya tersimpan sebagai playlist, belum dirangkai.
4. **Quarantine proxy saat unduh gagal** belum tersambung (Queue tak menjangkau
   adapter; butuh mediasi controller).
5. **Mismatch semantik ABP penuh** (`domain=`/`third-party`) fail-open (allow)
   secara sadar.
6. **Captcha/anti-bot host video** di luar scope mekanisme ini.

## 6. Cara kerja yang aman di sini

1. Baca `flows-yuzvid.md` § yang relevan + kode yang dikutipnya (format `file:baris`).
2. Perubahan perilaku → buktikan dulu dari `popup-trace.log` (`nav-start` ada
   `user=` + `path`; `popup-req` ada `kind=`; `nav-cancel`/`dup-cancel`/`redirect-block`
   menjelaskan tiap kematian navigasi).
3. Jalankan guard + build modul tiap perubahan. Klaim tanpa bukti = belum selesai.
4. Jangan tambah daftar tangan untuk hal yang ada sinyal mesinnya (gesture,
   host tujuan, kind request). Daftar hanya dari bukti trace.
