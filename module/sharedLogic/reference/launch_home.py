"""launch_home: buka rumah persisten yang SUDAH login.

Kebalikan dari human_login.py: human_login membuat rumah baru
(LO login sekali), launch_home memanggil rumah yang sudah ada
dan membukanya kembali dengan sesi yang tersimpan (cookie + device
trust ada di dalam folder user_data_dir tersebut).

Jalankan (dari mana saja):
    python launch_home.py                 # tampilkan menu pilih rumah
    python launch_home.py <name>          # langsung buka rumah tertentu
    python launch_home.py <name> --start URL

Alur: pilih rumah (menu/nama) -> pilih URL (default aman) -> buka.
Rumah: Credenz/google/profiles/<name>/
"""

import argparse
import asyncio
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = _HERE
for _ in range(4):
    _ROOT = os.path.dirname(_ROOT)


def profiles_parent():
    return os.path.join(_ROOT, "Credenz", "google", "profiles")


def list_profiles():
    parent = profiles_parent()
    if not os.path.isdir(parent):
        return []
    return sorted(
        d for d in os.listdir(parent)
        if os.path.isdir(os.path.join(parent, d))
    )


def profile_dir(name):
    return os.path.join(profiles_parent(), name)


def choose_profile():
    homes = list_profiles()
    if not homes:
        print(">> Belum ada rumah tersimpan di:", profiles_parent())
        sys.exit(1)

    print(">> Rumah tersimpan:")
    for i, h in enumerate(homes, 1):
        print(f"   {i}) {h}")
    print()

    raw = input("Pilih (nomor atau nama): ").strip()
    if raw == "":
        print(">> Dibatalkan.")
        sys.exit(1)

    if raw.isdigit():
        idx = int(raw) - 1
        if 0 <= idx < len(homes):
            return homes[idx]
        print(">> Nomor di luar jangkauan.")
        sys.exit(1)

    if raw in homes:
        return raw

    print(">> Rumah tidak ada:", raw)
    print(">> Yang ada:", ", ".join(homes))
    sys.exit(1)


async def main():
    ap = argparse.ArgumentParser(prog="launch_home")
    ap.add_argument("name", nargs="?", default=None,
                    help="nama profil (kosong = tampilkan menu)")
    ap.add_argument("--start", default=None,
                    help="halaman pembuka (default myaccount.google.com)")
    args = ap.parse_args()

    if args.name:
        home = profile_dir(args.name)
        if not os.path.isdir(home):
            print(">> Rumah tidak ada:", home)
            print(">> Yang ada:", ", ".join(list_profiles()))
            sys.exit(1)
    else:
        args.name = choose_profile()
        home = profile_dir(args.name)

    default_url = "https://myaccount.google.com/"
    if args.start:
        start = args.start
    else:
        typed = input(f"Start URL (default {default_url}): ").strip()
        start = typed or default_url

    from camoufox.async_api import AsyncCamoufox

    print("=" * 62)
    print("YUZZENI - buka rumah tersimpan")
    print("Rumah :", home)
    print("Buka  :", start)
    print("=" * 62)

    async with AsyncCamoufox(
        persistent_context=True,
        user_data_dir=home,
        headless=False,
        humanize=True,
        os="windows",
        disable_coop=True,
        i_know_what_im_doing=True,
        config={"forceScopeAccess": True},
    ) as context:
        page = context.pages[0] if context.pages else await context.new_page()
        await page.goto(start, wait_until="domcontentloaded")
        await page.wait_for_timeout(4000)

        url = (page.url or "").lower()
        if "accounts.google.com" in url and "myaccount" not in url:
            print(">> Session habis - Google lempar ke signin. Login ulang di jendela.")
        else:
            print(">> Session hidup! Google mengenal rumah ini:", page.url)

        print(">> Browser terbuka. Tekan ENTER di sini untuk menutup.")
        await asyncio.to_thread(input, "")

    print("\nRumah ditutup:", home)


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(main()))
    except KeyboardInterrupt:
        print("\nDibatalkan - rumah tetap tersimpan seadanya.")
