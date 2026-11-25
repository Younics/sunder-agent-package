"""The shipped distribution, rather than a source build, owns the test boundary."""
import hashlib
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile, ZipInfo

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import published_app as app


class PublishedAppTests(unittest.TestCase):
    def test_wrong_distribution_bytes_never_reach_the_platform_extractor(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / "native"
            rid = "linux-arm64"
            approved = dict(app.selection(rid), size=4, sha256=hashlib.sha256(b"good").hexdigest())

            def download(*_args, **_kwargs):
                (target / approved["name"]).write_bytes(b"bad!")

            with patch.object(app, "current_rid", return_value=rid), \
                 patch.object(app, "selection", return_value=approved), \
                 patch.object(app.subprocess, "run", side_effect=download) as command, \
                 self.assertRaisesRegex(ValueError, "differs from the reviewed publication"):
                app.prepare(target)
            command.assert_called_once()
            self.assertFalse((target / "app-input.json").exists())
            self.assertEqual(b"bad!", (target / approved["name"]).read_bytes())

    def test_velopack_projects_the_app_and_its_bundled_host_without_installing(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "full.nupkg"
            host = "RuntimeHost/RuntimeHost/Sunder.Runtime.Host.dll"
            with ZipFile(archive, "w") as package:
                package.writestr("lib/app/Sunder.App.dll", b"shipped App")
                package.writestr("lib/app/" + host, b"bundled Host")
                package.writestr("other/update.exe", b"not part of the payload")
            app.extract_full(archive, root / "app")
            self.assertEqual(b"shipped App", (root / "app/Sunder.App.dll").read_bytes())
            self.assertEqual(b"bundled Host", (root / "app" / host).read_bytes())
            self.assertFalse((root / "app/other").exists())

    def test_ambiguous_or_linked_windows_payload_cannot_create_partial_projection(self):
        for path, mode in (("sunder.App.dll", stat.S_IFREG), ("Linked.dll", stat.S_IFLNK)):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                archive = root / "full.nupkg"
                with ZipFile(archive, "w") as package:
                    package.writestr("lib/app/Sunder.App.dll", b"published")
                    member = ZipInfo("lib/app/" + path)
                    member.create_system = 3
                    member.external_attr = (mode | 0o644) << 16
                    package.writestr(member, b"ambiguous")
                with self.assertRaises(ValueError):
                    app.extract_full(archive, root / "app")
                self.assertFalse((root / "app").exists())


if __name__ == "__main__":
    unittest.main()
