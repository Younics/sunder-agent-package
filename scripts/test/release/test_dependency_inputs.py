"""Automatic dependency discovery; real synthetic archives, no remote/build writes."""
from pathlib import Path
import runpy
import shutil
import sys
import unittest
from unittest.mock import patch
import zipfile

RELEASE = Path(__file__).resolve().parents[2] / "release"
sys.path.insert(0, str(RELEASE))
import dependency_inputs as d

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class DependencyInputTests(unittest.TestCase):
    def setUp(self):
        fixture_setup(self)
        self.output = self.root / "resolved"
        self.publications = {d.AGENT: [self.publication(self.archive, 2)]}

    def publication(self, path, identifier):
        return dict(id=identifier, draft=False, tag_name=f"fixture/v{identifier}.0.0", assets=[dict(
            id=identifier + 100, name=path.name, size=path.stat().st_size, digest="sha256:" + d.r.file_sha(path), state="uploaded")])

    def resolve(self, *, allow_missing=False):
        def download(asset, destination, maximum=None):
            shutil.copyfile(self.bundle / asset["name"], destination)
        with patch.object(d, "releases", side_effect=lambda repo: self.publications[repo]), \
             patch.object(d.inputs, "download", side_effect=download), patch.object(d.r, "git", return_value=self.source_sha.encode()), \
             patch.object(d.public_platform_catalog, "download", return_value=self.catalog_path.read_bytes()):
            return d.resolve(self.source, self.output, allow_missing=allow_missing)

    def test_published_dependencies_and_identical_coordinator_copies_need_no_seeds(self):
        duplicate = self.publication(self.archive, 3)
        self.publications[d.AGENT].append(duplicate)
        found = d.locate(d.AGENT, list(reversed(self.publications[d.AGENT])), self.archive.name)
        self.assertEqual(2, found[1]["releaseId"])
        before = d.inputs.source_snapshot(self.source)
        value = self.resolve()
        self.assertEqual(before, d.inputs.source_snapshot(self.source))
        self.assertEqual(self.source_sha, value["sourceCommit"])
        self.assertEqual([d.record(self.archive)], value["candidates"])
        self.assertEqual(d.record(self.catalog_path), value["platformCatalog"])
        self.assertEqual(value, d.r.load(self.output / "ci-inputs.json"))

    def test_unpublished_owner_waits_for_six_native_candidate_before_consumer_admission(self):
        self.publications[d.AGENT] = []
        before = d.inputs.source_snapshot(self.source)
        pending = self.resolve(allow_missing=True)
        self.assertEqual(["younics.ai.agent"], pending["pending"])
        self.assertFalse((self.output / "ci-inputs.json").exists())
        with patch.object(d.r, "git", return_value=self.source_sha.encode()):
            d.complete(self.source, self.output, self.bundle)
        self.assertFalse((self.output / "preparation.json").exists())
        self.assertEqual(before, d.inputs.source_snapshot(self.source))
        d.inputs.verify_selected_files(self.source, [self.output / self.archive.name], self.output / self.catalog_path.name)

    def test_cold_acceptance_inventory_is_generated_from_the_same_automatic_inputs(self):
        with patch.object(d, "releases", side_effect=lambda repo: self.publications[repo]), \
             patch.object(d.inputs, "download", side_effect=lambda asset, path, maximum=None: shutil.copyfile(self.bundle / asset["name"], path)), \
             patch.object(d.r, "git", return_value=self.source_sha.encode()), \
             patch.object(d.public_platform_catalog, "download", return_value=self.catalog_path.read_bytes()), \
             patch.object(d.inputs.subprocess, "check_output", return_value=self.source_sha):
            value = d.inputs.fetch(self.source, self.output, self.source_sha)
        self.assertEqual(value, d.inputs.verify(self.source, self.output, d.r.file_sha(self.output / d.inputs.MANIFEST), self.source_sha))
        self.assertFalse((self.output / "ci-inputs.json").exists())

    def test_published_same_version_with_another_content_identity_is_never_rebuilt_or_reselected(self):
        with zipfile.ZipFile(self.archive) as archive:
            entries = {name: archive.read(name) for name in archive.namelist()}
        entries["manifest/sunder-package.json"] += b"\n"
        with zipfile.ZipFile(self.archive, "w") as archive:
            for name, data in entries.items(): archive.writestr(name, data)
        self.publications[d.AGENT] = [self.publication(self.archive, 2)]
        before = d.inputs.source_snapshot(self.source)
        with self.assertRaisesRegex(d.r.Invalid, "exact committed package selection"):
            self.resolve()
        self.assertEqual(before, d.inputs.source_snapshot(self.source))
        self.assertFalse((self.output / "ci-inputs.json").exists())

    def test_mismatched_seed_never_reaches_a_downstream_seed_or_emits_prepared_inputs(self):
        self.publications[d.AGENT] = []
        self.resolve(allow_missing=True)
        before = d.inputs.source_snapshot(self.source)
        with zipfile.ZipFile(self.archive) as archive:
            entries = {name: archive.read(name) for name in archive.namelist()}
        entries["manifest/sunder-package.json"] += b"\n"
        with zipfile.ZipFile(self.archive, "w") as archive:
            for name, data in entries.items(): archive.writestr(name, data)
        with patch.object(d.r, "git", return_value=self.source_sha.encode()), \
             self.assertRaisesRegex(d.r.Invalid, "exact committed package selection") as failure:
            d.complete(self.source, self.output, self.bundle)
        self.assertIn("expected (version, content identity, public inventory)=", str(failure.exception))
        self.assertIn("; actual=", str(failure.exception))
        self.assertEqual(before, d.inputs.source_snapshot(self.source))
        self.assertFalse((self.output / "ci-inputs.json").exists())

    def test_conflicting_publications_and_inconsistent_locks_fail_before_bootstrap(self):
        duplicate = self.publication(self.archive, 3)
        duplicate["assets"][0]["digest"] = "sha256:" + "0" * 64
        with self.assertRaisesRegex(d.r.Invalid, "Conflicting dependency publications"):
            d.locate(d.AGENT, [self.publications[d.AGENT][0], duplicate], self.archive.name)
        path = self.source / Path(self.rows[-1]["projectPath"]).parent / "sunder-contracts.lock.json"
        lock = d.r.load(path)
        lock["packageArtifacts"]["younics.ai.agent"]["selectedPackageContentIdentity"] = "0" * 64
        path.write_bytes(d.r.canonical(lock))
        with self.assertRaisesRegex(d.r.Invalid, "Conflicting exact dependency selections"):
            self.resolve()
        self.assertFalse(self.output.exists())

    def test_absent_dependency_cannot_use_another_authored_version_or_a_cycle(self):
        self.publications[d.AGENT] = []
        project = self.source / self.rows[0]["projectPath"]
        project.write_text('<Project><PropertyGroup><Version>3.0.0</Version></PropertyGroup></Project>')
        with self.assertRaisesRegex(d.r.Invalid, "source cannot substitute another version"):
            self.resolve()

    def test_public_catalog_requires_the_exact_authored_release_and_rejects_wrong_bytes(self):
        self.output.mkdir()
        with patch.object(d.public_platform_catalog, "download", return_value=b"wrong catalog") as network, \
             self.assertRaisesRegex(d.r.Invalid, "authored platform selection"):
            d.public_platform_catalog.fetch(self.source, self.output)
        version = d.r.load(self.catalog_path)["releaseVersion"]
        network.assert_called_once_with(f"https://hub.sunderapp.io/api/v1/platform-contracts/sunder.core/releases/{version}/catalog")
        self.assertFalse((self.output / self.catalog_path.name).exists())

if __name__ == "__main__": unittest.main()
