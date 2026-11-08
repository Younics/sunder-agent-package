"""Ordinary CI uses exact inputs without production approval or cache synthesis."""
import argparse
import io
import json
import os
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SCRIPTS / "ci"))
import agent_ci as ci
import test_lanes
import ci_platforms
import bash_runtime

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class AgentCiTests(unittest.TestCase):
    setUp = fixture_setup

    def write_test_inventory(self):
        path = self.source / "artifacts/reports/test-inventory.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"projects": [{"path": "tests/Fixture.Tests/Fixture.Tests.csproj", "modes": ["source"]}]}))

    def test_windows_checkout_preserves_reviewed_inventory_and_contract_record_bytes(self):
        for path in ("compatibility/agent-current-inventory.json", "compatibility/agent-current-contracts.json"):
            blob = subprocess.check_output(["git", "cat-file", "blob", "HEAD:" + path], cwd=SCRIPTS.parent)
            checkout = subprocess.check_output(["git", "-c", "core.autocrlf=true", "cat-file", "--filters",
                                                "--path=" + path, "HEAD:" + path], cwd=SCRIPTS.parent)
            self.assertTrue(blob == checkout, path + ": checkout must not rewrite reviewed input bytes")

    def test_ci_resolves_dependencies_without_an_operator_locator(self):
        output = self.root / "missing-ci-inputs"
        with patch.object(sys, "argv", ["agent_ci.py", "resolve", "--source", str(self.source), "--core", "../sunder-core", "--output", str(output)]), \
             patch.dict(os.environ, {}, clear=True), patch.object(ci, "resolve") as resolve:
            ci.main()
        resolve.assert_called_once_with(str(self.source), "../sunder-core", str(output))
        self.assertFalse(output.exists())

    def test_workflow_retains_input_failure_without_masking_exit_or_reporting_unstarted_tests(self):
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-smoke.yml").read_text()
        section = workflow.split("      - name: Resolve exact selected archives, catalog and Core source\n")[1].split("\n      - ")[0]
        script = textwrap.dedent(section.split("        run: |\n")[1])
        stub = self.root / "scripts/release/release_inputs.py"
        stub.parent.mkdir(parents=True)
        stub.write_text('import sys\nprint("fixture: exact CI inputs missing", file=sys.stderr)\nsys.exit(7)\n')
        result = subprocess.run(bash_runtime.command("-c", script), cwd=self.root, text=True, capture_output=True, timeout=10,
                                 env={**os.environ, "GITHUB_SHA": self.source_sha, "CORE_COMMIT": self.core_sha, "RUNNER_TEMP": str(self.root)})
        self.assertEqual(7, result.returncode, result.stdout + result.stderr)
        self.assertIn("fixture: exact CI inputs missing", (self.root / "artifacts/reports/input-preparation.log").read_text())
        self.assertIn("agent_ci.py validate", workflow)
        self.assertIn("if-no-files-found: error", workflow)
        upload = workflow.split("      - name: Retain CI reports\n")[1]
        self.assertIn("steps.inputs.outcome != 'skipped'", upload)
        self.assertIn("steps.inputs.outcome != ''", upload)

    def test_empty_selection_stops_before_inventory_sdk_or_dependency_preparation(self):
        args = argparse.Namespace(source=self.source, packages="[]")
        with patch.object(ci.subprocess, "run") as run, patch.object(ci, "prepare") as prepare:
            ci.validate(args)
            ci.preflight(args)
        run.assert_not_called()
        prepare.assert_not_called()
        self.assertFalse((self.source / "artifacts/reports").exists())

    def test_selected_builds_and_reports_share_the_owned_project_inventory(self):
        selected = "younics.ai.agent.provider.openai"
        inventory = dict(schemaVersion=2, solution="Sunder.AgentPackage.slnx", projects=[
            dict(path="tests/Selected.csproj", owner=selected, modes=["source", "package"]),
            dict(path="tests/Family.csproj", owner="agent-family", modes=["source", "package"]),
            dict(path="tests/Unchanged.csproj", owner="younics.ai.agent.provider.gemini", modes=["source", "package"]),
            dict(path="tests/Local.csproj", owner="younics.ai.agent.execution.local", modes=["source", "package"], sourceOnlyTests=["ordinary"])])
        for platform in ("linux", "win32", "darwin"):
            for mode in ("source", "package"):
                for package in (selected, "younics.ai.agent"):
                    with self.subTest(platform=platform, mode=mode, package=package):
                        args = argparse.Namespace(source=self.source, core=self.root / "sunder-core", mode=mode, inputs=self.root,
                                                  candidate=None, packages=json.dumps([package]), stress=False)
                        result = subprocess.CompletedProcess(["fixture-bash"], 0, json.dumps(inventory), "")
                        with patch.object(sys, "platform", platform), patch.object(bash_runtime, "command", return_value=["fixture-bash"]), \
                             patch.object(ci.subprocess, "run", return_value=result), patch.object(ci, "prepare"), \
                             patch.object(ci, "execute", return_value=0) as build, patch.object(test_lanes, "validate") as tests, \
                             patch.object(test_lanes, "retain_verifier"):
                            ci.validate(args)
                        expected = inventory["projects"] if package == "younics.ai.agent" else inventory["projects"][:2]
                        if package == selected and mode == "source" and platform == "linux":
                            expected = [*expected, dict(inventory["projects"][-1], requiredOnly=True, sourceOnlyTests=[])]
                        self.assertEqual([row["path"] for row in expected], [call.args[1][2] for call in build.call_args_list])
                        observed = json.loads((self.source / "artifacts/reports/test-inventory.json").read_text())
                        self.assertEqual(expected, observed["projects"])
                        tests.assert_called_once()

    def test_preflight_runs_platform_script_groups_without_dependency_preparation(self):
        args = argparse.Namespace(source=self.source, packages=json.dumps(["younics.ai.agent.provider.openai"]))
        for platform, groups in (("linux", ["source", "release"]), ("win32", ["source"]), ("darwin", ["source"])):
            with self.subTest(platform=platform), patch.object(sys, "platform", platform), \
                 patch.object(ci, "generate_inventory") as inventory, patch.object(ci.subprocess, "run") as run, \
                 patch.object(ci, "prepare", side_effect=AssertionError("preflight must not hydrate dependencies")):
                ci.preflight(args)
                inventory.assert_called_once_with(self.source.resolve())
                self.assertEqual(groups, [call.args[0][-1] for call in run.call_args_list])
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-smoke.yml").read_text()
        dependencies = workflow.split("  dependencies:\n")[1].split("\n  source:")[0]
        self.assertIn("needs: [prepare, preflight]", dependencies)
        self.assertIn("needs: [prepare, preflight, dependencies, source]", workflow)

    def test_preparation_failure_cleans_temporary_state_and_does_not_report_unstarted_tests(self):
        work = []
        def fail(args):
            work.append(args.work.parent)
            raise ValueError("preparation failed")
        args = argparse.Namespace(source=self.source, core=self.root / "sunder-core", mode="source", inputs=self.root, candidate=None)
        with patch.object(ci, "generate_inventory"), patch.object(ci, "prepare", side_effect=fail), \
                patch.object(test_lanes, "validate") as tests:
            with self.assertRaisesRegex(ValueError, "preparation failed"): ci.validate(args)
            tests.assert_not_called()
        self.assertFalse(work[0].exists())

    def test_family_results_are_uploaded_only_after_archive_tests_were_attempted(self):
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-release.yml").read_text()
        upload = workflow.split("      - name: Retain family test results\n")[1].split("\n  publish:")[0]
        self.assertIn("id: archive_tests", workflow)
        for outcome in ("success", "failure", "cancelled"):
            self.assertIn(f"steps.archive_tests.outcome == '{outcome}'", upload)
        self.assertNotIn("steps.archive_tests.outcome == 'skipped'", upload)
        self.assertIn("if-no-files-found: error", upload)

    def prepare(self):
        self.core = self.root / "sunder-core"
        self.core.mkdir()
        self.work = self.root / "ordinary-ci"
        self.commands = []
        def run(command, **kwargs):
            self.commands.append(command)
            self.assertNotIn("GH_TOKEN", kwargs["env"])
            self.assertNotIn("SUNDER_REGISTRY_PUBLISH_TOKEN", kwargs["env"])
            self.assertNotIn("-m:1", command)
            self.assertNotIn("--force-evaluate", command)
            if command[:2] == ["dotnet", "build"]:
                output = Path(command[2]).parent / "bin/Release/custom-framework"
                output.mkdir(parents=True)
                for name in (*ci.inputs.TOOL_FILES, "Sunder.Package.Build.Tests.dll"):
                    (output / name).write_bytes(b"built tool " + name.encode())
            if "-t:RestoreSunderContracts" in command:
                Path(kwargs["env"]["SunderContractCacheRoot"]).mkdir(parents=True, exist_ok=True)
            return subprocess.CompletedProcess(command, 0, str(Path(command[2]).parent / "bin/Release/custom-framework") if "-getProperty:TargetDir" in command else "")
        args = argparse.Namespace(source=self.source, core=self.core, work=self.work,
                                  inputs=None, candidate=[self.archive], catalog=self.catalog_path, mode="source")
        with patch.object(ci, "head", return_value=self.source_sha), patch.object(ci.subprocess, "run", side_effect=run), \
             patch.object(ci.inputs, "offline_prefix", side_effect=AssertionError("ordinary CI does not request a production offline runner")), \
             patch.dict(os.environ, {"GH_TOKEN": "read-only-download", "SUNDER_REGISTRY_PUBLISH_TOKEN": "must-not-reach-build"}), \
             patch("sys.stdout", new_callable=io.StringIO):
            ci.prepare(args)
        return self.work / "ci-state.json"

    def test_fresh_preparation_restores_full_graph_and_only_core_hydrates_contracts(self):
        before = ci.inputs.source_snapshot(self.source)
        state_path = self.prepare()
        restores = [c for c in self.commands if c[:2] == ["dotnet", "restore"]]
        self.assertEqual(2, len(restores))
        self.assertTrue(all("--locked-mode" in c and "-r" not in c for c in restores))
        hydration = [c for c in self.commands if "-t:RestoreSunderContracts" in c]
        self.assertEqual(15, len(hydration))
        self.assertEqual(14, sum(any(a.startswith("-p:SunderPackageCandidatePath=") for a in c) for c in hydration))
        self.assertFalse(any("Accept" in a or "Freeze" in a or "ImplementSunder" in a for c in self.commands for a in c))
        self.assertEqual(before, ci.inputs.source_snapshot(self.source))
        with patch.object(ci, "head", return_value=self.source_sha):
            state, env = ci.context(state_path)
        self.assertEqual(str(self.catalog_path.resolve()), env["SUNDER_AGENT_SOURCE_PLATFORM_CATALOG"])
        self.assertEqual("true", state["properties"]["SunderPackageBuildTaskSourceIsImmutable"])
        self.assertNotIn("GeneratePackageOnBuild", state["properties"])

    def test_changed_tool_or_selected_archive_is_rejected_before_build(self):
        state_path = self.prepare()
        state = ci.inputs.load(state_path)
        for path in (Path(state["properties"]["SunderPackageBuildTaskAssembly"]), self.archive):
            before = path.read_bytes()
            path.write_bytes(before + b"changed")
            with patch.object(ci, "head", return_value=self.source_sha), patch.object(ci.subprocess, "run") as run:
                with self.assertRaisesRegex(ValueError, "bytes changed"):
                    ci.execute(state_path, ["dotnet", "build", "Sunder.AgentPackage.slnx", "--no-restore"])
                run.assert_not_called()
            path.write_bytes(before)

    def test_wrong_or_missing_candidates_fail_before_any_restore_or_state_creation(self):
        self.assertEqual({"younics.ai.agent"}, set(ci.inputs.verify_selected_files(self.source, [self.archive], self.catalog_path)))
        for paths in ([], [self.archive, self.archive]):
            with self.assertRaises(ValueError): ci.inputs.verify_selected_files(self.source, paths, self.catalog_path)
        self.catalog_path.write_bytes(self.catalog_path.read_bytes() + b" ")
        with self.assertRaisesRegex(ValueError, "Catalog bytes"):
            ci.inputs.verify_selected_files(self.source, [self.archive], self.catalog_path)

    def test_shared_dependency_bundle_is_portable_and_reverified_without_runner_local_seeds(self):
        transported = self.root / "another-runner"
        shutil.copytree(self.bundle, transported)
        core = self.root / "sunder-core"
        args = argparse.Namespace(inputs=transported)
        with patch.object(ci, "head", side_effect=lambda root: self.core_sha if root == core else self.source_sha), \
             patch.object(ci, "resolve", side_effect=AssertionError("consumers must reuse shared bytes")):
            candidates, catalog = ci.selected_files(args, self.source, core)
            self.assertEqual(self.archive.read_bytes(), candidates[0].read_bytes())
            self.assertEqual(self.catalog_path.read_bytes(), catalog.read_bytes())
            candidates[0].write_bytes(candidates[0].read_bytes() + b"changed in transport")
            with self.assertRaises(ValueError): ci.selected_files(args, self.source, core)

    def test_command_failure_and_input_mutation_are_not_hidden(self):
        state_path = self.prepare()
        with patch.object(ci, "head", return_value=self.source_sha), \
             patch.object(ci.subprocess, "run", return_value=subprocess.CompletedProcess([], 7)):
            self.assertEqual(7, ci.execute(state_path, ["dotnet", "build", "Sunder.AgentPackage.slnx", "--no-restore"]))
        def mutate(*_, **__):
            (self.source / "src/P1/sunder-contracts.lock.json").write_bytes(b"{}")
            return subprocess.CompletedProcess([], 0)
        with patch.object(ci, "head", return_value=self.source_sha), patch.object(ci.subprocess, "run", side_effect=mutate):
            with self.assertRaisesRegex(ValueError, "inputs changed"):
                ci.execute(state_path, ["dotnet", "build", "Sunder.AgentPackage.slnx", "--no-restore"])

    def test_source_runner_orders_readers_artifact_producer_and_required_cases_with_combined_evidence(self):
        lanes = []
        def run(state, lane):
            lanes.append(lane)
            self.assertEqual("1", os.environ["SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION"])
            if lane == "required-linux":
                self.assertEqual("1", os.environ["SUNDER_REQUIRE_TEST_PREREQUISITES"])
                raise ValueError("required case failed")
        with patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
                patch.object(sys, "platform", "linux"), patch.object(test_lanes, "tests", side_effect=run), \
                patch.object(test_lanes, "report") as report:
            with self.assertRaisesRegex(ValueError, "required case failed"): test_lanes.validate("state", "source")
            self.assertEqual(["source", "artifact", "required-linux"], lanes)
            report.assert_called_once_with("source", "linux")
        self.write_test_inventory()
        with patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), patch.object(test_lanes, "run_project", return_value=1) as execute:
            test_lanes.tests("state", "source")
            test_lanes.tests("state", "artifact")
        commands = [call.args[3] for call in execute.call_args_list]
        case = test_lanes.POLICY["artifactProducer"]
        self.assertIn("FullyQualifiedName!~" + case, execute.call_args_list[0].args[4])
        self.assertIn("FullyQualifiedName~" + case, execute.call_args_list[1].args[4])
        self.assertIn("Fixture.Tests", execute.call_args_list[0].args[2])
        self.assertIn("Integration.Tests", execute.call_args_list[1].args[2])
        for command in commands:
            self.assertIn("--no-build", command)
            self.assertIn("--no-restore", command)

    def test_platform_matrices_preserve_native_coverage_and_validate_runner_overrides(self):
        value = ci_platforms.matrices(True, {"SUNDER_AGENT_RUNNER_LINUX_ARM64": '["self-hosted","linux","ARM64"]'})
        self.assertEqual(set(ci_platforms.RIDS), {row["rid"] for row in value["native"]["include"]})
        self.assertEqual(4, len(value["behavior"]["include"]))
        self.assertEqual({"windows", "linux"}, {row["platform"] for row in value["preflight"]["include"]})
        self.assertEqual(2, len(value["preflight"]["include"]))
        self.assertEqual(["self-hosted", "linux", "ARM64"], next(row["runner"] for row in value["native"]["include"] if row["rid"] == "linux-arm64"))
        for labels in ('[]', '"ubuntu-latest"', '["linux\\noutput=changed"]'):
            with self.assertRaises(ValueError): ci_platforms.matrices(variables={"SUNDER_AGENT_RUNNER_LINUX_ARM64": labels})

    def test_required_linux_lane_rejects_missing_test_identity_even_with_successful_command(self):
        self.write_test_inventory()
        results = self.source / "artifacts/test-results/required"
        def execute(*args):
            (results / "fixture.trx").write_text('<TestRun><TestDefinitions><UnitTest id="1"><TestMethod name="AnotherPassingTest" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="1" outcome="Passed" /></Results></TestRun>')
            return 1
        with patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
             patch.object(test_lanes, "run_project", side_effect=execute), patch.object(test_lanes, "run"):
            with self.assertRaisesRegex(ValueError, "identity missing"): test_lanes.tests("state", "required-linux")

    def test_adapter_selection_is_saved_before_execution_and_empty_selection_does_not_run(self):
        output = self.root / "reports"
        output.mkdir()
        common = ["dotnet", "test", "solution", "--no-restore", "--no-build"]
        cases = ["Fixture.Tests.Theory(value: 1)"]
        def execute(state, command, log):
            log.write_text("The following Tests are available:\n" + "".join("    " + case + "\n" for case in cases)
                           if "--list-tests" in command else "passed\n")
            return 0
        with patch.object(ci, "execute", side_effect=execute) as run, \
             patch.object(ci, "context", return_value=({}, {"SUNDER_REQUIRE_TEST_PREREQUISITES": "1"})):
            self.assertEqual(1, test_lanes.run_project("state", self.source, "Fixture.Tests.csproj", common, "Lane=Test", output))
            self.assertIn("--blame-hang", run.call_args.args[1])
            self.assertIn("--diag", run.call_args.args[1])
            record = json.loads((output / "Fixture.Tests.discovery.json").read_text())
            self.assertEqual(cases, record["tests"])
            self.assertEqual("Lane=Test", record["filter"])
            self.assertEqual("1", record["prerequisites"]["SUNDER_REQUIRE_TEST_PREREQUISITES"])
            cases.clear()
            run.reset_mock()
            self.assertEqual(0, test_lanes.run_project("state", self.source, "Fixture.Tests.csproj", common, "Lane=Empty", output))
            self.assertEqual(1, run.call_count)
            cases.append("Fixture.Tests." + test_lanes.POLICY["artifactProducer"])
            test_lanes.run_project("state", self.source, "Fixture.Tests.csproj", common, "Lane=Artifact", output)
            self.assertIn("--blame", run.call_args.args[1])
            self.assertNotIn("--blame-hang", run.call_args.args[1])
        with self.assertRaisesRegex(ValueError, "no discovery header"):
            test_lanes.discovered_tests("adapter crashed")


class InventoryProcessTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory(prefix="agent inventory with spaces ")
        self.addCleanup(scope.cleanup)
        self.source = Path(scope.name)
        self.script = self.source / "scripts/test/generate-test-inventory.sh"
        self.script.parent.mkdir(parents=True)
        self.reports = self.source / "artifacts/reports"
        self.reports.mkdir(parents=True)
        self.inventory = self.reports / "test-inventory.json"

    def test_failure_removes_stale_inventory_and_keeps_stdout_stderr_and_exit_code(self):
        for status, output in ((23, "fixture inventory discovery failed"), (0, "not JSON"), (0, '{}'),
                               (0, '{"schemaVersion":2,"solution":"Sunder.AgentPackage.slnx","projects":[{}]}')):
            with self.subTest(status=status, output=output):
                self.inventory.write_text('{"stale":true}')
                self.script.write_bytes(f"printf '%s\\n' '{output}'\nprintf 'fixture stderr\\n' >&2\nexit {status}\n".encode("utf-8"))
                diagnostics = io.StringIO()
                with patch("sys.stderr", diagnostics), self.assertRaises((ValueError, subprocess.CalledProcessError)) as failure:
                    ci.generate_inventory(self.source)
                if status:
                    self.assertEqual(status, failure.exception.returncode)
                self.assertFalse(self.inventory.exists())
                self.assertFalse(list(self.reports.glob(".test-inventory-*")))
                record = json.loads((self.reports / "test-inventory.command.json").read_text())
                self.assertEqual(status, record["exitCode"])
                self.assertTrue(record["error"])
                self.assertIn(output, diagnostics.getvalue())
                self.assertIn("fixture stderr", diagnostics.getvalue())
                self.assertEqual(output + "\n", (self.reports / "test-inventory.stdout.log").read_text())
                self.assertEqual("fixture stderr\n", (self.reports / "test-inventory.stderr.log").read_text())

    def test_success_writes_only_valid_inventory_and_cli_preserves_failure_status(self):
        value = dict(schemaVersion=2, solution="Sunder.AgentPackage.slnx",
                     projects=[dict(path="tests/Fixture.csproj", owner="agent-family", modes=["source"])])
        self.script.write_bytes(("printf '%s\\n' '" + json.dumps(value) + "'\n").encode("utf-8"))
        self.assertEqual(self.inventory, ci.generate_inventory(self.source))
        self.assertEqual(value, json.loads(self.inventory.read_text()))
        self.assertFalse(list(self.reports.glob(".test-inventory-*")))
        self.script.write_bytes(b"printf 'original failure\\n' >&2\nexit 23\n")
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "ci/agent_ci.py"), "preflight", "--source", str(self.source)],
                                capture_output=True, text=True)
        self.assertEqual(23, result.returncode, result.stdout + result.stderr)
        self.assertIn("original failure", result.stderr)
        self.assertFalse(self.inventory.exists())


if __name__ == "__main__": unittest.main()
