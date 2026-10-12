#!/usr/bin/env python3
"""Execute the real family publisher against isolated filesystem/GitHub/Registry mocks."""

import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


def mock_main(tool):
    root = Path(os.environ["FAMILY_FIXTURE_ROOT"])
    state_file = root / "github.json"
    args = sys.argv[1:]
    with (root / "events").open("a") as log:
        log.write(tool + " " + " ".join(args) + "\n")
    if tool == "git":
        if args == ["rev-parse", "HEAD"]:
            print("1" * 40)
        elif args != ["status", "--porcelain", "--untracked-files=no"]:
            raise RuntimeError("Unexpected git command")
        return
    if tool == "dotnet":
        if args == ["--version"]:
            print("10.0.100")
        elif args == ["--list-sdks"]:
            print("10.0.100 [/mock/sdk]")
        else:
            raise RuntimeError("No Build/Restore/Pack is allowed")
        return
    if tool == "curl":
        raise RuntimeError("No real HTTP is allowed")
    if tool == "registry":
        sys.path.insert(0, str(root / "source/scripts/release"))
        import agent_release as release
        require = release.require
        require(len(args) == 6 and args[0] == "--initial-yanked" and args[2] == "https://registry.test", "Family did not select Yanked publication")
        expected, archive, package_id, version = release.load(args[1]), Path(args[3]), args[4], args[5]
        _, public, public_sha = release.archive_metadata(archive)
        require(expected == {"package": {"packageId": package_id, "version": version, "packageContentIdentity": public["packageContentIdentity"]},
                             "ownerPrincipalId": "1" * 32, "publicInventorySha256": public_sha, "initialAvailability": "Yanked"}, "Expected tuple was not derived from retained bytes")
        state_path = root / "registry.json"
        state = json.loads(state_path.read_text()) if state_path.exists() else {}
        retained = {"publication": expected, "sha256": release.file_sha(archive), "availability": "Yanked"}
        if package_id in state:
            require(state[package_id] == retained, "Accepted tuple cannot change")
        else:
            state[package_id] = retained
            state_path.write_text(json.dumps(state))
            with (root / "uploads").open("a") as log:
                log.write(package_id + "\n")
        if os.environ.get("FAMILY_FAIL_PACKAGE") == package_id:
            sys.exit(75)  # Accepted bytes but evidence-readback outage: pause exact family.
        return
    if tool != "gh":
        raise RuntimeError("Unknown mock")
    state = json.loads(state_file.read_text()) if state_file.exists() else None
    if args[:2] == ["release", "view"]:
        if state is None:
            sys.exit(1)
        if "--json" in args:
            print(json.dumps(state))
        return
    if args[:2] == ["release", "create"]:
        assert state is None
        state = {"databaseId": 424242, "tagName": "agent/v2.0.0", "isDraft": True, "isPrerelease": False,
                 "body": args[args.index("--notes") + 1], "assets": []}
        (root / "remote").mkdir()
    elif args[:2] == ["release", "upload"]:
        assert not state["assets"]
        for index, path in enumerate(args[5:]):
            path = Path(path)
            shutil.copyfile(path, root / "remote" / path.name)
            state["assets"].append({"id": 1001 + index, "name": path.name, "size": path.stat().st_size})
    elif args[:2] == ["release", "download"]:
        destination = Path(args[args.index("--dir") + 1])
        for path in (root / "remote").iterdir():
            shutil.copyfile(path, destination / path.name)
        return
    elif args[:3] == ["api", "--method", "PATCH"]:
        payload = json.loads(Path(args[args.index("--input") + 1]).read_text())
        assert set(payload) == {"body"}  # Never announce during immutable publication.
        state["body"] = payload["body"]
    else:
        raise RuntimeError("Unsupported GitHub operation: " + str(args))
    state_file.write_text(json.dumps(state))


class FamilyPublisherTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.assets = self.root / "assets"
        self.assets.mkdir()
        scripts = Path(__file__).resolve().parents[2] / "release"
        sys.path.insert(0, str(scripts))
        spec = importlib.util.spec_from_file_location("release_fixtures", Path(__file__).with_name("agent-release.fixture.py"))
        fixtures = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fixtures)
        self.release = fixtures.release
        self.ids = ["sunder.package.agent"] + [f"sunder.package.agent.fixture{i:02}" for i in range(14)]
        fixtures.seed_source(self.source, self.ids)
        for index, package_id in enumerate(self.ids):
            dependencies = [] if not index else [{"packageId": self.ids[0], "versionRange": ">=2.0.0 <3.0.0"}]
            if index == 1:
                dependencies.append({"packageId": self.ids[-1], "versionRange": "=2.0.0"})
            fixtures.pack(self.assets / f"{package_id}.2.0.0.sunderpkg",
                          {"id": package_id, "version": "2.0.0", "dependsOn": dependencies, "contractBundles": []})
        for name in ("native-test-evidence.json", "package-output-audit.json"):
            (self.assets / name).write_text('{"mockedNativeBoundary":true}\n')
        policy = self.source / "scripts/release"
        policy.mkdir(parents=True)
        for name in ("publish-agent-family.sh", "agent_release.py"):
            shutil.copyfile(scripts / name, policy / name)
        shutil.copyfile(scripts / "generate-release-evidence.sh", policy / "generate-release-evidence.sh")
        (policy / "verify-native-family-evidence.sh").write_text('set -eu\n[[ $# == 8 && -f "$1" && -d "$2" && -f "$3" ]]\n')
        (policy / "generate-package-output-audit.sh").write_text('set -eu\n[[ $# == 4 ]]\nprintf \'{"mockedNativeBoundary":true}\\n\' > "$4"\n')
        (policy / "publish-registry-package.sh").write_text('set -eu\nexec registry "$@"\n')
        mock_bin = self.root / "bin"
        mock_bin.mkdir()
        for name in ("git", "gh", "curl", "dotnet", "registry"):
            path = mock_bin / name
            shutil.copyfile(__file__, path)
            path.chmod(0o755)
        self.env = {**os.environ, "PATH": str(mock_bin) + os.pathsep + os.environ["PATH"],
                    "FAMILY_FIXTURE_ROOT": str(self.root), "CORE_COMMIT": "2" * 40, "CORE_TAG": "sdk/v1.1.0",
                    "CORE_SDK_VERSION": "1.1.0", "GH_TOKEN": "mock-token", "GH_REPO": "Younics/sunder-agent-package",
                    "GITHUB_REPOSITORY": "Younics/sunder-agent-package", "GITHUB_REPOSITORY_ID": "1234",
                    "GITHUB_REF": "refs/tags/agent/v2.0.0", "GITHUB_REF_NAME": "agent/v2.0.0", "GITHUB_RUN_ATTEMPT": "1",
                    "GITHUB_RUN_ID": "8001", "GITHUB_SHA": "1" * 40, "GITHUB_WORKFLOW": "Sunder Agent Family Release",
                    "GITHUB_WORKFLOW_SHA": "1" * 40, "GITHUB_EVENT_NAME": "push",
                    "GITHUB_WORKFLOW_REF": "Younics/sunder-agent-package/.github/workflows/sunder-package-release.yml@refs/tags/agent/v2.0.0",
                    "IS_PRERELEASE": "false", "REGISTRY_API_URL": "https://registry.test",
                    "SUNDER_REGISTRY_PUBLISH_TOKEN": "sunder_pub_v1_" + "a" * 43, "VERSION": "2.0.0",
                    "SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT": "1" * 40,
                    "SUNDER_AGENT_LOCK_SET_SHA256": self.release.locks(self.source),
                    "SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE": "https://approval.test/fixture",
                    "SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID": "1" * 32,
                    "PYTHONDONTWRITEBYTECODE": "1"}
        for inherited in ("GITHUB_OUTPUT", "SUNDER_AGENT_RELEASE_SOURCE_DIRECTORY", "FAMILY_FAIL_PACKAGE"):
            self.env.pop(inherited, None)

    def run_publisher(self):
        return subprocess.run(["bash", "scripts/release/publish-agent-family.sh", str(self.assets)], cwd=self.source,
                              env=self.env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def resume_inputs(self):
        for path in self.assets.iterdir():
            path.unlink()
        for path in (self.root / "remote").iterdir():
            if path.suffix == ".sunderpkg" or path.name in ("native-test-evidence.json", "package-output-audit.json"):
                shutil.copyfile(path, self.assets / path.name)

    def test_partial_yanked_family_resumes_same_bytes_without_rebuild_or_reupload(self):
        self.env["FAMILY_FAIL_PACKAGE"] = self.ids[3]
        first = self.run_publisher()
        self.assertNotEqual(0, first.returncode, first.stdout)
        self.assertIn("Preserving draft", first.stdout)
        state = json.loads((self.root / "registry.json").read_text())
        self.assertEqual(3, len(state))
        self.assertTrue(all(p["availability"] == "Yanked" for p in state.values()))
        retained = {p.name: self.release.file_sha(p) for p in (self.root / "remote").iterdir()}
        self.assertEqual(21, len(retained))
        del self.env["FAMILY_FAIL_PACKAGE"]
        self.env["GITHUB_RUN_ATTEMPT"] = "2"
        self.resume_inputs()
        self.env["SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID"] = "2" * 32
        changed_owner = self.run_publisher()
        self.assertNotEqual(0, changed_owner.returncode, changed_owner.stdout)
        self.assertEqual(3, len((self.root / "uploads").read_text().splitlines()))
        self.env["SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID"] = "1" * 32
        second = self.run_publisher()
        self.assertEqual(0, second.returncode, second.stdout)
        self.assertEqual(retained, {p.name: self.release.file_sha(p) for p in (self.root / "remote").iterdir()})
        uploads = (self.root / "uploads").read_text().splitlines()
        self.assertEqual(15, len(uploads))
        self.assertEqual(self.ids[0], uploads[0])
        self.assertLess(uploads.index(self.ids[-1]), uploads.index(self.ids[1]))
        self.assertTrue(json.loads((self.root / "github.json").read_text())["isDraft"])
        self.assertTrue(all(p["availability"] == "Yanked" for p in json.loads((self.root / "registry.json").read_text()).values()))
        self.assertEqual(2, sum(line.startswith("dotnet ") for line in (self.root / "events").read_text().splitlines()))

    def test_approval_drift_stops_before_any_external_write(self):
        self.env["SUNDER_AGENT_LOCK_SET_SHA256"] = "0" * 64
        completed = self.run_publisher()
        self.assertNotEqual(0, completed.returncode)
        self.assertIn("approved lock set", completed.stdout)
        self.assertFalse((self.root / "github.json").exists())
        self.assertFalse((self.root / "registry.json").exists())


if __name__ == "__main__":
    tool = Path(sys.argv[0]).name
    if tool in ("git", "gh", "curl", "dotnet", "registry"):
        mock_main(tool)
    else:
        unittest.main()
