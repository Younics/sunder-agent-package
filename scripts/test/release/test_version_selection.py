"""Change-based validation: real shallow Git history, owned suites and outcome gates."""
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

    def initialize(self):
        (self.source / "Sunder.AgentPackage.slnx").write_text("<Solution />")
        self.git("init", "-q")
        self.git("add", ".")
        self.git("commit", "-qm", "base")
        return self.git("rev-parse", "HEAD")

    def commit(self):
        self.git("add", "-A")
        self.git("commit", "-qm", "change")

    def test_shallow_event_base_selects_unbumped_package_without_sdk_or_registry(self):
        base = self.initialize()
        changed = self.rows[2]
        (self.source / changed["projectPath"]).with_name("Provider.cs").write_text("// Unversioned source change")
        self.commit()
        checkout = self.root / "shallow"
        self.git("clone", "-q", "--depth=1", self.source.as_uri(), str(checkout))
        self.assertEqual("true", self.git("rev-parse", "--is-shallow-repository", cwd=checkout))
        self.assertNotEqual(0, subprocess.run(["git", "cat-file", "-e", base + "^{commit}"], cwd=checkout,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode)
        self.assertEqual([changed["packageId"]], selection.affected_by_changes(checkout, base))
        self.assertEqual([], selection.affected_by_changes(checkout, self.git("rev-parse", "HEAD", cwd=checkout)))
        with self.assertRaises(selection.r.Invalid):
            selection.affected_by_changes(checkout, "main")
        with self.assertRaises(subprocess.CalledProcessError):
            selection.affected_by_changes(checkout, "f" * 40)

    def test_docs_only_can_skip_but_workflow_only_and_unowned_source_cannot(self):
        base = self.initialize()
        (self.source / "docs").mkdir()
        (self.source / "docs/guide.md").write_text("Documentation")
        self.commit()
        self.assertEqual([], selection.affected_by_changes(self.source, base))
        for name in (".github/workflows/validate.yml", "src/Shared/Helper.cs"):
            path = self.source / name
            path.parent.mkdir(parents=True)
            path.write_text("changed")
            self.commit()
            self.assertEqual(sorted(selection.r.FAMILY_PACKAGE_IDS), selection.affected_by_changes(self.source, base))
            base = self.git("rev-parse", "HEAD")

    def test_owned_test_and_cross_folder_rename_select_both_ends_and_reverse_dependencies(self):
        self.initialize()
        package, consumer, other = self.rows[2:5]
        project = self.source / "tests/Owned/Owned.csproj"
        project.parent.mkdir(parents=True)
        project.write_text(f'<Project><PropertyGroup><SunderTestOwner>{other["packageId"]}</SunderTestOwner>'
                           '</PropertyGroup></Project>')
        (self.source / "Sunder.AgentPackage.slnx").write_text('<Solution><Project Path="tests/Owned/Owned.csproj" /></Solution>')
        test = project.with_name("Behavior.cs")
        test.write_text("// First location.")
        lock = (self.source / consumer["projectPath"]).with_name("sunder-contracts.lock.json")
        value = json.loads(lock.read_bytes())
        value["packageArtifacts"][package["packageId"]] = {}
        lock.write_text(json.dumps(value))
        self.commit()
        base = self.git("rev-parse", "HEAD")
        test.write_text("// Changed test.")
        self.commit()
        self.assertEqual([other["packageId"]], selection.affected_by_changes(self.source, base))
        base = self.git("rev-parse", "HEAD")
        test.rename((self.source / package["projectPath"]).with_name(test.name))
        self.commit()
        self.assertEqual(sorted([package["packageId"], consumer["packageId"], other["packageId"]]),
                         selection.affected_by_changes(self.source, base))

    def test_explicit_full_and_empty_selection_do_not_evaluate_or_resolve_dependencies(self):
        for option, expected in ((["--all"], len(self.rows)), (["--packages", "[]"], 0)):
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

    def test_component_selection_does_not_expand_to_family_or_unrelated_security_suites(self):
        inventory = dict(projects=[dict(path="family", owner="agent-family", modes=["source", "package"]),
            dict(path="agent", owner="younics.ai.agent", modes=["source", "package"]),
            dict(path="openai", owner="younics.ai.agent.provider.openai", modes=["source", "package"]),
            dict(path="gemini", owner="younics.ai.agent.provider.gemini", modes=["source", "package"]),
            dict(path="filesystem", owner="younics.ai.agent.execution.local", modes=["source", "package"], sourceOnlyTests=["ordinary"])])
        before = copy.deepcopy(inventory)
        selected = ["younics.ai.agent.provider.openai"]
        for platform in ("linux", "win32", "darwin"):
            for mode in ("source", "package"):
                with self.subTest(platform=platform, mode=mode):
                    value = selection.scope_inventory(inventory, selected, mode, platform)
                    self.assertEqual(["openai"], [row["path"] for row in value["projects"]])
                    self.assertEqual([], selection.scope_inventory(inventory, [], mode, platform)["projects"])
                    self.assertEqual(["agent"], [row["path"] for row in selection.scope_inventory(inventory, ["younics.ai.agent"], mode, platform)["projects"]])
                    self.assertEqual(inventory, selection.scope_inventory(inventory, None, mode, platform))
                    local = selection.scope_inventory(inventory, ["younics.ai.agent.execution.local"], mode, platform)
                    self.assertEqual([inventory["projects"][-1]], local["projects"])
        self.assertEqual(before, inventory)

    def test_summary_rejects_failure_cancellation_and_unexpected_skips_in_every_selected_stage(self):
        def audit(jobs, mode, **options):
            summary.verify(jobs, mode, **options)
            for name in jobs:
                for outcome in ("failure", "cancelled", "skipped" if jobs[name]["result"] == "success" else "success"):
                    broken = copy.deepcopy(jobs)
                    broken[name]["result"] = outcome
                    with self.subTest(mode=mode, job=name, outcome=outcome), self.assertRaises(ValueError):
                        summary.verify(broken, mode, **options)
        family_size = len(self.rows)
        for count, pending in ((0, False), (1, False), (family_size, True), (1, True)):
            active = count > 0
            required = dict(prepare=True, preflight=active, bootstrap=pending, dependencies=active,
                 components=active, verifier=active, source=count == family_size)
            required["dependency-discovery"] = active
            jobs = {name: dict(result="success" if value else "skipped", outputs={}) for name, value in required.items()}
            jobs["prepare"]["outputs"] = dict(selected_count=str(count), full=str(count == family_size).lower())
            jobs["dependency-discovery"]["outputs"]["pending"] = json.dumps(["younics.ai.agent"] if pending else [])
            audit(jobs, "validate")
        for count, resume, owner, production in ((0, False, False, 0), (1, False, False, 1), (1, False, True, 0),
                                                  (family_size, False, True, family_size - 1), (1, True, True, 0)):
            active, build = count > 0, count > 0 and not resume
            consumers = [f"consumer-{index}" for index in range(count - int(owner))]
            required = dict(prepare=True, validation=build, production=build, native=build and production > 0,
                 archive=build, smoke=build, stage=active, release=active)
            required.update({"fan-in": build, "publish-owner": owner, "publish-consumers": bool(consumers), "installed-native": active})
            jobs = {name: dict(result="success" if value else "skipped", outputs={}) for name, value in required.items()}
            jobs["prepare"]["outputs"] = dict(selected_count=str(count), resume=str(resume).lower(),
                owner_selected=str(owner).lower(), consumers=json.dumps(consumers))
            jobs["production"]["outputs"]["selected_count"] = str(production)
            audit(jobs, "release")
        for operation in ("test", "native", "publish"):
            required = {"test": operation == "test", "native": operation == "native", "compose": operation == "native", "publish": operation == "publish"}
            audit({name: dict(result="success" if value else "skipped") for name, value in required.items()}, "component", operation=operation)


if __name__ == "__main__": unittest.main()
