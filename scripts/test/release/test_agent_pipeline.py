"""Integrated release boundaries; registry and GitHub mutations are mocked."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_pipeline as pipeline


class AgentPipelineTests(unittest.TestCase):
    def provenance(self):
        return dict(invocation=dict(releaseId=42), publication=dict(registryOrigin="https://registry.test", ownerPrincipalId="a" * 32),
                    artifacts=[dict(packageId=package_id, version="2.0.0",
                                    packageContentIdentity=f"{i:064x}", path=f"fixture{i}.sunderpkg")
                               for i, package_id in enumerate(pipeline.r.FAMILY_PACKAGE_IDS)])

    def test_source_guard_rejects_foreign_workflow_dirty_checkout_and_unapproved_locks(self):
        env = dict(GITHUB_SHA="a" * 40, CORE_COMMIT="b" * 40, GITHUB_REPOSITORY=pipeline.REPOSITORY,
                   GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/agent/v2.0.0", GITHUB_REF_NAME="agent/v2.0.0",
                   GITHUB_WORKFLOW_SHA="a" * 40, GITHUB_REF_PROTECTED="true",
                   GITHUB_WORKFLOW_REF=pipeline.REPOSITORY + "/" + pipeline.WORKFLOW + "@refs/tags/agent/v2.0.0",
                   SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT="a" * 40, SUNDER_AGENT_LOCK_SET_SHA256="c" * 64,
                   SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE="https://review.test/releases/1", CORE_TAG="sdk/v1.1.0", CORE_SDK_VERSION="1.1.0")
        core = Path("/fixture/core")
        def git(root, *args):
            return (("b" if root == core else "a") * 40).encode() if args[0] == "rev-parse" else b""
        with patch.dict(os.environ, env), patch.object(pipeline.r, "git", side_effect=git), \
             patch.object(pipeline.r, "locks", return_value="c" * 64), patch.object(pipeline.r, "inventory", return_value=dict(releaseVersion="2.0.0")):
            self.assertEqual(("a" * 40, "agent/v2.0.0"), pipeline.source_guard(core))
            for key, value in (("GITHUB_EVENT_NAME", "workflow_dispatch"), ("GITHUB_REF_PROTECTED", "false"),
                               ("GITHUB_WORKFLOW_SHA", "d" * 40), ("SUNDER_AGENT_LOCK_SET_SHA256", "d" * 64),
                               ("GITHUB_WORKFLOW_REF", "untrusted/workflow@main")):
                with self.subTest(key=key), patch.dict(os.environ, {key: value}), self.assertRaises(ValueError):
                    pipeline.source_guard(core)
            with patch.object(pipeline.r, "git", side_effect=lambda root, *args: git(root, *args) if args[0] == "rev-parse" else b" M source.cs"), self.assertRaises(ValueError):
                pipeline.source_guard(core)

    def test_remote_annotated_tag_must_still_select_the_artifact_source(self):
        from unittest.mock import Mock
        store = Mock(tag="agent/v2.0.0")
        store.api.side_effect = [dict(object=dict(type="tag", sha="b" * 40)), dict(object=dict(type="commit", sha="a" * 40))]
        pipeline.remote_tag(store, "a" * 40)
        store.api.side_effect = [dict(object=dict(type="commit", sha="c" * 40))]
        with self.assertRaises(ValueError): pipeline.remote_tag(store, "a" * 40)

    def test_visibility_request_is_one_exact_sorted_family_and_retains_its_fence(self):
        provenance = self.provenance()
        request = pipeline.cohort_request(provenance, 8)
        self.assertEqual(15, len(request["versions"]))
        self.assertEqual(15, len(request["distTags"]))
        self.assertEqual("agent-release-42", request["idempotencyKey"])
        self.assertEqual(8, request["expectedPackagePolicyRevision"])
        self.assertTrue(all(item["availability"] == "Listed" and item["expectedAvailability"] == "Yanked" for item in request["versions"]))
        self.assertTrue(all(item["tag"] == "latest" for item in request["distTags"]))
        self.assertEqual(request, pipeline.cohort_request({**provenance, "artifacts": list(reversed(provenance["artifacts"]))}, 8))
        with self.assertRaises(ValueError): pipeline.cohort_request(provenance, 0)

    def test_registry_verification_failure_stops_before_next_package(self):
        provenance = self.provenance()
        publications = [dict(package={key: item[key] for key in ("packageId", "version", "packageContentIdentity")}) for item in provenance["artifacts"]]
        plan = dict(order=[item["package"]["packageId"] for item in publications], publications=publications)
        with patch.object(pipeline.r, "family_plan", return_value=plan), patch.object(pipeline.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "readback")) as run:
            with self.assertRaises(subprocess.CalledProcessError): pipeline.registry_versions(Path("/candidate"), provenance, "Listed")
        self.assertEqual(1, run.call_count)
        self.assertIn("--verify-only", run.call_args.args[0])
        self.assertIn("Listed", run.call_args.args[0])

    def test_published_retry_checks_retained_transition_without_mutating_visibility(self):
        from types import SimpleNamespace
        provenance = self.provenance()
        provenance["artifactSourceCommit"] = "a" * 40
        proposed = pipeline.cohort_request(provenance, 8)
        receipt = dict(request=proposed, artifactSourceCommit="a" * 40, registryOrigin="https://registry.test", result={"retained": True})
        with tempfile.TemporaryDirectory() as directory:
            pipeline.cohort.store(Path(directory) / pipeline.REQUEST, proposed)
            with patch.object(pipeline, "verified", return_value=(SimpleNamespace(tag="agent/v2.0.0"), dict(draft=False), provenance)), \
                 patch.object(pipeline.receipts, "download_bytes", return_value=receipt), \
                 patch.object(pipeline.cohort, "verify_current") as verify, patch.object(pipeline.cohort, "apply") as mutate:
                pipeline.visibility(Path("/core"), directory, 42)
                verify.assert_called_once_with("https://registry.test", proposed, receipt["result"])
                mutate.assert_not_called()
                receipt["artifactSourceCommit"] = "b" * 40
                with self.assertRaises(ValueError): pipeline.visibility(Path("/core"), directory, 42)

    def test_public_postchecks_require_all_fifteen_exact_identities_and_current_rid_roles(self):
        provenance = self.provenance()
        rids = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
        manifest = dict(version="2.0.0", targets=[dict(role=role, rid=rid, requiredHostCapabilities=["core.v1"]) for rid in rids for role in ("runtime", "app")])
        calls = []
        mutation = None
        def http(registry, route, payload):
            request = json.loads(payload); calls.append(request)
            self.assertEqual("/api/v1/packages/resolve-package-changes", route)
            self.assertEqual(15, len(request["packages"]))
            rid = request["compatibility"]["rid"]
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
