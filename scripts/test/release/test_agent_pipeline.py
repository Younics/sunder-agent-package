"""Integrated release boundaries; registry and GitHub mutations are mocked."""
import json
import copy
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_publication as pipeline
import reverse_agent_cohort as reverse


class MemoryPublication(pipeline.GitHubRelease):
    def __init__(self):
        super().__init__(pipeline.REPOSITORY, "agent-builder/v2.0.0")
        self.value = dict(id=42, tag_name=self.tag, target_commitish="a" * 40, draft=True, prerelease=False, assets=[])
        self.exists = True

    def resolve(self):
        return copy.deepcopy(self.value) if self.exists else None

    def api(self, suffix, method="GET", value=None):
        if suffix == "releases" and method == "POST":
            self.exists = True
            raise subprocess.TimeoutExpired("lost creation reply", 300)
        return copy.deepcopy(self.value)


class AgentTransportTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory()
        self.addCleanup(scope.cleanup)
        self.root = Path(scope.name)
        self.path = self.root / "archive.sunderpkg"
        self.path.write_bytes(b"tested bytes")
        self.store = MemoryPublication()
        self.asset = dict(id=101, name=self.path.name, size=self.path.stat().st_size,
                          digest="sha256:" + pipeline.r.file_sha(self.path), state="uploaded")

    def response(self, data):
        response = Mock(stdout=io.BytesIO(data))
        response.wait.return_value = response.poll.return_value = 0
        return response

    def test_tag_identity_cannot_escape_the_agent_stream_or_misclassify_build_metadata(self):
        for tag in ("agent-other/../../v2.0.0", "agentish/v2.0.0", "agent/v2.0.0/extra", "agent", "agent/v02.0.0"):
            with self.subTest(tag=tag), self.assertRaises(ValueError): pipeline.GitHubRelease(pipeline.REPOSITORY, tag)
        self.assertFalse(pipeline.GitHubRelease(pipeline.REPOSITORY, "agent-builder/v2.0.0+build-hyphen").prerelease)
        self.assertTrue(pipeline.GitHubRelease(pipeline.REPOSITORY, "agent-builder/v2.0.0-rc.1").prerelease)

    def test_release_creation_recovers_a_lost_reply_and_refuses_foreign_source_or_prerelease(self):
        self.store.exists = False
        self.assertEqual(42, self.store.create("a" * 40, "fixture")["id"])
        for field, value in (("target_commitish", "b" * 40), ("prerelease", True)):
            original = self.store.value[field]
            self.store.value[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError): self.store.create("a" * 40, "fixture")
            self.store.value[field] = original

    def test_remote_inventory_rejects_collisions_bad_ids_sizes_and_unsafe_paths(self):
        for asset in ({**self.asset, "name": "../outside"}, {**self.asset, "id": True}, {**self.asset, "size": pipeline.MAX_ASSET + 1},
                      {**self.asset, "digest": None}, {**self.asset, "state": "new"}):
            self.store.value["assets"] = [asset]
            with self.subTest(asset=asset), self.assertRaises(ValueError): self.store.get(42)
        self.store.value["assets"] = [self.asset, {**self.asset, "id": 102, "name": self.path.name.upper()}]
        with self.assertRaisesRegex(ValueError, "ambiguous"): self.store.get(42)

    def test_download_is_length_bounded_hash_verified_and_never_replaces_other_local_bytes(self):
        for label, data in (("valid", b"tested bytes"), ("changed", b"changedbytes"), ("oversized", b"tested bytes+")):
            destination = self.root / label
            with patch.object(pipeline.subprocess, "Popen", return_value=self.response(data)):
                if label == "valid":
                    self.store.download(self.asset, destination)
                    self.assertEqual(data, destination.read_bytes())
                else:
                    with self.assertRaises(ValueError): self.store.download(self.asset, destination)
                    self.assertFalse(destination.exists())
        destination = self.root / "keep"; destination.write_bytes(b"different")
        with patch.object(pipeline.subprocess, "Popen", return_value=self.response(b"tested bytes")), self.assertRaisesRegex(ValueError, "overwrite"):
            self.store.download(self.asset, destination)
        self.assertEqual(b"different", destination.read_bytes())

    def test_upload_retries_reconcile_exact_bytes_and_never_modify_announced_assets(self):
        def upload(*args, **kwargs):
            self.store.value["assets"] = [self.asset]
            raise subprocess.TimeoutExpired("lost upload reply", 300)
        with patch.object(pipeline.subprocess, "run", side_effect=upload) as write, \
             patch.object(pipeline.subprocess, "Popen", side_effect=lambda *args, **kwargs: self.response(b"tested bytes")), \
             patch.object(pipeline.time, "sleep"):
            self.store.upload(42, [self.path])
            self.store.upload(42, [self.path])
            self.assertEqual(1, write.call_count)
            self.path.write_bytes(b"other bytes")
            with self.assertRaisesRegex(ValueError, "Immutable asset conflict"): self.store.upload(42, [self.path])
            self.store.value["draft"] = False
            with self.assertRaisesRegex(ValueError, "announced"): self.store.upload(42, [self.path])
            self.assertEqual(1, write.call_count)


class AgentPipelineTests(unittest.TestCase):
    def test_rollback_discovers_source_from_one_draft_and_refuses_ambiguity(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            env = dict(GITHUB_REPOSITORY=pipeline.REPOSITORY, GITHUB_REF="refs/heads/main", GITHUB_REF_PROTECTED="true",
                GITHUB_WORKFLOW_SHA="a" * 40, GITHUB_SHA="a" * 40,
                GITHUB_WORKFLOW_REF=pipeline.REPOSITORY + "/.github/workflows/sunder-agent-cohort-rollback.yml@refs/heads/main",
                CONFIRMATION="reverse-unannounced-cohort", EXPECTED_REVISION="7", IDEMPOTENCY_KEY="reviewed-reverse", GITHUB_OUTPUT=str(root / "outputs"))
            value = dict(id=42, tag_name="agent-builder/v2.0.0", draft=True, target_commitish="b" * 40,
                         assets=[dict(name="agent-family-cohort-" + "c" * 64 + ".json")])
            with patch.dict(os.environ, env, clear=True), patch.object(reverse.receipts, "gh", side_effect=lambda _: json.dumps(value).encode()), \
                 patch.object(reverse.receipts, "download_bytes", return_value=dict(artifactSourceCommit="b" * 40)) as download, \
                 patch.object(reverse.cohort, "store") as store:
                reverse.resolve("42", root / "forward")
                download.assert_called_once_with(pipeline.REPOSITORY, "42", "agent-builder/v2.0.0", "c" * 64)
                self.assertIn("source_sha=" + "b" * 40, (root / "outputs").read_text())
                for changed in ({"draft": False}, {"assets": []}, {"assets": value["assets"] * 2}, {"target_commitish": "main"}):
                    original = copy.deepcopy(value)
                    value.update(changed)
                    with self.assertRaises(ValueError): reverse.resolve("42", root / "other")
                    value = original
                self.assertEqual(1, store.call_count)
                with patch.dict(os.environ, GITHUB_REF_PROTECTED="false"), self.assertRaises(ValueError):
                    reverse.resolve("42", root / "other")

    def provenance(self):
        return dict(invocation=dict(releaseId=42), publication=dict(registryOrigin="https://registry.test", ownerPrincipalId="a" * 32),
                    artifacts=[dict(packageId=package_id, version="2.0.0",
                                    packageContentIdentity=f"{i:064x}", path=f"fixture{i}.sunderpkg")
                               for i, package_id in enumerate(pipeline.r.FAMILY_PACKAGE_IDS)])

    def test_remote_annotated_tag_must_still_select_the_artifact_source(self):
        from unittest.mock import Mock
        store = Mock(tag="agent/v2.0.0")
        store.api.side_effect = [dict(object=dict(type="tag", sha="b" * 40)), dict(object=dict(type="commit", sha="a" * 40))]
        pipeline.remote_tag(store, "a" * 40)
        store.api.side_effect = [dict(object=dict(type="commit", sha="c" * 40))]
        with self.assertRaises(ValueError): pipeline.remote_tag(store, "a" * 40)

    def test_public_postchecks_require_all_fifteen_exact_identities_and_current_rid_roles(self):
        provenance = self.provenance()
        rids = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
        manifest = dict(version="2.0.0", targets=[dict(role=role, rid=rid, kind="worker" if role == "runtime" else "avalonia",
            targetFramework="net10.0", avaloniaVersion=None if role == "runtime" else "12.1.4",
            requiredHostCapabilities=["core.v1", "worker-protocol.v1" if role == "runtime" else "app-bridge.v1"])
            for rid in rids for role in ("runtime", "app")])
        calls = []
        mutation = None
        def http(registry, route, payload):
            request = json.loads(payload); calls.append(request)
            self.assertEqual("/api/v1/packages/resolve-package-changes", route)
            self.assertEqual(15, len(request["packages"]))
            rid = request["compatibility"]["rid"]
            self.assertEqual({"profiles", "rid"}, set(request["compatibility"]))
            app, worker = request["compatibility"]["profiles"]
            self.assertEqual(("app", "avalonia", [1], ["net10.0"], "12.1.4"),
                (app["role"], app["kind"], app["sdkMajors"], app["targetFrameworks"], app["avaloniaVersion"]))
            self.assertEqual(["app-bridge.v1", "core.v1"], app["hostCapabilities"])
            self.assertEqual(["core.v1", "worker-protocol.v1"], worker["hostCapabilities"])
            self.assertEqual([], worker["targetFrameworks"])
            items = [{**{key: item[key] for key in ("packageId", "version", "packageContentIdentity")},
                      "targets": [dict(role=role, rid=rid) for role in ("runtime", "app")],
                      "artifacts": [dict(kind=role, rid=rid) for role in ("runtime", "app")] + [dict(kind="shared", rid=None)]} for item in provenance["artifacts"]]
            if mutation == "identity": items[0]["packageContentIdentity"] = "f" * 64
            if mutation == "missing": items.pop()
            if mutation == "rid": items[0]["artifacts"][0]["rid"] = "foreign-rid"
            if mutation == "role": items[0]["targets"].pop()
            return 200, json.dumps(dict(success=True, errors=[], conflicts=[], items=items)).encode(), False
        with patch.object(pipeline.r, "archive_metadata", return_value=(manifest, {}, "")), patch.object(pipeline.cohort, "http", side_effect=http):
            pipeline.install_plans("/candidate", provenance)
            self.assertEqual(list(rids), [item["compatibility"]["rid"] for item in calls])
            for mutation in ("identity", "missing", "rid", "role"):
                with self.subTest(mutation=mutation), self.assertRaises(ValueError): pipeline.install_plans("/candidate", provenance)


if __name__ == "__main__": unittest.main()
