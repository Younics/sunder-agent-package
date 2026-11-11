"""Regression coverage for false-green, incomplete and platform-dependent reports."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).with_name("generate-test-duration-report.py")
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
import bash_runtime
import agent_ci
import test_lanes
SPEC = importlib.util.spec_from_file_location("evidence", SCRIPT)
evidence = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(evidence)


class TestEvidence(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.results = self.root / "results/full"
        self.results.mkdir(parents=True)
        source = self.root / "tests/Fixture.Tests/EvidenceTests.cs"
        source.parent.mkdir(parents=True)
        source.write_text('''namespace Fixture.Tests;
public sealed class EvidenceTests
{
    [PackageArtifactsFact]
    public void ExpectedArtifactSkip() { }
    [Fact]
    public void OrdinaryTest() { }
    [PlatformTheory(TestPlatform.Linux, "The seeded theory is Linux-specific.")]
    [InlineData(1)]
    [InlineData(2)]
    public void LinuxTheory(int value) { }
}
''')
        self.inventory = self.root / "inventory.json"
        self.inventory.write_text(json.dumps({"projects": [{
            "path": "tests/Fixture.Tests/Fixture.Tests.csproj", "owner": "fixture-owner",
            "modes": ["source"], "lanes": [], "sourceOnlyTests": [],
            "sources": [str(source.relative_to(self.root))],
        }]}))
        self.artifact_reason = "Set SUNDER_AGENT_PACKAGE_ARCHIVE_DIR to the complete package archive directory."
        self.rows = [("ExpectedArtifactSkip", None, "NotExecuted", self.artifact_reason),
                     ("OrdinaryTest", None, "Passed", ""),
                     ("LinuxTheory", "(value: 1)", "Passed", ""),
                     ("LinuxTheory", "(value: 2)", "Passed", "")]
        self.write_reports()

    def write_reports(self, *, discovery=True, outcome="Completed"):
        root = ET.Element("TestRun", name="fixture")
        results, definitions = ET.SubElement(root, "Results"), ET.SubElement(root, "TestDefinitions")
        names = []
        for index, (method, arguments, status, reason) in enumerate(self.rows):
            name = "Fixture.Tests.EvidenceTests." + method + (arguments or "")
            names.append(name)
            result = ET.SubElement(results, "UnitTestResult", testId=str(index), executionId=f"execution-{index}",
                                   testName=name, outcome=status, duration="00:00:00.001")
            if reason:
                error = ET.SubElement(ET.SubElement(result, "Output"), "ErrorInfo")
                ET.SubElement(error, "Message").text = reason
                ET.SubElement(error, "StackTrace").text = "at Fixture.Tests.EvidenceTests"
            definition = ET.SubElement(definitions, "UnitTest", id=str(index), name=name, storage="Fixture.Tests.dll")
            ET.SubElement(definition, "TestMethod", className="Fixture.Tests.EvidenceTests", name=method)
        summary = ET.SubElement(root, "ResultSummary", outcome=outcome)
        ET.SubElement(summary, "Counters", total=str(len(self.rows)),
                      executed=str(sum(row[2] != "NotExecuted" for row in self.rows)),
                      passed=str(sum(row[2] == "Passed" for row in self.rows)),
                      failed=str(sum(row[2] == "Failed" for row in self.rows)))
        ET.ElementTree(root).write(self.results / "Fixture.Tests.trx")
        if discovery:
            (self.results / "Fixture.Tests.discovery.json").write_text(json.dumps({
                "schemaVersion": 1, "project": "Fixture.Tests", "filter": "", "report": "Fixture.Tests.trx", "tests": names,
            }))

    def report(self, platform="linux", success=True):
        environment = {key: value for key, value in os.environ.items()
                       if key not in {"SUNDER_AGENT_PACKAGE_ARCHIVE_DIR", "SUNDER_REQUIRE_TEST_PREREQUISITES"}}
        process = subprocess.run([sys.executable, "-B", str(SCRIPT), str(self.root / "results"),
                                  "--inventory", str(self.inventory), "--repository-root", str(self.root),
                                  "--mode", "source", "--platform", platform, "--verify"],
                                 text=True, capture_output=True, env=environment)
        self.assertEqual(0 if success else 1, process.returncode, process.stdout + process.stderr)
        return json.loads(process.stdout)

    def test_new_test_is_discovered_without_refreshing_a_checked_inventory(self):
        self.rows.append(("NewRegression", None, "Passed", ""))
        self.write_reports()
        report = self.report()
        self.assertEqual(5, report["summary"]["total"])
        self.assertEqual([], report["evidenceViolations"])

    def test_discovered_theory_rows_and_whole_methods_cannot_disappear(self):
        for omitted in (1, 3):
            with self.subTest(omitted=omitted):
                saved = self.rows.pop(omitted)
                self.write_reports(discovery=False)
                report = self.report(success=False)
                self.assertTrue(any("discovery omitted result" in error for error in report["evidenceViolations"]))
                self.rows.insert(omitted, saved)
        # Distinct adapter cases may share a truncated display name. Compare their
        # multiplicity, rather than treating a second identical name as a rerun.
        self.rows.append(self.rows[-1])
        self.write_reports()
        self.report()
        self.rows.pop()
        self.write_reports(discovery=False)
        self.report(success=False)

    def test_platform_theory_collapse_and_deferred_theory_expansion(self):
        self.rows[2:] = [("LinuxTheory", None, "NotExecuted", "The seeded theory is Linux-specific.")]
        self.write_reports()
        self.report(platform="windows")
        self.rows[2:] = [("LinuxTheory", "(value: 1)", "Passed", ""), ("LinuxTheory", "(value: 2)", "Passed", "")]
        self.write_reports(discovery=False)
        self.report()

    def test_skip_policy_restores_outer_type_after_nested_helpers_and_fixture_literals(self):
        source = self.root / "Scopes.cs"
        source.write_text('''namespace Fixture { namespace Tests {
public class Outer {
    private class Helper {
        string Raw = """ } [PlatformFact(TestPlatform.Windows, "fake")] public void Fake() { """;
        string Interpolated = $"{Call("}")} {{{1}}}";
    }
    [PlatformFact(TestPlatform.Windows, "Windows boundary.")]
    public void AfterHelper() { }
    public class Nested {
        [UnixTheory("Unix boundary."), InlineData("]")]
        public void NestedCase(string value) { }
    }
    [PlatformFact(TestPlatform.Linux, "Linux boundary.")]
    public void AfterNested() { }
}}}
''')
        declarations = evidence.method_attribute_blocks(source)
        self.assertEqual([
            ("Fixture.Tests.Outer", "AfterHelper", ['[PlatformFact(TestPlatform.Windows,"Windows boundary.")]']),
            ("Fixture.Tests.Outer+Nested", "NestedCase", ['[UnixTheory("Unix boundary.")]', '[InlineData("]")]']),
            ("Fixture.Tests.Outer", "AfterNested", ['[PlatformFact(TestPlatform.Linux,"Linux boundary.")]']),
        ], declarations)

    def test_unprivileged_unix_policy_distinguishes_windows_root_and_required_execution(self):
        policy = {"project": "fixture", "owner": "fixture", "fullyQualifiedName": "Fixture.Case",
                  "attribute": "UnprivilegedUnixTheory", "platform": None, "capability": None, "reason": ""}
        self.assertEqual("This permission-denied fixture requires Unix.", evidence.expected_skip(policy, "windows", {})["reason"])
        with patch.object(evidence.os, "geteuid", return_value=0, create=True):
            self.assertEqual("A real permission-denied fixture requires a non-root Unix user.", evidence.expected_skip(policy, "linux", {})["reason"])
            self.assertIsNone(evidence.expected_skip(policy, "linux", {"SUNDER_REQUIRE_TEST_PREREQUISITES": "1"}))
        with patch.object(evidence.os, "geteuid", return_value=1000, create=True):
            self.assertIsNone(evidence.expected_skip(policy, "linux", {}))

    def test_case_sensitive_directory_capability_is_probed_and_bound_to_the_exact_case(self):
        source = self.root / "tests/Fixture.Tests/EvidenceTests.cs"
        reason = "Case-distinct containment requires a case-sensitive directory."
        source.write_text(source.read_text().replace('[Fact]\n    public void OrdinaryTest()',
            f'[FileSystemFact(TestFileSystemCapability.CaseSensitiveDirectories, "{reason}")]\n    public void OrdinaryTest()'))
        available = evidence.filesystem_capability_available("CaseSensitiveDirectories")
        if not available:
            self.rows[1] = ("OrdinaryTest", None, "NotExecuted",
                            reason + " Required filesystem capability 'CaseSensitiveDirectories' is unavailable.")
        self.write_reports()
        report = self.report()
        self.assertEqual([], report["evidenceViolations"])
        self.assertEqual(1 if available else 2, report["summary"]["skipped"])
        self.assertEqual("CaseSensitiveDirectories", next(row["capability"] for row in report["skipPolicyEvidence"]
            if row["fullyQualifiedName"] == "Fixture.Tests.EvidenceTests.OrdinaryTest"))

    def test_skips_cannot_trade_identity_or_change_reason(self):
        self.rows[:2] = [("ExpectedArtifactSkip", None, "Passed", ""),
                         ("OrdinaryTest", None, "NotExecuted", self.artifact_reason)]
        self.write_reports()
        report = self.report(success=False)
        self.assertTrue(any("exact identity, reason, or owner" in error for error in report["evidenceViolations"]))
        self.rows[:2] = [("ExpectedArtifactSkip", None, "NotExecuted", "wrong prerequisite"),
                         ("OrdinaryTest", None, "Passed", "")]
        self.write_reports()
        self.report(success=False)

    def test_crashed_run_cannot_pass_with_only_completed_successes(self):
        self.write_reports(outcome="Failed")
        report = self.report(success=False)
        self.assertTrue(any("run failed or aborted" in error for error in report["evidenceViolations"]))

    def test_failed_row_keeps_message_stack_and_report(self):
        self.rows[1] = ("OrdinaryTest", None, "Failed", "unique failure")
        self.write_reports(outcome="Failed")
        report = self.report(success=False)
        self.assertEqual("unique failure", report["failures"][0]["message"])
        self.assertEqual("at Fixture.Tests.EvidenceTests", report["failures"][0]["stackTrace"])
        self.assertEqual("full/Fixture.Tests.trx", report["failures"][0]["trx"])

    def test_assertion_notifications_are_bound_and_do_not_hide_platform_errors(self):
        self.rows[1] = ("OrdinaryTest", None, "Failed", "assertion failure")
        self.write_reports(outcome="Failed")
        path = self.results / "Fixture.Tests.trx"
        tree = ET.parse(path)
        infos = ET.SubElement(tree.find("ResultSummary"), "RunInfos")
        notification = "[xUnit.net 00:00:08.39]     Fixture.Tests.EvidenceTests.OrdinaryTest [FAIL]"
        ET.SubElement(ET.SubElement(infos, "RunInfo", outcome="Error"), "Text").text = notification
        tree.write(path)
        self.assertEqual([], evidence.read_trx(self.results, path)[1]["violations"])
        discovery = json.loads((self.results / "Fixture.Tests.discovery.json").read_text())
        self.assertTrue(test_lanes.completed_test_failure(self.results, "Fixture.Tests", discovery))
        for message in ("test platform failed", notification, notification.replace("OrdinaryTest", "UnknownCase"),
                        notification.replace("OrdinaryTest", "LinuxTheory(value: 1)"), notification + "\nTest host crashed"):
            with self.subTest(message=message):
                info = ET.SubElement(infos, "RunInfo", outcome="Error")
                ET.SubElement(info, "Text").text = message
                tree.write(path)
                self.assertFalse(test_lanes.completed_test_failure(self.results, "Fixture.Tests", discovery))
                report = self.report(success=False)
                self.assertTrue(any("test-platform errors" in error for error in report["evidenceViolations"]))
                infos.remove(info)

    def test_only_complete_assertion_runs_are_classified_as_project_failures(self):
        self.rows[1] = ("OrdinaryTest", None, "Failed", "assertion failure")
        path = self.results / "Fixture.Tests.trx"
        common = ["dotnet", "test", "solution", "--no-restore", "--no-build"]
        def execute(state, command, log):
            if "--list-tests" in command:
                log.write_text("The following Tests are available:\n" + "".join("    " + case + "\n" for case in discovery["tests"]))
                return 0
            return status
        for defect in ("none", "missing", "malformed", "incomplete", "aborted", "counters", "platform", "unexpected-exit"):
            with self.subTest(defect=defect):
                self.write_reports(outcome="Failed")
                discovery = json.loads((self.results / "Fixture.Tests.discovery.json").read_text())
                status = 2 if defect == "unexpected-exit" else 1
                tree = ET.parse(path)
                if defect == "incomplete": tree.find("Results").remove(tree.find("Results")[0])
                if defect == "aborted": tree.find("ResultSummary").set("outcome", "Aborted")
                if defect == "counters": tree.find("ResultSummary/Counters").set("aborted", "1")
                if defect == "platform": ET.SubElement(tree.find("ResultSummary"), "RunInfo", outcome="Error")
                tree.write(path)
                if defect == "malformed": path.write_text("<TestRun")
                if defect == "missing": path.unlink()
                with patch.object(agent_ci, "execute", side_effect=execute), patch.object(agent_ci, "context", return_value=({}, {})):
                    with self.assertRaises(subprocess.CalledProcessError) as caught:
                        test_lanes.run_project("state", self.root, "Fixture.Tests.csproj", common, "", self.results)
                self.assertEqual(defect == "none", isinstance(caught.exception, test_lanes.TestProjectFailure))
                # Even exit zero must stop on absent, incomplete or inconsistent TRX.
                status = 0
                with patch.object(agent_ci, "execute", side_effect=execute), patch.object(agent_ci, "context", return_value=({}, {})):
                    with self.assertRaises((ValueError, OSError, ET.ParseError)):
                        test_lanes.run_project("state", self.root, "Fixture.Tests.csproj", common, "", self.results)
        self.rows[1] = ("OrdinaryTest", None, "Passed", "")
        self.write_reports()
        with patch.object(agent_ci, "execute", side_effect=execute), patch.object(agent_ci, "context", return_value=({}, {})):
            self.assertEqual(len(self.rows), test_lanes.run_project("state", self.root, "Fixture.Tests.csproj", common, "", self.results))
        for error in (OSError("capture failure"), ValueError("input integrity"), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__), patch.object(agent_ci, "execute", side_effect=error):
                with self.assertRaises(type(error)) as caught:
                    test_lanes.run_project("state", self.root, "Fixture.Tests.csproj", common, "", self.results)
                self.assertIs(error, caught.exception)

    def test_successful_child_cannot_hide_capture_or_integrity_failure(self):
        for stage in ("console", "log", "verification"):
            with self.subTest(stage=stage):
                (self.results / "Fixture.Tests.run.command.json").write_text(json.dumps({
                    "exitCode": 0, "command": ["dotnet", "test"], "errors": {stage: "fixture failure"}}))
                report = self.report(success=False)
                self.assertTrue(any("command diagnostics failed" in error and stage in error for error in report["evidenceViolations"]))

    def test_malformed_missing_and_counter_only_reports_fail(self):
        path = self.results / "Fixture.Tests.trx"
        for contents in ("<TestRun", '<TestRun><ResultSummary outcome="Completed"><Counters total="4" /></ResultSummary></TestRun>'):
            path.write_text(contents)
            self.report(success=False)
        path.unlink()
        self.report(success=False)

    def test_discovery_and_command_failures_remain_visible(self):
        (self.results / "Fixture.Tests.discovery.json").unlink()
        self.report(success=False)
        self.write_reports()
        (self.results / "Fixture.Tests.run.command.json").write_text(json.dumps({"exitCode": 1, "command": ["dotnet", "test"]}))
        report = self.report(success=False)
        self.assertTrue(any("command did not succeed" in error for error in report["evidenceViolations"]))
        for name in ("Fixture.Tests.run.command.json", "Fixture.Tests.discovery.json"):
            path = self.results / name
            saved = path.read_text()
            for invalid in ('{"interrupted":', '[]', '{}'):
                path.write_text(invalid)
                self.assertTrue(self.report(success=False)["evidenceViolations"])
            path.write_text(saved)

    def test_inventory_propagates_solution_discovery_failure(self):
        process = subprocess.run(bash_runtime.command(
            "-c", 'dotnet() { return 23; }; export -f dotnet; "$BASH" "$1"',
            "inventory-fixture", str(SCRIPT.with_name("generate-test-inventory.sh")),
        ), text=True, capture_output=True)
        self.assertEqual(23, process.returncode, process.stdout + process.stderr)
        self.assertEqual("", process.stdout)

    def test_inventory_streams_large_source_lists_and_complete_project_inventory(self):
        repository = SCRIPT.resolve().parents[2]
        projects = [f"tests/{name}.Tests/{name}.Tests.csproj" for name in ("First", "Second")]
        expected = []
        for index, project in enumerate(projects):
            directory = repository / Path(project).parent
            sources = [directory / ("LongSourceFileName" * 4 + str(number) + ".cs") for number in range(600)]
            value = dict(Properties=dict(MSBuildProjectDirectory=str(directory), IsTestProject="true",
                SunderTestOwner="agent-family", SunderTestCategory="Unit", SunderTestCapabilities="None",
                SunderTestOS="Any", SunderTestSize="Small", SunderTestSourceMode="true", SunderTestPackageMode="false",
                SunderTestLanes="", SunderTestSourceOnlyTests=""), Items=dict(Compile=[dict(FullPath=str(path)) for path in sources]))
            (self.root / f"metadata-{index}.json").write_bytes(json.dumps(value).encode("utf-8"))
            expected.append(sorted(path.relative_to(repository).as_posix() for path in sources))
        # Model Windows' native command-line bound even on Unix, before invoking jq.
        script = '''
          jq() {
            local bytes=0 value
            for value; do bytes=$((bytes + ${#value} + 1)); done
            if (( bytes > 30000 )); then printf 'fixture: native argv limit exceeded\\n' >&2; return 126; fi
            command jq "$@"
          }
          dotnet() {
            if [[ "$1" == sln ]]; then
              printf '%s\\n' tests/First.Tests/First.Tests.csproj tests/Second.Tests/Second.Tests.csproj
            elif [[ "$2" == tests/First.Tests/First.Tests.csproj ]]; then
              cat "$INVENTORY_FIXTURE_METADATA/metadata-0.json"
            else
              cat "$INVENTORY_FIXTURE_METADATA/metadata-1.json"
            fi
          }
          export -f jq dotnet
          "$BASH" "$1"
        '''
        result = subprocess.run(bash_runtime.command("-c", script, "inventory-fixture", SCRIPT.with_name("generate-test-inventory.sh")),
                                env={**os.environ, "INVENTORY_FIXTURE_METADATA": str(self.root)}, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        inventory = json.loads(result.stdout)
        self.assertEqual(projects, [row["path"] for row in inventory["projects"]])
        self.assertEqual(expected, [row["sources"] for row in inventory["projects"]])
        self.assertGreater(len(result.stdout), 100000)

    def test_result_definition_and_execution_completeness(self):
        path = self.results / "Fixture.Tests.trx"
        for mutate in (lambda tree: tree.find("Results").remove(tree.find("Results")[0]),
                       lambda tree: tree.find("Results")[0].set("testId", "unknown"),
                       lambda tree: tree.find("Results")[1].set("executionId", "execution-0"),
                       lambda tree: tree.find("ResultSummary/Counters").attrib.pop("executed")):
            self.write_reports()
            tree = ET.parse(path)
            mutate(tree)
            tree.write(path)
            self.report(success=False)

    def test_primary_evidence_includes_required_and_artifact_lanes(self):
        required = self.results.parent / "required"
        self.results.rename(required)
        report = self.report()
        self.assertEqual(4, report["primarySummary"]["total"])


if __name__ == "__main__":
    unittest.main()
