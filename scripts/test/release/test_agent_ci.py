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
                        expected = [row for row in inventory["projects"] if row["owner"] == package]
                        canonical = next(row["projectPath"] for row in self.rows if row["packageId"] == package)
                        self.assertEqual(sorted([canonical, *[row["path"] for row in expected]]), [call.args[1][2] for call in build.call_args_list])
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

    def prepare(self):
        self.core = self.root / "sunder-core"
        self.core.mkdir()
        self.work = self.root / "ordinary-ci"
        (self.source / "Sunder.AgentPackage.slnx").write_text("<Solution />")
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
        self.assertEqual(4, len(value["behavior"]["include"]))
        self.assertEqual({"windows", "linux"}, {row["platform"] for row in value["preflight"]["include"]})
        self.assertEqual(2, len(value["preflight"]["include"]))
        self.assertEqual(["self-hosted", "linux", "ARM64"], next(row["runner"] for row in value["native"]["include"] if row["rid"] == "linux-arm64"))
        for labels in ('[]', '"ubuntu-latest"', '["linux\\noutput=changed"]'):
            with self.assertRaises(ValueError): ci_platforms.matrices(variables={"SUNDER_AGENT_RUNNER_LINUX_ARM64": labels})

    def test_windows_filesystem_native_adds_arm64_without_replacing_default_or_six_native_lanes(self):
        for packages in (False, True):
            with self.subTest(package_tests=packages):
                ordinary = ci_platforms.matrices(packages)
                expanded = ci_platforms.matrices(packages, {"SUNDER_AGENT_RUNNER_WIN_ARM64": '["windows-11-arm"]'},
                                                windows_filesystem_native=True)
                self.assertEqual(ordinary["native"], expanded["native"])
                self.assertEqual(ordinary["behavior"]["include"],
                    [row for row in expanded["behavior"]["include"] if row["rid"] != "win-arm64"])
                arm = [row for row in expanded["behavior"]["include"] if row["rid"] == "win-arm64"]
                self.assertEqual({"source", "package"} if packages else {"source"}, {row["mode"] for row in arm})
                self.assertTrue(all(row["platform"] == "windows" and row["runner"] == ["windows-11-arm"] for row in arm))
                self.assertEqual({"win-x64", "win-arm64", "linux-x64"},
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
        self.assertNotIn("builder-diagnostics", workflow.split("  workflow_call:\n")[1].split("\npermissions:")[0])
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.builder-diagnostics", workflow)
        self.assertIn("if: github.event_name != 'workflow_dispatch' || !inputs.builder-diagnostics", workflow)

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
