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


class HostedAttemptTests(unittest.TestCase):
    def pages(self, route):
        page = int(route.rsplit("=", 1)[-1])
        return dict(total_count=45, jobs=[dict(id=value) for value in range((page - 1) * 20 + 1, min(45, page * 20) + 1)])

    def test_authenticates_complete_attempt_without_loading_unrelated_artifacts(self):
        with patch.object(history.hosted, "api_json", side_effect=self.pages):
            jobs = history.hosted.attempt_jobs(17, 5)
        self.assertEqual(list(range(1, 46)), [job["id"] for job in jobs])
        with patch.object(history.hosted, "api_json", side_effect=self.pages):
            self.assertTrue(history.hosted.attempt_jobs(17, 5, reverse=True, stop_after_first=lambda job: job["id"] == 43))

    def test_refuses_to_bind_a_job_listing_that_changed_mid_read(self):
        def mutate(route):
            result = self.pages(route)
            if route.endswith("page=2"):
                result["jobs"][0]["id"] = 1
            return result
        with patch.object(history.hosted, "api_json", side_effect=mutate), \
             self.assertRaisesRegex(ValueError, "Duplicate Actions job"):
            history.hosted.attempt_jobs(17, 5)


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
        with patch.object(author.dependency_inputs, "releases") as releases, \
                 patch.object(author.dependency_inputs.public_platform_catalog, "download", return_value=self.catalog_path.read_bytes()) as catalog, \
                 redirect_stdout(io.StringIO()):
            result = self.prepare()
        self.assertEqual(self.owner, result["package"])
        value = inputs.verify(self.source, self.output, result["sha256"], self.source_sha, self.core_sha, [self.owner])
        self.assertEqual([], value["candidates"])
        self.assertEqual("4.5.6", value["core"]["buildToolVersion"])
        self.assertEqual([self.owner], value["packageIds"])
        self.assertEqual([self.rows[0]["projectPath"]], self.graph.call_args.args[1])
        self.evaluated.assert_called_once_with(self.source.resolve(), [self.owner])
        releases.assert_not_called()
        catalog.assert_called_once()
        with self.assertRaisesRegex(ValueError, "does not cover"):
            inputs.verify(self.source, self.output, result["sha256"], self.source_sha, self.core_sha, [history.OWNER])

    def test_cannot_use_consumer_or_build_an_unpublished_catalog_to_author(self):
        with patch.object(author.dependency_inputs, "releases", return_value=[]) as releases, \
                 patch.object(author.dependency_inputs.public_platform_catalog, "download",
                              side_effect=subprocess.CalledProcessError(22, "curl")) as catalog:
            with self.assertRaisesRegex(ValueError, "import-free"):
                self.prepare(history.OWNER)
            releases.assert_not_called()
            with self.assertRaisesRegex(ValueError, "cannot reach"):
                with patch.object(author.project_scope, "packages", return_value=[self.owner, history.OWNER]):
                    self.prepare()
            releases.assert_not_called()
            catalog.assert_not_called()
            with self.assertRaises(subprocess.CalledProcessError):
                self.prepare()
            catalog.assert_called_once()

    def test_final_handoff_reproduces_raw_owner_bytes_not_just_the_locked_inventory(self):
        transport = self.root / "authoring.zip"
        with ZipFile(transport, "w") as archive:
            archive.write(self.archive, self.archive.name)
        self.assertTrue(author.same_archive(transport, self.archive))
        self.archive.write_bytes(self.archive.read_bytes() + b"different ZIP bytes")
        self.assertFalse(author.same_archive(transport, self.archive))


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
        policy_name = "scripts/test/required-platform-cases.json"
        policy = self.source / policy_name
        policy.parent.mkdir(parents=True)
        shutil.copyfile(SCRIPTS.parent / policy_name, policy)
        self.row = history.policy_row(self.source)
        self.selected = [history.OWNER]
        self.source_sha = commit(self.source, "Fixture inputs")
        self.originals = self.root / "historical"
        self.originals.mkdir()
        self.results = self.root / "current"

    def test_unrelated_commit_does_not_invalidate_scope_but_worker_and_test_changes_do(self):
        worker = Path(next(row["projectPath"] for row in self.rows if row["packageId"] == history.OWNER)).parent
        (self.source / "README.md").write_text("unrelated")
        commit(self.source, "Unrelated documentation")
        self.assertTrue(history.same_case_scope(self.source, self.source_sha, self.row, worker.as_posix()))
        (self.source / worker / "Worker.cs").write_text("changed Worker")
        commit(self.source, "Behavioral input change")
        self.assertFalse(history.same_case_scope(self.source, self.source_sha, self.row, worker.as_posix()))

    def test_only_windows_source_local_discovery_and_execution_get_the_exclusion(self):
        inventory = self.source / "artifacts/reports/test-inventory.json"
        inventory.parent.mkdir(parents=True)
        paths = [history.PROJECT, "tests/Other.Tests.csproj"]
        inventory.write_text(json.dumps(dict(projects=[
            dict(path=path, owner=history.OWNER, modes=["source", "package"], lanes=["PackageCompatibility"]) for path in paths])))
        state = dict(source=str(self.source), coreCommit=self.core_sha, selectedPackages=self.selected)
        with patch.object(agent_ci, "context", return_value=(state, {})), \
                 patch.dict("os.environ", {"WINDOWS_HISTORY_SHA256": "c" * 64, "RUNNER_TEMP": str(self.root)}), \
                 patch.object(history, "checked_history") as receipt, \
                 patch.object(test_lanes.sys, "platform", "win32"), \
                 patch.object(test_lanes, "run_project", return_value=1) as run:
            test_lanes.tests("state", "source")
            filters = {call.args[2]: call.args[4] for call in run.call_args_list}
            self.assertIn("!~" + history.CLASS + ".", filters[history.PROJECT])
            receipt.assert_called_once()
            self.assertEqual("FullyQualifiedName!~" + test_lanes.POLICY["artifactProducer"], filters[paths[1]])
            run.reset_mock()
            test_lanes.tests("state", "package")
            self.assertTrue(all(call.args[4] == "Lane=PackageCompatibility&Category!=Artifact" for call in run.call_args_list))
            with patch.dict("os.environ", {"WINDOWS_HISTORY_SHA256": ""}):
                run.reset_mock()
                test_lanes.tests("state", "source")
                self.assertTrue(all(history.CLASS not in call.args[4] for call in run.call_args_list))

    def original(self, rid, identifier):
        data = make_trx(history.CLASS, self.row["cases"])
        path = self.originals / f"{identifier}.zip"
        with ZipFile(path, "w") as archive:
            archive.writestr(history.TRX, data)
            archive.writestr("origin", rid)
        return dict(artifactId=identifier, jobId=identifier + 10, size=path.stat().st_size,
                    zipSha256=inputs.file_sha(path), trxSha256=inputs.sha(data))

    def test_original_groups_cannot_be_duplicated_swapped_or_counted_as_current(self):
        originals = {rid: self.original(rid, identifier) for identifier, rid in enumerate(sorted(history.RIDS), 1)}
        approved = dict(schemaVersion=1, destinationSource=self.source_sha, originalReleaseVerdict="failure",
                        originalRun=123, originalAttempt=1, row=self.row, windows=originals)
        report = self.originals / history.REPORT
        report.write_text(json.dumps(approved))
        self.assertEqual(approved, history.checked_history(self.source, self.originals, inputs.file_sha(report)))
        current = {}
        for rid in originals:
            directory = self.results / f"agent-component-{history.OWNER}-source-{rid}-1"
            directory.mkdir(parents=True)
            current[rid] = directory / "result.trx"
            current[rid].write_bytes(make_trx("Fixture.OtherTests", ["CurrentCase"]))
        fresh = dict(self.row, className="Fixture.OtherTests", expectedCount=1, cases=["CurrentCase"])
        with redirect_stdout(io.StringIO()):
            remaining, receipt = history.audit(approved, [self.row, fresh], self.results, self.originals)
            self.assertEqual([fresh], remaining)
            self.assertFalse(receipt["executedOnDestination"])
            self.assertEqual("failure", receipt["originalReleaseVerdict"])
            self.assertEqual({rid: (len(self.row["cases"]), 0, 0) for rid in originals},
                             {group["rid"]: (group["passed"], group["failed"], group["skipped"]) for group in receipt["groups"]})
            source = self.originals / f'{originals["win-x64"]["artifactId"]}.zip'
            duplicate = self.originals / "another-copy.zip"
            shutil.copyfile(source, duplicate)
            with self.assertRaisesRegex(ValueError, "duplicate"):
                history.audit(approved, [self.row], self.results, self.originals)
            duplicate.unlink()
            expected = source.read_bytes()
            source.write_bytes((self.originals / f'{originals["win-arm64"]["artifactId"]}.zip').read_bytes())
            with self.assertRaisesRegex(ValueError, "ZIP digest"):
                history.audit(approved, [self.row], self.results, self.originals)
            source.write_bytes(expected)
            current["win-arm64"].write_bytes(make_trx(history.CLASS, [self.row["cases"][0]]))
            with self.assertRaisesRegex(ValueError, "repeats historical"):
                history.audit(approved, [self.row], self.results, self.originals)
            current["win-arm64"].unlink()
            with self.assertRaisesRegex(ValueError, "current evidence is missing"):
                history.audit(approved, [self.row], self.results, self.originals)


if __name__ == "__main__":
    unittest.main()
