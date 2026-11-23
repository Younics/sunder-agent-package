"""Ordinary CI uses exact inputs without production approval or cache synthesis."""
import argparse
from contextlib import nullcontext, redirect_stdout
import errno
import io
import json
import os
from pathlib import Path
import plistlib
import re
import runpy
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SCRIPTS / "ci"))
import agent_ci as ci
import platform
import test_lanes
import ci_platforms
import bash_runtime
import test_filesystems
import project_scope
import required_test_evidence

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class AgentCiTests(unittest.TestCase):
    def setUp(self):
        fixture_setup(self)
        # Python 3.11 on Windows may query cmd.exe for platform metadata. Keep
        # that OS probe outside the build-command mock without inventing a RID.
        machine = platform.machine()
        architecture = patch("core_tools.platform.machine", return_value=machine)
        architecture.start()
        self.addCleanup(architecture.stop)

    def write_test_inventory(self):
        path = self.source / "artifacts/reports/test-inventory.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"projects": [{"path": "tests/Fixture.Tests/Fixture.Tests.csproj", "modes": ["source"], "owner": "younics.ai.agent.tools.files"}]}))

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
        resolve.assert_called_once_with(str(self.source), "../sunder-core", str(output), None, allow_missing=False)
        self.assertFalse(output.exists())

    def test_workflow_retains_input_failure_without_masking_exit_or_reporting_unstarted_tests(self):
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-smoke.yml").read_text()
        section = workflow.split("      - name: Resolve exact selected archives, catalog and Core source\n")[1].split("\n      - ")[0]
        script = textwrap.dedent(section.split("        run: |\n")[1])
        stub = self.root / "scripts/ci/agent_ci.py"
        stub.parent.mkdir(parents=True)
        stub.write_text('import sys\nprint("fixture: exact CI inputs missing", file=sys.stderr)\nsys.exit(7)\n')
        result = subprocess.run(bash_runtime.command("-c", script), cwd=self.root, text=True, capture_output=True, timeout=10,
                                 env={**os.environ, "GITHUB_SHA": self.source_sha, "CORE_COMMIT": self.core_sha, "RUNNER_TEMP": str(self.root), "PACKAGES": '["younics.ai.agent"]'})
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

    def test_workflow_optional_stress_arguments_preserve_bash_nounset_and_exact_arguments(self):
        workflows = SCRIPTS.parent / ".github/workflows"
        for workflow, step, component in (
                ("sunder-package-component.yml", "Build and test the selected component", True),
                ("sunder-package-smoke.yml", "Build and test selected reference mode", False)):
            text = (workflows / workflow).read_text()
            section = text.split("      - name: " + step + "\n", 1)[1].split("\n      - name:", 1)[0]
            script = "set -euo pipefail\n" + textwrap.dedent(section.split("        run: |\n", 1)[1])
            script = script.replace("python3 -B scripts/ci/agent_ci.py validate", 'printf "%s\\n" validate')
            for stress in ("false", "true"):
                with self.subTest(workflow=workflow, stress=stress):
                    environment = {**os.environ, "RUNNER_TEMP": str(self.root / "input space"),
                                   "MODE": "package", "PACKAGE": "younics.ai.agent", "STRESS": stress}
                    result = subprocess.run(bash_runtime.command("-c", script), env=environment,
                                            text=True, capture_output=True, timeout=10)
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    expected = ["validate", "--core", "../sunder-core", "--inputs",
                                environment["RUNNER_TEMP"] + "/agent-inputs", "--tools",
                                environment["RUNNER_TEMP"] + "/core-tools", "--mode", "package"]
                    expected += ["--packages", '["younics.ai.agent"]'] if component else ["--shared"]
                    if stress == "true": expected.append("--stress")
                    self.assertEqual(expected, result.stdout.splitlines())

    def test_windows_native_gate_requires_exact_current_rows_once_on_both_abis(self):
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-qualify.yml").read_text()
        self.assertIn("needs: selection\n", workflow)
        self.assertNotIn("\n  arm-components:", workflow)
        self.assertNotIn("\n  push:", workflow)
        self.assertNotIn("younics.", workflow)
        self.assertIn("default: all", workflow)
        self.assertIn("cancel-in-progress: false", workflow)
        reusable = (SCRIPTS.parent / ".github/workflows/sunder-package-smoke.yml").read_text()
        self.assertIn('required_test_evidence.py --packages "$PACKAGES" --plan', reusable)
        self.assertIn('required_test_evidence.py --packages "$PACKAGES" --results required-results', reusable)
        matrix = ci_platforms.matrices(package_tests=True)["behavior"]["include"]
        self.assertEqual({"source", "package"}, {row["mode"] for row in matrix if row["rid"] == "win-arm64"})
        package = "younics.ai.agent.execution.local"
        policy = json.loads(required_test_evidence.POLICY.read_text())
        requirements = required_test_evidence.requirements(self.source, policy, [package])
        self.assertEqual(1, len(requirements))
        cases = sorted(requirements[0]["cases"])
        self.assertEqual(34, len(cases))
        class_name = "Sunder.Package.Agent.Execution.Local.Tests.WindowsSecureMutationTests"
        self.assertEqual(class_name, requirements[0]["className"])
        self.assertEqual(["win-x64", "win-arm64"], requirements[0]["rids"])
        self.assertEqual(34, requirements[0]["expectedCount"])
        for defect in ("none", "omitted", "duplicate", "substituted", "skipped", "failed", "missing-abi"):
            directory = self.root / ("native-gate-" + defect)
            directory.mkdir()
            for rid in ("win-x64", "win-arm64"):
                if defect == "missing-abi" and rid == "win-arm64": continue
                selected = cases[:-1] if defect == "omitted" and rid == "win-arm64" else list(cases)
                if defect == "duplicate" and rid == "win-arm64": selected.append(cases[0])
                if defect == "substituted" and rid == "win-arm64": selected[0] += "-unexpected-row"
                document = ET.Element("TestRun")
                definitions = ET.SubElement(document, "TestDefinitions")
                results = ET.SubElement(document, "Results")
                for index, case in enumerate(selected):
                    definition = ET.SubElement(definitions, "UnitTest", id=str(index))
                    ET.SubElement(definition, "TestMethod", className=class_name, name=case.split("(", 1)[0])
                    outcome = ("NotExecuted" if defect == "skipped" else "Failed") if index == 0 and rid == "win-arm64" and defect in ("skipped", "failed") else "Passed"
                    ET.SubElement(results, "UnitTestResult", testId=str(index), testName=class_name + "." + case, outcome=outcome)
                output = directory / "windows-results" / f"agent-component-{package}-source-{rid}-1"
                output.mkdir(parents=True)
                ET.ElementTree(document).write(output / "native.trx")
            with self.subTest(defect=defect):
                output = io.StringIO()
                with redirect_stdout(output):
                    if defect == "none":
                        result = required_test_evidence.verify(directory / "windows-results", requirements)
                        self.assertEqual([34, 34], [row["passed"] for row in result])
                    else:
                        with self.assertRaises(ValueError):
                            required_test_evidence.verify(directory / "windows-results", requirements)
                if defect == "none": self.assertEqual(2, output.getvalue().count("34 passed; zero skipped"))

    def test_required_evidence_is_metadata_driven_for_other_owners_modes_and_platforms(self):
        owner = self.rows[2]["packageId"]
        row = dict(owner=owner, mode="package", rids=["linux-arm64", "osx-x64"],
                   className="Future.Tests.RoundTripTests", methodName="RoundTrip", expectedCount=2)
        policy = dict(schemaVersion=1, requirements=[row])
        required = required_test_evidence.requirements(self.source, policy, [owner])
        selection = required_test_evidence.plan(required)
        self.assertTrue(selection["has_requirements"])
        for rid in row["rids"]:
            name = f"agent-component-{owner}-package-{rid}-7"
            self.assertIn(name.removesuffix("7") + "*", selection["pattern"])
            output = self.root / "required-results" / name
            output.mkdir(parents=True)
            document = ET.Element("TestRun")
            definitions, results = ET.SubElement(document, "TestDefinitions"), ET.SubElement(document, "Results")
            for index in range(2):
                definition = ET.SubElement(definitions, "UnitTest", id=str(index))
                ET.SubElement(definition, "TestMethod", className=row["className"], name=row["methodName"])
                ET.SubElement(results, "UnitTestResult", testId=str(index), testName=f"RoundTrip(value: {index})", outcome="Passed")
            ET.ElementTree(document).write(output / "future.trx")
        with redirect_stdout(io.StringIO()):
            verified = required_test_evidence.verify(self.root / "required-results", required)
        self.assertEqual([2, 2], [row["passed"] for row in verified])
        self.assertEqual(dict(has_requirements=False, pattern=""),
                         required_test_evidence.plan(required_test_evidence.requirements(self.source, policy, [])))
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "ci/required_test_evidence.py"),
                                 "--packages", '["younics.ai.agent.skills"]', "--plan"],
                                cwd=self.root, text=True, capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(dict(has_requirements=False, pattern=""), json.loads(result.stdout))
        for changed in (dict(row, owner="unknown.package"), dict(row, rids=["unknown-rid"]), dict(row, expectedCount=0),
                        dict(row, cases=["one"]), dict(row, unrecognized=True)):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                required_test_evidence.requirements(self.source, dict(policy, requirements=[changed]), [owner])
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            required_test_evidence.requirements(self.source, dict(policy, requirements=[row, row]), [owner])

    def test_selected_builds_and_reports_share_the_owned_project_inventory(self):
        selected = "younics.ai.agent.provider.openai"
        inventory = dict(schemaVersion=2, solution="Sunder.AgentPackage.slnx", projects=[
            dict(path="tests/Selected.csproj", owner=selected, modes=["source", "package"]),
            dict(path="tests/Family.csproj", owner="agent-family", modes=["source", "package"]),
            dict(path="tests/Agent.csproj", owner="younics.ai.agent", modes=["source", "package"]),
            dict(path="tests/Unchanged.csproj", owner="younics.ai.agent.provider.gemini", modes=["source", "package"]),
            dict(path="tests/Local.csproj", owner="younics.ai.agent.execution.local", modes=["source", "package"], sourceOnlyTests=["ordinary"])])
        for row in inventory["projects"]:
            row["lanes"] = ["PackageCompatibility"]
        (self.source / "Sunder.AgentPackage.slnx").write_text("<Solution>" + "".join(
            f'<Project Path="{row["projectPath"]}" />' for row in self.rows) + "".join(
            f'<Project Path="{row["path"]}" />' for row in inventory["projects"]) + "</Solution>")
        def prepare(args):
            args.work.mkdir(parents=True)
            target = project_scope.write_build_graph(self.source, args.project, args.work)
            (args.work / "ci-state.json").write_bytes(ci.inputs.canonical(dict(
                projects=args.project, buildProject=str(target), inputHashes={str(target): ci.inputs.file_sha(target)})))
        for platform in ("linux", "win32", "darwin"):
            for mode in ("source", "package"):
                for package in (selected, "younics.ai.agent"):
                    with self.subTest(platform=platform, mode=mode, package=package):
                        args = argparse.Namespace(source=self.source, core=self.root / "sunder-core", mode=mode, inputs=self.root,
                                                  candidate=None, packages=json.dumps([package]), stress=False)
                        result = subprocess.CompletedProcess(["fixture-bash"], 0, json.dumps(inventory), "")
                        with patch.object(sys, "platform", platform), patch.object(bash_runtime, "command", return_value=["fixture-bash"]), \
                             patch.object(ci.subprocess, "run", return_value=result), patch.object(ci, "prepare", side_effect=prepare), \
                             patch.object(ci, "execute", return_value=0) as build, patch.object(test_lanes, "validate") as tests, \
                             patch.object(test_lanes, "retain_verifier"):
                            ci.validate(args)
                        expected = [row for row in inventory["projects"] if row["owner"] == package]
                        canonical = next(row["projectPath"] for row in self.rows if row["packageId"] == package)
                        self.assertEqual(1, build.call_count)
                        selection = json.loads((self.source / "artifacts/reports/build-selection.json").read_text())
                        self.assertEqual(sorted([canonical, *[row["path"] for row in expected]]), selection["projects"])
                        self.assertEqual(selection["buildProject"], build.call_args.args[1][2])
                        self.assertTrue(selection["buildProject"].endswith("selected.slnf"))
                        self.assertEqual(self.source.resolve() / "artifacts/reports/build.log", build.call_args.kwargs["log"])
                        observed = json.loads((self.source / "artifacts/reports/test-inventory.json").read_text())
                        self.assertEqual(expected, observed["projects"])
                        tests.assert_called_once()

    def test_component_without_owned_compatibility_stops_before_restore_or_build(self):
        inventory = dict(projects=[dict(path="tests/SourceOnly.csproj", modes=["source"], lanes=[]),
                                   dict(path="tests/Ordinary.csproj", modes=["source", "package"], lanes=[])])
        path = self.source / "artifacts/reports/test-inventory.json"
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(inventory))
        args = argparse.Namespace(source=self.source, mode="package", packages=json.dumps([self.rows[0]["packageId"]]))
        with patch.object(ci, "generate_inventory", return_value=path), patch.object(ci, "prepare") as prepare, \
             patch.object(ci, "execute") as execute, patch.object(test_lanes, "validate") as validate:
            ci.validate(args)
        prepare.assert_not_called()
        execute.assert_not_called()
        validate.assert_not_called()
        self.assertEqual([], json.loads(path.read_text())["projects"])
        receipt = json.loads((path.parent / "compatibility-selection.json").read_text())
        self.assertEqual("no-owned-compatibility", receipt["disposition"])
        self.assertEqual([self.rows[0]["packageId"]], receipt["packageIds"])

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
        dependencies = workflow.split("  dependency-discovery:\n")[1].split("\n  tools:")[0]
        self.assertIn("needs: [prepare, preflight]", dependencies)
        self.assertIn("needs: [prepare, preflight, dependency-discovery, tools, bootstrap, dependencies, components, verifier, source]", workflow)

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
        upload = workflow.split("      - name: Retain family test results\n")[1].split("\n  stage:")[0]
        self.assertIn("id: archive_tests", workflow)
        for outcome in ("success", "failure", "cancelled"):
            self.assertIn(f"steps.archive_tests.outcome == '{outcome}'", upload)
        self.assertNotIn("steps.archive_tests.outcome == 'skipped'", upload)
        self.assertIn("if-no-files-found: error", upload)

    def test_release_plan_path_is_not_exported_as_an_msbuild_version(self):
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-release.yml").read_text()
        for job, following in (("smoke", "fan-in"), ("fan-in", "stage")):
            with self.subTest(job=job):
                section = workflow.split(f"\n  {job}:\n", 1)[1].split(f"\n  {following}:", 1)[0]
                self.assertNotRegex(section, r"(?im)^\s+VERSION:|\$(?:VERSION\b|\{VERSION\})")
                self.assertIn("SUNDER_AGENT_RELEASE_PLAN_PATH: ${{ github.workspace }}/sunder-agent-package/artifacts/release-plan.json", section)
                self.assertIn('file-sha "$SUNDER_AGENT_RELEASE_PLAN_PATH"', section)

    def test_native_smoke_passes_exact_preparation_arguments_under_bash_nounset(self):
        launcher = (SCRIPTS / "release/run-native-runtime-smoke.sh").read_text()
        self.assertIn("\nset -euo pipefail\n", launcher)
        preparation = launcher[launcher.index("# The Maker job driver"):].split("\napp_target=", 1)[0]
        script = textwrap.dedent("""\
            set -euo pipefail
            mode="$1"
            script_dir="agent source/scripts/release"
            core_repository="core source"
            metrics_directory="native metrics"
            expected_core_commit="$2"
            python3() { printf '%s\\n' "$@"; }
            """) + preparation
        for mode in ("development", "published"):
            with self.subTest(mode=mode):
                result = subprocess.run(bash_runtime.command("-c", script, "--", mode, self.core_sha),
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)
                expected = ["-B", "agent source/scripts/release/prepare_maker_inputs.py", "core source",
                            "native metrics/maker-inputs", self.core_sha]
                self.assertEqual(expected + (["--host-only"] if mode == "published" else []), result.stdout.splitlines())

    def test_prepublication_family_uses_development_and_supervised_app_gates_without_credentials(self):
        import release_inputs
        core = self.root / "family-core"
        baseline = core / "eng/platform-contracts/sunder-contracts.owned.json"
        baseline.parent.mkdir(parents=True)
        baseline.write_text(json.dumps({"contracts": {"stack": {"current": {"catalogRelease": {"releaseVersion": "2.0.0"}}}}}))
        archives = self.root / "family-archives"
        archives.mkdir()
        for name in ("owner", "maker"):
            (archives / (name + ".sunderpkg")).write_bytes(name.encode())
        output = self.root / "family-proof"
        ids = {"owner.sunderpkg": "younics.ai.agent", "maker.sunderpkg": "younics.sunder.package.maker"}
        with patch.object(test_lanes, "ROOT", self.source), patch.object(test_lanes, "run") as run, \
             patch.object(test_lanes.subprocess, "check_output", return_value="tool.dll") as evaluate, \
             patch.object(release_inputs, "archive_metadata", side_effect=lambda path: ({"id": ids[path.name]}, None)), \
             patch.dict(os.environ, {"GH_TOKEN": "must-not-reach-tests", "SUNDER_REGISTRY_API_KEY": "must-not-reach-tests",
                                     "SUNDER_REGISTRY_PUBLISH_TOKEN": "must-not-reach-tests", "SUNDER_REGISTRY_CLI_TOKEN": "must-not-reach-tests",
                                     "SUNDER_AGENT_NATIVE_REGISTRY_TOKEN": "must-not-reach-tests", "SUNDER_REGISTRY_NATIVE_READ_TOKEN": "must-not-reach-tests",
                                     "SUNDER_REQUIRE_AGENT_INSTALLED_FAMILY": "1"}):
            test_lanes.family(core, archives, output)
            calls = run.call_args_list
            before = len(calls)
            with self.assertRaises(FileExistsError):
                test_lanes.family(core, archives, output)
            self.assertEqual(before, run.call_count, "a repeated result directory must fail before invoking any tools")
        tests = [call for call in calls if call.args[:2] == ("dotnet", "test")]
        self.assertEqual(2, len(tests))
        self.assertIn("Sunder.App.Integration.Tests", str(tests[1].args[2]))
        selectors = " ".join(str(arg) for call in tests for arg in call.args)
        self.assertIn("DevelopmentArchives_ActivateUnloadReloadAndReplaceAsOneFamily", selectors)
        self.assertIn("RuntimeProducedAgentSnapshot_MaterializesAndActivatesInSupervisedApp", selectors)
        self.assertNotIn("ReleaseArchives_InstallActivateUnloadReloadAndReinstallAsOneFamily", selectors)
        self.assertEqual(2, evaluate.call_count)
        for call in [*calls, *evaluate.call_args_list]:
            environment = call.kwargs["env"]
            self.assertFalse(any(key in environment for key in ("GH_TOKEN", "SUNDER_REGISTRY_API_KEY", "SUNDER_REGISTRY_PUBLISH_TOKEN", "SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_AGENT_NATIVE_REGISTRY_TOKEN", "SUNDER_REGISTRY_NATIVE_READ_TOKEN", "SUNDER_REQUIRE_AGENT_INSTALLED_FAMILY")))
            self.assertEqual(str(output.resolve() / "app-snapshot"), environment["SUNDER_AGENT_UI_SNAPSHOT_DIR"])
            self.assertEqual(str(self.source), environment["SUNDER_AGENT_CURRENT_BASELINE_ROOT"])
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-release.yml").read_text()
        build = workflow.split("      - name: Build Core validation paths\n")[1].split("      - name:")[0]
        self.assertIn("tests/Sunder.App.Integration.Tests", build)

    def test_publication_api_key_keeps_its_environment_secret_name_through_component_calls(self):
        workflows = SCRIPTS.parent / ".github/workflows"
        workflow = (workflows / "sunder-package-release.yml").read_text()
        for name, following in (("publish-owner", "publish-consumers"), ("publish-consumers", "installed-native")):
            caller = workflow.split(f"  {name}:\n")[1].split(f"\n  {following}:")[0]
            self.assertIn("SUNDER_REGISTRY_API_KEY: ${{ secrets.SUNDER_REGISTRY_API_KEY }}", caller)
        component = (workflows / "sunder-package-component.yml").read_text()
        self.assertIn("      SUNDER_REGISTRY_API_KEY:\n        required: false", component)
        publish = component.split("  publish:\n")[1].split("\n  summary:")[0]
        self.assertIn("environment: sunder-agent-release-promotion", publish)
        self.assertIn("SUNDER_REGISTRY_API_KEY: ${{ secrets.SUNDER_REGISTRY_API_KEY }}", publish)
        self.assertNotIn("publish_token", component)
        for path in workflows.glob("*.yml"):
            text = path.read_text()
            for credential in ("SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_NATIVE_READ_TOKEN", "SUNDER_REGISTRY_PUBLISH_TOKEN"):
                self.assertNotIn("secrets." + credential, text, str(path))
        rollback = (workflows / "sunder-agent-cohort-rollback.yml").read_text()
        self.assertIn("reverse_agent_cohort.py plan", rollback)
        self.assertNotIn("reverse_agent_cohort.py apply", rollback)

    def test_published_native_acceptance_is_mandatory_after_publication_and_before_visibility(self):
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-release.yml").read_text()
        installed = workflow.split("  installed-native:\n")[1].split("\n  release:")[0]
        release = workflow.split("  release:\n")[1].split("\n  summary:")[0]
        self.assertIn("needs: [prepare, stage, publish-owner, publish-consumers]", installed)
        self.assertIn('"$CORE_COMMIT" published', installed)
        self.assertNotIn("secrets.", installed)
        self.assertIn("REGISTRY_API_URL: ${{ vars.SUNDER_REGISTRY_API_URL }}", installed)
        self.assertIn("agent_pipeline.py recover --read-only", installed)
        self.assertIn("needs.installed-native.result == 'success'", release)
        self.assertLess(release.index("retain-installed-native"), release.index("agent_pipeline.py visibility"))
        self.assertIn("installed-native", workflow.split("  summary:\n")[1])
        launcher = (SCRIPTS / "release/run-native-runtime-smoke.sh").read_text()
        self.assertIn("SUNDER_REGISTRY_API_KEY", launcher.split("unset ", 1)[1])
        self.assertNotIn("SUNDER_AGENT_NATIVE_REGISTRY_TOKEN=", launcher)
        # Exercise the real launcher prologue without native artifacts/toolchains.
        # Both phases must strip credentials before the first external operation.
        prologue = launcher.split('[[ "$expected_core_commit" =~', 1)[0]
        credentials = ("GH_TOKEN", "GITHUB_TOKEN", "SUNDER_REGISTRY_API_KEY", "SUNDER_REGISTRY_PUBLISH_TOKEN",
                       "SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_NATIVE_READ_TOKEN", "SUNDER_AGENT_NATIVE_REGISTRY_TOKEN")
        script = prologue + '\nfor credential in ' + " ".join(credentials) + '; do [[ -z "${!credential+x}" ]] || exit 1; done\n'
        for mode in ("development", "published"):
            with self.subTest(mode=mode):
                result = subprocess.run(bash_runtime.command("-c", script, "--", self.root.as_posix(), self.bundle.as_posix(),
                    "metrics", "a" * 40, mode),
                    env={**os.environ, **dict.fromkeys(credentials, "must-not-reach-tools"),
                         "SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN": "https://registry.test"},
                    capture_output=True, text=True, check=False)
                self.assertEqual(0, result.returncode, result.stderr)
        fixture = (SCRIPTS.parent / "tests/fixtures/AgentNativeReleaseSmokeTests.cs").read_text()
        self.assertNotIn("RegistryApiRoutes.CurrentUser", fixture)
        self.assertIn("Assert.Null(request.Headers.Authorization)", fixture)
        self.assertNotIn("request.Headers.Authorization =", fixture)
        self.assertNotIn("SUNDER_AGENT_NATIVE_REGISTRY_TOKEN", fixture)
        self.assertIn('archiveDownloadAuthentication = "anonymous"', fixture)
        section = installed.split("      - name: Download anonymously and exercise exact published archives before visibility\n", 1)[1].split("\n      - name:", 1)[0]
        script = textwrap.dedent(section.split("        run: |\n", 1)[1]).split("bash scripts/release/run-native-runtime-smoke.sh", 1)[0]
        script += 'printf "%s" "$SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN" > launcher-reached\n'
        for name, plan in (("malformed", "{not-json"), ("missing-origin", "{}"),
                           ("valid", '{"registryOrigin":"https://registry.test"}')):
            with self.subTest(plan=name):
                directory = self.root / ("origin-read-" + name)
                (directory / "artifacts/release").mkdir(parents=True)
                (directory / "artifacts/release/release-plan.json").write_text(plan)
                result = subprocess.run(bash_runtime.command("-c", script), cwd=directory, text=True, capture_output=True,
                                        env={**os.environ, "SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN": "https://stale.test"})
                self.assertEqual(name == "valid", result.returncode == 0, result.stdout + result.stderr)
                self.assertEqual(name == "valid", (directory / "launcher-reached").exists())
                if name == "valid": self.assertEqual("https://registry.test", (directory / "launcher-reached").read_text())

    def prepare(self):
        self.core = self.root / "sunder-core"
        self.core.mkdir()
        self.work = self.root / "ordinary-ci"
        (self.source / "Sunder.AgentPackage.slnx").write_text("<Solution />")
        self.commands = []
        def run(command, **kwargs):
            self.commands.append(command)
            self.assertNotIn("GH_TOKEN", kwargs["env"])
            self.assertFalse("SUNDER_REGISTRY_API_KEY" in kwargs["env"], "API key leaked into build environment")
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
             patch.dict(os.environ, {"GH_TOKEN": "read-only-download", "SUNDER_REGISTRY_API_KEY": "must-not-reach-build",
                                     "SUNDER_REGISTRY_PUBLISH_TOKEN": "must-not-reach-build"}), \
             patch("sys.stdout", new_callable=io.StringIO):
            ci.prepare(args)
        return self.work / "ci-state.json"

    def test_fresh_preparation_restores_full_graph_and_only_core_hydrates_contracts(self):
        before = ci.inputs.source_snapshot(self.source)
        state_path = self.prepare()
        restores = [c for c in self.commands if c[:2] == ["dotnet", "restore"]]
        self.assertEqual(2, len(restores))
        self.assertTrue(all("--locked-mode" in c and "-r" not in c for c in restores))
        restore = next(command for command in restores if command[2] == str((self.source / "Sunder.AgentPackage.slnx").resolve()))
        self.assertIn("-p:SunderPlatformCatalogPath=" + str(self.catalog_path.resolve()), restore)
        self.assertIn("-p:SunderPackageCandidatePath=" + str(self.archive.resolve()), restore)
        self.assertNotIn("-p:SunderRestoreContracts=false", restore)
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
        self.assertNotIn("SunderPackageCandidatePath", state["properties"])
        self.assertNotIn("SunderPlatformCatalogPath", state["properties"])

    def test_bootstrap_owner_supplies_verified_catalog_to_initial_restore_without_a_candidate_seed(self):
        tools = Path(ci.inputs.load(self.prepare())["tools"])
        owner = self.rows[0]
        args = argparse.Namespace(source=self.source, core=self.core, work=self.root / "owner-ci", mode="package",
                                  packages=json.dumps([owner["packageId"]]), candidate=[], catalog=self.catalog_path,
                                  inputs=None, tools=tools)
        result = subprocess.CompletedProcess([], 0, json.dumps(dict(Items=dict(ProjectReference=[]))))
        with patch.object(ci, "head", return_value=self.source_sha), patch.object(ci.subprocess, "run", return_value=result) as run, \
             patch("core_tools.package_tasks", return_value={name: tools / name for name in ci.inputs.TOOL_FILES}), \
             patch("sys.stdout", new_callable=io.StringIO):
            ci.prepare(args)
        restore = next(call.args[0] for call in run.call_args_list if call.args[0][:2] == ["dotnet", "restore"])
        self.assertIn("-p:SunderPlatformCatalogPath=" + str(self.catalog_path.resolve()), restore)
        self.assertFalse(any(part.startswith("-p:SunderPackageCandidatePath=") for part in restore))
        self.assertNotIn("-p:SunderRestoreContracts=false", restore)
        state = ci.inputs.load(args.work / "ci-state.json")
        self.assertEqual([], state["candidates"])
        self.assertEqual([owner["packageId"]], state["packages"])
        self.assertFalse((self.source / ".sunder/contract-inputs").exists())

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

    def test_component_preparation_reuses_tools_and_only_restores_and_hydrates_its_graph(self):
        full_state = self.prepare()
        tools = ci.inputs.load(full_state)["tools"]
        component = self.rows[1]
        args = argparse.Namespace(source=self.source, core=self.core, work=self.root / "component-ci",
                                  inputs=None, candidate=[self.archive], catalog=self.catalog_path, mode="package",
                                  packages=json.dumps([component["packageId"]]), tools=tools)
        published = (self.root / "nuget/tasks").resolve()
        published.mkdir(parents=True)
        for name in ci.inputs.TOOL_FILES:
            (published / name).write_bytes(b"published immutable " + name.encode())
        commands = []
        def run(command, **kwargs):
            commands.append(command)
            target = "_CollectSunderPackageBuildTaskDependencyFiles"
            if "-getTargetResult:" + target in command:
                return subprocess.CompletedProcess(command, 0, json.dumps(dict(
                    Properties=dict(SunderPackageBuildTaskSourceIsImmutable="true"),
                    TargetResults={target: dict(Result="Success", Items=[dict(TargetFileName=name, FullPath=str(published / name))
                        for name in ci.inputs.TOOL_FILES])})))
            return subprocess.CompletedProcess(command, 0, json.dumps(dict(Items=dict(ProjectReference=[]))))
        with patch.object(ci, "head", return_value=self.source_sha), patch.object(ci.subprocess, "run", side_effect=run), \
             patch("sys.stdout", new_callable=io.StringIO):
            ci.prepare(args)
            state, _ = ci.context(args.work / "ci-state.json")
            with self.assertRaisesRegex(ValueError, "outside the prepared component scope"):
                ci.execute(args.work / "ci-state.json", ["dotnet", "build", self.rows[2]["projectPath"], "--no-restore"])
        self.assertEqual([component["packageId"]], state["packages"])
        self.assertEqual([component["projectPath"]], state["projects"])
        self.assertEqual(1, sum(command[:2] == ["dotnet", "restore"] for command in commands))
        self.assertEqual(1, sum("-t:RestoreSunderContracts" in command for command in commands))
        self.assertFalse(any(command[:2] == ["dotnet", "build"] or command[2].endswith(".slnx") for command in commands))
        self.assertEqual(str(published / "Sunder.Package.Build.dll"), state["properties"]["SunderPackageBuildTaskAssembly"])
        self.assertTrue(all(str(published / name) in state["inputHashes"] for name in ci.inputs.TOOL_FILES))
        (published / "Sunder.Package.Build.Editor.dll").write_bytes(b"changed published analyzer")
        with patch.object(ci, "head", return_value=self.source_sha):
            with self.assertRaisesRegex(ValueError, "bytes changed"):
                ci.context(args.work / "ci-state.json")

    def test_selected_graph_restores_once_and_rejects_changed_filter_or_roots_before_execution(self):
        full_state = self.prepare()
        roots = [row["projectPath"] for row in self.rows[:2]]
        solution = self.source / "Sunder.AgentPackage.slnx"
        solution.write_text("<Solution>" + "".join(f'<Project Path="{row["projectPath"]}" />' for row in self.rows) + "</Solution>")
        args = argparse.Namespace(source=self.source, core=self.core, work=self.root / "graph-ci",
                                  inputs=None, candidate=[self.archive], catalog=self.catalog_path, mode="source",
                                  packages=json.dumps([row["packageId"] for row in self.rows[:2]]),
                                  project=roots, tools=ci.inputs.load(full_state)["tools"])
        result = subprocess.CompletedProcess([], 0, json.dumps(dict(Items=dict(ProjectReference=[]))))
        with patch.object(ci, "head", return_value=self.source_sha), \
             patch.object(ci.subprocess, "run", return_value=result) as run, patch("sys.stdout", new_callable=io.StringIO):
            ci.prepare(args)
            state_path = args.work / "ci-state.json"
            state, _ = ci.context(state_path)
            graph = Path(state["buildProject"])
            self.assertEqual(1, sum(call.args[0][:2] == ["dotnet", "restore"] for call in run.call_args_list))
            restore = next(call.args[0] for call in run.call_args_list if call.args[0][:2] == ["dotnet", "restore"])
            self.assertIn(str(graph), restore)
            self.assertIn("--locked-mode", restore)
            self.assertEqual(2, sum("-t:RestoreSunderContracts" in call.args[0] for call in run.call_args_list))
            self.assertEqual(roots, state["projects"])
            self.assertEqual(0, ci.execute(state_path, ["dotnet", "build", str(graph), "--no-restore"]))
            run.reset_mock()
            with self.assertRaisesRegex(ValueError, "outside the prepared component scope"):
                ci.execute(state_path, ["dotnet", "build", self.rows[2]["projectPath"], "--no-restore"])
            original = graph.read_bytes()
            graph.write_bytes(original + b" ")
            with self.assertRaisesRegex(ValueError, "bytes changed"):
                ci.execute(state_path, ["dotnet", "build", str(graph), "--no-restore"])
            graph.write_bytes(original)
            state["projects"] = [roots[0], self.rows[2]["projectPath"]]
            state_path.write_bytes(ci.inputs.canonical(state))
            with self.assertRaisesRegex(ValueError, "filter differs"):
                ci.execute(state_path, ["dotnet", "build", str(graph), "--no-restore"])
            run.assert_not_called()

    def test_wrong_or_missing_candidates_fail_before_any_restore_or_state_creation(self):
        self.assertEqual({"younics.ai.agent"}, set(ci.inputs.verify_selected_files(self.source, [self.archive], self.catalog_path)))
        for paths in ([], [self.archive, self.archive]):
            with self.assertRaises(ValueError): ci.inputs.verify_selected_files(self.source, paths, self.catalog_path)
        self.catalog_path.write_bytes(self.catalog_path.read_bytes() + b" ")
        with self.assertRaisesRegex(ValueError, "Catalog bytes"):
            ci.inputs.verify_selected_files(self.source, [self.archive], self.catalog_path)

    def test_archive_only_preparation_has_no_canonical_restore_or_contract_hydration(self):
        full_state = self.prepare()
        tools = ci.inputs.load(full_state)["tools"]
        project = "tests/Archive.Tests/Archive.Tests.csproj"
        path = self.source / project
        path.parent.mkdir(parents=True)
        path.write_text('<Project Sdk="Microsoft.NET.Sdk" />')
        args = argparse.Namespace(source=self.source, core=self.core, work=self.root / "archive-ci", mode="package",
                                  packages="[]", project=[project], tools=tools, inputs=None, candidate=None, catalog=None)
        commands = []
        def run(command, **kwargs):
            commands.append(command)
            return subprocess.CompletedProcess(command, 0, json.dumps(dict(Items=dict(ProjectReference=[]))))
        with patch.object(ci, "head", return_value=self.source_sha), patch.object(ci.subprocess, "run", side_effect=run), \
             patch.object(ci, "selected_files", side_effect=AssertionError("No package-contract dependency exists")), \
             patch("sys.stdout", new_callable=io.StringIO):
            ci.prepare(args)
            state, env = ci.context(args.work / "ci-state.json")
        self.assertEqual([], state["packages"])
        self.assertIsNone(state["catalog"])
        self.assertNotIn("SUNDER_AGENT_SOURCE_PLATFORM_CATALOG", env)
        self.assertEqual([project], state["projects"])
        self.assertEqual([str(path.resolve())], [command[2] for command in commands if command[:2] == ["dotnet", "restore"]])
        self.assertFalse(any("-t:RestoreSunderContracts" in command for command in commands))

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
        self.write_test_inventory()
        lanes = []
        def run(state, lane):
            lanes.append(lane)
            self.assertEqual("1", os.environ["SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES"])
            self.assertEqual("1", os.environ["SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION"])
            if lane == "required-linux":
                self.assertEqual("1", os.environ["SUNDER_REQUIRE_TEST_PREREQUISITES"])
                raise ValueError("required case failed")
        for platform, report_platform in (("linux", "linux"), ("win32", "windows"), ("darwin", "macos")):
            lanes.clear()
            with self.subTest(platform=platform), patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
                    patch.object(sys, "platform", platform), patch.object(test_lanes, "tests", side_effect=run), \
                    patch.object(test_lanes, "report") as report:
                if platform == "linux":
                    with self.assertRaisesRegex(ValueError, "required case failed"): test_lanes.validate("state", "source")
                else:
                    test_lanes.validate("state", "source")
                self.assertEqual(["source", "artifact"] + (["required-linux"] if platform == "linux" else []), lanes)
                report.assert_called_once_with("source", report_platform)
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
        self.assertEqual({(rid, mode) for rid in ci_platforms.RIDS for mode in ("source", "package")},
                         {(row["rid"], row["mode"]) for row in value["behavior"]["include"]})
        self.assertEqual(12, len(value["behavior"]["include"]))
        self.assertEqual({"windows", "linux", "macos"}, {row["platform"] for row in value["preflight"]["include"]})
        self.assertEqual(6, len(value["preflight"]["include"]))
        self.assertEqual(["self-hosted", "linux", "ARM64"], next(row["runner"] for row in value["native"]["include"] if row["rid"] == "linux-arm64"))
        self.assertTrue(all(row["runner"] == ["self-hosted", "linux", "ARM64"] for row in value["behavior"]["include"] if row["rid"] == "linux-arm64"))
        for labels in ('[]', '"ubuntu-latest"', '["linux\\noutput=changed"]'):
            with self.assertRaises(ValueError): ci_platforms.matrices(variables={"SUNDER_AGENT_RUNNER_LINUX_ARM64": labels})

    def test_windows_filesystem_option_keeps_all_six_default_behavior_owners_without_duplicates(self):
        for packages in (False, True):
            with self.subTest(package_tests=packages):
                ordinary = ci_platforms.matrices(packages)
                expanded = ci_platforms.matrices(packages, {"SUNDER_AGENT_RUNNER_WIN_ARM64": '["windows-11-arm"]'},
                                                windows_filesystem_native=True)
                self.assertEqual(ordinary["native"], expanded["native"])
                self.assertEqual(ordinary["behavior"], expanded["behavior"])
                arm = [row for row in expanded["behavior"]["include"] if row["rid"] == "win-arm64"]
                self.assertEqual({"source", "package"} if packages else {"source"}, {row["mode"] for row in arm})
                self.assertTrue(all(row["platform"] == "windows" and row["runner"] == ["windows-11-arm"] for row in arm))
                self.assertEqual(set(ci_platforms.RIDS),
                    {row["rid"] for row in expanded["preflight"]["include"]})

    def test_scoped_source_archives_preserve_tested_payloads_and_reject_rebuilt_bytes(self):
        fixture = runpy.run_path(str(Path(__file__).with_name("native-family.fixture.py")))
        root = self.root / "component-archives"
        source = fixture["create_producer"](root)
        inventory = json.loads((source / "packages.json").read_text())
        inventory["packages"] = [dict(row, version=fixture["VERSION"]) for row in inventory["packages"]]
        row = inventory["packages"][0]
        state = dict(source=str(source), selectedPackages=[row["packageId"]])
        target = source / Path(row["projectPath"]).parent / "bin/Release/net10.0"
        leaf = target / "sunder-targets/osx-arm64/sunder-dev"
        output = source / "artifacts/source-components"
        payload = next(leaf.rglob("*.dll"))
        original = payload.read_bytes()
        for fault in (None, "pack", "rebuilt"):
            with self.subTest(fault=fault):
                if output.exists(): shutil.rmtree(output)
                payload.write_bytes(original)
                def execute(state_path, command, log):
                    self.assertEqual(row["projectPath"], command[2])
                    if any(item.startswith("-getProperty:") for item in command):
                        log.write_text(json.dumps(dict(Properties=dict(NETCoreSdkRuntimeIdentifier="osx-arm64", TargetDir=str(target)))))
                        return 0
                    self.assertIn("-t:PackSunderPackage", command)
                    self.assertIn("-p:RuntimeIdentifier=osx-arm64", command)
                    self.assertIn("-p:BuildProjectReferences=false", command)
                    if fault == "pack": return 19
                    if fault == "rebuilt": payload.write_bytes(b"new bytes after tests")
                    archive = next(item.split("=", 1)[1] for item in command if item.startswith("-p:SunderPackageOutputPath="))
                    fixture["zip_bytes"](archive, {path.relative_to(leaf).as_posix(): path.read_bytes() for path in leaf.rglob("*") if path.is_file()})
                    log.write_text("packed\n")
                    return 0
                with patch.object(ci, "context", return_value=(state, {})), patch.object(ci.inputs, "inventory", return_value=inventory), \
                     patch.object(ci, "execute", side_effect=execute) as run:
                    if fault == "pack":
                        with self.assertRaises(subprocess.CalledProcessError) as caught: test_lanes.source_artifacts("state")
                        self.assertEqual(19, caught.exception.returncode)
                    elif fault == "rebuilt":
                        with self.assertRaisesRegex(ValueError, "Source role bytes changed"): test_lanes.source_artifacts("state")
                    else:
                        test_lanes.source_artifacts("state")
                    self.assertEqual(2, run.call_count)
                self.assertTrue((output / (row["packageId"] + ".source.json")).exists())
                receipt = output / "source-artifact-evidence.json"
                self.assertEqual(fault is None, receipt.exists())
                if fault is None:
                    value = json.loads(receipt.read_text())
                    self.assertEqual(state["selectedPackages"], value["packageIds"])
                    self.assertEqual([row["packageId"]], [item["packageId"] for item in value["packages"]])
                    self.assertEqual(1, len(list(output.glob("*.sunderpkg"))))

    def test_component_source_artifacts_follow_owned_required_and_stress_tests_only(self):
        self.write_test_inventory()
        selected = ["younics.ai.agent.tools.files"]
        state = dict(source=str(self.source), selectedPackages=selected)
        calls = []
        with patch.object(ci, "context", return_value=(state, {})), patch.object(sys, "platform", "linux"), \
             patch.object(test_lanes, "tests", side_effect=lambda state, lane: calls.append(lane)), \
             patch.object(test_lanes, "source_artifacts", side_effect=lambda state: calls.append("source-artifacts")), \
             patch.object(test_lanes, "report"):
            test_lanes.validate("state", "source", stress=True)
        self.assertEqual(["source", "required-linux", "stress", "source-artifacts"], calls)

    def test_required_linux_policy_names_current_cases_in_their_owning_projects(self):
        rows = ci.inputs.inventory(SCRIPTS.parent)["packages"]
        for method in test_lanes.POLICY["requiredLinux"]:
            owner = test_lanes.POLICY["requiredLinuxOwners"][method]
            project = next(row["projectPath"] for row in rows if row["packageId"] == owner)
            tests = SCRIPTS.parent / "tests" / (Path(project).stem + ".Tests")
            declarations = [path for path in tests.glob("*.cs")
                            if re.search(r"\b(?:void|Task|ValueTask)\s+" + re.escape(method) + r"\s*\(", path.read_text())]
            self.assertEqual(1, len(declarations), f"{owner}: required case {method} must exist exactly once")

    def test_required_docker_lane_provisions_selected_image_and_never_hides_pull_failure(self):
        self.write_test_inventory()
        inventory = self.source / "artifacts/reports/test-inventory.json"
        value = json.loads(inventory.read_text())
        owner = "younics.ai.agent.execution.docker"
        value["projects"][0]["owner"] = owner
        inventory.write_text(json.dumps(value))
        state = dict(source=str(self.source), selectedPackages=[owner])
        image = "registry.test/integration@sha256:" + "a" * 64
        for fails in (False, True):
            calls = []
            with self.subTest(pull_fails=fails), patch.object(ci, "context", return_value=(state, {})), \
                 patch.object(sys, "platform", "linux"), patch.dict(os.environ, SUNDER_DOCKER_INTEGRATION_IMAGE=image), \
                 patch.object(test_lanes, "tests", side_effect=lambda state, lane: calls.append(lane)), \
                 patch.object(test_lanes, "source_artifacts"), patch.object(test_lanes, "report") as report, \
                 patch.object(test_lanes, "run", side_effect=subprocess.CalledProcessError(7, "docker pull") if fails
                              else lambda *args, **kwargs: calls.append("docker-pull")) as provision:
                if fails:
                    with self.assertRaises(subprocess.CalledProcessError): test_lanes.validate("state", "source")
                else:
                    test_lanes.validate("state", "source")
                provision.assert_called_once_with("docker", "pull", "--", image, cwd=self.source, env={})
                self.assertEqual(["source"] if fails else ["source", "docker-pull", "required-linux"], calls)
                report.assert_called_once_with("source", "linux")

    def test_linux_stress_excludes_the_current_windows_only_mutation_case(self):
        name = "WindowsMutations_ReachCommitCheckpointsAndPreserveOutsidePaths"
        source = (SCRIPTS.parent / "tests/Sunder.Package.Agent.Execution.Local.Tests/LocalSecureFileSystemRaceTests.cs").read_text()
        self.assertRegex(source, r'\[PlatformFact\(TestPlatform\.Windows,[^\n]+\)\]\s+public async Task ' + name + r'\(')
        self.assertIn("FullyQualifiedName!~" + name, test_lanes.POLICY["stress"]["filter"])
        self.assertNotIn("WindowsMutations_FailClosedBeforeFinalSyscallCheckpoints", test_lanes.POLICY["stress"]["filter"])

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
             patch.object(ci, "context", return_value=({}, {"SUNDER_REQUIRE_TEST_PREREQUISITES": "1"})), \
             patch.object(test_lanes, "project_results", return_value=[{"outcome": "passed"}]):
            self.assertEqual(1, test_lanes.run_project("state", self.source, "Fixture.Tests.csproj", common, "Lane=Test", output))
            self.assertIn("--blame-hang", run.call_args.args[1])
            self.assertNotIn("console;verbosity=normal", run.call_args.args[1])
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
            self.assertIn("console;verbosity=normal", run.call_args.args[1])
            self.assertNotIn("--blame-hang", run.call_args.args[1])
        with self.assertRaisesRegex(ValueError, "no discovery header"):
            test_lanes.discovered_tests("adapter crashed")

    def test_macos_filesystem_prerequisites_partition_exact_cases_without_changing_alias_roots(self):
        directory = self.source / "tests/Fixture.Tests"
        directory.mkdir(parents=True)
        (directory / "Cases.cs").write_text('''namespace Fixture;
public class Cases {
    [FileSystemFact(TestFileSystemCapability.CaseSensitiveDirectories, "Distinct names are required.")]
    public void Sensitive() {}
    [Fact]
    public void Ordinary() {}
}''')
        project = "tests/Fixture.Tests/Fixture.Tests.csproj"
        inventory = dict(projects=[dict(path=project, modes=["source"], owner="fixture",
                                       sources=["tests/Fixture.Tests/Cases.cs"])])
        output = self.root / "filesystem-results"
        output.mkdir()
        previous = dict(os.environ)
        common = ["dotnet", "test", "solution", "--no-build", "--no-restore"]
        for fails in (False, True):
            calls = []
            primary = test_lanes.TestProjectFailure(1, "ordinary assertions")
            def execute(*args):
                calls.append((args[4], args[5], os.environ.get("TMPDIR")))
                if fails and len(calls) == 1: raise primary
                return 1
            with self.subTest(ordinary_fails=fails), patch.object(sys, "platform", "darwin"), \
                 patch.object(ci, "context", return_value=({"home": str(self.root)}, {})), \
                 patch.object(test_lanes, "run_project", side_effect=execute), \
                 patch.object(test_filesystems, "case_sensitive_volume", return_value=nullcontext(self.root / "sensitive")) as volume:
                if fails:
                    with self.assertRaises(test_lanes.TestProjectFailure) as caught:
                        test_lanes.run_project_with_filesystems("state", self.source, project, common, "Category!=Artifact", output, inventory, "source")
                    self.assertIs(primary, caught.exception)
                else:
                    self.assertEqual(2, test_lanes.run_project_with_filesystems("state", self.source, project, common, "Category!=Artifact", output, inventory, "source"))
                self.assertEqual("(Category!=Artifact)&FullyQualifiedName!=Fixture.Cases.Sensitive", calls[0][0])
                self.assertEqual(previous.get("TMPDIR"), calls[0][2])
                self.assertEqual("(Category!=Artifact)&(FullyQualifiedName=Fixture.Cases.Sensitive)", calls[1][0])
                self.assertEqual(output / "case-sensitive", calls[1][1])
                self.assertEqual(str(self.root / "sensitive"), calls[1][2])
                volume.assert_called_once()
            self.assertEqual(previous, dict(os.environ))

    def test_filesystem_volume_detaches_on_test_failure_but_never_deletes_a_failed_detach(self):
        for fails in ("test", "detach", "create"):
            log = self.root / ("filesystem-" + fails + ".log")
            commands = []
            def run(arguments, **kwargs):
                commands.append(arguments)
                code = 7 if arguments[1] == fails else 0
                info = plistlib.dumps(dict(images=[])).decode() if arguments[1] == "info" else ""
                return subprocess.CompletedProcess(arguments, code, stdout=info, stderr="")
            with self.subTest(fails=fails), patch.dict(os.environ, RUNNER_TEMP=str(self.root)), \
                 patch.object(test_filesystems.subprocess, "run", side_effect=run), \
                 patch.object(test_filesystems, "require_case_sensitive"), \
                 patch.object(test_filesystems.shutil, "rmtree") as cleanup:
                with self.assertRaises((RuntimeError, subprocess.CalledProcessError)):
                    with test_filesystems.case_sensitive_volume(log):
                        raise RuntimeError("test failed")
                self.assertEqual(fails != "detach", bool(cleanup.call_count))
                expected = {"create": ["create"], "detach": ["create", "attach", "detach"],
                            "test": ["create", "attach", "detach", "info"]}
                self.assertEqual(expected[fails],
                                 [command[1] for command in commands])

    def test_busy_eject_retries_only_the_backing_device_and_requires_verified_detach(self):
        device = "/dev/disk27"
        # Exercise JSON path escaping even on hosts without backslash separators.
        runner_temp = self.root / "runner-\u00e9"
        runner_temp.mkdir()
        cases = {
            "detached": (2, 2, 2),
            "busy": (6, 5, 5),
            "unknown-device": (1, 0, 1),
            "lost-device": (2, 1, 2),
            "still-attached": (1, 1, 1),
        }
        for result, (busy_attempts, retries, pauses) in cases.items():
            with self.subTest(result=result), patch.dict(os.environ, RUNNER_TEMP=str(runner_temp)):
                log = self.root / (result + ".log")
                detaches = []
                image = None
                def run(arguments, **kwargs):
                    nonlocal image
                    action = arguments[1]
                    code, output, error = 0, "", ""
                    if action == "create":
                        image = Path(arguments[-1])
                        image.write_bytes(b"fixture disk image")
                    elif action == "detach":
                        detaches.append(arguments)
                        if len(detaches) <= busy_attempts:
                            code, error = errno.EBUSY, "hdiutil: couldn't eject disk - Resource busy\n"
                    elif action == "info":
                        def attachment(path, dev):
                            return {"image-path": str(path), "system-entities": [
                                {"content-hint": "GUID_partition_scheme", "dev-entry": dev}]}
                        images = [attachment(self.root / "other.sparseimage", "/dev/disk19")]
                        has_device = result != "unknown-device" and not (result == "lost-device" and len(detaches) > 1)
                        if has_device and (len(detaches) <= busy_attempts or result == "still-attached"):
                            images.append(attachment(image, device))
                        output = plistlib.dumps(dict(images=images)).decode()
                    return subprocess.CompletedProcess(arguments, code, stdout=output, stderr=error)
                with tempfile.TemporaryDirectory(dir=self.root) as prepared, \
                     patch.object(test_filesystems.subprocess, "run", side_effect=run), \
                     patch.object(test_filesystems, "require_case_sensitive"), \
                     patch.object(test_filesystems.time, "sleep") as pause:
                    def exercise():
                        with test_filesystems.case_sensitive_volume(log) as mount:
                            self.assertFalse(mount.is_relative_to(Path(prepared)))
                    if result == "detached": exercise()
                    elif result == "still-attached":
                        with self.assertRaisesRegex(ValueError, "still attached"): exercise()
                    else:
                        with self.assertRaises(subprocess.CalledProcessError) as caught: exercise()
                        self.assertEqual(errno.EBUSY, caught.exception.returncode)
                    self.assertEqual([unittest.mock.call(1)] * pauses, pause.call_args_list)
                self.assertEqual(("hdiutil", "detach", str(image.parent / "mount")), detaches[0])
                self.assertEqual([("hdiutil", "detach", device)] * retries, detaches[1:])
                self.assertEqual(result != "detached", image.is_file())
                text = log.read_text(encoding="utf-8")
                commands = [json.loads(line)["command"] for line in text.splitlines() if line.startswith("{")]
                self.assertIn("Resource busy", text)
                self.assertIn(["/usr/sbin/lsof", "-nP", "--", str(image)], commands)
                self.assertNotIn("other.sparseimage", text)

    def test_builder_diagnostics_use_release_build_then_independently_verified_focused_and_full_runs(self):
        self.write_test_inventory()
        inventory_path = self.source / "artifacts/reports/test-inventory.json"
        inventory_path.write_text(json.dumps({"projects": [
            dict(path=test_lanes.BUILDER_PROJECT, modes=["source"]), dict(path="Other.csproj", modes=["source"])]}))
        historical = self.source / "artifacts/test-results/historical.txt"
        historical.parent.mkdir(parents=True)
        historical.write_text("retained")
        for failure_stage in (None, "build", "focused", "full", "report"):
            with self.subTest(failure_stage=failure_stage):
                primary = ValueError("fixture stage failed")
                def project(*args):
                    if args[-1].name == failure_stage: raise primary
                    return 1
                def report(*args, **kwargs):
                    if failure_stage in ("focused", "report"): raise OSError("fixture report failed")
                with patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
                     patch.object(ci, "execute", return_value=7 if failure_stage == "build" else 0) as build, \
                     patch.object(test_lanes, "run_project", side_effect=project) as tests, \
                     patch.object(test_lanes, "report", side_effect=report) as reports:
                    if failure_stage is None:
                        test_lanes.builder_diagnostics("state", inventory_path)
                    else:
                        with self.assertRaises((ValueError, OSError, subprocess.CalledProcessError)) as caught:
                            test_lanes.builder_diagnostics("state", inventory_path)
                        if failure_stage in ("focused", "full"): self.assertIs(primary, caught.exception)
                        if failure_stage == "focused": self.assertIn("fixture report failed", ci.describe_error(primary))
                    self.assertEqual(["dotnet", "build", test_lanes.BUILDER_PROJECT, "-c", "Release", "--no-restore"], build.call_args.args[1])
                    stages = [] if failure_stage == "build" else ["focused"] if failure_stage in ("focused", "report") else ["focused", "full"]
                    self.assertEqual(stages, [call.args[-1].name for call in tests.call_args_list])
                    self.assertEqual(stages, [call.kwargs["results"].name for call in reports.call_args_list])
                    for call in tests.call_args_list:
                        self.assertIn("--no-build", call.args[3])
                    if len(stages) == 2:
                        self.assertIn("BuilderToolchainTests", tests.call_args_list[0].args[4])
                        self.assertEqual("", tests.call_args_list[1].args[4])
                self.assertEqual("retained", historical.read_text())
                scoped = json.loads((self.source / "artifacts/builder-diagnostics/test-inventory.json").read_text())
                self.assertEqual([test_lanes.BUILDER_PROJECT], [row["path"] for row in scoped["projects"]])
        args = argparse.Namespace(source=self.source, mode="package", builder_diagnostics=True)
        with patch.object(ci, "prepare") as prepare, self.assertRaisesRegex(ValueError, "require source mode"):
            ci.validate(args)
        prepare.assert_not_called()
        workflow = (SCRIPTS.parent / ".github/workflows/sunder-package-smoke.yml").read_text()
        self.assertNotIn("builder-diagnostics", workflow)
        self.assertNotIn("windows-filesystem-native", workflow)
        self.assertNotIn("younics.sunder.package.maker", workflow)
        self.assertNotIn("younics.ai.agent.execution.local", workflow)

    def test_only_independent_ordinary_projects_aggregate_completed_assertion_failures(self):
        self.write_test_inventory()
        projects = [f"tests/{name}.Tests.csproj" for name in ("First", "Second", "Third")]
        (self.source / "artifacts/reports/test-inventory.json").write_text(json.dumps({"projects": [
            dict(path=path, modes=["source", "package"], lanes=["PackageCompatibility"], owner="younics.ai.agent.tools.files") for path in projects]}))
        for lane in ("source", "package"):
            failures = [test_lanes.TestProjectFailure(1, project) for project in (projects[0], projects[2])]
            with self.subTest(lane=lane), patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
                 patch.object(test_lanes, "run_project", side_effect=[failures[0], 1, failures[1]]) as run:
                with self.assertRaises(test_lanes.TestProjectFailure) as caught:
                    test_lanes.tests("state", lane)
                self.assertIs(failures[0], caught.exception)
                self.assertEqual(projects, [call.args[2] for call in run.call_args_list])
                self.assertIn(projects[0] + ", " + projects[2], ci.describe_error(caught.exception))
        for lane in ("source", "package", "required-linux", "stress", "artifact"):
            failures = [ValueError("input integrity"), OSError("capture failed"), KeyboardInterrupt(),
                        subprocess.CalledProcessError(1, "incomplete TRX")]
            if lane not in ("source", "package"):
                failures.append(test_lanes.TestProjectFailure(1, "assertion"))
            for failure in failures:
                with self.subTest(lane=lane, failure=type(failure).__name__), \
                     patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
                     patch.object(test_lanes, "run_project", side_effect=failure) as run:
                    with self.assertRaises(type(failure)) as caught:
                        test_lanes.tests("state", lane)
                    self.assertIs(failure, caught.exception)
                    self.assertEqual(1, run.call_count)

    def test_report_failure_retains_primary_execution_error_and_restores_environment(self):
        self.write_test_inventory()
        for primary in (OSError("output mirror failed"), KeyboardInterrupt(), test_lanes.TestProjectFailure(1, "assertions"), None):
            report_error = subprocess.CalledProcessError(9, "evidence report")
            previous = dict(os.environ)
            with self.subTest(primary=type(primary).__name__), patch.object(ci, "context", return_value=({"source": str(self.source)}, {})), \
                 patch.object(test_lanes, "tests", side_effect=primary) as tests, \
                 patch.object(test_lanes, "report", side_effect=report_error) as report:
                with self.assertRaises(type(primary or report_error)) as caught:
                    test_lanes.validate("state", "source")
                self.assertIs(primary or report_error, caught.exception)
                if primary is not None:
                    self.assertEqual(1, tests.call_count)
                    self.assertIn("Report verification also failed", ci.describe_error(primary))
                    self.assertIn("evidence report", ci.describe_error(primary))
                report.assert_called_once()
            self.assertEqual(previous, dict(os.environ))

    def test_report_prints_bounded_failure_summary_without_masking_verifier_status(self):
        self.write_test_inventory()
        value = dict(summary=dict(passed=5, failed=21, skipped=6),
                     failures=[dict(test=f"Case{index}", message="assertion ↓\nlong detail", trx="Fixture.trx") for index in range(21)],
                     evidenceViolations=[f"violation-{index}" for index in range(25)])
        def run(command, **kwargs):
            json.dump(value, kwargs["stdout"])
            return subprocess.CompletedProcess(command, 9)
        diagnostics = io.StringIO()
        with patch.object(test_lanes, "ROOT", self.source), patch.object(test_lanes.subprocess, "run", side_effect=run), \
             patch("sys.stderr", diagnostics):
            with self.assertRaises(subprocess.CalledProcessError) as caught:
                test_lanes.report("source", "windows")
        self.assertEqual(9, caught.exception.returncode)
        text = diagnostics.getvalue()
        self.assertIn("5 passed, 21 failed, 6 skipped", text)
        self.assertIn("FAIL Case0: assertion ↓ (Fixture.trx)", text)
        self.assertNotIn("long detail", text)
        self.assertNotIn("FAIL Case20:", text)
        self.assertIn("1 further failures", text)
        self.assertIn("5 further evidence violations", text)
        self.assertEqual(value, json.loads((self.source / "artifacts/reports/results.json").read_text()))
        with patch.object(test_lanes, "ROOT", self.source), patch.object(test_lanes.subprocess, "run", side_effect=run), \
             patch.object(ci, "console_write", side_effect=BrokenPipeError("summary sink failed")):
            with self.assertRaises(subprocess.CalledProcessError) as caught:
                test_lanes.report("source", "windows")
        self.assertEqual(9, caught.exception.returncode)
        self.assertIn("summary sink failed", ci.describe_error(caught.exception))


class CommandProcessTests(unittest.TestCase):
    setUp = fixture_setup
    prepare = AgentCiTests.prepare

    def child(self, status=23, before=""):
        # Exceed pipe capacity; a broken mirror must still drain the trailing output.
        # The child watchdog bounds the regression if the parent stops draining.
        self.output = "fixture ↓ 项目\n" + "payload\n" * 40000 + "last child line\n"
        return [sys.executable, "-X", "utf8", "-c", textwrap.dedent(f"""\
            import os, sys, threading
            timer = threading.Timer(10, lambda: os._exit(97))
            timer.daemon = True
            timer.start()
            {before}
            sys.stdout.write('fixture ↓ 项目\\n' + 'payload\\n' * 40000 + 'last child line\\n')
            sys.stdout.flush()
            timer.cancel()
            sys.exit({status})
            """)]

    def test_unicode_and_nonzero_child_status_survive_ansi_and_utf8_consoles(self):
        state = self.prepare()
        log = self.root / "child.log"
        for configure in (False, True):
            with self.subTest(configure=configure):
                raw = io.BytesIO()
                with io.TextIOWrapper(raw, encoding="cp1252", errors="strict", newline="\n") as console, \
                     patch.object(ci, "head", return_value=self.source_sha), patch("sys.stdout", console), patch("sys.stderr", io.StringIO()):
                    if configure: ci.configure_console()
                    self.assertEqual(23, ci.execute(state, self.child(), log=log))
                    rendered = raw.getvalue().decode("utf-8" if configure else "cp1252")
                    self.assertIn("↓ 项目" if configure else r"\u2193 \u9879\u76ee", rendered)
                    self.assertTrue(rendered.endswith("last child line\n"))
                self.assertEqual(self.output, log.read_text(encoding="utf-8"))
                record = json.loads(log.with_suffix(".command.json").read_text())
                self.assertEqual(23, record["exitCode"])
                self.assertEqual({}, record["errors"])

    def test_broken_console_or_log_still_drains_child_and_records_real_exit(self):
        state = self.prepare()
        log = self.root / "child.log"
        original_open = Path.open
        class BrokenSink(io.StringIO):
            def write(self, value):
                if sink == "console": raise OSError("fixture mirror failed")
                return super().write(value)
            def flush(self): raise OSError("fixture mirror failed")
        for sink in ("console", "log"):
            for status in (0, 23):
                console = BrokenSink() if sink == "console" else io.StringIO()
                def open_file(path, *args, **kwargs):
                    return BrokenSink() if sink == "log" and path == log else original_open(path, *args, **kwargs)
                with self.subTest(sink=sink, status=status), patch.object(ci, "head", return_value=self.source_sha), \
                     patch("sys.stdout", console), patch.object(Path, "open", open_file):
                    with self.assertRaisesRegex(OSError, "CI output capture failed"):
                        ci.execute(state, self.child(status), log=log)
                record = json.loads(log.with_suffix(".command.json").read_text())
                self.assertEqual(status, record["exitCode"])
                self.assertIn("fixture mirror failed", record["errors"][sink])
                retained = log.read_text(encoding="utf-8") if sink == "console" else console.getvalue()
                self.assertEqual(self.output, retained)

    def test_post_execution_integrity_failure_keeps_real_child_status(self):
        state = self.prepare()
        tool = self.work / "tools/Sunder.Package.Build.dll"
        log = self.root / "child.log"
        command = self.child(0, f"open({str(tool)!r}, 'ab').write(b'changed')")
        with patch.object(ci, "head", return_value=self.source_sha), patch("sys.stdout", io.StringIO()):
            with self.assertRaisesRegex(ValueError, "bytes changed"):
                ci.execute(state, command, log=log)
        record = json.loads(log.with_suffix(".command.json").read_text())
        self.assertEqual(0, record["exitCode"])
        self.assertIn("bytes changed", record["errors"]["verification"])
        self.assertEqual(self.output, log.read_text(encoding="utf-8"))

    def test_execution_failure_retains_secondary_integrity_and_evidence_write_errors(self):
        state = self.prepare()
        tool = self.work / "tools/Sunder.Package.Build.dll"
        original = tool.read_bytes()
        log = self.root / "child.log"
        write_text = Path.write_text
        for fail_evidence in (False, True):
            tool.write_bytes(original)
            log.with_suffix(".command.json").unlink(missing_ok=True)
            def write(path, *args, **kwargs):
                if fail_evidence and path == log.with_suffix(".command.json"):
                    raise PermissionError("fixture evidence storage failed")
                return write_text(path, *args, **kwargs)
            with self.subTest(fail_evidence=fail_evidence), patch.object(ci, "head", return_value=self.source_sha), \
                 patch.object(ci, "console_write", side_effect=BrokenPipeError("fixture console failed")), \
                 patch.object(Path, "write_text", write):
                with self.assertRaisesRegex(OSError, "CI output capture failed") as caught:
                    ci.execute(state, self.child(before=f"open({str(tool)!r}, 'ab').write(b'changed')"), log=log)
            description = ci.describe_error(caught.exception)
            self.assertIn("Post-execution input verification also failed", description)
            self.assertIn("bytes changed", description)
            if fail_evidence:
                self.assertIn("Command evidence could not be written", description)
                self.assertIn("fixture evidence storage failed", description)
            else:
                record = json.loads(log.with_suffix(".command.json").read_text())
                self.assertEqual(23, record["exitCode"])
                self.assertIn("verification", record["errors"])
                self.assertIn("console", record["errors"])

    def test_child_launch_failure_is_retained_without_inventing_an_exit_code(self):
        state = self.prepare()
        log = self.root / "child.log"
        with patch.object(ci, "head", return_value=self.source_sha):
            with self.assertRaises(FileNotFoundError):
                ci.execute(state, [str(self.root / "missing-executable")], log=log)
        record = json.loads(log.with_suffix(".command.json").read_text())
        self.assertIsNone(record["exitCode"])
        self.assertIn("FileNotFoundError", record["errors"]["execution"])


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
