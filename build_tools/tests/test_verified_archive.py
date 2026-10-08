"""Deterministic offline regression tests; never build or touch the real cache."""
import base64
import hashlib
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

sys.dont_write_bytecode = True
from verified_archive import prepare_archive, reparse


class VerifiedArchiveTests(unittest.TestCase):
    def setUp(self):
        task_root = Path(r"D:\Temp\agent\markdown-ru") if Path(r"D:\Temp").is_dir() else Path(tempfile.gettempdir()) / "markdown-ru"
        parent = task_root / "build-cache-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="fixtures-", dir=parent)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "fixture.zip"
        self.destination = self.root / "verified-hash" / "fixture"
        with zipfile.ZipFile(self.archive, "w") as bundle:
            for name, content in [("AnotherMarkdown.dll", b"fixture dll"), ("assets/nested/data.txt", b"fixture data")]:
                bundle.writestr(zipfile.ZipInfo(name, (2020, 1, 1, 0, 0, 0)), content)
        self.hash = hashlib.sha256(self.archive.read_bytes()).hexdigest()

    def prepare(self, sha256=None):
        return prepare_archive(self.root, self.archive, self.destination, sha256 or self.hash, "https://fixture.invalid/never-requested")

    def junction(self, name, target):
        link = self.root / name
        root, target = self.root.resolve(), target.resolve()
        self.assertEqual(str(root), os.path.commonpath([str(root), str(target)]))
        self.assertEqual(root, link.parent.resolve())
        if os.name == "nt":
            subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)
        else:
            link.symlink_to(target, target_is_directory=True)
        self.addCleanup(self.remove_link, link, target)
        self.assertTrue(reparse(link))
        return link

    def remove_link(self, link, expected_target):
        if not os.path.lexists(link):
            return
        root, target = self.root.resolve(), link.resolve()
        # Remove only the checked fixture link, never recursively follow it.
        self.assertEqual(expected_target, target)
        self.assertEqual(str(root), os.path.commonpath([str(root), str(target)]))
        self.assertEqual(root, link.parent.resolve())
        self.assertTrue(reparse(link))
        if os.name == "nt" and not link.is_symlink():
            os.rmdir(link)
        else:
            link.unlink()

    def test_verified_archive_is_reused_without_network(self):
        with mock.patch("urllib.request.urlopen", side_effect=AssertionError("Offline test attempted network")):
            self.prepare()
            stamp = (self.destination / "AnotherMarkdown.dll").stat().st_mtime_ns
            self.prepare()
            self.assertEqual(stamp, (self.destination / "AnotherMarkdown.dll").stat().st_mtime_ns)

    def test_wrong_cached_hash_is_rejected_before_extraction(self):
        with mock.patch("urllib.request.urlopen", side_effect=AssertionError("Must not replace a bad cached archive")):
            with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
                self.prepare("0" * 64)
        self.assertFalse(self.destination.exists())
        self.assertEqual(self.hash, hashlib.sha256(self.archive.read_bytes()).hexdigest())

    def test_cached_archive_is_verified_before_reusing_complete_extraction(self):
        self.prepare()
        self.archive.write_bytes(b"corrupted after first extraction")
        with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
            self.prepare()
        self.assertEqual(b"fixture dll", (self.destination / "AnotherMarkdown.dll").read_bytes())

    def test_wrong_download_is_not_cached_or_extracted(self):
        self.archive.unlink()
        with mock.patch("urllib.request.urlopen", return_value=io.BytesIO(b"wrong response")):
            with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
                self.prepare()
        self.assertFalse(self.archive.exists())
        self.assertFalse(self.destination.exists())
        self.assertEqual([], list(self.root.glob("*.download-*")))

    def test_correct_download_is_verified_then_published(self):
        payload = self.archive.read_bytes()
        self.archive.unlink()
        with mock.patch("urllib.request.urlopen", return_value=io.BytesIO(payload)):
            self.prepare()
        self.assertEqual(payload, self.archive.read_bytes())
        self.assertEqual(b"fixture data", (self.destination / "assets/nested/data.txt").read_bytes())
        self.assertEqual([], list(self.root.glob("*.download-*")))

    def test_official_sha512_is_also_required_when_locked(self):
        with self.assertRaisesRegex(ValueError, "SHA512 mismatch"):
            prepare_archive(self.root, self.archive, self.destination, self.hash, "https://fixture.invalid", base64.b64encode(bytes(64)).decode("ascii"))
        self.assertFalse(self.destination.exists())

    def test_incomplete_extraction_with_existing_dll_is_rebuilt(self):
        self.destination.mkdir(parents=True)
        (self.destination / "AnotherMarkdown.dll").write_bytes(b"fixture dll")
        self.prepare()
        self.assertEqual(b"fixture data", (self.destination / "assets/nested/data.txt").read_bytes())
        self.assertEqual([], list(self.destination.parent.glob("*.extract-*")))
        self.assertEqual([], list(self.destination.parent.glob("*.invalid-*")))

    def test_modified_extracted_file_is_restored_from_verified_archive(self):
        self.prepare()
        (self.destination / "AnotherMarkdown.dll").write_bytes(b"tampered dll")
        self.prepare()
        self.assertEqual(b"fixture dll", (self.destination / "AnotherMarkdown.dll").read_bytes())

    def test_corrupt_crc_cannot_publish_a_partial_extraction(self):
        payload = bytearray(self.archive.read_bytes())
        first = payload.index(b"PK\x01\x02")
        second = payload.index(b"PK\x01\x02", first + 4)
        payload[second + 16] ^= 1
        self.archive.write_bytes(payload)
        self.hash = hashlib.sha256(payload).hexdigest()
        with self.assertRaises(zipfile.BadZipFile):
            self.prepare()
        self.assertFalse(self.destination.exists())
        self.assertEqual([], list(self.destination.parent.glob("*.extract-*")))

    def test_zip_path_traversal_is_rejected(self):
        with zipfile.ZipFile(self.archive, "w") as bundle:
            bundle.writestr("../escaped.txt", b"must not escape")
        self.hash = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "Unsafe path"):
            self.prepare()
        self.assertFalse((self.root.parent / "escaped.txt").exists())

    def test_destination_outside_cache_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "outside CacheRoot"):
            prepare_archive(self.root, self.archive, self.root.parent / "outside", self.hash, "https://fixture.invalid")

    def test_destination_junction_preserves_link_and_sibling_sentinel(self):
        sibling = self.root / "sibling"
        sibling.mkdir()
        sentinel = sibling / "sentinel.txt"
        sentinel.write_bytes(b"do not replace or delete")
        link = self.junction("destination-link", sibling)
        with self.assertRaisesRegex(ValueError, "contains a link"):
            prepare_archive(self.root, self.archive, link, self.hash, "https://fixture.invalid")
        self.assertTrue(reparse(link))
        self.assertEqual(sibling.resolve(), link.resolve())
        self.assertEqual([sentinel], list(sibling.iterdir()))
        self.assertEqual(b"do not replace or delete", sentinel.read_bytes())

    def test_destination_parent_junction_preserves_sibling(self):
        sibling = self.root / "sibling"
        sibling.mkdir()
        sentinel = sibling / "sentinel.txt"
        sentinel.write_bytes(b"parent sentinel")
        link = self.junction("parent-link", sibling)
        with self.assertRaisesRegex(ValueError, "contains a link"):
            prepare_archive(self.root, self.archive, link / "new-extraction", self.hash, "https://fixture.invalid")
        self.assertTrue(reparse(link))
        self.assertEqual([sentinel], list(sibling.iterdir()))
        self.assertEqual(b"parent sentinel", sentinel.read_bytes())

    def test_archive_parent_junction_is_rejected_before_reading(self):
        store = self.root / "archive-store"
        store.mkdir()
        stored = store / "fixture.zip"
        payload = self.archive.read_bytes()
        stored.write_bytes(payload)
        link = self.junction("archive-link", store)
        with self.assertRaisesRegex(ValueError, "contains a link"):
            prepare_archive(self.root, link / stored.name, self.destination, self.hash, "https://fixture.invalid")
        self.assertTrue(reparse(link))
        self.assertEqual(payload, stored.read_bytes())
        self.assertFalse(self.destination.exists())

    def test_junction_before_parent_component_is_not_normalized_away(self):
        sibling = self.root / "sibling"
        sibling.mkdir()
        link = self.junction("parent-link", sibling)
        with self.assertRaisesRegex(ValueError, "contains a link"):
            prepare_archive(self.root, self.archive, link / ".." / "new-extraction", self.hash, "https://fixture.invalid")
        self.assertTrue(reparse(link))
        self.assertFalse((self.root / "new-extraction").exists())

    def test_archive_file_symlink_is_rejected_when_supported(self):
        link = self.root / "archive-symlink.zip"
        try:
            link.symlink_to(self.archive)
        except OSError as error:
            if os.name == "nt" and getattr(error, "winerror", None) == 1314:
                self.skipTest("File symlinks require Windows privileges; real junction cases run without them.")
            raise
        self.addCleanup(self.remove_link, link, self.archive.resolve())
        with self.assertRaisesRegex(ValueError, "contains a link"):
            prepare_archive(self.root, link, self.destination, self.hash, "https://fixture.invalid")
        self.assertTrue(link.is_symlink())
        self.assertEqual(self.hash, hashlib.sha256(self.archive.read_bytes()).hexdigest())
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=2))
