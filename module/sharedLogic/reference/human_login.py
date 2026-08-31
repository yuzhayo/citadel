"""human_login: rumah persisten untuk akun veteran — LO login SENDIRI.

Pola anti-DBSC: Google percaya perangkat yang membangun kepercayaan
sendiri. Jadi setiap akun existing punya rumah tetap:
    Credenz/google/profiles/<name>/
LO login manual sekali di jendela Camoufox asli; seluruh jejak sesi
(cookie + storage + device trust) menetap di folder itu. Modul mana pun
kemudian memanggil rumah yang sama dan bangun sudah-login.

Jalankan (dari mana saja):
    python human_login.py <name> [--start URL]

Rumah TIDAK pernah pindah; tidak ada transfer cookie.
"""

import argparse
import asyncio
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = _HERE
for _ in range(4):
    _ROOT = os.path.dirname(_ROOT)

CHECK_URL = "https://myaccount.google.com/"
SIGNIN_MARKER = "accounts.google.com"


def profile_dir(name):
    return os.path.join(_ROOT, "Credenz", "google", "profiles", name)


async def main():
    ap = argparse.ArgumentParser(prog="human_login")
    ap.add_argument("name", help="nama profil, mis. dhepil-main")
    ap.add_argument("--start", default="https://www.google.com/",
                    help="halaman pembuka (default google.com)")
    args = ap.parse_args()

    home = profile_dir(args.name)
    os.makedirs(home, exist_ok=True)

    from camoufox.async_api import AsyncCamoufox  # lazim: berat

    print("=" * 62)
    print("RUMAH BARU :", home)
    print("Membuka Camoufox asli (jendela tampil)...")
    print("Login seperti biasa di jendela itu.")
    print("=" * 62)

    async with AsyncCamoufox(
            persistent_context=True,
            user_data_dir=home,
            headless=False,
            humanize=True,
            os="windows",
            disable_coop=True,
            i_know_what_im_doing=True,
            config={"forceScopeAccess": True}) as context:

        page = context.pages[0] if context.pages \
            else await context.new_page()
        await page.goto(args.start, wait_until="domcontentloaded")

        while True:
            await asyncio.to_thread(input,
                                    "\nSelesai login? Tekan ENTER di sini"
                                    " untuk verifikasi (Ctrl+C batal): ")
            try:
                await page.goto(CHECK_URL, wait_until="domcontentloaded",
                                timeout=45000)
            except Exception:
                print(">> Jendela ditutup lebih awal - tidak apa-apa.")
                break
            await page.wait_for_timeout(4000)
            url = (page.url or "").lower()
            if SIGNIN_MARKER in url and "myaccount" not in url:
                print(">> Belum masuk (Google lempar ke signin). "
                      "Coba lagi di jendela browser.")
                try:
                    await page.goto(args.start,
                                    wait_until="domcontentloaded")
                except Exception:
                    break
                continue
            print(">> HIDUP! Google mengenal rumah ini:", page.url)
            break

        state_path = os.path.join(home, "_session_state.json")
        try:
            await context.storage_state(path=state_path)
            print(">> Kunci sesi dicetak:", state_path)
        except Exception:
            print(">> Browser sudah ditutup - kunci akan dicetak"
                  " otomatis saat summon pertama.")

    print("\nRumah tersimpan:", home)
    print("Modul memanggil lewat summon_home(name) -"
          " kunci dipasang ulang otomatis setiap kali.")


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(main()))
    except KeyboardInterrupt:
        print("\nDibatalkan - rumah tetap tersimpan seadanya.")
