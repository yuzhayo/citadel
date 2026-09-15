# Yuzvid ownership refactor — prerequisite Phase 2–4

> Status: **SELESAI, diverifikasi 2026-09-16.** Baseline hasil: `2.8.16`.
> Tujuan tunggal: parent `YuzvidView` berhenti memiliki detail Browser/proxy/runtime.
> Tidak mengubah perilaku browser, proxy pool, DNS, bookmark, UI, extraction, atau queue.

## Target akhir

```text
YuzvidView (shell: tabs, bookmark, status binding)
       │ memakai kontrak publik saja
       ▼
IYuzvidBrowserController ── BrowserFeature
                                 ├─ YuzvidBrowserView / WebView2
                                 ├─ LocalProxyServer
                                 ├─ YuzvidProxyPoolAdapter
                                 └─ Browser runtime-status snapshot

YuzvidRuntimeView ── menerima IYuzvidBrowserController, bukan delegate dari parent

`YuzvidModule` adalah composition root: ia membuat controller Browser dan `YuzvidRuntimeView`,
kemudian memberi parent kontrak Browser + `FrameworkElement` Settings. Dengan demikian Browser
tidak bergantung pada feature Runtime, dan parent tidak mengimpor tipe Runtime. Runtime melakukan
refresh awal pada `Loaded`; parent tidak perlu memanggil API Runtime setelah memasangnya.
```

`YuzvidView` boleh menangani bookmark dan memilih tab. Ia tidak boleh membuat, dispose, atau
mengetahui tipe `LocalProxyServer`, `YuzvidProxyPoolAdapter`, dan `YuzvidRuntimeView`.

## Kontrak minimum

Tambahkan satu kontrak publik di `Features/Browser/`:

```csharp
public interface IYuzvidBrowserController
{
    FrameworkElement View { get; }
    BrowserState State { get; }
    string CurrentUrl { get; }
    string CurrentTitle { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    BrowserRuntimeSnapshot Runtime { get; }
    event EventHandler<string> Navigated;
    event EventHandler<bool> NavigationStateChanged;
    event EventHandler<string> NavigationFailed;
    event EventHandler<BrowserState> StateChanged;
    void Navigate(string input);
    void GoBack(); void GoForward(); void Refresh();
    void SetProxyEnabled(bool enabled);
    void SetDnsMode(YuzvidDnsMode mode);
    Task RetryInitAsync();
}
```

`BrowserRuntimeSnapshot` adalah record immutable berisi status proxy (`enabled`, endpoint masked,
available count). `BrowserState` dan `YuzvidDnsMode` adalah enum **publik kontrak**;
`YuzvidDnsMode` diterjemahkan Browser ke `BrowserDnsMode` internal. Tidak ada
`LocalProxyServer`, endpoint mentah, atau mutable collection keluar dari feature.

## Urutan implementasi

### R1 — Tambah kontrak/controller pasif (**selesai**)

- Tambah `YuzvidBrowserController` yang mengimplementasikan kontrak di atas.
- Pindahkan `BrowserState` ke file kontrak publik, lalu tambah `YuzvidDnsMode` dan
  `BrowserRuntimeSnapshot`; jangan mengekspos `BrowserDnsMode` internal.
- Controller membuat dan memiliki satu `YuzvidBrowserView` sendiri, serta menerima `Lifetime`,
  tetapi **belum** start local proxy / menjadi instance yang dipakai layar. Parent baseline tetap
  satu-satunya owner sampai R2.
- Teruskan event/state Browser dan compile kontrak tanpa mengubah inisialisasi WebView2.
- Acceptance: build hijau; controller dapat dibuat tanpa mulai listener atau mengubah Browser aktif.

**Batas file:** kontrak + controller baru + perubahan terbatas pada Browser feature. Jangan sentuh
`YuzvidView` pada tahap ini.

### R2 — Cutover Browser/proxy atomik ke controller (**selesai**)

- Di `YuzvidModule.CreateView`, buat satu controller dengan `Lifetime`, panggil aktivasi controller
  **tepat satu kali** (start listener + set port sebelum WebView2 dimuat), lalu pass ke `YuzvidView`.
- Ubah konstruktor `YuzvidView` menerima `IYuzvidBrowserController`.
- Dalam perubahan yang sama, controller mulai/memiliki `LocalProxyServer` dan
  `YuzvidProxyPoolAdapter`; hapus field dan setup/cleanup proxy dari parent. Tidak boleh ada
  fase ketika keduanya start listener.
- Handler toolbar hanya meneruskan perintah ke kontrak; status toolbar tetap ditentukan event kontrak.
- XAML parent mengganti `browser:YuzvidBrowserView` dengan satu `ContentPresenter` host; code-behind
  memasang `controller.View` ke host itu. Jangan redesain toolbar.
- **Jangan hapus** `_runtimeView` atau `EnsureRuntimeView` pada R2. Keduanya sementara tetap ada,
  tetapi membaca state/proxy lewat controller sampai R3 selesai.
- Acceptance: pindah dari Yuzvid ke module lain lalu kembali mempertahankan WebView2, URL, history,
  proxy state, dan tidak ada local proxy kedua.

### R3 — Lepaskan Runtime dari parent (**selesai**)

- Ubah `YuzvidRuntimeView.Wire(...)` agar menerima `IYuzvidBrowserController`, atau constructor
  yang sama; Runtime membaca snapshot dan memanggil `RetryInitAsync()` lewat kontrak. Pindahkan
  refresh pertama ke event `Loaded`, sehingga parent tidak perlu memegang `ActivateAsync()`.
- `YuzvidModule` membuat Runtime view dan memberikannya ke parent sebagai `FrameworkElement`.
  Parent hanya memasangnya ketika tab Settings dibuka; Browser controller tidak membuat Runtime view.
- Hapus `using Module.Yuzvid.Features.Runtime` serta detail delegate Browser dari parent.
- Acceptance: Refresh status dan Retry dari Settings tetap bekerja ketika Browser sudah pernah atau belum
  pernah dibuka; tidak ada null/crash saat pindah tab/module.

### R4 — Guard penuh dan cek regresi kecil (**selesai untuk guard/build**)

- Tulis guard lokal yang melarang parent mengimpor/menginstansiasi **tipe implementasi internal** dari
  `Features/Browser`, `Features/Runtime`, `Features/Queue`, dan nanti `Features/Extraction`.
  Kontrak publik `IYuzvidBrowserController`, `BrowserState`, `YuzvidDnsMode`, dan
  `BrowserRuntimeSnapshot` diizinkan.
- Jalankan build `module/yuzvid`.
- Smoke visual minimum: direct navigation, proxy enable/disable, DNS change, retry browser init,
  pindah module lalu kembali.
- Acceptance: guard hijau; parent hanya menyebut `IYuzvidBrowserController` dan tipe UI/framework umum.

**Bukti:** guard lokal 3/3 hijau; parser guard mencakup `partial class`; `dotnet build
module/yuzvid/Module.Yuzvid.csproj --no-restore` lulus 0 error. Smoke visual retained masih
verifikasi runtime terpisah, bukan klaim selesai dari build.

## Di luar scope

- Tidak ada perubahan konfigurasi CI atau pytest.
- Tidak ada perubahan LocalProxyServer/DNS algorithm maupun SafeSearch workaround.
- Tidak ada JS bridge, extraction, regex host, drawer, queue/download engine, atau UI baru.
- Tidak ada refactor MangaReader atau module lain.

## Risiko dan pagar

1. **Lifecycle:** controller dibuat dan diaktifkan satu kali oleh `YuzvidModule` dengan `Lifetime`
   retained; jangan membuat controller baru saat tab berubah atau saat view dipasang ulang.
2. **WebView2 visual host:** detach/attach hanya memindahkan `controller.View`; jangan dispose Browser
   ketika parent berpindah module.
3. **Proxy:** hanya controller yang boleh start/dispose `LocalProxyServer`; pastikan satu listener saja.
4. **Tahap:** berhenti setelah setiap R1–R4; bila R2 mengubah perilaku baseline, rollback tahap itu saja,
   jangan lanjut ke Phase 2–4.
