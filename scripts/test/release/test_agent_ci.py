"""Ordinary CI uses exact inputs without production approval or cache synthesis."""
import argparse
import io
import os
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SCRIPTS / "ci"))
import agent_ci as ci

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class AgentCiTests(unittest.TestCase):
    setUp = fixture_setup

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
                output = Path(command[2]).parent / "bin/Release/net10.0"
                output.mkdir(parents=True)
                for name in (*ci.inputs.TOOL_FILES, "Sunder.Package.Build.Tests.dll"):
                    (output / name).write_bytes(b"built tool " + name.encode())
            if "-t:RestoreSunderContracts" in command:
                Path(kwargs["env"]["SunderContractCacheRoot"]).mkdir(parents=True, exist_ok=True)
            return subprocess.CompletedProcess(command, 0)
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


if __name__ == "__main__": unittest.main()
