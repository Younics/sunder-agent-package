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
        self.core = self.root / "sunder-core"
        self.core.mkdir()
        self.output = self.root / "resolved"
        self.publications = {d.CORE: [self.publication(self.catalog_path, 1)], d.AGENT: [self.publication(self.archive, 2)]}

    def publication(self, path, identifier):
        return dict(id=identifier, draft=False, tag_name=f"fixture/v{identifier}.0.0", assets=[dict(
            id=identifier + 100, name=path.name, size=path.stat().st_size, digest="sha256:" + d.r.file_sha(path), state="uploaded")])

    def resolve(self, *, allow_missing=False):
        def download(asset, destination, maximum=None):
            shutil.copyfile(self.bundle / asset["name"], destination)
        with patch.object(d, "releases", side_effect=lambda repo: self.publications[repo]), \
             patch.object(d.inputs, "download", side_effect=download), patch.object(d.r, "git", return_value=self.core_sha.encode()):
            return d.resolve(self.source, self.core, self.output, allow_missing=allow_missing)

    def test_published_dependencies_and_identical_coordinator_copies_need_no_seeds(self):
        duplicate = self.publication(self.archive, 3)
        self.publications[d.AGENT].append(duplicate)
        found = d.locate(d.AGENT, list(reversed(self.publications[d.AGENT])), self.archive.name)
        self.assertEqual(2, found[1]["releaseId"])
        before = d.inputs.source_snapshot(self.source)
        with patch.object(d, "run") as seed:
            value = self.resolve()
        seed.assert_not_called()
        self.assertEqual(before, d.inputs.source_snapshot(self.source))
        self.assertEqual(2, value["schemaVersion"])
        self.assertEqual([d.record(self.archive)], value["candidates"])
        self.assertEqual(d.record(self.catalog_path), value["platformCatalog"])
        self.assertEqual(value, d.r.load(self.output / "ci-inputs.json"))

    def test_unpublished_owner_waits_for_six_native_candidate_before_consumer_admission(self):
        self.publications[d.AGENT] = []
        before = d.inputs.source_snapshot(self.source)
        with patch.object(d, "run", side_effect=AssertionError("Dependency discovery must not build")) as build:
            pending = self.resolve(allow_missing=True)
            self.assertEqual(["younics.ai.agent"], pending["pending"])
            self.assertFalse((self.output / "ci-inputs.json").exists())
            with patch.object(d.r, "git", return_value=self.core_sha.encode()):
                d.complete(self.source, self.core, self.output, self.bundle)
        build.assert_not_called()
        self.assertFalse((self.output / "preparation.json").exists())
        self.assertEqual(before, d.inputs.source_snapshot(self.source))
        d.inputs.verify_selected_files(self.source, [self.output / self.archive.name], self.output / self.catalog_path.name)

    def test_cold_acceptance_inventory_is_generated_from_the_same_automatic_inputs(self):
        with patch.object(d, "releases", side_effect=lambda repo: self.publications[repo]), \
             patch.object(d.inputs, "download", side_effect=lambda asset, path, maximum=None: shutil.copyfile(self.bundle / asset["name"], path)), \
             patch.object(d.r, "git", return_value=self.core_sha.encode()), \
             patch.object(d.inputs.subprocess, "check_output", return_value=self.core_sha), \
             patch.object(d, "core_packages", return_value=[("Sunder.Package.Build", "1.2.3")]):
            value = d.inputs.fetch(self.source, self.output, self.source_sha, self.core_sha)
        self.assertEqual(dict(artifactSourceCommit=self.core_sha, buildToolVersion="1.2.3"), value["core"])
        self.assertEqual(2, value["schemaVersion"])
        self.assertEqual(value, d.inputs.verify(self.source, self.output, d.r.file_sha(self.output / d.inputs.MANIFEST), self.source_sha, self.core_sha))
        self.assertFalse((self.output / "ci-inputs.json").exists())

    def test_published_same_version_with_another_content_identity_is_never_rebuilt_or_reselected(self):
        with zipfile.ZipFile(self.archive) as archive:
            entries = {name: archive.read(name) for name in archive.namelist()}
        entries["manifest/sunder-package.json"] += b"\n"
        with zipfile.ZipFile(self.archive, "w") as archive:
            for name, data in entries.items(): archive.writestr(name, data)
        self.publications[d.AGENT] = [self.publication(self.archive, 2)]
        before = d.inputs.source_snapshot(self.source)
        with patch.object(d, "run") as build, self.assertRaisesRegex(d.r.Invalid, "exact committed package selection"):
            self.resolve()
        build.assert_not_called()
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
        with patch.object(d, "run") as build, patch.object(d.r, "git", return_value=self.core_sha.encode()), \
             self.assertRaisesRegex(d.r.Invalid, "exact committed package selection") as failure:
            d.complete(self.source, self.core, self.output, self.bundle)
        self.assertIn("expected (version, content identity, public inventory)=", str(failure.exception))
        self.assertIn("; actual=", str(failure.exception))
        build.assert_not_called()
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
        with patch.object(d, "run") as build, self.assertRaisesRegex(d.r.Invalid, "Conflicting exact dependency selections"):
            self.resolve()
        build.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_absent_dependency_cannot_use_another_authored_version_or_a_cycle(self):
        self.publications[d.AGENT] = []
        project = self.source / self.rows[0]["projectPath"]
        project.write_text('<Project><PropertyGroup><Version>3.0.0</Version></PropertyGroup></Project>')
        with patch.object(d, "run") as build, self.assertRaisesRegex(d.r.Invalid, "source cannot substitute another version"):
            self.resolve()
        build.assert_not_called()

    def test_source_catalog_export_must_match_the_selected_hash(self):
        self.publications[d.CORE] = []
        def run(command, source, home):
            self.assertNotIn("SelectSunder", " ".join(command))
            if "--output" in command:
                Path(command[command.index("--output") + 1]).write_bytes(b"wrong catalog")
        with patch.object(d, "run", side_effect=run), patch.object(d.subprocess, "check_output", return_value="/fixture/tool.dll"), \
             self.assertRaisesRegex(d.r.Invalid, "committed dependency"):
            self.resolve()

    def test_core_coordinates_follow_only_the_build_dependency_not_other_package_versions(self):
        versions = {"Sunder.Sdk": "1.2.3", "Sunder.Sdk.Avalonia": "2.3.4", "Sunder.Sdk.Worker": "3.4.5", "Sunder.Package.Build": "4.5.6"}
        for row in self.rows:
            path = self.source / Path(row["projectPath"]).parent / "packages.release.lock.json"
            path.write_bytes(d.r.canonical(dict(dependencies={"net10.0": {name: dict(type="Direct", resolved=version, contentHash="fixture") for name, version in versions.items()}})))
        release = dict(id=1, tag_name="nuget-sunder-package-build/v4.5.6", target_commitish="main", draft=False, prerelease=False,
                       assets=[dict(id=2, name="Sunder.Package.Build.4.5.6.nupkg", size=1, digest="sha256:" + "c" * 64, state="uploaded"),
                               dict(id=3, name="release-provenance.json", size=1, digest="sha256:" + "d" * 64, state="uploaded")])
        provenance = dict(schemaVersion=1, sources=[dict(name="sunder-core", sha=self.core_sha)],
                          invocation=dict(repository=d.CORE, releaseId=1, sha=self.core_sha),
                          artifacts=[dict(path="Sunder.Package.Build.4.5.6.nupkg", size=1, sha256="c" * 64)])
        with patch.object(d.inputs, "gh_json", side_effect=[dict(object=dict(type="tag", sha="d" * 40)), dict(object=dict(type="commit", sha=self.core_sha))]), \
             patch.object(d.inputs, "download", side_effect=lambda _, path, maximum: path.write_bytes(d.r.canonical(provenance))):
            value = d.coordinates(self.source, [release])
        self.assertEqual(dict(core_commit=self.core_sha, core_tag=release["tag_name"], core_version="4.5.6"), value)
        provenance["sources"][0]["sha"] = "f" * 40
        with patch.object(d.inputs, "gh_json", return_value=dict(object=dict(type="commit", sha=self.core_sha))), \
             patch.object(d.inputs, "download", side_effect=lambda _, path, maximum: path.write_bytes(d.r.canonical(provenance))), \
             self.assertRaisesRegex(d.r.Invalid, "another source"):
            d.coordinates(self.source, [release])
        release["tag_name"] = "sdk/v9.9.9"
        with self.assertRaisesRegex(d.r.Invalid, "version/tag"):
            d.coordinates(self.source, [release])


if __name__ == "__main__": unittest.main()
