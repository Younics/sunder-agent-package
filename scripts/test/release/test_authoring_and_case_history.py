"""Control cases for owner-only lock authoring and one-cohort Windows evidence."""
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
from zipfile import ZipFile

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SCRIPTS / "ci"))
import agent_ci
import owner_authoring as author
import test_lanes
import windows_case_history as history
from windows_case_history import inputs

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def commit(root, subject):
    git(root, "add", "--all")
    git(root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
        "-c", "commit.gpgSign=false", "commit", "-qm", subject)
    return git(root, "rev-parse", "HEAD")


class OwnerAuthoringTests(unittest.TestCase):
    def setUp(self):
        fixture_setup(self)
        self.owner = self.rows[0]["packageId"]
        self.core = self.root / "sunder-core"
        self.core.mkdir()
        git(self.core, "init", "-q")
        (self.core / "fixture").write_text("published source")
        self.core_sha = commit(self.core, "Core source")
        git(self.source, "init", "-q")
        lock = (self.source / self.rows[0]["projectPath"]).with_name("packages.release.lock.json")
        lock.write_text(json.dumps(dict(dependencies={"future-framework": {"Sunder.Package.Build": dict(
            type="Direct", resolved="4.5.6", contentHash="fixture-hash")}})))
        self.source_sha = commit(self.source, "Source with an import-free project")
        self.output = self.root / "authoring"
        evaluated = patch.object(author.agent_selection, "evaluated_packages")
        graph = patch.object(author.project_scope, "packages", return_value=[self.owner])
        self.evaluated = evaluated.start()
        self.addCleanup(evaluated.stop)
        self.graph = graph.start()
        self.addCleanup(graph.stop)

    def prepare(self, package=None):
        return author.prepare(self.source, package or self.owner, self.source_sha, self.core_sha, self.output)

    def test_owner_authoring_uses_only_the_published_catalog_and_exact_scoped_locks(self):
        asset = dict(id=303, name=self.catalog_path.name, size=self.catalog_path.stat().st_size,
                     digest="sha256:" + inputs.file_sha(self.catalog_path), state="uploaded")
        published = [dict(id=202, draft=False, assets=[asset])]
        with patch.object(author.dependency_inputs, "releases", return_value=published) as releases, \
                 patch.object(inputs, "download", side_effect=lambda item, path, maximum: shutil.copyfile(self.catalog_path, path)), \
                 patch.object(author.dependency_inputs, "run") as tool, redirect_stdout(io.StringIO()):
            result = self.prepare()
        self.assertEqual(self.owner, result["package"])
        value = inputs.verify(self.source, self.output, result["sha256"], self.source_sha, self.core_sha, [self.owner])
        self.assertEqual([], value["candidates"])
        self.assertEqual("4.5.6", value["core"]["buildToolVersion"])
        self.assertEqual([self.owner], value["packageIds"])
        self.assertEqual([self.rows[0]["projectPath"]], self.graph.call_args.args[1])
        self.evaluated.assert_called_once_with(self.source.resolve(), [self.owner])
        releases.assert_called_once_with(author.dependency_inputs.CORE)
        tool.assert_not_called()
        with self.assertRaisesRegex(ValueError, "does not cover"):
            inputs.verify(self.source, self.output, result["sha256"], self.source_sha, self.core_sha, [history.OWNER])

    def test_cannot_use_consumer_or_build_an_unpublished_catalog_to_author(self):
        with patch.object(author.dependency_inputs, "releases", return_value=[]) as releases, \
                 patch.object(author.dependency_inputs, "run") as tool:
            with self.assertRaisesRegex(ValueError, "import-free"):
                self.prepare(history.OWNER)
            releases.assert_not_called()
            with self.assertRaisesRegex(ValueError, "cannot reach"):
                with patch.object(author.project_scope, "packages", return_value=[self.owner, history.OWNER]):
                    self.prepare()
            releases.assert_not_called()
            with self.assertRaisesRegex(ValueError, "locked catalog is not published"):
                self.prepare()
            tool.assert_not_called()

    def test_final_handoff_reproduces_raw_owner_bytes_not_just_the_locked_inventory(self):
        record = self.source / author.REPRODUCTION
        record.parent.mkdir()
        asset = self.manifest["candidates"][0]["asset"]
        record.write_text(json.dumps(dict(schemaVersion=1, packageId=self.owner, version="2.0.0",
            authoredSource="8" * 40, coreCommit=self.manifest["core"]["artifactSourceCommit"],
            authoringRunId=123, authoringArtifactId=456, authoringZipSha256="9" * 64,
            archive={key: asset[key] for key in ("size", "sha256")})))
        with redirect_stdout(io.StringIO()):
            author.verify_reproduction(self.source, self.bundle)
        self.archive.write_bytes(self.archive.read_bytes() + b"different ZIP bytes")
        # The same descriptors and contract lock are insufficient.
        with self.assertRaisesRegex(ValueError, "on disk did not reproduce"):
            author.verify_reproduction(self.source, self.bundle)


def make_trx(class_name, cases):
    document = ET.Element("TestRun")
    definitions = ET.SubElement(document, "TestDefinitions")
    results = ET.SubElement(document, "Results")
    for index, case in enumerate(cases):
        definition = ET.SubElement(definitions, "UnitTest", id=str(index))
        ET.SubElement(definition, "TestMethod", className=class_name, name=case.split("(", 1)[0])
        ET.SubElement(results, "UnitTestResult", testId=str(index), testName=class_name + "." + case, outcome="Passed")
    ET.SubElement(ET.SubElement(document, "ResultSummary"), "Counters", failed="0", notExecuted="0")
    return ET.tostring(document)


class WindowsHistoryTests(unittest.TestCase):
    def setUp(self):
        fixture_setup(self)
        git(self.source, "init", "-q")
        policy = self.source / history.POLICY
        policy.parent.mkdir(parents=True)
        shutil.copyfile(SCRIPTS.parent / history.POLICY, policy)
        self.row = history.policy_row(self.source)
        self.selected = [history.OWNER]
        commit(self.source, "Fixture inputs")
        self.marker = self.source / history.ACTIVATION
        self.marker.parent.mkdir()
        self.originals = self.root / "historical"
        self.originals.mkdir()
        self.results = self.root / "current"

    def activate(self):
        tree, ledger = history.source_tree(self.source)
        self.assertFalse(ledger)
        self.marker.write_text(json.dumps(dict(schemaVersion=1, sourceTreeSha256=tree, coreCommit=self.core_sha,
                                               lockSetSha256=inputs.locks(self.source))))
        return commit(self.source, "Reviewed destination and exact Build/locks")

    def test_carry_is_opt_in_and_fails_closed_if_destination_or_build_changes(self):
        self.assertIsNone(history.active(self.source, self.selected, self.core_sha))
        selected = self.activate()
        self.assertEqual(selected, history.active(self.source, self.selected, self.core_sha)["destinationSource"])
        self.assertIsNone(history.active(self.source, ["younics.ai.agent"], self.core_sha))
        with self.assertRaisesRegex(ValueError, "does not bind"):
            history.active(self.source, self.selected, "f" * 40)
        lock = (self.source / self.rows[0]["projectPath"]).with_name("sunder-contracts.lock.json")
        lock.write_bytes(lock.read_bytes() + b"\n")
        with self.assertRaisesRegex(ValueError, "checkout differs"):
            history.active(self.source, self.selected, self.core_sha)
        commit(self.source, "Different lock bytes")
        with self.assertRaisesRegex(ValueError, "does not bind"):
            history.active(self.source, self.selected, self.core_sha)

    def test_only_windows_source_local_discovery_and_execution_get_the_exclusion(self):
        self.activate()
        inventory = self.source / "artifacts/reports/test-inventory.json"
        inventory.parent.mkdir(parents=True)
        paths = [history.PROJECT, "tests/Other.Tests.csproj"]
        inventory.write_text(json.dumps(dict(projects=[
            dict(path=path, owner=history.OWNER, modes=["source", "package"], lanes=["PackageCompatibility"]) for path in paths])))
        state = dict(source=str(self.source), coreCommit=self.core_sha, selectedPackages=self.selected)
        with patch.object(agent_ci, "context", return_value=(state, {})), \
                 patch.object(test_lanes.sys, "platform", "win32"), \
                 patch.object(test_lanes, "run_project", return_value=1) as run:
            test_lanes.tests("state", "source")
            filters = {call.args[2]: call.args[4] for call in run.call_args_list}
            self.assertIn("!~" + history.CLASS + ".", filters[history.PROJECT])
            self.assertEqual("FullyQualifiedName!~" + test_lanes.POLICY["artifactProducer"], filters[paths[1]])
            run.reset_mock()
            test_lanes.tests("state", "package")
            self.assertTrue(all(call.args[4] == "Lane=PackageCompatibility&Category!=Artifact" for call in run.call_args_list))

    def original(self, rid, identifier):
        data = make_trx(history.CLASS, self.row["cases"])
        path = self.originals / f"{identifier}.zip"
        with ZipFile(path, "w") as archive:
            archive.writestr(history.TRX, data)
            archive.writestr("origin", rid)
        return dict(artifactId=identifier, jobId=identifier + 10, size=path.stat().st_size,
                    zipSha256=inputs.file_sha(path), trxSha256=inputs.sha(data))

    def test_original_groups_cannot_be_duplicated_swapped_or_counted_as_current(self):
        self.activate()
        approved = history.active(self.source, self.selected, self.core_sha)
        originals = {rid: self.original(rid, identifier) for identifier, rid in enumerate(history.ORIGINALS, 1)}
        current = {}
        for rid in originals:
            directory = self.results / f"agent-component-{history.OWNER}-source-{rid}-1"
            directory.mkdir(parents=True)
            current[rid] = directory / "result.trx"
            current[rid].write_bytes(make_trx("Fixture.OtherTests", ["CurrentCase"]))
        fresh = dict(self.row, className="Fixture.OtherTests", expectedCount=1, cases=["CurrentCase"])
        with patch.object(history, "ORIGINALS", originals), redirect_stdout(io.StringIO()):
            remaining, receipt = history.audit(approved, [self.row, fresh], self.results, self.originals)
            self.assertEqual([fresh], remaining)
            self.assertFalse(receipt["executedOnDestination"])
            self.assertEqual("failure", receipt["originalReleaseVerdict"])
            self.assertEqual({rid: (len(self.row["cases"]), 0, 0) for rid in originals},
                             {group["rid"]: (group["passed"], group["failed"], group["skipped"]) for group in receipt["groups"]})
            source = self.originals / history.file_name(originals["win-x64"])
            duplicate = self.originals / "another-copy.zip"
            shutil.copyfile(source, duplicate)
            with self.assertRaisesRegex(ValueError, "duplicate"):
                history.audit(approved, [self.row], self.results, self.originals)
            duplicate.unlink()
            expected = source.read_bytes()
            source.write_bytes((self.originals / history.file_name(originals["win-arm64"])).read_bytes())
            with self.assertRaisesRegex(ValueError, "ZIP digest"):
                history.audit(approved, [self.row], self.results, self.originals)
            source.write_bytes(expected)
            current["win-arm64"].write_bytes(make_trx(history.CLASS, [self.row["cases"][0]]))
            with self.assertRaisesRegex(ValueError, "repeats historical"):
                history.audit(approved, [self.row], self.results, self.originals)
            current["win-arm64"].unlink()
            with self.assertRaisesRegex(ValueError, "current Windows evidence is missing"):
                history.audit(approved, [self.row], self.results, self.originals)


if __name__ == "__main__":
    unittest.main()
