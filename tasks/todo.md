# CamoProf Add Profile — todo (kontrak puzzle-block, tasks/plan.md)

## Bongkar menara + adapter tipis

- [x] Task 1: Rewrite kontrak (tasks/plan.md) ke puzzle-block
- [ ] Task 2: Hapus `core/` (SessionHost/lease/CommandRegistry); pyhost.py
      dapat 3 method generik kecil (register_commands, add_lifecycle_hook,
      set_primary_page); revert guard navigate
- [ ] Task 3: Plugin enrollment.py tipis: buat E → arm → set primary →
      tutup R → navigasi latar; teardown buat F dulu baru tutup E;
      host/sess sebagai parameter (tanpa backlink tersimpan)
- [ ] Task 4: Port test plugin (26) + rewrite invariant + guard arsitektur
      ke kontrak baru
- [ ] Task 5: Full gates (Python 4 suite, solution, live smoke real
      browser) → commit → jalankan aplikasi untuk operator test

### Checkpoint
- [ ] test_pyhost (23) hijau tanpa perubahan
- [ ] Satu jendela terbukti di live smoke
- [ ] Operator login test lulus

## Live smoke operator (tetap berlaku, dari kontrak lama)

- [ ] Add Profile → login → dialog auto-close → row Active
- [ ] Restart app → Check Google → auto-relog tanpa prompt
- [ ] Cancel di tengah → row Unlinked, capture mati total
- [ ] Tutup browser di tengah → status jujur, secret dibuang
- [ ] Tidak ada password di log; hanya password.dat di credenz
- [ ] Satu jendela OS terbukti saat enrollment aktif
- [ ] Tidak ada proses pyhost/Camoufox yatim atau PROFILE_BUSY palsu

## Riwayat (jangan diulang)

- Refactor provider (da0779b) → enrollment feature (d4f689d) → 4 fix
  audit/live (c28f77d..9da973c) → menara plugin+lease
  (3c9f29e..f31843e) → **menara terbukti over-engineered: dibongkar**.
  Pelajaran: fitur kecil = adapter di atas blok yang ada; jelaskan
  dulu dalam bahasa awam sebelum menulis kode.
