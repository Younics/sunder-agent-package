"""A downloaded executable handoff must bind its source, platform and complete bytes."""
import os
from pathlib import Path
import platform
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(SCRIPTS / "ci"), str(SCRIPTS / "release")]
import agent_ci
import core_tools


class CoreToolsTests(unittest.TestCase):
    def test_handoff_rejects_changed_missing_extra_and_foreign_tools(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(agent_ci, "head", return_value="a" * 40):
            root = Path(temporary)
            for name in (*core_tools.inputs.TOOL_FILES, core_tools.DRIVER):
                (root / name).write_bytes(b"fixture tool bytes")
            value = dict(schemaVersion=1, coreCommit="a" * 40, platform=sys.platform,
                         architecture=platform.machine().lower(), files=core_tools.files(root))
            manifest = root / core_tools.MANIFEST
            manifest.write_bytes(core_tools.inputs.canonical(value))
            self.assertEqual(root.resolve(), core_tools.verify("core", root))
            tool = root / core_tools.DRIVER
            original = tool.read_bytes()
            for mutation in (lambda: tool.write_bytes(b"changed"), tool.unlink,
                             lambda: (root / "unexpected.dll").write_bytes(b"extra")):
                with self.subTest(mutation=mutation):
                    mutation()
                    with self.assertRaisesRegex(ValueError, "bytes changed|incomplete"):
                        core_tools.verify("core", root)
                    tool.write_bytes(original)
                    (root / "unexpected.dll").unlink(missing_ok=True)
            for field in ("coreCommit", "platform", "architecture"):
                with self.subTest(field=field):
                    manifest.write_bytes(core_tools.inputs.canonical({**value, field: "foreign"}))
                    with self.assertRaises(ValueError):
                        core_tools.verify("core", root)
            manifest.write_bytes(core_tools.inputs.canonical(value))
            if os.name != "nt":
                tool.unlink()
                tool.symlink_to(root / "Sunder.Sdk.dll")
                with self.assertRaisesRegex(ValueError, "Linked"):
                    core_tools.verify("core", root)


if __name__ == "__main__":
    unittest.main()
