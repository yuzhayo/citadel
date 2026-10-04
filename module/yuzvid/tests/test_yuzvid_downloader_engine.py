"""Guard untuk engine unduh Yuzvid (port doodlib bagian unduh).

Menguji logika murni yang tidak butuh jaringan: pembagian byte-range harus
tanpa celah, tanpa tumpang tindih, dan presisi total. Ini menangkap regresi
split yang akan merusak file hasil tanpa terlihat di UI.

Jalankan dari C:\\VSCODE\\citadel:
  python -m unittest module.yuzvid.tests.test_yuzvid_downloader_engine -v
"""

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MODULE = os.path.dirname(_HERE)
_ENGINE_PATH = os.path.join(_MODULE, "Features", "Queue", "yuzvid_downloader", "engine.py")

# Muat engine.py by path: folder plugin tidak punya __init__ yang bisa diimpor
# bersih saat test (plugin.py jadi __init__ hanya setelah deploy).
_spec = importlib.util.spec_from_file_location("yuzvid_downloader_engine", _ENGINE_PATH)
engine = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(engine)


def _ranges_are_complete(size, min_part, max_parts, ranges):
    """Cek: tanpa celah, tanpa tumpang tindih, menutupi [0, size)."""
    if size <= 0:
        return ranges == []
    # urut & rapat
    expected_start = 0
    for start, end in ranges:
        if start != expected_start:
            return False
        if end < start:
            return False
        expected_start = end + 1
    return expected_start == size


class SplitToRangesTest(unittest.TestCase):
    def test_empty_and_zero(self):
        self.assertEqual([], engine.split_to_ranges(0, 256, 4))
        self.assertEqual([], engine.split_to_ranges(-5, 256, 4))

    def test_single_part_when_below_min(self):
        self.assertEqual([(0, 99)], engine.split_to_ranges(100, 256, 4))

    def test_exact_division(self):
        ranges = engine.split_to_ranges(1000, 100, 10)
        self.assertEqual(10, len(ranges))
        self.assertTrue(_ranges_are_complete(1000, 100, 10, ranges))

    def test_remainder_distributed_no_gap_no_overlap(self):
        for size in (1001, 1023, 4097, 999999, 1048577):
            for parts in (2, 3, 4, 7):
                ranges = engine.split_to_ranges(size, 1, parts)
                self.assertTrue(
                    _ranges_are_complete(size, 1, parts, ranges),
                    "size=%d parts=%d -> %r" % (size, parts, ranges))

    def test_covers_every_byte_exactly_once(self):
        size = 10_000
        ranges = engine.split_to_ranges(size, 256, 4)
        covered = []
        for start, end in ranges:
            covered.extend(range(start, end + 1))
        self.assertEqual(list(range(size)), covered)

    def test_part_count_capped_by_min_part_size(self):
        # File kecil dengan min_part besar -> jumlah part dibatasi ukuran.
        ranges = engine.split_to_ranges(500, 256, 16)
        self.assertEqual(2, len(ranges))  # ceil(500/256) = 2


class PartStateTest(unittest.TestCase):
    def test_downloaded_and_remaining(self):
        p = engine.PartState(index=0, start=100, end=200, current=150)
        self.assertEqual(50, p.downloaded)
        self.assertEqual(51, p.remaining)

    def test_completed_reports_zero_remaining(self):
        p = engine.PartState(index=0, start=0, end=10, current=10, completed=True)
        self.assertEqual(0, p.remaining)


if __name__ == "__main__":
    unittest.main()
