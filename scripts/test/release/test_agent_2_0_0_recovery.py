"""Fail-closed edges of the read-only recovery collection and native proof."""
import copy
import json
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from zipfile import ZipFile, ZipInfo

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import collect_agent_2_0_0 as collect
import run_agent_2_0_0_recovery as runner
from native_release_inputs import file_record


class RecoveryControlTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name).resolve()

    def test_actions_zip_requires_exact_version_and_protected_build(self):
        pin = collect.recovery.read_inventory(collect.INVENTORY)
        source = pin["archiveSource"]
        feed = pin["buildInputs"]["makerFeeds"]["osx-x64"]
        name = "agent-native-diagnostics-osx-x64-3"
        meta = dict(id=feed["artifact"], name=name, digest=feed["zipDigest"], expired=False,
                    expires_at=feed["expiresAt"], size_in_bytes=feed["zipSize"],
                    workflow_run=dict(id=source["releaseRun"], head_sha=source["commit"],
                                      repository_id=collect.AGENT_REPO_ID, head_repository_id=collect.AGENT_REPO_ID,
                                      head_branch="main"))

        def validate(value):
            collect.validate_artifact(value, feed["artifact"], name, feed["zipDigest"][7:], source,
                                      feed["zipSize"], feed["expiresAt"])

        validate(meta)
        for fields, value in (
            (("id",), pin["buildInputs"]["makerFeeds"]["osx-arm64"]["artifact"]),
            (("expired",), True),
            (("digest",), "sha256:" + "0" * 64),
            (("name",), "agent-native-diagnostics-osx-arm64-3"),
            (("workflow_run", "head_sha"), pin["testInputs"]["commit"]),
            (("workflow_run", "head_branch"), "another-branch"),
            (("workflow_run", "head_repository_id"), collect.AGENT_REPO_ID + 1),
        ):
            with self.subTest(fields=fields):
                changed = copy.deepcopy(meta)
                if len(fields) == 1:
                    changed[fields[0]] = value
                else:
                    changed[fields[0]][fields[1]] = value
                with self.assertRaisesRegex(ValueError, "coordinates differ or expired"):
                    validate(changed)

    def test_closed_members_fail_before_extracting_foreign_path(self):
        path = self.root / "invalid.zip"
        with ZipFile(path, "x") as bundle:
            bundle.writestr("original.json", b"{}")
            bundle.writestr("../outside.json", b"{}")
        with self.assertRaisesRegex(ValueError, "artifact inventory"):
            collect.unpack(path, self.root, {"original.json": 10})
        self.assertFalse((self.root / "original.json").exists())
        with self.assertRaisesRegex(ValueError, "Unsafe path"):
            collect.unpack(path, self.root, {"../outside.json": 10, "original.json": 10})

    def test_zip_link_is_not_input(self):
        path = self.root / "linked.zip"
        with ZipFile(path, "x") as bundle:
            entry = ZipInfo("original.json")
            entry.create_system = 3
            entry.external_attr = (stat.S_IFLNK | 0o777) << 16
            bundle.writestr(entry, b"target")
        with self.assertRaisesRegex(ValueError, "Invalid retained artifact member"):
            collect.unpack(path, self.root, {"original.json": 20})

    def test_loaded_runtime_must_be_native_and_from_the_test_output(self):
        built, assemblies = {}, []
        for name in sorted(runner.REQUIRED_TEST_ASSEMBLIES):
            path = self.root / (name + ".dll")
            path.write_text(name, encoding="utf-8")
            built[name] = (path, file_record(path))
            assemblies.append(dict(name=name, path=str(path), mvid="1d977ff5-92ce-4e72-ab84-96137ec3c8bc"))
        report = dict(runtimeIdentifier="osx-x64", processArchitecture="X64", osArchitecture="X64", pid=123, assemblies=assemblies)
        self.assertEqual(set(built), set(runner.loaded_runtime(report, "X64", "osx-x64", built)))
        with self.assertRaisesRegex(ValueError, "execute natively"):
            runner.loaded_runtime(dict(report, osArchitecture="Arm64"), "X64", "osx-x64", built)
        with self.assertRaisesRegex(ValueError, "evidence is incomplete"):
            runner.loaded_runtime(dict(report, assemblies=assemblies[:-1]), "X64", "osx-x64", built)
        assemblies[0] = dict(assemblies[0], path=str(self.root / "foreign.dll"))
        with self.assertRaisesRegex(ValueError, "differs from the exact test output"):
            runner.loaded_runtime(report, "X64", "osx-x64", built)

    def test_app_readiness_needs_the_exact_executable_and_hook_marker(self):
        executable = self.root / runner.APP
        path = self.root / "apps" / runner.OWNER / ("9" * 32)
        path.mkdir(parents=True)
        log = path / "process.txt"
        log.write_text("pid=123; exited=True; exitCode=0\nstdout:\nSUNDER_NATIVE_APP_RPC:ready\n", encoding="utf-8")
        events = [dict(phase=phase, pid=123, command=str(executable), arguments=[], timeoutMilliseconds=timeout, exitCode=code)
                  for phase, timeout, code in [("start", None, "<running>"), ("wait", 15000, "<running>"),
                                                ("exit", 15000, "0"), ("cleanup", None, "0")]]
        (path / "app.command.jsonl").write_text("".join(json.dumps(row) + "\n" for row in events), encoding="utf-8")
        self.assertEqual([runner.OWNER], [row["packageId"] for row in runner.app_processes(self.root, executable, "published")])
        with self.assertRaisesRegex(ValueError, "App did not run"):
            runner.app_processes(self.root, executable.with_name("another-app"), "published")
        log.write_text("pid=123; exited=True; exitCode=0\nstdout:\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "No startup-hook readiness"):
            runner.app_processes(self.root, executable, "published")


if __name__ == "__main__":
    unittest.main()
