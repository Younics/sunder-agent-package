"""Preparation command/byte-isolation regressions, not native acceptance."""
from contextlib import redirect_stderr
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import prepare_maker_inputs as preparation


class PrepareMakerInputsTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory()
        self.addCleanup(scope.cleanup)
        self.root = Path(scope.name).resolve()
        self.core, self.output = self.root / "core", self.root / "prepared"
        self.commit = "a" * 40
        self.rows = [dict(packageId=name, project=f"packages/{name}/{name}.csproj") for name in
            ("Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build", "Sunder.Package.Templates")]
        self.versions = {row["packageId"]: f"1.{index + 1}.0" for index, row in enumerate(self.rows)}
        self.write("eng/test-oracles/core-public-packages.json", dict(nuget=self.rows))
        self.write("eng/platform-contracts/sunder-contracts.owned.json", dict(contracts={"current": dict(current=dict(catalogRelease=dict(releaseVersion="1.1.0")))}))
        self.calls, self.verified = [], []
        self.clean = True

    def write(self, name, value):
        path = self.core / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))

    def row(self, command):
        return next(row for row in self.rows if str(self.core / row["project"]) in command)

    def target(self, command):
        artifacts = Path(next(value.split("=", 1)[1] for value in command if value.startswith("-p:ArtifactsPath=")))
        name = Path(command[2]).stem
        return artifacts / "bin" / name / "release" / (name + ".dll")

    def evaluate(self, command, **kwargs):
        if command[0] == "git":
            return self.commit + "\n" if command[-2:] == ["rev-parse", "HEAD"] else ("" if self.clean else " M tracked.cs\n")
        self.calls.append((command, kwargs))
        if "-getProperty:PackageId,PackageVersion" in command:
            row = self.row(command)
            return json.dumps(dict(Properties=dict(PackageId=row["packageId"], PackageVersion=self.versions[row["packageId"]])))
        return str(self.target(command)) + "\n"

    def run_build(self, command, **kwargs):
        self.calls.append((command, kwargs))
        if command[1] == "pack":
            row = self.row(command)
            (self.output / "feed" / (row["packageId"] + "." + self.versions[row["packageId"]] + ".nupkg")).write_bytes(row["packageId"].encode())
        elif command[1] == "build":
            target = self.target(command)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(target.name.encode())
            if target.stem == "Sunder.App":
                (target.parent / "Sunder.App.runtimeconfig.json").write_text('{}')
        elif command[2:4] == ["platform-contracts", "export"]:
            Path(command[-1]).write_bytes(b'{"catalog":"source-bound"}')
        return subprocess.CompletedProcess(command, 0)

    def verify(self, archive, package_id, version, commit):
        self.assertEqual(package_id.encode(), archive.read_bytes())
        self.assertEqual(self.versions[package_id], version)
        self.assertEqual(self.commit, commit)
        self.verified.append(archive.name)

    def mocks(self):
        from contextlib import ExitStack
        stack = ExitStack()
        stack.enter_context(patch.object(preparation.subprocess, "check_output", side_effect=self.evaluate))
        stack.enter_context(patch.object(preparation.subprocess, "run", side_effect=self.run_build))
        stack.enter_context(patch.object(preparation.runpy, "run_path", return_value=dict(PACKAGE_IDS=tuple(self.versions), verify_archive=self.verify)))
        return stack

    def test_six_independent_versions_and_exact_app_catalog_are_prepared_without_credential_or_output_leaks(self):
        credentials = {key: "test-secret" for key in ("GH_TOKEN", "GITHUB_TOKEN", "SUNDER_REGISTRY_CLI_TOKEN",
            "SUNDER_REGISTRY_API_KEY", "SUNDER_REGISTRY_PUBLISH_TOKEN", "SUNDER_AGENT_NATIVE_REGISTRY_TOKEN", "SUNDER_REGISTRY_NATIVE_READ_TOKEN")}
        with self.mocks(), patch.dict(os.environ, credentials):
            preparation.prepare(self.core, self.output, self.commit)
        value = json.loads((self.output / "inputs.json").read_text())
        self.assertEqual(2, value["schemaVersion"])
        self.assertTrue(value["workingTreeClean"])
        self.assertEqual(self.versions, {row["packageId"]: row["version"] for row in value["packages"]})
        self.assertEqual(6, len(self.verified))
        for row in value["app"]["files"] + [value["platformCatalog"]]:
            path = self.output / row["path"]
            self.assertEqual(row, preparation.file_record(path, self.output))
        self.assertIn(value["app"]["targetPath"], {row["path"] for row in value["app"]["files"]})
        for command, kwargs in self.calls:
            self.assertFalse(set(credentials) & set(kwargs["env"]))
            self.assertEqual(self.core, kwargs["cwd"])
            if command[1] in ("restore", "pack", "build", "msbuild"):
                self.assertTrue(any(value.startswith("-p:ArtifactsPath=" + str(self.output)) for value in command))
            self.assertFalse(any(value.startswith(("-p:Version=", "-p:PackageVersion=")) for value in command))

    def test_host_only_does_not_restore_pack_or_verify_authoring_nugets(self):
        with self.mocks(): preparation.prepare(self.core, self.output, self.commit, host_only=True)
        value = json.loads((self.output / "inputs.json").read_text())
        self.assertEqual([], value["packages"])
        self.assertEqual([], self.verified)
        self.assertFalse(list((self.output / "feed").iterdir()))
        self.assertFalse(any(command[1] == "pack" for command, _ in self.calls))
        self.assertTrue(value["app"]["files"])

    def test_existing_output_and_wrong_source_fail_before_build_without_replacing_any_bytes(self):
        self.output.mkdir()
        marker = self.output / "retained"
        marker.write_bytes(b"original failure")
        with self.mocks(), self.assertRaisesRegex(ValueError, "must be new"):
            preparation.prepare(self.core, self.output, self.commit)
        self.assertEqual(b"original failure", marker.read_bytes())
        with self.mocks(), self.assertRaisesRegex(ValueError, "selected Core commit"):
            preparation.prepare(self.core, self.root / "unstarted", "b" * 40)
        self.assertFalse((self.root / "unstarted").exists())
        self.assertFalse(self.calls)

    def test_failed_build_keeps_logs_and_partial_inputs_but_never_writes_success_receipt(self):
        stderr = io.StringIO()
        diagnostic = b"error NU1105: invalid project information\n"
        def fail(command, **kwargs):
            kwargs["stdout"].write(b"earlier output\n" + b"x" * 8192 + b"\n" + diagnostic)
            raise subprocess.CalledProcessError(1, command)
        with self.mocks(), patch.object(preparation.subprocess, "run", side_effect=fail), redirect_stderr(stderr), \
                self.assertRaises(subprocess.CalledProcessError):
            preparation.prepare(self.core, self.output, self.commit)
        [log] = list(self.output.glob("*.log"))
        self.assertTrue(log.read_bytes().startswith(b"earlier output\n"))
        self.assertTrue(log.read_bytes().endswith(diagnostic))
        self.assertIn(str(log), stderr.getvalue())
        self.assertIn(diagnostic.decode().strip(), stderr.getvalue())
        self.assertNotIn("earlier output", stderr.getvalue())
        self.assertFalse((self.output / "inputs.json").exists())
        with self.mocks(), self.assertRaisesRegex(ValueError, "must be new"):
            preparation.prepare(self.core, self.output, self.commit)

    def test_dirty_working_tree_is_not_promoted_to_clean_source_evidence(self):
        self.clean = False
        with self.mocks(): preparation.prepare(self.core, self.output, self.commit, host_only=True)
        self.assertFalse(json.loads((self.output / "inputs.json").read_text())["workingTreeClean"])

    def test_ambiguous_catalog_release_and_app_output_escape_are_rejected(self):
        self.write("eng/platform-contracts/sunder-contracts.owned.json", dict(contracts={}))
        with self.mocks(), self.assertRaisesRegex(ValueError, "Ambiguous"):
            preparation.prepare(self.core, self.output, self.commit, host_only=True)
        self.assertFalse((self.output / "inputs.json").exists())
        outside = self.root / "ambient-app.dll"
        outside.write_bytes(b"not this preparation")
        with self.mocks(), patch.object(preparation.subprocess, "check_output", return_value=str(outside)), self.assertRaises(ValueError):
            preparation.build_host_project(self.core, self.output, {}, Path("host/Sunder.App/Sunder.App.csproj"), "escape.log")


if __name__ == "__main__":
    unittest.main()
