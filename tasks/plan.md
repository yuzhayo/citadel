# Add Profile — kontrak final (puzzle-block)

## Prinsip

Fitur = **adapter tipis yang merangkai blok generik yang sudah ada**.
Satu jendela sejak awal. Blok baru cuma satu. Menara infrastruktur
dilarangan: kalau sesuatu bisa dirangkai dari yang ada, jangan dibangun.

## Blok yang dipakai (sudah ada, TIDAK diubah)

| Blok | Tugas |
|---|---|
| `session.open` (pyhost) | buka browser + satu jendela |
| helper Google `providers/google/` | validasi origin, deteksi email, deteksi login |
| pola `google.inspect`/`relogin` | plugin menerima dict session dari handler pyhost |
| `GoogleCredentialStore` (C#) | simpan password DPAPI |
| shared `SettingDialog`/`SettingButton` | dialog |

## Blok baru (SATU-SATUNYA yang ditulis untuk fitur ini)

`password_capture.py` — listener page: dipasang sebelum navigasi,
menerima nilai hanya dari field password Google (host tepat
accounts.google.com), mati bersama page-nya.

## Alur (satu jendela dari awal sampai akhir)

```text
Launcher -> AddProfileFeature.ExecuteAsync          (kontrak tunggal)
  -> session.open (blok lama; page resident R)
  -> camoprof.add_profile.start
       1. buat page E (ctx.new_page — pola yang terbukti)
       2. pasang listener di E              ← arm SEBELUM navigasi
       3. sess["page"] = E  (via helper kecil host.set_primary_page)
       4. tutup R (E sudah hidup — context tak pernah kehabisan page)
       5. navigasi E ke halaman login Google (task latar, start kembali
          segera; cancel tidak pernah mengantri di belakang goto)
  -> status (poll 500ms) — state machine dengan helper Google lama
  -> finish (sekali saja) -> DPAPI save -> dialog tutup
  teardown (finish/cancel/expiry/gagal): FLOW BERAKHIR — page E
       ditutup (listener mati bersamanya), session dijatuhkan,
       browser milik flow tutup. Tidak ada page pengganti.
```

## Batas keamanan (tetap penuh, ini kebijakan bukan infrastruktur)

- capture hanya aktif karena start eksplisit; origin+field divalidasi
  dua lapis (JS event-time dan Python);
- password hidup di satu variabel, dibuang saat consume/teardown,
  tidak pernah masuk log/status/UI; satu-satunya penyeberangan:
  respons finish, sekali, langsung disimpan DPAPI;
- secret mati bersama session: hook lifecycle (list kecil generik)
  dipanggil dari semua jalur kematian session di _drop_session;
- salah akun = penolakan terminal; passkey = jujur tanpa password;
  kosongkan field = kandidat dibuang.

## Yang DIBONGKAR (menara buatan refactor sebelumnya)

- `core/session_host.py` (SessionHost, SessionLease, claim_primary,
  rotate_primary, owner token, guard SESSION_BUSY)
- `core/command_registry.py` (CommandRegistry)
- semua mekanisme claim/lease/rotasi di plugin

Diganti: `host.register_commands()`, `host.add_lifecycle_hook()`,
`host.set_primary_page()` — tiga method kecil generik di pyhost.py
mengikuti idiom proyek (handler menerima dict session seperti
google.inspect/relogin). Plugin menerima `host`/`sess` sebagai
PARAMETER fungsi — tidak menyimpan backlink.

## Flow END (keputusan operator 2026-09-03)

Setelah login sukses (atau cancel/gagal/expiry), flow TAMAT: browser
yang dibuka flow ditutup. Finish/cancel MENYERAHKAN hasil dalam
milidetik (secret dibuang + registry dilepas sinkron) — kematian
browser berjalan sebagai task latar; respons command tidak pernah
menunggu shutdown browser (yang membuat dialog terpaku di "saving
credential…" berdetik-detik). Tidak ada page baru, tidak ada jendela
menganggur, tidak ada blank page. Listener mati bersama browser.
Profile persist menyimpan login — Launch/Check berikutnya terbuka
dalam keadaan sudah login. C# membersihkan registry lokalnya di
finally (cancel + close, keduanya idempotent).

## Definition of Done

- satu klik Add Profile = satu jendela Camoufox, halaman login Google.
- login selesai -> dialog tutup sendiri -> row Active -> restart ->
  Check Google auto-relog.
- suite lama (test_pyhost 23) hijau tanpa perubahan; suite plugin +
  invariant + arsitektur hijau mengikuti kontrak ini; live smoke
  real-browser PASS; operator login test lulus.
