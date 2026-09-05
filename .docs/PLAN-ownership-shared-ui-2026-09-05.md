# Plan handoff: ownership fitur dan shared UI Citadel

Status: PLANNED — belum dieksekusi. Dokumen ini bukan laporan implementasi.
Baseline inspeksi: `C:\VSCODE\citadel`, `main`, commit `ce578d567b26c3ef5f0b19aa4af2761aa8f00f68`; worktree bersih sebelum penambahan dokumen ini.
Reasoning yang direkomendasikan: High. Tidak perlu agent paralel atau audit ulang seluruh repository.

## 1. Goal

Rapikan ownership pada temuan audit yang terdaftar di bawah, sehingga:

- Fitur memiliki alur, state, adapter khusus, dan komposisi UI-nya sendiri.
- Parent hanya merakit, merutekan kontrak, dan menjaga lifetime.
- Shared logic hanya memuat mekanisme/data dengan consumer nyata; tidak memuat command khusus Add Profile.
- Universal UI behavior memakai owner bersama yang sudah ada, tanpa template atau behavior tandingan di screen.
- Implementasi lama yang benar-benar tergantikan dihapus setelah semua caller dipindahkan.
- Perilaku pengguna, data tersimpan, deployment citizen, serta lifecycle browser/Reader tetap terjaga.

Goal tercapai bila seluruh acceptance criteria yang Required di dokumen ini selesai, affected builds/checks lulus, dan smoke pengguna tercatat. Jika WPF/browser live belum diuji, laporkan IMPLEMENTATION COMPLETE / LIVE VERIFICATION PENDING, bukan seluruh goal selesai.

Ini bukan goal untuk merombak arsitektur Citadel atau memaksimalkan jumlah file. Penambahan fitur normal boleh mengubah registration/composition point, tetapi tidak menambah pengetahuan internal fitur ke parent/sibling/shared transport.

## 2. Batas kerja

### Required

1. Routing instruksi project ke Yuzskill yang maintained.
2. Satu jalur adapter Add Profile; keluarkan command fitur dari shared transport.
3. Pisahkan orchestration action CamoProf yang masih dijalankan Launcher.
4. Tempatkan logic privat Library/CoverBuilder/History pada pemiliknya; kontrak lintas fitur tidak membawa card model UI.
5. Pindahkan kontrak/helper kontribusi Drawer yang dipakai bersama ke area bersama Reader.
6. Hilangkan template ListBoxItem lokal yang redundant pada Chapter Selector.
7. Hubungkan scrollbar Sidebar ke shared scrolling setelah memastikan resource lookup-nya.

### Tidak termasuk

- Downloader/Comix, fitur Reader baru, perubahan semantics pin/unpin, speed, zoom, atau fullscreen.
- Rewrite Python enrollment/page ownership, Google login, password capture, session.open, atau pyhost protocol.
- Perubahan format/history/library path/credential storage, migrasi data, archive algorithm, RAR dependency atau bundled prerequisite.
- Framework MVVM/DI/event bus/plugin registry baru; upgrade dependency atau SDK.
- Menyamakan semua nama folder, memecah setiap class/controller, memindahkan seluruh `shareLogic`, atau memindahkan semua UI ke satu folder.
- Merombak Appearance/Gallery, RailButton, atau kartu manga hanya karena UI dibangun dengan C# atau template khusus fitur.
- Menghapus tests, skills, reference code, atau artifacts berdasarkan jumlah file/ukuran saja.
- Commit, push, bump, dan publikasi release otomatis. Itu checkpoint release terpisah sesuai instruksi user; bukan efek samping refactor ini.

Jika source terbaru sudah memperbaiki satu temuan, verifikasi lalu tandai SATISFIED dengan bukti. Jangan menulis ulang agar bentuknya persis seperti plan.

## 3. Onboarding agent dan pemulihan setelah compaction

1. Pastikan repository aktual `C:\VSCODE\citadel`. Jangan memakai checkout lama `C:\VSCODE\TELEGRAM-CITADEL\Citadel` atau baseline audit secara buta.
2. Baca `AGENTS.md`, instruksi yang lebih dekat ke file, dokumen ini, `.docs/README.md`, `module/README.md`, dan `.docs/SHARED-UI-BEHAVIOR.md`.
3. Aktifkan Yuzskill melalui MCP `skills_begin` dengan intent refactor dan path aktual; baca/ack semua required skill, lalu periksa status. Nama tool mengikuti server yang tersedia.
4. Jika MCP Yuzskill tidak tersedia, baca manual `C:\Users\YUZHA\Yuzskill\AGENTS.md`, `.agents/skill-map.json`, core skills dan workflow terkait. Laporkan manual activation; jangan mengklaim receipt MCP.
5. Skill relevan: engineering-quality, modular-architecture, planning-and-delivery, architecture-and-contracts, shared-ui, verification-and-review, stack-guidance (.NET/WPF dan Python jika tersentuh), citadel-project. Baca seluruh active SKILL.md; jangan memuat archive SKILLS-ORIGINAL.
6. Periksa `git status`, HEAD, diff, manifest, caller dan test target fase yang akan dikerjakan. Dirty worktree lain tetap utuh.
7. `tasks/plan.md` dan `tasks/todo.md` memuat kontrak Add Profile serta smoke yang belum selesai. Baca ketika mengerjakan CamoProf; jangan overwrite atau menandainya selesai berdasarkan build. Plan baru ini hanya mengubah ownership, bukan mengganti kontrak terminal flow lama.
8. Setelah compaction: berhenti sebelum edit berikutnya; baca ulang skill aktif, dokumen ini beserta checkpoint terakhir, status/diff, dan owner/caller fase aktif. Ringkasan percakapan bukan pengganti source.

Gunakan dokumen ini sebagai tracker tunggal pekerjaan ini. Tidak perlu membuat salinan plan/todo/ADR baru. Update checklist dan checkpoint singkat saat eksekusi.

## 4. Target ownership dan kontrak

| Area | Target dan batas |
|---|---|
| `core/`, `setting/`, `module/` | Root/project boundary tetap. Citizen tetap standalone dan tidak menjadi project dependency Shell/solution. |
| `module/sharedLogic/cs/PyHost.cs` | Transport request/response generik, timeout, process lifetime. Tidak mengenal `camoprof.add_profile.*`. |
| CamoProf `sharedLogic/BrowserSessionCoordinator.cs` | Session identity, operation gate, session lookup/forget, lifecycle. Tidak menyimpan payload/command/policy Add Profile. |
| `camoprof/Features/AddProfile/` | Satu adapter aktif, coordinator, request/result/policy dan UI fitur. Reuse file yang ada. |
| `camoprof/Launcher/` | Tabel, binding, event adapter, status rendering. Alur domain action di owner fitur, bukan lambda orchestration di screen. |
| `mangareader/Library/` | Scan, scan-attempt/path persistence, UI Library. |
| `mangareader/CoverBuilder/` | Fetch/bake coordination dan UI; archive adapters bersama tetap dipakai. |
| `mangareader/History/` | History persistence/recording, state dan UI History. |
| `mangareader/shareLogic/` | Data manga/chapter, archive/cache dan helper presentasi yang benar-benar dipakai bersama. Tidak perlu rename folder sebagai pekerjaan tersendiri. |
| `Reader/ReaderCore/` | Kontrak/helper Reader yang memang dipakai beberapa fitur. Bukan tempat semua feature logic. |
| `Reader/Features/*/` | Behavior dan komposisi masing-masing fitur. |
| `setting/Components/` | Primitive/behavior universal yang sudah ada. Jangan menduplikasi ke Shell atau module. |

Nama file baru di bawah adalah usulan minimal, bukan kewajiban membuat class/layer jika owner yang sesuai sudah tersedia. Tidak ada kuota baris atau jumlah file.

## 5. Urutan eksekusi

### P0 — Satukan discovery Yuzskill

Owner/files: `AGENTS.md`; client adapter project yang benar-benar ditemukan mereferensikan instruksi lama. Tidak mengedit collection global Yuzskill.

- Jadikan AGENTS adapter discovery Yuzskill plus aturan khusus Citadel: shared UI inventory, approval boundary, module/build contract dan lokasi plan.
- Jangan menyalin seluruh isi core skill ke AGENTS atau client adapter.
- Pertahankan aturan project yang masih berlaku; jangan menghapus `.agents/skills/` secara massal. Jika ada consumer lama, adapter kompatibel hanya menunjuk sumber canonical, tidak memelihara salinan policy kedua.

Acceptance: agent baru menemukan collection yang sama dan aturan Citadel tanpa asumsi lokasi; tidak ada aturan universal baru yang bersaing dengan Yuzskill.
Check: baca hasil routing, periksa path/link dan diff. Tidak perlu build/test.
Dependency: tidak ada.

### P1 — Satu jalur Add Profile, shared session tetap aman

Owner/files: `Features/AddProfile/AddProfilePyHostClient.cs`, `AddProfileCoordinator.cs`, `sharedLogic/BrowserSessionCoordinator.cs`, `module/sharedLogic/cs/PyHost.cs`, caller/compile includes/test CamoProf yang terdampak.

Alur target:

`AddProfileCoordinator -> AddProfilePyHostClient -> generic gated session request -> PyHost.SendAsync -> plugin yang sudah ada`.

- Aktifkan adapter yang sekarang tidak dipakai sebagai pemilik empat command start/status/finish/cancel dan payload fitur. Jangan menambah adapter pengganti kedua.
- Ganti empat wrapper khusus fitur di session coordinator dengan satu jalur generik minimal untuk session-bound request. Reuse `PyHost.SendAsync`; jangan membuka akses mutable session/raw host kepada fitur atau membuat registry/lease/service locator baru.
- Jalur generik menjaga operation gate, session lookup, disposal check, release di finally, dan Forget pada BROWSER_GONE. Tidak boleh membuka browser baru sebagai side effect request status/cancel.
- Session ID di-resolve oleh pemilik session dan dikirim dengan wire field yang sama (`session`). Tidak boleh memakai ID stale yang dicache UI.
- Missing session pada start/status/finish tetap error yang sesuai; cancel tetap best-effort/idempotent sesuai kontrak lama. Adapter memetakan hasil missing-session tanpa menelan error lain.
- Pindahkan pilihan command/payload ke adapter; jangan mengganti protocol name, timeout, polling interval, secret handling, cleanup order atau terminal result.
- Finish secret tetap hanya diproses coordinator/credential owner dan tidak masuk UI/log. Pertahankan cleanup cancellation yang sudah berjalan; jangan memakai token yang sudah cancelled untuk menghalangi teardown wajib.
- Hapus wrapper khusus Add Profile di PyHost dan session coordinator setelah semua caller bermigrasi. Komentar dan test include yang tergantikan ikut diperbarui.
- Registrasi plugin saat composition/spawn diperbolehkan. Jangan mengubah packaging plugin.py/__init__.py hanya karena tampak duplikat: Citizen.targets memang mempunyai transform deploy tersebut.

Acceptance: tidak ada command/payload Add Profile pada shared transport/session owner; hanya satu adapter aktif; add/cancel/finish/failure menjaga outcome dan lifecycle lama.
Check: build CamoProf; existing AddProfileCoordinator/Policy/Contract tests. Tambah hanya regression yang belum tercakup untuk generic gate/missing-session/cancel/BROWSER_GONE. Python suite relevan jika packaging/Python berubah; jangan rewrite Python demi fase ini.
Live: operator menguji Add Profile, cancel/close saat proses, terminal browser close dan check akun lama. Gunakan profile disposable; jangan merekam password.
Dependency: P0.

### P2 — Launcher meneruskan action, bukan menjalankan alur domain

Owner/files: `camoprof/Launcher/LauncherView.xaml.cs`, fitur action profile yang ada/owner kecil baru di `Features/`, `Providers/Google/GoogleAccountService.cs` hanya bila wiring memerlukannya.

- Inventarisasi Launch/Close, Open GitHub, Check Google/repair, Delete; pertahankan public behavior dan status error aktual.
- Urutan `close session -> delete catalog -> delete credential` menjadi satu command fitur Delete/ProfileActions. Confirmation visual tetap boleh di view; tidak ada destructive effect sebelum user confirm.
- Check Google tetap memakai GoogleAccountService. Keputusan domain seperti kapan meminta repair/enrollment ada di fitur, bukan view. Hasil/action contract boleh dirutekan parent ke AddProfileFeature; jangan memanggil internal sibling coordinator.
- Launch/Close dan GitHub memakai mekanisme session existing. Satu pemanggilan event adapter yang sederhana tidak perlu dibungkus lagi; pindahkan hanya keputusan/alur yang nyata.
- Pertahankan busy/disabled, cancellation, selection dan disposal. Parent boleh membangun fitur serta menerjemahkan result menjadi UI; jangan membangun LauncherController besar yang hanya menampung seluruh code-behind lama.

Acceptance: handler UI tidak lagi mengurutkan operasi delete atau memutuskan policy repair; satu owner untuk setiap effect; confirmation dan browser reuse tetap sama.
Check: build CamoProf bila belum tercakup build terbaru; focused existing checks dan satu smoke Launch/Check/Delete disposable. Jangan membuat suite baru untuk tiap tombol.
Dependency: P1.

### P3 — Ownership MangaReader dan kontrak data lintas fitur

Owner/files: `Library/`, `CoverBuilder/`, `History/`, file privat terkait di `shareLogic/`, `MangaReaderEvents.cs`, `MangaReaderView.xaml.cs`, test project link yang menunjuk file pindahan.

Kerjakan tiga increment koheren, bukan pemindahan massal:

1. Library: tempatkan LibraryScanner di Library; scan coordination di owner Library yang kecil bila masih tertanam di view. Pertahankan captured LibraryScanAttempt, field disabled saat scan, stale-result guard, restore sekali, zero-title success, dan persist hanya setelah scan sukses.
2. Kontrak data + History: LibraryChanged membawa snapshot `IReadOnlyList<MangaTitle>` menggunakan domain yang sudah ada, bukan `MangaTitleCardModel`. History dan CoverBuilder diubah dalam increment yang sama. Pindahkan ReadingHistoryStore/HistoryCardModel ke History jika tidak ada consumer lain. Expose Record/Changed melalui owner History, sehingga recording tidak membutuhkan instance HistoryTab; parent hanya merutekan chapter event.
3. CoverBuilder: pindahkan CoverBuilderService/CoverSourceLoader ke CoverBuilder. Feature owner memegang fetch-before-bake dan operation identity/cancellation; view hanya binding/event/status. Tetap gunakan archive dispatcher/transaksi/cache yang sudah ada.

Perhatian khusus: HistoryCardModel saat ini berlangganan perubahan Cover pada MangaTitleCardModel. Jangan sekadar mengganti tipe dan kehilangan cover refresh. Model kartu boleh tetap reusable presentation di module; setiap fitur memiliki instance/state-nya, memakai MangaCoverLoader/cache yang ada. Atur update/invalidasi saat scan/bake dan subscription disposal tanpa menjalankan pipeline fetch baru atau menggandakan render mahal.

Tidak memindahkan ChapterRenderCache/MangaCoverLoader/Archive ke satu fitur jika consumer bersama masih ada. Jangan membuat metadata store, cache framework, atau kontrak baru di Citadel.Contract hanya untuk komunikasi internal MangaReader.

Acceptance: file privat berada pada owner; data lintas fitur tidak membawa card model; History tetap mencatat saat tab History belum dibuka; cover/status/selected title/refresh dan persisted paths tidak regresi.
Check: build citizen MangaReader; existing Library persistence tests, Archive tests bila linked source/service path berubah. Smoke library disposable: scan sukses/gagal/cancel/empty, restart restore, buka chapter, History, fetch/bake cover lalu refresh. Jangan bake koleksi asli untuk QA.
Dependency: P0; dikerjakan setelah P2 untuk satu agent serial. Tidak bergantung pada detail internal CamoProf.

### P4 — Kontribusi Drawer adalah kontrak bersama Reader

Owner/files: `Reader/Features/Drawer/ReaderDrawerContributions.cs`, `Reader/ReaderCore/`, `ReaderFeatureContract.cs`, consumer card dan reader test includes yang terdampak.

- Pindahkan tipe/helper yang digunakan beberapa fitur (ReaderDrawerCardContribution/ReaderDrawerCards) ke ReaderCore dengan nama/path yang jelas. Reuse kode yang ada; tidak menambah primitive, template, atau service baru.
- Cocokkan lokasi kontrak kontribusi abstrak dan konkrit agar tidak ada common contract milik internal Drawer. Hindari membongkar seluruh ReaderFeatureContract hanya untuk kosmetik.
- Drawer tetap order/render opaque card; feature tetap memiliki kontrol, command dan state card-nya. SettingCardStyle/SettingActionCard/Button/Slider tetap owner rendering/input.
- ReaderDefaultFeatureCatalog tetap registration point. ReaderFeatureHost saat ini membutuhkan tepat satu Drawer host: pertahankan sebagai required role; jangan membuat Drawer optional atau menghapus guard tanpa keputusan produk.
- Model presentation khusus viewport seperti ChapterSurfaceModel dapat ditempatkan di kontrak bersama Reader jika consumer mapping mengonfirmasi tidak dipakai di luar Reader; bukan dipaksa masuk satu child ChapterLoading.

Acceptance: sibling fitur tidak membutuhkan file internal Drawer untuk mendefinisikan card; fitur opsional diregistrasikan via catalog; tidak ada perubahan Drawer/overlay/pin/input semantics atau layout kartu.
Check: existing ReaderFeatureHost/Controller/InputRouter tests yang relevan; build MangaReader. Smoke buka/tutup/pin Drawer dan kontrol card pada window normal. Tidak menambah tes per getter/class.
Dependency: P3 (selesaikan perubahan model/compile links dahulu).

### P5 — Chapter Selector memakai satu shared item behavior

Owner/files: `Library/ChapterSelectorView.xaml`; shared style hanya jika ada perubahan yang benar-benar diperlukan dan diizinkan.

- Periksa effective ItemContainerStyle. Saat baseline, SettingListStyle menunjuk SettingListItemStyle, tetapi screen juga mendefinisikan implicit ListBoxItem template lokal.
- Hapus template lokal yang superseded. Pertahankan binding title, accessible name, command dan data template fitur tanpa menduplikasi hover/selected/focus behavior.
- Default target cukup shared style existing. Jika ternyata diperlukan style turunan atau capability universal yang belum tersedia, jangan diam-diam membuat primitive/style/template baru: jelaskan gap dan minta izin sesuai aturan Citadel. `BasedOn` bukan izin membuat desain paralel.

Acceptance: satu template/behavior owner; selected/hover/focus/keyboard/open chapter dan label tetap benar.
Check: build MangaReader dan existing shared list/selector check yang tersedia; live inspeksi item normal/selected/focus. Klaim visual harus dari WPF, bukan XML diff.
Dependency: P3.

### P6 — Sidebar memakai shared scrollbar

Owner/files: `core/Citadel.Ui/Theme/ThemeResources.xaml`; resource composition `core/Citadel.Shell/App.xaml` dan test resource setup hanya jika diperlukan.

- Konfirmasi effective style saat Sidebar overflow. Baseline ScrollViewer di SidebarListStyle tidak menunjuk SettingScrollViewerStyle.
- Reuse keyed shared style lewat resource lookup yang benar. App sudah merge ThemeResources, RailButtonResources dan SettingResources; perhatikan StaticResource versus DynamicResource serta standalone test resource setup.
- Prefer wiring resource minimal, bukan memindahkan assembly atau menambah dependency project baru. Tidak membuat implicit style global yang diam-diam mengubah seluruh ScrollViewer.
- Pertahankan CanContentScroll, ItemsPresenter, horizontal disabled, keyboard focus/scroll dan layout Sidebar. Universal timer/fade/drag behavior tetap milik setting/Components/ScrollBar.

Acceptance: Sidebar overflow menggunakan behavior shared (idle 1.5s sesuai kontrak scrollbar, bukan chrome 500ms); non-overflow tanpa scrollbar; fade tidak menggeser layout; unload/reload tidak meninggalkan handler/timer aktif.
Check: Shell build + relevant Ui/Uia resource/shared scroll checks. Live normal dan minimum window, overflow, wheel, keyboard, thumb drag, idle dan reload. Gunakan existing fixture, jangan menginstall citizen palsu ke deployment pengguna untuk menambah item.
Dependency: P0; serial setelah P5 untuk handoff sederhana.

## 6. Context7: kapan dan apa yang dibaca

Server Context7 sudah callable saat penyusunan plan. Library ID di bawah sudah di-resolve, bukan ditebak. Agent tetap melakukan resolve-library-id sebelum query-docs sesuai tool contract sesi sendiri. Dokumentasi membantu memahami API; local source menentukan owner dan kontrak Citadel.

| Stack / trigger | Acuan Context7 | Pertanyaan terarah |
|---|---|---|
| WPF pada P4-P6 atau saat binding/resource/threading berubah | `/websites/learn_microsoft_en-us_dotnet_desktop` (utama); `/dotnet/wpf` untuk source comparison | ItemContainerStyle dan explicit/implicit style precedence; resource lookup/merged dictionary; BasedOn; Dispatcher dan Unloaded cleanup. Query satu topik per call, bukan dump framework. |
| .NET async/session pada P1 jika API atau semantics cancellation diragukan | Resolve `.NET` / Microsoft Learn .NET documentation di sesi eksekusi; ID belum dipilih dalam plan ini | SemaphoreSlim.WaitAsync cancellation dan release ownership; CancellationToken teardown; JsonObject payload ownership. Cocokkan source/types SDK lokal sebelum mengubah pola. |
| Playwright Python, hanya bila file browser/plugin memang perlu disentuh | `/websites/playwright_dev_python` atau `/microsoft/playwright-python` | Async Python page/context close, event listener lifetime. Jangan memakai contoh Node/TypeScript sebagai signature Python. |
| Camoufox, hanya bila perilaku integrasi diragukan | Resolve `Camoufox` ketika diperlukan; belum diverifikasi tersedia di Context7 | Cocokkan versi 0.5.5 dengan installed source atau official repository tag jika Context7 tidak menyediakan versi itu. Jangan mengasumsikan docs Playwright membuktikan perilaku window Camoufox. |

Version guard baseline:

- `global.json`: SDK `10.0.400`, rollForward latestPatch; `Directory.Build.props`: `net10.0-windows`. Ini konfigurasi repo, bukan bukti SDK tersebut sudah terinstall. Verifikasi `dotnet --info` sebelum build.
- `module/sharedLogic/requirements.txt`: `camoufox==0.5.5`, `playwright>=1.51,<1.52`. Periksa versi environment yang benar-benar dipakai jika Python disentuh; jangan otomatis memakai latest atau upgrade untuk mengikuti snippet.
- Context7 WPF source bisa menunjuk branch main, dan docs Python tidak selalu versioned. API setelah versi project tidak boleh diadopsi tanpa bukti dukungan versi lokal.
- Tidak perlu membaca AntD/MUI, Suwayomi, SharpCompress/RAR writer, atau framework lain: tidak ada migration/fitur tersebut dalam scope.
- Bila Context7 kurang tepat, gunakan official docs/source versi yang cocok; laporkan gap. Jangan mengarang method dari ingatan atau menyalin source project/secret ke query eksternal.

Referensi resmi yang sudah ditemukan melalui Context7:

- [WPF dependency property value precedence](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/properties/dependency-property-value-precedence) — explicit/implicit/default Style precedence.
- [WPF data templating overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/data/data-templating-overview) — pemisahan item data template dan container style.
- [WPF source](https://github.com/dotnet/wpf) — pembanding implementasi, bukan instruksi mengganti style Citadel dengan template default/Fluent.

## 7. Verifikasi proporsional dan handoff akhir

Jalankan dari root aktual. Reuse hasil sukses pada revision/input yang sama; jangan mengulang full suite per fase.

Build target relevan (citizen tidak otomatis ikut solution build):

```powershell
dotnet build module/camoprof/Module.Camoprof.csproj -c Release
dotnet build module/mangareader/Module.Mangareader.csproj -c Release
dotnet build core/Citadel.Shell/Citadel.Shell.csproj -c Release
```

Test project yang tersedia: `tests/Module.Camoprof.Tests`, `tests/Module.Mangareader.Library.Tests`, `tests/Module.Mangareader.Reader.Tests`, `tests/Module.Mangareader.Archive.Tests`, `tests/Citadel.Ui.Tests`, `tests/Citadel.Uia`. Pilih existing tests sesuai fase; sesuaikan linked Compile Include setelah move. Jangan menambah citizen ke solution demi testing.

Jika Python/deployment plugin berubah, gunakan interpreter project yang terverifikasi dan stdlib unittest pada `module/sharedLogic/tests` (test_pyhost dan tiga test_add_profile suites). Jangan menginstall browser/package hanya untuk memindahkan C# wrapper. Jika hanya C# berubah, Python evidence lama tetap dilabel historical, bukan freshly passed.

Pada integrasi akhir, satu run suite solution dengan concurrency MSBuild dibatasi (`dotnet test Citadel.slnx -c Release -m:1`) layak karena kontrak lintas beberapa consumer dan shared resource tersentuh. Ini tidak menggantikan build citizen. Jika gagal, diagnosis target yang gagal; jangan loop suite atau mengurangi assertion agar hijau.

Perhatikan build citizen dapat men-deploy payload ke output Shell. Jangan overwrite module yang sedang dipakai app untuk smoke; tutup hanya instance milik sesi test/koordinasikan dengan user, bukan kill semua process browser.

Cleanup acceptance:

- Tidak ada caller ke adapter/wrapper/path yang dihapus; registration dan linked test paths diperbarui.
- Tidak ada dua implementasi aktif untuk satu alur; tidak ada temporary logging/harness tersisa.
- Tidak menghapus data user, RAR prerequisite, shared DLL deployment rules, ataupun untracked work agent lain.
- Citizen tetap discoverable dan output mengikuti Citizen.targets; jangan menganggap sukses build Shell membuktikan module ikut ter-deploy.
- `git diff --check` bersih; diff tidak menyentuh subsystem di luar plan.
- Perubahan behavior contract dicatat hanya bila memang diizinkan; jangan menulis status visual PASS dari build/test.

Smoke akhir cukup satu daftar representatif yang menggabungkan fase: CamoProf add/check/delete disposable; Library scan/restore/History/CoverBuilder disposable; Reader Drawer controls; Chapter Selector; Sidebar overflow. Operator memasukkan login sendiri. Catat belum teruji jika native UI atau akses operator tidak tersedia.

## 8. Tracker dan format checkpoint

- [ ] P0 — Discovery Yuzskill canonical.
- [ ] P1 — Adapter Add Profile tunggal, generic gated transport.
- [ ] P2 — Launcher action ownership. IMPLEMENTATION COMPLETE / LIVE VERIFICATION PENDING — lihat Checkpoint P2.
- [ ] P3 — Ownership MangaReader dan domain-only feature contracts. IMPLEMENTATION COMPLETE / LIVE VERIFICATION PENDING — lihat Checkpoint P3.
- [ ] P4 — Common Reader contribution ownership. IMPLEMENTATION COMPLETE / LIVE VERIFICATION PENDING — lihat Checkpoint P4.
- [ ] P5 — Chapter Selector shared template.
- [ ] P6 — Sidebar shared scrollbar.
- [ ] Cleanup caller/compile/deploy references selesai.
- [ ] Affected builds dan regression checks selesai.
- [ ] Live smoke selesai; jika belum, goal belum fully verified.

Isi checkpoint di dokumen ini setelah increment koheren:

`Phase/status | file changed/moved/deleted | keputusan kontrak | checks aktual | pending/next`.

### Checkpoint P2 — 2026-09-05

**Phase/status:** P2 / IMPLEMENTATION COMPLETE, LIVE VERIFICATION PENDING.

**File changed/added:** tidak ada file dipindah atau dihapus.

- Baru: `module/camoprof/Features/ProfileActions/ProfileActionsFeature.cs` — `ProfileActionsFeature` (Delete + CheckGoogle) dan kontrak `GoogleCheckOutcome`.
- Berubah: `module/camoprof/Launcher/LauncherView.xaml.cs` — dua handler menjadi penerusan; field dan parameter ctor `GoogleAccountService` + `GoogleCredentialStore` dihapus.
- Berubah: `module/camoprof/CamoprofView.xaml.cs` — composition root membangun dan meneruskan `ProfileActionsFeature`.
- Tidak ada perubahan XAML, style, template, atau primitive `setting/Components/`; `SettingDialog.Confirm` dipakai ulang tanpa diubah, jadi approval boundary shared UI tidak tersentuh.

**Keputusan kontrak:**

- Delete: urutan `close session -> delete catalog -> delete credential` kini satu command `ProfileActionsFeature.DeleteAsync`. Confirmation tetap di view; tidak ada efek destructive sebelum confirm. Return `Task` karena view tidak memakai hasil antara — tidak ada record hasil yang diciptakan tanpa consumer.
- Check Google: keputusan kapan pairing/credential repair diperlukan pindah ke `ProfileActionsFeature.CheckGoogleAsync`; `GoogleAccountService` tetap dipakai apa adanya.
- `GoogleCheckOutcome(GoogleAccountResult? Account, AddProfileRequest? Enrollment)`: `Account == null` menandai tidak ada live check (path unlinked) sehingga view tidak mengubah `row.Google` — identik dengan perilaku lama. `Enrollment != null` adalah keputusan fitur, dan view meneruskannya ke `AddProfileFeature` (kontrak publik sibling), bukan ke internal coordinator.
- Placeholder `Checking` tetap rendering milik view tetapi dipicu callback `checkStarting`, yang hanya dipanggil fitur saat live check benar-benar berjalan. Ini menjaga satu pemilik keputusan tanpa menduplikasi branch `IsLinked` di view.
- Launch/Close dan Open GitHub sengaja TIDAK dipindah: keduanya pass-through satu effect ke `BrowserSessionCoordinator` (mekanisme session existing) tanpa sequencing domain, dan plan mengizinkan handler penerusan sederhana tetap di view.
- Generasi `profileId` di `AddProfileButton_Click` tetap di view: Add Profile bukan bagian inventaris P2 dan `AddProfileFeature` sudah menjadi satu-satunya entry point flow tersebut.

**Checks aktual:**

- `dotnet build module/camoprof/Module.Camoprof.csproj -c Release -p:CitizenRuntimeRoot=<temp>` → Build succeeded, 0 Warning, 0 Error. Deploy sengaja dialihkan ke temp karena `Citadel.Shell` (PID 14824) sedang berjalan dengan session browser aktif; output Shell milik pengguna tidak disentuh dan temp sudah dibersihkan.
- `dotnet test tests/Module.Camoprof.Tests -c Release` → Passed 55, Failed 0, Skipped 0 (run segar, bukan evidence historis).
- `git diff --check` bersih. Grep membuktikan tidak ada sisa referensi `_google`/`_credentials`/`GoogleAccountService`/`GoogleCredentialStore` di `Launcher/`; satu-satunya `_google` tersisa adalah backing field `LauncherProfileRow.Google` yang tidak terkait.
- Test csproj tidak diubah: `LauncherView`/`CamoprofView`/`ProfileActionsFeature` memang tidak termasuk Compile Include test, jadi tidak ada linked path yang perlu diperbarui.
- Tidak ada test baru, dan ini gap yang disengaja: `ProfileActionsFeature` bergantung pada `ProfileCatalog`, `GoogleCredentialStore`, dan `GoogleAccountService` konkret (filesystem Credenz nyata, DPAPI, network, browser) tanpa seam. Mengonstruksinya di test memanggil `CredenzPath.Resolve()` yang menulis file probe ke vault Credenz pengguna. Menambah interface atau parameter nullable demi testability berarti abstraksi baru di luar scope P2, dan test berisi delegate palsu yang hanya menghitung panggilan dilarang instruksi operator.

**Pending/next:**

- Live smoke BELUM dijalankan (butuh WPF live, operator, dan profile disposable): Delete pada profile disposable termasuk confirm/batal, Check Google pada profile linked, unlinked, dan credential-rejected, Launch/Close, Open GitHub saat session terbuka dan tertutup, serta keadaan busy/disabled selama aksi berjalan.
- Tracker P1 masih `[ ]` padahal P1 sudah ter-commit sebagai `3158dee`; tidak diubah karena di luar scope P2.
- P0 (discovery Yuzskill di AGENTS.md) masih pending, tidak menghalangi P2, dan tidak disentuh.

### Checkpoint P3 — 2026-09-05

**Phase/status:** P3 / IMPLEMENTATION COMPLETE, LIVE VERIFICATION PENDING. Dikerjakan dalam tiga increment (A Library, B kontrak+History, C CoverBuilder), masing-masing di-build hijau sebelum lanjut.

**File dipindah (5, isi tidak berubah selain namespace/using):**

- `shareLogic/LibraryScanner.cs` → `Library/LibraryScanner.cs` (ns `Module.Mangareader.Library`). Consumer satu-satunya `LibraryView`, yang sudah meng-import namespace itu.
- `shareLogic/ReadingHistoryStore.cs` → `History/ReadingHistoryStore.cs` (ns `Module.Mangareader.History`).
- `shareLogic/HistoryCardModel.cs` → `History/HistoryCardModel.cs` (ns `Module.Mangareader.History`).
- `shareLogic/CoverBuilderService.cs` → `CoverBuilder/CoverBuilderService.cs` (ns `Module.Mangareader.CoverBuilder`).
- `shareLogic/CoverSourceLoader.cs` → `CoverBuilder/CoverSourceLoader.cs` (ns `Module.Mangareader.CoverBuilder`), termasuk `CoverSourceResult`/`FetchedCoverResult`.

Bukti privat: grep seluruh repo menunjukkan tidak ada consumer di luar fitur pemilik untuk kelima tipe itu, dan tidak ada test project yang me-link filenya.

**File baru (2):**

- `History/ReadingHistory.cs` — owner recording: `Record`, `Changed`, `Read`.
- `tests/Module.Mangareader.Reader.Tests/ChapterRenderCacheIdentityTests.cs` — 2 test identitas cache (lihat Checks).

**File berubah (6):**

- `shareLogic/MangaReaderEvents.cs` — `LibraryChangedEventArgs.Titles` kini `IReadOnlyList<MangaTitle>`.
- `shareLogic/MangaCoverLoader.cs` — tambah `public const int PreviewPixelWidth = 320` (satu sumber untuk lebar decode semua consumer).
- `Library/LibraryView.xaml.cs` — `NotifyTitlesChanged(IReadOnlyList<MangaTitle>)`; menerbitkan domain titles pada jalur sukses, pasca-cover, dan gagal (`[]`); memakai `PreviewPixelWidth`.
- `History/HistoryView.xaml.cs` — `SetLibrary(IReadOnlyList<MangaTitle>)`, `UseHistory(ReadingHistory)`, cover milik sendiri, `Record` publik dihapus.
- `CoverBuilder/CoverBuilderView.xaml.cs` — `SetLibrary(IReadOnlyList<MangaTitle>)` membangun card model sendiri, preview cover dimuat fitur.
- `MangaReaderView.xaml.cs` — memiliki `ReadingHistory`, meneruskan chapter event ke owner itu.

**Keputusan ownership dan kontrak:**

- Kontrak lintas fitur kini domain-only. Yang tetap shared karena consumer nyatanya lebih dari satu: `MangaTitle`/`ChapterInfo` (semua fitur), `MangaTitleCardModel` (Library + CoverBuilder), `MangaCardPresentation` (Library + History), `MangaCoverLoader` + `ChapterRenderCache` (Library + History + CoverBuilder), `OpenChapterRequestedEventArgs`, `Archive/`. Tidak ada rename folder massal dan tidak ada kontrak internal yang dipromosikan ke `Citadel.Contract`.
- `HistoryCardModel` tidak lagi berlangganan `Cover` dari card model Library; ia punya `Cover` mutable sendiri yang diisi `MangaCoverLoader`. Karena tidak ada subscription tersisa, `IDisposable` dan `Dispose` dihapus — bukan menghilangkan cleanup, melainkan tidak ada lagi yang perlu dilepas. Efek sampingnya History tidak lagi menahan referensi ke card milik Library.
- **Cover refresh dijaga lewat identitas cache, bukan lewat tipe kartu.** Cover lama dipakai ulang hanya ketika `ChapterRenderCache.GetChapterFolder(chapter)` tidak berubah; folder itu diturunkan dari path + length + mtime file chapter. Jadi re-scan biasa dan `Record` tidak menyebabkan flicker, sementara chapter yang di-bake/diganti menghasilkan key baru dan cover-nya dimuat ulang. Ini invariant yang sebelumnya tidak di-test dan kini menjadi tumpuan correctness P3.
- Recording tidak lagi membutuhkan instance `HistoryTab`: composition root memiliki `ReadingHistory`, History screen hanya subscribe `Changed`. `Changed` sengaja synchronous — `ReaderWindow.OnActiveChapterChanged` memanggil `UpdateWindowTitle()` sebelum raise, jadi sudah pasti di UI thread; menambah Dispatcher guard hanya mengubah timing tanpa alasan.
- Cover History dimuat paralel (`MaxDegreeOfParallelism = 4`, pola dan stale-guard sama dengan `LibraryView.LoadCoversAsync`) hanya untuk kartu yang belum punya cover. Lebar decode sama dengan Library → cache hit, bukan decode kedua.
- CoverBuilder hanya memuat cover title terpilih, karena binding preview `SelectedItem.Cover` adalah satu-satunya consumer cover di layar itu; memuat semua N cover berarti men-decode yang tidak pernah ditampilkan. Load dipicu setelah `SetLibrary` memulihkan selection dan saat user mengganti pilihan, dan dilewati selama rebuild agar tidak start/cancel berulang.
- Card model di CoverBuilder dibangun sendiri dari domain titles, bukan meminjam instance Library: `DisplayMemberPath="Title"` butuh judul ternormalisasi dan preview butuh `Cover`, keduanya tidak ada di `MangaTitle`. Pemilihan title, source lokal/URL, urutan fetch-before-bake, hasil/error, dan `ArchiveReplacementTransaction` tidak diubah.
- **Library: tidak ada controller/state framework baru.** `LibraryScanPersistence` sudah menjadi owner aturan capture-attempt/persist-only-on-success dan tetap dipakai apa adanya; yang tersisa di view adalah state UI (busy, status, empty panel, cards) dan lifecycle CTS. Semua invariant yang diminta (captured attempt, field disabled saat scan, cancellation + stale-result guard, restore/auto-scan sekali, persist hanya setelah sukses, zero-title tetap eligible, gagal/cancel tidak menimpa path, folder unavailable tidak menghapus preferensi) tidak disentuh dan tetap pada pemiliknya.
- `LibraryView.Titles` (public, nol caller) sengaja dibiarkan: cleanup tidak terkait P3.

**Checks aktual:**

- `dotnet build module/mangareader/Module.Mangareader.csproj -c Release -p:CitizenRuntimeRoot=<temp>` dijalankan setelah tiap increment (3×): Build succeeded, 0 Warning, 0 Error. Deploy dialihkan ke temp karena `Citadel.Shell` masih berjalan; output Shell milik pengguna tidak disentuh, temp sudah dibersihkan.
- `Module.Mangareader.Library.Tests` 24/24; `Module.Mangareader.Reader.Tests` 97/97 (95 lama + 2 baru); `Module.Mangareader.Archive.Tests` 33/33. Total 154 passed, 0 failed, 0 skipped — run segar.
- Test csproj TIDAK perlu diubah: tidak ada file yang dipindah berada di Compile Include test manapun (diverifikasi grep pada semua csproj). `ChapterRenderCache.cs` dan `MangaLibrary.cs` sudah ter-link di Reader.Tests, jadi test baru tidak menambah include.
- Test baru memakai file temp nyata, tanpa mock: chapter tidak berubah → folder cache sama; chapter ditulis ulang (length berubah) → folder cache berbeda. Test kedua akan gagal seandainya identitas cache berhenti mengikuti isi file, yaitu tepat cara cover basi bisa bocor setelah bake.
- Grep memastikan tidak ada sisa `ShareLogic.<tipe yang dipindah>` maupun path `shareLogic/<file lama>`; `git diff --check` bersih.
- Tidak ada perubahan XAML, style, template, atau primitive `setting/Components/`, jadi approval boundary shared UI tidak tersentuh.

**Perbedaan perilaku kecil yang disengaja (dicatat, bukan disembunyikan):**

- Preview cover di Cover Builder kini dimuat async oleh fiturnya sendiri, jadi ada jeda beberapa milidetik (cache hit) setelah memilih title atau setelah scan; sebelumnya bitmap sudah tersedia karena diisi Library. Yang tampil tetap sama.
- History mengisi cover sendiri. Dengan carry-over berbasis key cache tidak ada flicker saat re-scan/Record, dan setelah bake cover benar-benar dimuat ulang.

**Pending/next:**

- LIVE VERIFICATION PENDING — build/test bukan bukti visual. Butuh WPF live dengan library disposable: scan sukses/empty/gagal/cancel → restart restore → buka chapter → pastikan History tercatat sebelum tab History dibuka → fetch/bake cover → refresh Library dan History serta konfirmasi cover BARU yang tampil, bukan cover lama.
- P4–P6 tidak disentuh. P0 masih pending.
- `.docs/PLAN-mangareader-downloader.md` ikut berubah di worktree selama P3 berjalan (reconcile Yuzskill bertanggal 2026-09-05, inspeksi HEAD `3158dee`), BUKAN oleh pekerjaan ini. Dibiarkan utuh sesuai batas "pertahankan perubahan user/agent lain".
- Worktree masih memuat perubahan P2 yang belum di-commit; keduanya dibiarkan tanpa commit sesuai instruksi.

### Checkpoint P4 — 2026-09-05

**Phase/status:** P4 / IMPLEMENTATION COMPLETE, LIVE VERIFICATION PENDING. Baseline aktual: HEAD `7cdc338` — P2 dan P3 sudah ter-commit dan worktree bersih sebelum P4 dimulai, jadi catatan "P2 belum di-commit" di Checkpoint P3 sudah tidak berlaku.

**File dipindah (1):**

- `Reader/Features/Drawer/ReaderDrawerContributions.cs` → `Reader/ReaderCore/ReaderDrawerContributions.cs`, namespace `Module.Mangareader` → `Module.Mangareader.ReaderCore` mengikuti konvensi folder ReaderCore. Isi kedua tipe tidak berubah; yang ditambah hanya doc comment yang menyatakan ownership. File lama dihapus, tidak ada wrapper atau salinan kedua.

**File berubah (3):**

- `Reader/Features/Drawer/ReaderDrawer.xaml` — tambah `xmlns:core`, dan `DataType` implicit DataTemplate menjadi `core:ReaderDrawerCardContribution`. Isi template (`ContentPresenter Content="{Binding Card}"`) dan layout tidak berubah.
- `Reader/Features/ChapterNavigation/ReaderChapterNavigation.cs` — tambah `using Module.Mangareader.ReaderCore;`. Ini satu-satunya dari tujuh consumer yang belum meng-import namespace itu; enam lainnya sudah.
- `tests/Module.Mangareader.Reader.Tests/Module.Mangareader.Reader.Tests.csproj` — Compile Include dan Link dipindah ke `Reader\ReaderCore\ReaderDrawerContributions.cs`.

**Keputusan ownership:**

- Bukti yang membenarkan pemindahan: grep menunjukkan `ReaderDrawer.xaml.cs` sebagai host TIDAK pernah memakai `ReaderDrawerCardContribution` maupun `ReaderDrawerCards` — ia hanya memakai `ReaderDrawerContribution` abstrak dari `ReaderFeatureContract.cs`. Ketujuh pemakai tipe konkrit adalah sibling: AutoScroll, Zoom, Dim, Pin, Reset, Fullscreen, ChapterNavigation. Jadi keduanya memang kontrak bersama Reader yang kebetulan tersimpan di internal satu fitur.
- Kontrak abstrak (`ReaderDrawerContribution`, `IReaderDrawerContributionProvider`, `IReaderDrawerContributionHost`) SENGAJA tetap di `Reader/ReaderFeatureContract.cs`. Memindahkannya ke ReaderCore akan mengganti namespace dua interface yang dipakai 7 fitur + Drawer + `ReaderFeatureHost` + test demi kosmetik — persis yang plan larang. Yang dihapus adalah kepemilikan Drawer atas kontrak bersama, bukan lokasi file kontrak utama.
- `ReaderDrawerCards` tetap `internal static` dan tetap hanya memakai `SettingCardStyle`/`SettingBodyStyle` melalui `SetResourceReference`. Tidak ada primitive, template, style, atau behavior baru; approval boundary shared UI tidak tersentuh.
- Drawer tetap hanya mengurutkan dan menampilkan: `ReaderFeatureHost` yang mengurutkan (`OrderBy(Order).ThenBy(Key)`, plus guard duplicate key) lalu memanggil `SetContributions`; Drawer menampilkannya lewat implicit DataTemplate. Ordering, guard, dan layout card tidak diubah.
- Guard "tepat satu Drawer contribution host" di `ReaderFeatureHost.cs` tidak disentuh; Drawer tetap required role dan tidak dijadikan optional.
- `ReaderDefaultFeatureCatalog` tetap satu-satunya registration point dan tidak berubah. Setelah P4, satu-satunya referensi ke kelas `ReaderDrawer` di luar folder Drawer adalah baris registrasi di catalog itu.
- `ReaderDrawerPolicy` tetap internal Drawer karena satu-satunya consumer adalah `ReaderDrawer.xaml.cs`.
- **`ChapterSurfaceModel` TIDAK dipindah, dengan bukti.** Consumer mapping-nya memang Reader-saja (ChapterLoadingFeature, ChapterPreloader, ChapterCoordinator, ReaderChapterNavigationHub, ReaderFeatureContract), jadi syarat plan terpenuhi. Tetapi ia satu grup kohesif dengan `ChapterLoadingContracts.cs` (`LoadedChapter`, `LoadedPage`, `PageRenderQuality`, `ChapterRenderRequest`) yang consumer-nya persis sama dan semuanya masih di `shareLogic`. Memindahkan hanya `ChapterSurfaceModel` memecah grup itu ke dua namespace tanpa mengurangi coupling apa pun, sedangkan memindahkan seluruh grup berarti "memindahkan seluruh shareLogic" yang dilarang batas fase ini.

**Checks aktual:**

- `dotnet build module/mangareader/Module.Mangareader.csproj -c Release -p:CitizenRuntimeRoot=<temp>` → Build succeeded, 0 Warning, 0 Error. Deploy dialihkan ke temp agar output Shell yang sedang dipakai tidak tersentuh; temp sudah dibersihkan. XAML WPF dikompilasi saat build, jadi `{x:Type core:ReaderDrawerCardContribution}` yang tidak resolve akan menggagalkan build — resolusi tipe template terbukti, tetapi itu BUKAN bukti tampilan.
- `dotnet test tests/Module.Mangareader.Reader.Tests -c Release` → 97/97 passed, 0 failed, 0 skipped (run segar). Suite ini yang relevan: `ReaderControllerTests` memuat 9 assertion `Assert.IsType<ReaderDrawerCardContribution>` untuk card tiap fitur, dan `ReaderFeatureHostTests` menguji guard satu-host, ordering, serta duplicate key.
- Library dan Archive tests tidak dijalankan ulang karena P4 tidak menyentuh satu pun file yang mereka link (diperiksa dari Compile Include csproj masing-masing).
- Grep memastikan tidak ada sisa referensi path lama di csproj/cs/md, dan tidak ada sibling yang masih membutuhkan file internal Drawer untuk mendefinisikan card.
- `git diff --check` bersih. Satu artifact build `*_wpftmp.csproj` sisa WPF markup-compile pass ditemukan untracked dan dihapus; bukan file sumber.
- Tidak ada test baru: pemindahan ini tidak mengubah perilaku apa pun dan behavior card/host sudah tercakup 97 test existing. Menambah test per class/getter dilarang instruksi fase ini.

**Pending/next:**

- LIVE VERIFICATION PENDING — belum ada bukti visual sama sekali. Perlu smoke native: buka/tutup Drawer, Pin, lalu tiap card control (Zoom, Dim, kecepatan Auto-scroll, Fullscreen, Reset, chapter picker) pada window normal; pastikan urutan card, layout, hover/focus, dan accessible name tidak berubah.
- P5 dan P6 tidak disentuh. P0 masih pending dan tidak menghalangi P4.
- Perubahan P4 belum di-commit sesuai instruksi.

Laporan akhir kepada user: ringkas perubahan ownership, kode redundant yang dihapus dan bukti tidak ada caller, hasil build/test aktual, status live yang jujur, serta sisa blocker. Jangan menyatakan goal selesai hanya karena token menipis atau semua file sudah dipindahkan.

## Prompt pembuka untuk agent pelaksana

> Implementasikan `.docs/PLAN-ownership-shared-ui-2026-09-05.md` di `C:\VSCODE\citadel`. Aktifkan Yuzskill sendiri, baca goal/scope/kontrak dan source terbaru, lalu kerjakan urut P0-P6 dengan checkpoint di dokumen yang sama. Fokus mengurangi coupling dan duplikasi; jangan rewrite aplikasi, menambah framework/primitive tanpa izin, atau membuat test suite berlebihan. Baca Context7 hanya untuk API relevan dengan versi project. Setelah compaction bangun ulang context dari skill, plan, git diff, dan owner aktif sebelum melanjutkan. Pertahankan semua behavior/data/lifecycle existing. Jangan commit/push/release tanpa instruksi user. Laporkan validation aktual dan live verification yang belum dilakukan.
