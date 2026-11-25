"""Exercise the Maker/native input boundary, not NuGet's integrity checks."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import prepare_maker_inputs as prepare
import published_app


class NativeInputsTests(unittest.TestCase):
    def test_public_app_provenance_and_catalog_are_recorded_for_both_modes(self):
        for host_only in (False, True):
            with self.subTest(host_only=host_only), tempfile.TemporaryDirectory() as temporary:
                output = Path(temporary) / "inputs"
                artifact = published_app.selection("osx-arm64")

                def app(directory):
                    directory.mkdir()
                    (directory / "app-input.json").write_text(json.dumps(dict(rid="osx-arm64", artifact=artifact)))

                def catalog(_, directory):
                    path = directory / "sunder-platform-contracts.v1.json"
                    path.write_bytes(b"test catalog")
                    return path

                packages = [dict(name="MakerFixture.nupkg")]
                with patch.object(prepare.published_app, "prepare", side_effect=app), \
                     patch.object(prepare.public_platform_catalog, "fetch", side_effect=catalog), \
                     patch.object(prepare.maker_public_feed, "prepare", return_value=packages) as maker:
                    prepare.prepare(output, host_only=host_only)
                    self.assertEqual(not host_only, maker.called)
                    with self.assertRaisesRegex(ValueError, "must be new"):
                        prepare.prepare(output, host_only=host_only)
                receipt = json.loads((output / "inputs.json").read_text())
                self.assertEqual(artifact, receipt["app"]["artifact"])
                self.assertEqual([] if host_only else packages, receipt["packages"])

    def test_rejected_app_input_cannot_emit_success_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "inputs"
            with patch.object(prepare.published_app, "prepare", side_effect=ValueError("invalid image")), \
                 patch.object(prepare.public_platform_catalog, "fetch"), \
                 self.assertRaisesRegex(ValueError, "invalid image"):
                prepare.prepare(output, host_only=True)
            self.assertTrue(output.is_dir())
            self.assertFalse((output / "inputs.json").exists())


if __name__ == "__main__":
    unittest.main()
