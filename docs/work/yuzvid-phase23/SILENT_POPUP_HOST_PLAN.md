# Yuzvid — new-window container routing plan

> Goal: Browser Yuzvid tetap **single visible tab**. Setiap tab/jendela baru yang dibuat
> otomatis oleh halaman dijalankan dalam WebView2 tersembunyi miliknya sendiri, lalu ditutup
> tanpa pernah mengganti URL Browser utama. Tidak ada batas jumlah session buatan aplikasi:
> empat request simultan berarti empat session tersembunyi yang independen.
>
> Scope hanya module/yuzvid/Features/Browser/ dan façade lifecycle retained yang sudah ada. Tidak mengubah
> proxy/DNS/profile, Extraction, Queue, ad-block rule, atau memberi UI/tab/history baru.

> STATUS FASE 1 (2026-09-16): floating + toggle DIPENSIUNKAN. Aturan aktif:
> window.open host konten → navigate tab utama (dedup vs navigasi searah);
> sisanya → hidden session.
> Tabel routing lama di bawah ini HISTORIS, bukan perilaku aktif.

## Routing contract (HISTORIS — lihat status di atas)

| Sumber NewWindowRequested | Container | Perilaku |
|---|---|---|
| IsUserInitiated=false dari Browser utama | Headless container | Satu WebView2 tersembunyi per request; main Browser tetap di URL lama; close silent. |
| Banyak auto request bersamaan | Headless container | Satu PopupSession per request; tanpa quota, queue, atau redirect. |
| Auto request dari container headless | Headless container baru | Session child terpisah; tidak pernah muncul di main/floating window. |
| IsUserInitiated=true dari Browser utama (misalnya right-click Open in new tab) | Floating container | Satu WebView2 visible di window mengambang; main Browser tetap di URL lama. |
| Request user-initiated dari floating container | Floating container baru | Aksi pengguna tetap visible dan tidak diarahkan ke main Browser. |
| Request non-user dari floating container | Headless container | Tidak membuat floating window tambahan. |
| Manager belum siap, detach, shutdown, atau init gagal | Handled/cancel | Tidak ada fallback Navigate(e.Uri) ke main Browser. |

IsUserInitiated menjadi satu-satunya policy gateway. Tidak ada heuristik berdasarkan URL,
domain, timing, proxy, atau isi halaman.

## Precondition / diagnosis

CoreWebView2.NewWindowRequested hanya terjadi saat halaman benar-benar meminta jendela baru.
Situs juga dapat menjalankan redirect same-tab (location, form submit, target=_top, atau navigation
frame utama); itu bukan jendela baru dan tidak punya child WebView untuk di-host/close.

R0 menambah trace sementara melalui Debug.WriteLine("[Yuzvid] ..."), tanpa log persisten atau
menyimpan URL/cookie:

| Event | Data minimum |
|---|---|
| Main NavigationStarting / NavigationCompleted | navigation id dan URI asal/tujuan ringkas |
| Main/floating/headless NewWindowRequested | request id, IsUserInitiated, host-kind |
| Session/container accepted / assigned / close | request id, session id, alasan close |

Acceptance R0: untuk satu reproduksi, trace dapat membedakan auto new-window dari same-tab navigation.
Jika tidak ada NewWindowRequested, isu tersebut dilaporkan sebagai same-tab behavior dan tidak dipalsukan
menjadi popup host. R1--R4 menangani seluruh auto new-window yang benar-benar menaikkan event tersebut.

## Ownership

~~~text
main Browser / floating child / headless child
  -> NewWindowRequested
       -> YuzvidWindowHostManager
            -> FloatingSession[id]  (aksi pengguna)
                 -> FloatingBrowserWindow + WebView2
            -> HeadlessSession[id]  (otomatis/non-user)
                 -> HiddenPopupWindow + WebView2

YuzvidView detach
  -> controller.CloseTransientHosts()
       -> manager.CloseAllTransient("detach")

YuzvidBrowserController.Dispose()
  -> view.Shutdown() -> manager.CloseAll -> LocalProxyServer.Dispose()
~~~

- Manager dimiliki YuzvidBrowserView, tidak static dan tidak keluar Browser feature.
- Manager mendapat satu CoreWebView2Environment dari Browser utama; semua child memakai
  environment/profile yang sama.
- Semua state dictionary, WPF window, WebView2 assignment, event, dan dispose berjalan pada UI Dispatcher.
- Parent hanya mengenal façade sinkron/idempotent CloseTransientHosts() dan ResumeTransientHosts();
  parent tidak mengetahui URL atau jenis session.

## R1 — policy gateway

1. Ganti entry point manager menjadi HandleNewWindowRequest(e, HostKind sourceKind).
2. Handler utama dan setiap child mengambil e.GetDeferral(), lalu pada try/finally memanggil manager
   dan selalu Complete().
3. Manager menentukan target dari e.IsUserInitiated:
   - false -> HeadlessSession;
   - true -> FloatingSession.
4. Ketika detached/shutdown/manager belum ready atau exception: set e.Handled=true; tidak pernah
   jalankan Browser.CoreWebView2.Navigate(e.Uri).
5. Hapus setiap fallback handler lama yang mengarahkan e.Uri ke main Browser dari jalur
   NewWindowRequested.

Acceptance: right-click Open in new tab tidak mengubah URL main; auto window.open juga tidak
mengubah URL main.

## R2 — per-request child environment

1. Sebelum await init, reserve ChildSession + GUID di dictionary. Tidak ada quota, queue, atau
   single-active field: empat request simultan punya empat owner terpisah.
2. Buat host berdasarkan target:
   - FloatingBrowserWindow: WPF floating window normal, title dari host/URI setelah navigation,
     ShowInTaskbar=true, close button; tidak menempel di tab utama.
   - HiddenPopupWindow: ShowInTaskbar=false, ShowActivated=false, WindowStyle=None,
     tak terlihat dan tidak mengambil fokus; Show() hanya bila diperlukan untuk init WebView2.
3. Jalankan EnsureCoreWebView2Async(sharedEnvironment). Sebelum assignment, terapkan setting dan
   document-created script yang sama seperti main Browser, lalu await. Core child belum boleh dinavigasi.
4. Setelah session masih aktif (bukan timeout/detach/dispose), assign:

~~~csharp
request.NewWindow = session.Core;
request.Handled = true;
~~~

   Kemudian pasang NavigationCompleted dan NewWindowRequested pada core child.
5. Event child kembali ke gateway dengan sourceKind; policy IsUserInitiated yang sama berlaku
   untuk seluruh tree. Dengan begitu auto child tidak bisa muncul sebagai floating/main, sedangkan
   aksi user dari floating child tetap mendapatkan floating window baru.
6. Floating window close oleh pengguna menutup/dispose hanya session tersebut. Tidak memengaruhi main
   atau child lain.

## R3 — lifecycle headless dan floating

Semua ChildSession memiliki host window, core, cancellation source, event subscription, dan state
close idempotent.

- Headless: hard timeout bernama HardPopupTimeout = 20s, idle grace bernama
  PopupIdleGrace = 3s; deadline dimulai saat reservation dan idle reset pada NavigationCompleted.
  Saat deadline/idle tercapai, close silent hanya session headless itu.
- Floating: **tidak** memakai auto-close timeout. Ia hidup sampai user menutup window atau parent
  lifecycle menutupnya.
- Setelah setiap await init, cek session masih terdaftar serta manager bukan detached/shutdown. Jika
  tidak, dispose orphan; set request handled tanpa assignment.
- CloseSession(id, reason) unsubscribe event, cancel/dispose timer dan CTS, dispose WebView/window,
  lalu hapus dictionary entry. Idempotent untuk init gagal, timeout, idle, user close, detach, dan dispose.
- CloseAllTransient("detach") menutup semua floating dan headless child lalu mark detached. Saat detached,
  request baru di-handle/cancel. ResumeTransientHosts() hanya menerima request baru, tidak menghidupkan
  session lama.
- Shutdown() close semua child sebelum controller melepas proxy/environment.

## R4 — verifikasi proporsional

1. Build module/yuzvid.
2. Right-click Open in new tab pada link: trace user -> floating accepted -> assigned; satu
   floating container terlihat, main URL tidak berubah; close container hanya menutupnya.
3. Halaman dengan satu auto window.open: trace auto -> headless accepted -> assigned -> closed;
   main URL tidak berubah, tanpa floating window/taskbar/flash.
4. Empat auto request simultan: empat headless session id berbeda; tiap session close sendiri dan
   main URL tetap.
5. Auto request dari floating container menjadi headless; user action dari floating container menjadi
   floating container baru.
6. Pindah module saat child aktif: seluruh child close; kembali ke Yuzvid mempertahankan Browser utama.
7. Tutup app saat child aktif: seluruh child close sebelum proxy dispose.

## Scope fence

- Tidak menangani same-tab redirect sebagai child window.
- Tidak membuat tab manager, popup UI list, persistent history/logging, quota, URL/domain rule,
  proxy/DNS change, automation/subprocess, atau perubahan ke Extraction/Queue.

