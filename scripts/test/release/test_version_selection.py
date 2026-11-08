"""Version-first CI: real shallow Git history, owned suites and outcome gates."""
import copy
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SCRIPTS / "ci"))
import version_selection as selection
import workflow_summary as summary

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class VersionSelectionTests(unittest.TestCase):
    setUp = fixture_setup

    def git(self, *arguments, cwd=None):
        return subprocess.check_output(["git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
            "-c", "commit.gpgsign=false", *arguments], cwd=cwd or self.source, text=True).strip()

    def test_shallow_event_base_selects_only_newer_literals_without_sdk_or_registry(self):
        self.git("init", "-q")
        self.git("add", ".")
        self.git("commit", "-qm", "base")
        base = self.git("rev-parse", "HEAD")
        bumped = self.rows[2]
        (self.source / bumped["projectPath"]).write_text('<Project><PropertyGroup><Version>2.0.1</Version></PropertyGroup></Project>')
        (self.source / self.rows[3]["projectPath"]).write_text('<Project><PropertyGroup><Version>1.9.0</Version></PropertyGroup></Project>')
        (self.source / "readme.txt").write_text("unversioned changes do not select components")
        self.git("add", ".")
        self.git("commit", "-qm", "patch")
        checkout = self.root / "shallow"
        self.git("clone", "-q", "--depth=1", self.source.as_uri(), str(checkout))
        self.assertEqual("true", self.git("rev-parse", "--is-shallow-repository", cwd=checkout))
        self.assertNotEqual(0, subprocess.run(["git", "cat-file", "-e", base + "^{commit}"], cwd=checkout,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode)
        self.assertEqual([bumped["packageId"]], selection.against_base(checkout, base))
        self.assertEqual([], selection.against_base(checkout, self.git("rev-parse", "HEAD", cwd=checkout)))
        with self.assertRaises(selection.r.Invalid): selection.against_base(checkout, "main")
        with self.assertRaises(subprocess.CalledProcessError): selection.against_base(checkout, "f" * 40)

    def test_explicit_full_and_empty_selection_do_not_evaluate_or_resolve_dependencies(self):
        for option, expected in ((["--all"], 15), (["--packages", "[]"], 0)):
            output = self.root / "outputs"
            with patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}, clear=True), \
                 patch.object(sys, "argv", ["version_selection.py", "--source", str(self.source), *option]), \
                 patch.object(selection.subprocess, "run", side_effect=AssertionError("no SDK/network")), \
                 patch.object(selection.subprocess, "check_output", side_effect=AssertionError("no SDK/network")):
                selection.main()
            self.assertIn(f"selected_count={expected}\n", output.read_text())
            output.unlink()
        for invalid in (["unknown"], [self.rows[0]["packageId"]] * 2, "all"):
            with self.assertRaises(selection.r.Invalid): selection.package_ids(self.source, invalid)

    def test_owned_suites_keep_family_boundaries_and_required_linux_security_cases(self):
        inventory = dict(projects=[dict(path="family", owner="agent-family", modes=["source", "package"]),
            dict(path="openai", owner="younics.ai.agent.provider.openai", modes=["source", "package"]),
            dict(path="gemini", owner="younics.ai.agent.provider.gemini", modes=["source", "package"]),
            dict(path="filesystem", owner="younics.ai.agent.execution.local", modes=["source", "package"], sourceOnlyTests=["ordinary"])])
        before = copy.deepcopy(inventory)
        selected = ["younics.ai.agent.provider.openai"]
        for platform in ("linux", "win32", "darwin"):
            for mode in ("source", "package"):
                with self.subTest(platform=platform, mode=mode):
                    value = selection.scope_inventory(inventory, selected, mode, platform)
                    required = mode == "source" and platform == "linux"
                    self.assertEqual(["family", "openai"] + (["filesystem"] if required else []), [row["path"] for row in value["projects"]])
                    if required:
                        self.assertEqual(True, value["projects"][-1]["requiredOnly"])
                        self.assertEqual([], value["projects"][-1]["sourceOnlyTests"])
                    self.assertEqual([], selection.scope_inventory(inventory, [], mode, platform)["projects"])
                    self.assertEqual(inventory, selection.scope_inventory(inventory, ["younics.ai.agent"], mode, platform))
                    local = selection.scope_inventory(inventory, ["younics.ai.agent.execution.local"], mode, platform)
                    self.assertEqual([inventory["projects"][0], inventory["projects"][-1]], local["projects"])
        self.assertEqual(before, inventory)

    def test_summary_rejects_failure_cancellation_and_unexpected_skips_in_every_selected_stage(self):
        for mode in ("validate", "release"):
            names = ("preflight", "dependencies", "source") if mode == "validate" else ("validation", "native", "fan-in", "publish", "release")
            for count, resume in ((0, False), (1, False), (15, False), (1, True)):
                jobs = {"prepare": dict(result="success", outputs=dict(selected_count=str(count), resume=str(resume).lower()))}
                for name in names:
                    skipped = count == 0 or (mode == "release" and resume and name in ("validation", "native", "fan-in"))
                    jobs[name] = dict(result="skipped" if skipped else "success")
                summary.verify(jobs, mode)
                for name in jobs:
                    for outcome in ("failure", "cancelled", "skipped" if jobs[name]["result"] == "success" else "success"):
                        broken = copy.deepcopy(jobs)
                        broken[name]["result"] = outcome
                        with self.assertRaises(ValueError): summary.verify(broken, mode)


if __name__ == "__main__": unittest.main()
