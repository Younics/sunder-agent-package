#!/usr/bin/env python3
"""Delivery and cold-CI orchestration tests; all services/tool processes are mocked."""

import copy
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import sys
RELEASE = Path(__file__).resolve().parents[2] / "release"
sys.path.insert(0, str(RELEASE))
import agent_release as release
import release_inputs as inputs


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(release.canonical(value) + b"\n")


class ColdInputsTests(unittest.TestCase):
    def setUp(self):
        self.scope = tempfile.TemporaryDirectory(prefix="agent-cold-inputs-")
        self.addCleanup(self.scope.cleanup)
        self.root = Path(self.scope.name)
        self.source = self.root / "source"
        self.bundle = self.root / "inputs"
        self.bundle.mkdir()
        self.source_sha, self.core_sha = "a" * 40, "b" * 40
        rows = [{"packageId": "sunder.package.agent" if i == 0 else f"sunder.package.agent.fixture{i:02}",
                 "artifactType": "sunderpkg", "projectPath": f"src/P{i}/P{i}.csproj"} for i in range(15)]
        write(self.source / "packages.json", {"releaseVersion": "2.0.0", "packages": rows})
        self.rows = rows
        descriptor = release.canonical({"contractId": "sunder.fixture.api", "version": "1.0.0"})
        platform_descriptor = release.canonical({"contractId": "sunder.stack.contributor", "version": "2.0.0"})
        platform_hash = release.sha(platform_descriptor)
        catalog = {"schemaVersion": 1, "authorityKind": "platform", "authorityId": "sunder.core", "releaseVersion": "1.1.0", "contractCount": 1,
                   "contracts": [{"contractId": "sunder.stack.contributor", "contractVersion": "2.0.0", "descriptorBytes": len(platform_descriptor),
                                  "descriptorSha256": platform_hash, "abiSha256": "e" * 64, "canonicalDescriptor": platform_descriptor.decode()}]}
        self.catalog_path = self.bundle / "sunder-platform-contracts.v1.json"
        self.catalog_path.write_bytes(release.encoded(catalog))
        catalog_sha = release.file_sha(self.catalog_path)
        self.archive = self.bundle / "Sunder.Package.Agent.2.0.0.sunderpkg"
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("manifest/sunder-package.json", release.encoded({
                "id": "sunder.package.agent", "version": "2.0.0", "contractBundles": [
                    {"contractId": "sunder.fixture.api", "version": "1.0.0", "source": "owned", "authorityKind": "package", "authorityId": "sunder.package.agent",
                     "descriptorPath": "contracts/api.json", "descriptorSha256": release.sha(descriptor), "abiSha256": "d" * 64, "exposure": "public"},
                    {"contractId": "sunder.stack.contributor", "version": "2.0.0", "source": "imported", "authorityKind": "platform", "authorityId": "sunder.core",
                     "selectedAuthorityRelease": "1.1.0", "selectedAuthorityContentIdentity": catalog_sha,
                     "descriptorPath": "contracts/platform.json", "descriptorSha256": platform_hash, "abiSha256": "e" * 64, "exposure": "public"}]}))
            archive.writestr("manifest/content-index.json", b'{"schemaVersion":1,"files":[]}')
            archive.writestr("payload/shared/contracts/api.json", descriptor)
            archive.writestr("payload/shared/contracts/platform.json", platform_descriptor)
        _, public, public_sha = release.archive_metadata(self.archive)
        contracts = {c["contractId"]: {
            **{k: c[k] for k in ("artifactSource", "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity")},
            "resolvedContractVersion": c["contractVersion"], "descriptorBytes": c["descriptorBytes"], "descriptorSha256": c["descriptorSha256"],
            "abiSha256": c["abiSha256"], "etag": '"' + c["descriptorSha256"] + '"'} for c in public["contracts"]}
        for i, row in enumerate(rows):
            parent = self.source / Path(row["projectPath"]).parent
            write(self.source / row["projectPath"], {})
            write(parent / "sunder-contracts.owned.json", {"contracts": {}})
            write(parent / "packages.release.lock.json", {"version": 1})
            write(parent / "sunder-contracts.lock.json", {"lockVersion": 1, "packageImports": {} if not i else {
                  "sunder.package.agent": {"localName": "agent", "presence": "required", "requiredContracts": [], "consumerRoles": ["runtime"]}}, "providerImports": {},
                  "packageArtifacts": {} if not i else {"sunder.package.agent": {"requestedVersionRange": ">=2.0.0 <3.0.0", "includePrerelease": False,
                      "selectedPackageVersion": "2.0.0", "selectedPackageContentIdentity": public["packageContentIdentity"],
                      "publicContractCount": 2, "publicInventorySha256": public_sha, "contracts": contracts}},
                  "platformImports": {"stack": {**contracts["sunder.stack.contributor"], "contractId": "sunder.stack.contributor"}} if not i else {}})
        def asset(path, repo, identifier):
            return {"repository": repo, "releaseId": 123, "assetId": identifier, "name": path.name,
                    "sha256": release.file_sha(path), "size": path.stat().st_size}
        self.manifest = {"schemaVersion": 1, "artifactSourceCommit": self.source_sha, "lockSetSha256": release.locks(self.source),
                         "core": {"artifactSourceCommit": self.core_sha, "sdkVersion": "1.1.0"},
                         "candidates": [{"packageId": "sunder.package.agent", "version": "2.0.0", "packageContentIdentity": public["packageContentIdentity"],
                                         "publicInventorySha256": public_sha, "asset": asset(self.archive, "Younics/sunder-agent-package", 2)}],
                         "platformCatalog": {"authorityId": "sunder.core", "releaseVersion": "1.1.0", "catalogSha256": catalog_sha,
                                             "asset": asset(self.catalog_path, "Younics/sunder-core", 3)}}
        write(self.bundle / inputs.MANIFEST, self.manifest)
        self.manifest_sha = release.file_sha(self.bundle / inputs.MANIFEST)
        self.calls = []
        self.bad_graph = self.bad_diagnostic = self.mutate_lock = False
        self.work = self.root / "cold"
        self.mode = "package"

    def verify(self):
        return inputs.verify(self.source, self.bundle, self.manifest_sha, self.source_sha, self.core_sha)

    def mock_run(self, command, env, cwd, capture=False):
        self.calls.append((command, env))
        self.assertEqual(str(self.work / "sunder-home"), env["SUNDER_HOME"])
        self.assertNotIn("GH_TOKEN", env)
        self.assertNotIn("SUNDER_REGISTRY_PUBLISH_TOKEN", env)
        if command[0] == "deny-network":
            command = command[2:]
        else:
            self.assertIn(command[1], ("restore", "msbuild", "build"))
        if command[1] == "build":
            self.assertEqual(self.mode, "source")
            self.assertTrue(command[2].endswith("Sunder.Package.Build.csproj"))
            tools = Path(command[2]).parent / "bin/Release/net10.0"
            tools.mkdir(parents=True)
            for name in inputs.TOOL_FILES:
                (tools / name).write_bytes(b"mock retained tool bytes")
            return subprocess.CompletedProcess(command, 0)
        self.assertNotIn("-r", command)
        self.assertIn("-p:Configuration=Release", command)
        self.assertIn("-p:SunderAgentVersion=2.0.0", command)
        self.assertIn("-p:ContinuousIntegrationBuild=true", command)
        if command[1] == "restore":
            self.assertEqual("--locked-mode" in command, self.mode == "package")
            return subprocess.CompletedProcess(command, 0)
        if any(arg.startswith("-getProperty:") for arg in command):
            path = self.root / (Path(command[2]).stem + "-assets.json")
            rids = inputs.RIDS[:-1] if self.bad_graph else inputs.RIDS
            write(path, {"targets": {"net10.0/" + rid: {} for rid in rids}})
            return subprocess.CompletedProcess(command, 0, release.encoded({"Properties": {
                "Configuration": "Release", "Version": "2.0.0", "ContinuousIntegrationBuild": "true",
                "RuntimeIdentifiers": ";".join(inputs.RIDS), "SunderPackageRuntimeIdentifiers": "", "ProjectAssetsFile": str(path)}}).decode())
        self.assertIn("-t:RestoreSunderContracts", command)
        cache = Path(env["SunderContractCacheRoot"])
        if not cache.exists() and not any(a.startswith("-p:SunderPackageCandidatePath=") for a in command) and "P1.csproj" in command[2]:
            return subprocess.CompletedProcess(command, 1, "unrelated error" if self.bad_diagnostic else "contract-cache.entry-missing: fixture")
        self.assertTrue(any(a.startswith("-p:SunderPlatformCatalogPath=") for a in command))
        cache.mkdir(parents=True, exist_ok=True)
        if self.mutate_lock:
            (self.source / "src/P1/sunder-contracts.lock.json").write_text("{}")
        return subprocess.CompletedProcess(command, 0)

    def prepare(self):
        with patch.object(inputs, "clean_source"), patch.object(inputs, "offline_prefix", return_value=["deny-network", "--"]), \
                patch.object(inputs, "verify_network_denial"), patch.object(inputs, "run", side_effect=self.mock_run), \
                patch.dict(os.environ, {"GH_TOKEN": "must-not-leak", "SUNDER_REGISTRY_PUBLISH_TOKEN": "must-not-leak"}), \
                patch("sys.stdout", new_callable=io.StringIO):
            inputs.prepare(self.source, self.bundle, self.work, self.mode, self.source_sha, self.core_sha, self.manifest_sha)

    def test_exact_numeric_assets_and_complete_local_lock_entries(self):
        self.assertEqual(self.manifest, self.verify())
        original = self.archive.read_bytes()
        self.archive.write_bytes(original + b"tamper")
        with self.assertRaisesRegex(release.Invalid, "bytes changed"):
            self.verify()

    def test_missing_extra_wrong_identity_and_catalog_are_rejected_before_restore(self):
        mutations = [lambda v: v.update(unknown=True), lambda v: v.update(artifactSourceCommit="f" * 40),
                     lambda v: v.update(lockSetSha256="f" * 64), lambda v: v["candidates"].clear(),
                     lambda v: v["candidates"].append(copy.deepcopy(v["candidates"][0])),
                     lambda v: v["candidates"][0].update(publicInventorySha256="f" * 64),
                     lambda v: v["platformCatalog"].update(catalogSha256="f" * 64)]
        for mutate in mutations:
            bad = copy.deepcopy(self.manifest)
            mutate(bad)
            with self.assertRaises(release.Invalid):
                inputs.validate_manifest(self.source, bad, self.source_sha, self.core_sha)
        (self.bundle / "unreviewed.json").write_text("{}")
        with self.assertRaises(release.Invalid):
            self.verify()

    def test_cold_diagnostic_full_rid_restore_exact_hydration_and_lock_invariance(self):
        before = inputs.source_snapshot(self.source)
        self.prepare()
        self.assertEqual(before, inputs.source_snapshot(self.source))
        self.assertEqual(15, sum(c[0][:2] == ["dotnet", "restore"] for c in self.calls))
        restores = [c for c, _ in self.calls if "-t:RestoreSunderContracts" in c]
        self.assertEqual(16, len(restores))  # one missing diagnostic, then fifteen exact hydrations
        self.assertTrue(all(c[0] == "deny-network" for c in restores))
        self.assertEqual(14, sum(any(a.startswith("-p:SunderPackageCandidatePath=") for a in c) for c in restores))
        self.assertTrue((self.work / "cold-state.json").is_file())

    def test_wrong_missing_diagnostic_fails_without_online_fallback(self):
        self.bad_diagnostic = True
        with self.assertRaisesRegex(release.Invalid, "missing-cache diagnostic"):
            self.prepare()
        self.assertEqual(1, sum("-t:RestoreSunderContracts" in c for c, _ in self.calls))
        self.assertFalse((self.work / "cold-state.json").exists())

    def test_narrowed_rid_graph_fails_before_hydration(self):
        self.bad_graph = True
        with self.assertRaisesRegex(release.Invalid, "declared RID"):
            self.prepare()
        self.assertFalse(any("-t:RestoreSunderContracts" in c for c, _ in self.calls))

    def test_lock_mutation_is_not_repaired_or_accepted(self):
        self.mutate_lock = True
        with self.assertRaisesRegex(release.Invalid, "changed reviewed"):
            self.prepare()
        self.assertFalse((self.work / "cold-state.json").exists())

    def test_missing_private_baseline_is_fenced_and_hydration_does_not_create_it(self):
        path = self.source / "src/P1/sunder-contracts.owned.json"
        path.unlink()
        before = inputs.source_snapshot(self.source)
        self.assertEqual(release.sha(b""), before["baseline-absent:src/P1/sunder-contracts.owned.json"])
        self.prepare()
        self.assertFalse(path.exists())
        self.assertEqual(before, inputs.source_snapshot(self.source))
        write(path, {"contracts": {}})
        with self.assertRaisesRegex(release.Invalid, "source/locks changed"):
            inputs.execute(self.work / "cold-state.json", ["dotnet", "build", "P1.csproj", "--no-restore"])

    def test_unmigrated_role_intent_is_rejected_before_any_restore(self):
        path = self.source / "src/P1/sunder-contracts.lock.json"
        lock = release.load(path)
        del lock["packageImports"]["sunder.package.agent"]["consumerRoles"]
        write(path, lock)
        self.manifest["lockSetSha256"] = release.locks(self.source)
        with self.assertRaisesRegex(release.Invalid, "consumer-roles-migration-required"):
            inputs.validate_manifest(self.source, self.manifest, self.source_sha, self.core_sha)
        self.assertFalse(self.calls)

    def test_new_home_is_required_and_an_ambient_cache_is_never_adopted(self):
        self.work.mkdir()
        with self.assertRaisesRegex(release.Invalid, "must be new"):
            self.prepare()
        self.assertFalse(self.calls)

    def test_source_mode_retains_tools_before_offline_hydration(self):
        self.mode = "source"
        self.prepare()
        tool_index = next(i for i, (command, _) in enumerate(self.calls) if command[:2] == ["dotnet", "build"])
        first_hydration = next(i for i, (command, _) in enumerate(self.calls) if "-t:RestoreSunderContracts" in command)
        self.assertLess(tool_index, first_hydration)
        state = release.load(self.work / "cold-state.json")
        self.assertIn("-p:SunderPackageBuildTaskSourceIsImmutable=true", state["properties"])
        self.assertTrue(all((self.work / "tools" / name).is_file() for name in inputs.TOOL_FILES))

    def test_offline_build_and_fanin_inherit_identical_cache_and_properties(self):
        self.prepare()
        executed = []
        def capture(command, env, cwd, capture=False):
            executed.append(command)
            self.assertEqual("Release", env["Configuration"])
            self.assertEqual("2.0.0", env["SunderAgentVersion"])
            self.assertEqual("true", env["ContinuousIntegrationBuild"])
            self.assertEqual(str(self.work / "sunder-home/contracts/v1/sha256"), env["SunderContractCacheRoot"])
            self.assertNotIn("GH_TOKEN", env)
            return subprocess.CompletedProcess(command, 0)
        with patch.object(inputs, "clean_source"), patch.object(inputs, "offline_prefix", return_value=["deny-network", "--"]), \
                patch.object(inputs, "verify_network_denial"), patch.object(inputs, "run", side_effect=capture):
            inputs.execute(self.work / "cold-state.json", ["dotnet", "build", "P0.csproj", "--no-restore"])
            inputs.execute(self.work / "cold-state.json", ["bash", "scripts/release/assemble-native-family.sh"])
        self.assertTrue(all(command[:2] == ["deny-network", "--"] for command in executed))
        self.assertIn("-p:Configuration=Release", executed[0])
        self.assertIn("-p:SunderAgentVersion=2.0.0", executed[0])
        self.assertIn("-p:ContinuousIntegrationBuild=true", executed[0])

    def test_offline_launcher_failure_has_no_unsandboxed_fallback(self):
        with patch.object(inputs, "clean_source"), patch.object(inputs, "offline_prefix", return_value=["denied", "--"]), \
                patch.object(inputs, "verify_network_denial", side_effect=release.Invalid("launcher unavailable")), \
                patch.object(inputs, "run") as execute:
            with self.assertRaisesRegex(release.Invalid, "launcher unavailable"):
                inputs.prepare(self.source, self.bundle, self.work, "package", self.source_sha, self.core_sha, self.manifest_sha)
            execute.assert_not_called()

    def test_external_offline_adapter_is_hash_pinned_and_snapshotted(self):
        adapter = self.root / "offline-runner"
        adapter.write_bytes(b"#!/bin/sh\nexit 0\n")
        adapter.chmod(0o755)
        digest = release.file_sha(adapter)
        callbacks = []
        with patch.dict(os.environ, {"SUNDER_AGENT_OFFLINE_RUNNER": str(adapter), "SUNDER_AGENT_OFFLINE_RUNNER_SHA256": digest}), \
                patch.object(inputs.atexit, "register", side_effect=callbacks.append):
            prefix = inputs.offline_prefix()
            for callback in callbacks:
                self.addCleanup(callback)
            snapshot = Path(prefix[0])
            self.assertNotEqual(adapter, snapshot)
            self.assertEqual(digest, release.file_sha(snapshot))
            self.assertEqual(0, snapshot.stat().st_mode & 0o222)
            adapter.write_bytes(b"changed executable")
            self.assertEqual(digest, release.file_sha(snapshot))
            with self.assertRaisesRegex(release.Invalid, "digest differs"):
                inputs.offline_prefix()

    def test_producer_routes_build_and_pack_through_shared_cold_state(self):
        # PowerShell execution is owned by native CI; inspect the actual producer's
        # integration boundary here, and execute the shared prepare/exec paths above.
        producer = (RELEASE / "build-native-family.ps1").read_text()
        self.assertIn("$releaseInputHelper prepare", producer)
        self.assertIn("$releaseInputHelper exec $coldState -- dotnet @Arguments", producer)
        self.assertNotIn('$restoreArguments = @("restore"', producer)
        self.assertLess(producer.index("$releaseInputHelper prepare"), producer.index("foreach ($package in $packages)"))
        self.assertIn('"--no-restore"', producer)
        self.assertIn('"-p:SunderPackageUsePrebuiltLeaves=true"', producer)

    def test_owner_asset_metadata_cannot_override_reviewed_locator(self):
        selected = self.manifest["candidates"][0]["asset"]
        record = {"id": selected["assetId"], "name": selected["name"], "size": selected["size"], "digest": "sha256:" + selected["sha256"],
                  "state": "uploaded", "url": f"https://api.github.com/repos/{selected['repository']}/releases/assets/{selected['assetId']}"}
        for field, changed in (("id", 99), ("name", "replacement.sunderpkg"), ("size", 1), ("digest", None), ("url", "https://evil.test/asset")):
            bad = {**record, field: changed}
            with patch.object(inputs, "gh_json", return_value={"id": 123, "assets": [bad]}), patch.object(inputs.subprocess, "Popen") as request:
                with self.assertRaises(release.Invalid):
                    inputs.download(selected, self.root / "download")
                request.assert_not_called()

    def test_durable_download_bounds_and_hashes_the_actual_response(self):
        selected = self.manifest["candidates"][0]["asset"]
        record = {"id": selected["assetId"], "name": selected["name"], "size": selected["size"], "digest": "sha256:" + selected["sha256"],
                  "state": "uploaded", "url": f"https://api.github.com/repos/{selected['repository']}/releases/assets/{selected['assetId']}"}
        class Response:
            def __init__(self, data):
                self.stdout = io.BytesIO(data)
            def wait(self):
                return 0
            def poll(self):
                return 0
        original = self.archive.read_bytes()
        for name, data in (("valid", original), ("oversized", original + b"x"), ("changed", b"x" * len(original))):
            with patch.object(inputs, "gh_json", return_value={"id": 123, "assets": [record]}), \
                    patch.object(inputs.subprocess, "Popen", return_value=Response(data)):
                if name == "valid":
                    inputs.download(selected, self.root / name)
                    self.assertEqual(original, (self.root / name).read_bytes())
                else:
                    with self.assertRaises(release.Invalid):
                        inputs.download(selected, self.root / name)


if __name__ == "__main__":
    unittest.main()
