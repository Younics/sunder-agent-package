"""Offline preparation checks; these do not execute a native qualification."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import recovery_inputs_2_0_0 as recovery


class RecoveryInputTests(unittest.TestCase):
    def setUp(self):
        workspace = tempfile.TemporaryDirectory()
        self.addCleanup(workspace.cleanup)
        self.root = Path(workspace.name).resolve()

    def write(self, name, data):
        path = self.root / name
        path.write_bytes(data)
        return path

    def test_review_inventory_cannot_be_changed_into_an_approval(self):
        path = self.write("inventory.json", b'{"approved":false,"testExecuted":false,"publishable":false}')
        with patch.object(recovery, "INVENTORY_SHA256", recovery.digest(path.read_bytes())):
            self.assertFalse(recovery.read_inventory(path)["publishable"])
            path.write_bytes(b'{"approved":true,"testExecuted":false,"publishable":true}')
            with self.assertRaisesRegex(ValueError, "pinned Agent"):
                recovery.read_inventory(path)

    def test_same_size_changed_archive_fails_hash_before_use(self):
        path = self.write("owner.2.0.0.sunderpkg", b"same")
        sha = recovery.digest(b"same")
        recovery.checked_file(path, 4, sha)
        path.write_bytes(b"fake")
        with self.assertRaisesRegex(ValueError, "pinned size/hash"):
            recovery.checked_file(path, 4, sha)

    def test_source_requires_its_exact_git_tree_and_clean_checkout(self):
        self.write("anchor", b"exact")
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        recovery.git(self.root, "add", "anchor")
        recovery.git(self.root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                     "commit", "-qm", "Original source")
        commit, tree = recovery.git(self.root, "rev-parse", "HEAD", "HEAD^{tree}").splitlines()
        self.assertEqual(self.root, recovery.checkout(self.root, commit, tree))
        with self.assertRaisesRegex(ValueError, "exact clean checkout"):
            recovery.checkout(self.root, commit, "c" * 40)
        self.write("anchor", b"dirty")
        with self.assertRaisesRegex(ValueError, "exact clean checkout"):
            recovery.checkout(self.root, commit, tree)

    def test_maker_preparation_rejects_wrong_rid_artifact_and_catalog_bytes(self):
        commit = "b" * 40
        original_catalog = b'{"original":"catalog"}'
        rows = []
        inputs = dict(schemaVersion=2, coreCommit=commit, workingTreeClean=True, packages=rows,
                      platformCatalog=dict(path=recovery.CATALOG, size=len(original_catalog),
                                           sha256=recovery.digest(original_catalog)))
        files = {}
        for package in sorted(recovery.NUGET_IDS):
            name, data = package + ".1.0.0.nupkg", package.encode()
            files["maker-inputs/feed/" + name] = data
            rows.append(dict(packageId=package, version="1.0.0", name=name, size=len(data), sha256=recovery.digest(data)))
        files["maker-inputs/inputs.json"] = json.dumps(inputs).encode()

        def bundle(path, catalog):
            with ZipFile(path, "x") as archive:
                for name, data in files.items():
                    archive.writestr(name, data)
                archive.writestr("maker-inputs/" + recovery.CATALOG, catalog)
            return dict(zipSize=path.stat().st_size,
                        zipDigest="sha256:" + recovery.digest(path.read_bytes()),
                        makerInputsDigest=recovery.digest(files["maker-inputs/inputs.json"]),
                        catalogSha256=recovery.digest(original_catalog), feed=rows)

        pinned_path = self.root / "intel.zip"
        pin = bundle(pinned_path, original_catalog)
        build = dict(commit=commit, platformCatalog=dict(sha256=pin["catalogSha256"]))
        feed, catalog = recovery.maker_feed(pinned_path, pin, build)
        self.assertEqual((6, original_catalog), (len(feed), catalog))
        wrong_lane = self.root / "another-rid.zip"
        wrong_pin = bundle(wrong_lane, original_catalog.replace(b"catalog", b"changed"))
        with self.assertRaisesRegex(ValueError, "pinned size/hash"):
            recovery.maker_feed(wrong_lane, pin, build)
        with self.assertRaisesRegex(ValueError, "catalog differs"):
            recovery.maker_feed(wrong_lane, wrong_pin, build)

    def test_windows_report_rejects_replaced_duplicate_and_skipped_cases(self):
        def fixture(name, duplicate=False, skipped=False):
            case = "WindowsCase"
            definitions = "".join(f'<UnitTest id="{n}"><TestMethod className="{recovery.WINDOWS_CLASS}" '
                                  f'name="{case}"/></UnitTest>' for n in range(34))
            results = "".join(f'<UnitTestResult testId="{0 if duplicate and n == 33 else n}" executionId="{n}" '
                              f'testName="{recovery.WINDOWS_CLASS}.{case}({n})" '
                              f'outcome="{"NotExecuted" if skipped and n == 33 else "Passed"}"/>' for n in range(34))
            raw = f"<TestRun><Results>{results}</Results><TestDefinitions>{definitions}</TestDefinitions></TestRun>".encode()
            path = self.root / name
            with ZipFile(path, "x") as archive:
                archive.writestr(recovery.WINDOWS_TRX, raw)
            return path, dict(size=path.stat().st_size,
                              zipDigest="sha256:" + recovery.digest(path.read_bytes()),
                              originalTrxSha256=recovery.digest(raw), nativeExecutionCount=1,
                              cases=dict(passed=34, failed=0, skipped=0))

        original, pin = fixture("original.zip")
        recovery.windows_report(original, pin)
        for name, duplicate, skipped in (("duplicate.zip", True, False), ("skipped.zip", False, True)):
            with self.subTest(name=name):
                invalid, changed = fixture(name, duplicate, skipped)
                with self.assertRaisesRegex(ValueError, "pinned size/hash"):
                    recovery.windows_report(invalid, pin)
                with self.assertRaisesRegex(ValueError, "34 original"):
                    recovery.windows_report(invalid, changed)


if __name__ == "__main__":
    unittest.main()
