"""Selected CI roots hydrate their actual evaluated package dependency closure."""
import json
import copy
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(SCRIPTS / "ci"), str(SCRIPTS / "release")]
import project_scope
import agent_ci

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class ProjectScopeTests(unittest.TestCase):
    setUp = fixture_setup

    def test_selected_roots_follow_real_references_without_restoring_unrelated_packages(self):
        owner, component, unrelated = self.rows[:3]
        test = "tests/Component.Tests/Component.Tests.csproj"
        (self.source / test).parent.mkdir(parents=True)
        (self.source / test).write_text("<Project />")
        inventory = dict(projects=[dict(path=test, modes=["source"])])
        roots = project_scope.projects(self.source, [component["packageId"]], inventory, "source")
        self.assertEqual(sorted([component["projectPath"], test]), roots)
        references = {test: [component["projectPath"], owner["projectPath"]], component["projectPath"]: [], owner["projectPath"]: []}
        commands = []
        def evaluate(command, **kwargs):
            commands.append(command)
            self.assertEqual(["dotnet", "msbuild"], command[:2])
            self.assertIn("-getItem:ProjectReference", command)
            self.assertIn("-p:SunderUseSourceSdk=false", command)
            name = Path(command[2]).relative_to(self.source.resolve()).as_posix()
            return subprocess.CompletedProcess(command, 0, json.dumps(dict(Items=dict(ProjectReference=[
                dict(FullPath=str(self.source / path)) for path in references[name]]))))
        with patch.object(project_scope.subprocess, "run", side_effect=evaluate):
            selected = project_scope.packages(self.source, roots, {"SunderUseSourceSdk": "false"}, {})
        self.assertEqual(sorted([owner["packageId"], component["packageId"]]), selected)
        self.assertEqual(3, len(commands))
        self.assertFalse(any(unrelated["projectPath"] in command[2] for command in commands))
        with patch.object(project_scope.subprocess, "run", side_effect=AssertionError("no evaluation for explicit full graph")):
            self.assertEqual(15, len(project_scope.packages(self.source, ["Sunder.AgentPackage.slnx"], {}, {})))

    def test_discovery_covers_source_and_package_test_dependencies_in_the_scoped_handoff(self):
        owner, component, package_only = self.rows[:3]
        path = self.root / "inventory.json"
        path.write_text(json.dumps(dict(projects=[
            dict(path="tests/Source.csproj", modes=["source"]),
            dict(path="tests/Package.csproj", modes=["package"])])))
        def closure(source, roots, properties, env):
            mode = properties["SunderUseSourceSdk"]
            self.assertIn(component["projectPath"], roots)
            self.assertEqual(mode == "true", "tests/Source.csproj" in roots)
            self.assertEqual(mode == "false", "tests/Package.csproj" in roots)
            return [owner["packageId"], component["packageId"]] + ([package_only["packageId"]] if mode == "false" else [])
        with patch.object(agent_ci, "generate_inventory", return_value=path), \
             patch.object(project_scope, "packages", side_effect=closure):
            scope = agent_ci.test_dependency_scope(self.source, [component["packageId"]])
        self.assertEqual(sorted(row["packageId"] for row in self.rows[:3]), scope)
        manifest = copy.deepcopy(self.manifest)
        manifest.update(schemaVersion=3, packageIds=scope)
        manifest["core"] = dict(artifactSourceCommit=self.core_sha, buildToolVersion="1.0.2")
        for item in [*manifest["candidates"], manifest["platformCatalog"]]:
            item["asset"] = {key: item["asset"][key] for key in ("name", "size", "sha256")}
        self.assertEqual(manifest, agent_ci.inputs.validate_manifest(
            self.source, manifest, self.source_sha, self.core_sha, [owner["packageId"], component["packageId"]]))


if __name__ == "__main__":
    unittest.main()
