"""Selective publication boundaries; no live mutation and no simulated native attestation."""
import copy
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_pipeline as pipeline
import agent_release as r
import agent_selection as selection


class MemoryRelease:
    def __init__(self, row, identifier):
        self.tag = row["tag"]
        self.value = dict(id=identifier, target_commitish="a" * 40, draft=True, prerelease=not r.version(row["version"])[1], assets=[])
        self.data = {}
        self.calls = []

    def api(self, suffix, method="GET", value=None):
        self.calls.append((suffix, method, value))
        if suffix == "git/refs": return {}
        if suffix.startswith("git/ref/"): return dict(object=dict(type="commit", sha="a" * 40))
        if method == "PATCH":
            self.value.update(value)
            raise subprocess.TimeoutExpired("lost announcement acknowledgement", 1)
        return copy.deepcopy(self.value)

    def create(self, source, _):
        assert source == self.value["target_commitish"]
        return copy.deepcopy(self.value)

    def upload(self, identifier, paths):
        assert identifier == self.value["id"] and self.value["draft"]
        for path in paths:
            name, data = path.name, path.read_bytes()
            if name in self.data:
                assert self.data[name] == data
            else:
                self.data[name] = data
                self.value["assets"].append(dict(name=name, id=1000 + len(self.data), size=len(data), digest="sha256:" + r.sha(data)))

    def download(self, asset, destination):
        pipeline.persist(destination, self.data[asset["name"]])

    def get(self, identifier):
        assert identifier == self.value["id"]
        return copy.deepcopy(self.value)

    def resolve(self): return self.get(self.value["id"])

    def verify(self, identifier, files, draft=None):
        assert set(files) == set(self.data)
        assert all(path.read_bytes() == self.data[name] for name, path in files.items())
        if draft is not None: assert self.value["draft"] is draft
        return self.get(identifier)


class SelectivePipelineTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory()
        self.addCleanup(scope.cleanup)
        self.directory = Path(scope.name)
        self.core = Path("/fixture/core")
        rows = r.inventory(Path(__file__).resolve().parents[3])["packages"]
        self.plan = dict(schemaVersion=2, repository=selection.REPOSITORY, artifactSourceCommit="a" * 40,
            lockSetSha256="c" * 64, registryOrigin="https://registry.test", packages=[])
        self.records = []
        for index, row in enumerate(sorted(rows, key=lambda row: row["packageId"])):
            version = "2.1.0-rc.1" if index == 2 else "2.0.0"
            item = {key: row[key] for key in ("key", "packageId", "projectPath")} | dict(version=version,
                tag=row["key"] + "/v" + version, selected=index in (1, 2), retainedArchive=None, retainedEvidence=None)
            self.plan["packages"].append(item)
            name = selection.archive_name(item)
            (self.directory / name).write_bytes(name.encode())
            self.records.append(dict(packageId=row["packageId"], version=version, path=name, size=len(name), sha256=r.sha(name.encode()),
                packageContentIdentity=f"{index + 1:064x}", publicInventorySha256="e" * 64))
        for name in ("native-test-evidence.json", "package-output-audit.json"):
            (self.directory / name).write_text('{}')
        (self.directory / selection.PLAN).write_bytes(r.canonical(self.plan) + b"\n")
        self.targets = [(row, MemoryRelease(row, 42 + index)) for index, row in enumerate(selection.selected(self.plan))]
        self.env = dict(GITHUB_SHA="a" * 40, CORE_COMMIT="b" * 40, GITHUB_REPOSITORY=selection.REPOSITORY,
            GITHUB_EVENT_NAME="push", GITHUB_REF="refs/heads/main", GITHUB_REF_NAME="main", GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="1",
            GITHUB_REPOSITORY_ID="777", GITHUB_WORKFLOW_SHA="a" * 40, GITHUB_REF_PROTECTED="true",
            GITHUB_WORKFLOW_REF=f"{selection.REPOSITORY}/{pipeline.publication.WORKFLOW}@refs/heads/main",
            CORE_TAG="nuget-sunder-package-build/v1.1.0", CORE_BUILD_VERSION="1.1.0",
            REGISTRY_API_URL="https://registry.test")
        self.publications = [r.publication(record, f"{index + 1:032x}") for index, record in enumerate(self.records)]

    def stage(self):
        return pipeline.stage(self.core, self.directory, self.plan)

    def publisher(self, calls, *, retained=False, fail_at=None):
        def run(command, **_):
            calls.append(command)
            index = next(index for index, record in enumerate(self.records) if record["packageId"] == command[-2])
            expected = r.load(command[command.index("--initial-yanked") + 1])
            self.assertEqual(self.publications[index] if retained else r.publication(self.records[index]), expected)
            if index == fail_at: raise subprocess.CalledProcessError(1, command)
            output = Path(command[command.index("--publication-output") + 1])
            output.write_bytes(r.canonical(self.publications[index]) + b"\n")
        return run

    def mocks(self):
        from contextlib import ExitStack
        stack = ExitStack()
        stack.enter_context(patch.dict(os.environ, self.env))
        stack.enter_context(patch.object(pipeline, "verify_primary", return_value=dict(artifacts=self.records)))
        stack.enter_context(patch.object(pipeline, "stores", return_value=self.targets))
        stack.enter_context(patch.object(pipeline.bash_runtime, "executable", return_value="/fixture/bash"))
        stack.enter_context(patch.object(pipeline.subprocess, "check_output", return_value=b"retained .NET toolchain\n"))
        return stack

    def test_only_selected_packages_get_tags_releases_and_their_tested_archives(self):
        with self.mocks():
            self.stage()
            old = [copy.deepcopy(store.data) for _, store in self.targets]
            self.stage()
            self.assertEqual(old, [store.data for _, store in self.targets])
            pipeline.verified(self.core, self.directory, self.plan)
        selected_names = {selection.archive_name(row) for row in selection.selected(self.plan)}
        first, second = [store for _, store in self.targets]
        self.assertEqual(selection.PLAN, first.value["assets"][0]["name"])
        self.assertEqual(selection.PLAN, second.value["assets"][0]["name"])
        self.assertEqual(selected_names, {name for name in first.data if name.endswith(".sunderpkg")})
        self.assertEqual({selection.archive_name(self.targets[1][0])}, {name for name in second.data if name.endswith(".sunderpkg")})
        self.assertEqual(4, len([call for store in (first, second) for call in store.calls if call[0] == "git/refs" and call[1] == "POST"]))

    def test_failed_native_or_lock_gate_precedes_every_github_mutation(self):
        with patch.object(pipeline, "verify_primary", side_effect=ValueError("unqualified native inputs")), \
             patch.object(pipeline, "stores") as stores, self.assertRaises(ValueError):
            self.stage()
        stores.assert_not_called()

    def test_selected_registry_uploads_leave_thirteen_unchanged_packages_read_only(self):
        with self.mocks():
            self.stage()
            value = r.load(self.directory / "release-provenance.json")
            calls = []
            with patch.object(pipeline.r, "verify_family", return_value=dict(order=[item["packageId"] for item in self.records])), \
                 patch.object(pipeline.subprocess, "run", side_effect=self.publisher(calls)):
                pipeline.publish_versions(self.core, self.directory, self.plan)
            self.assertEqual(15, len(calls))
            writes = [call for call in calls if "--verify-only" not in call]
            self.assertEqual(2, len(writes))
            self.assertEqual({row["packageId"] for row in selection.selected(self.plan)}, {call[-2] for call in writes})
            self.assertTrue(all("Listed" in call for call in calls if "--verify-only" in call))
            self.assertEqual({"registryOrigin": "https://registry.test"}, value["publication"])
            first, second = [store for _, store in self.targets]
            self.assertEqual(self.publications, r.decode(first.data[r.PUBLICATIONS]))
            self.assertNotIn(r.PUBLICATIONS, second.data)
            # A fresh runner recovers the actual per-package owners from GitHub.
            (self.directory / r.PUBLICATIONS).unlink()
            with patch.object(pipeline, "source_guard"), patch.object(selection, "retain"):
                pipeline.recover(self.core, self.directory, self.plan)
            before = (self.directory / r.PUBLICATIONS).read_bytes()
            calls.clear()
            with patch.object(pipeline.r, "verify_family", return_value=dict(order=[item["packageId"] for item in self.records])), \
                 patch.object(pipeline.subprocess, "run", side_effect=self.publisher(calls, retained=True)):
                pipeline.registry_versions(self.directory, self.plan, value, "Listed")
            self.assertTrue(all("--verify-only" in call for call in calls))
            self.assertEqual(before, (self.directory / r.PUBLICATIONS).read_bytes())

    def test_partial_upload_retry_captures_only_a_complete_verified_publication_batch(self):
        with self.mocks():
            self.stage()
            with patch.object(pipeline.r, "verify_family", return_value=dict(order=[item["packageId"] for item in self.records])), \
                 patch.object(pipeline.subprocess, "run", side_effect=self.publisher([], fail_at=2)), self.assertRaises(subprocess.CalledProcessError):
                pipeline.publish_versions(self.core, self.directory, self.plan)
            self.assertFalse((self.directory / r.PUBLICATIONS).exists())
            self.assertNotIn(r.PUBLICATIONS, self.targets[0][1].data)
            with patch.object(pipeline.r, "verify_family", return_value=dict(order=[item["packageId"] for item in self.records])), \
                 patch.object(pipeline.subprocess, "run", side_effect=self.publisher([])):
                pipeline.publish_versions(self.core, self.directory, self.plan)
            self.assertEqual(self.publications, r.decode(self.targets[0][1].data[r.PUBLICATIONS]))

    def test_visibility_requires_retained_publications_before_any_registry_mutation(self):
        with self.mocks():
            self.stage()
            with patch.object(pipeline.cohort, "http") as http, patch.object(pipeline.cohort, "apply") as apply, \
                 self.assertRaisesRegex(ValueError, "Missing retained Registry publication"):
                pipeline.visibility(self.core, self.directory, self.plan)
            http.assert_not_called(); apply.assert_not_called()

    def test_cohort_is_only_the_selected_mixed_version_batch_with_per_package_channels(self):
        value = dict(invocation=dict(releaseId=42), artifacts=self.records)
        proposed = pipeline.cohort_request(self.plan, value, 8)
        self.assertEqual(2, len(proposed["versions"]))
        self.assertEqual(["latest", "preview"], [item["tag"] for item in proposed["distTags"]])
        self.assertEqual(proposed, pipeline.cohort_request(self.plan, {**value, "artifacts": list(reversed(self.records))}, 8))

    def test_supported_major_publication_keeps_newer_channels_and_replays_original_tag_subset(self):
        value = dict(invocation=dict(releaseId=42), artifacts=self.records)
        proposed = pipeline.cohort_request(self.plan, value, 8)
        observed = [dict(packageId=tag["packageId"], tags=[dict(tag, target=dict(tag["target"], version="3.0.0"))])
                    for tag in proposed["distTags"]]
        tags = pipeline.advancing_tags(proposed, observed)
        self.assertEqual([], tags)
        retained = pipeline.cohort_request(self.plan, value, 8, tags)
        self.assertEqual(proposed["versions"], retained["versions"])
        self.assertEqual([], retained["distTags"])
        self.assertEqual(retained, pipeline.cohort_request(self.plan, value, 8, retained["distTags"]))
        for snapshot in observed: snapshot["tags"][0]["target"]["version"] = "1.0.0"
        self.assertEqual(proposed["distTags"], pipeline.advancing_tags(proposed, observed))
        with self.assertRaisesRegex(ValueError, "foreign channel"):
            pipeline.cohort_request(self.plan, value, 8, [dict(proposed["distTags"][0], tag="unreviewed")])

    def test_main_and_manual_require_protected_exact_clean_source_without_manual_approvals(self):
        def git(root, *args): return (("b" if root == self.core else "a") * 40).encode() if args[0] == "rev-parse" else b""
        with patch.dict(os.environ, self.env, clear=True), patch.object(pipeline.r, "git", side_effect=git), \
             patch.object(pipeline.r, "locks", return_value="c" * 64), patch.object(pipeline.selection, "validate"):
            pipeline.source_guard(self.core, self.plan)
            with patch.dict(os.environ, GITHUB_EVENT_NAME="workflow_dispatch"): pipeline.source_guard(self.core, self.plan)
            for key, value in (("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_REF", "refs/tags/agent/v2.0.0"),
                ("GITHUB_REF_PROTECTED", "false"), ("GITHUB_WORKFLOW_SHA", "f" * 40)):
                with self.subTest(key=key), patch.dict(os.environ, {key: value}), self.assertRaises(ValueError):
                    pipeline.source_guard(self.core, self.plan)

    def test_mutation_guard_accepts_retained_channel_subsets_but_binds_every_selected_version(self):
        value = dict(invocation=dict(releaseId=42), artifacts=self.records)
        proposed = pipeline.cohort_request(self.plan, value, 8)
        path = self.directory / pipeline.REQUEST
        with patch.object(sys, "argv", ["agent_pipeline.py", "guard", "--core", str(self.directory), "--directory", str(self.directory)]), \
             patch.object(selection, "load", return_value=self.plan), \
             patch.object(pipeline, "verified", return_value=(self.targets, [dict(draft=True)], value)):
            for tags in ([], proposed["distTags"][:1], proposed["distTags"]):
                retained = pipeline.cohort_request(self.plan, value, 8, tags)
                path.write_bytes(r.canonical(retained))
                pipeline.main()
            for mutate in (lambda v: v["versions"].pop(),
                           lambda v: v["versions"][0]["package"].update(packageContentIdentity="f" * 64),
                           lambda v: v["distTags"][0].update(tag="unreviewed"),
                           lambda v: v.update(idempotencyKey="another-release")):
                changed = copy.deepcopy(proposed)
                mutate(changed)
                path.write_bytes(r.canonical(changed))
                with self.assertRaises(ValueError): pipeline.main()

    def test_recovery_reuses_original_invocation_and_never_rebuilds_after_missing_bytes(self):
        with self.mocks():
            self.stage()
            first = self.targets[0][1]
            first.value["assets"] = [asset for asset in first.value["assets"] if not asset["name"].endswith(".sunderpkg")]
            with patch.object(pipeline, "source_guard"), patch.object(selection, "retain"), \
                 patch.object(pipeline.subprocess, "run") as run, self.assertRaisesRegex(ValueError, "never rebuild"):
                pipeline.recover(self.core, self.directory, self.plan)
            run.assert_not_called()
            with patch.dict(os.environ, GITHUB_RUN_ID="456", GITHUB_RUN_ATTEMPT="1"):
                self.assertEqual("123", pipeline.verify_provenance(self.directory, self.plan, 42, dict(artifacts=self.records))["invocation"]["runId"])
            changed = copy.deepcopy(self.records); changed[0]["sha256"] = "f" * 64
            with self.assertRaises(ValueError): pipeline.verify_provenance(self.directory, self.plan, 42, dict(artifacts=changed))

    def test_finalization_reconciles_lost_patch_replies_and_announces_coordinator_last(self):
        with self.mocks():
            self.stage()
            events = []
            for _, store in self.targets:
                original = store.api
                def api(*args, _original=original, _tag=store.tag):
                    if len(args) > 1 and args[1] == "PATCH": events.append(_tag)
                    return _original(*args)
                store.api = api
            with patch.object(pipeline, "visibility"), patch.object(pipeline, "source_guard"), \
                  patch.object(pipeline.publication, "install_plans") as plans:
                pipeline.finalize(self.core, self.directory, self.plan)
                plans.assert_called_once()
            self.assertEqual([self.targets[1][0]["tag"], self.targets[0][0]["tag"]], events)
            self.assertTrue(all(not store.value["draft"] for _, store in self.targets))

    def test_announced_retry_confirms_the_original_selected_request_without_a_new_visibility_mutation(self):
        with self.mocks():
            self.stage()
            value = r.load(self.directory / "release-provenance.json")
            proposed = pipeline.cohort_request(self.plan, value, 8)
            result = dict(idempotencyKey=proposed["idempotencyKey"], requestSha256=pipeline.cohort.request_hash(proposed),
                packagePolicy=dict(revision=9, fingerprint="f" * 64), committedAtUtc="2026-11-03T01:02:03Z",
                versions=proposed["versions"], previousTags=[dict(packageId=change["package"]["packageId"], tags=[]) for change in proposed["versions"]],
                resultingTags=[dict(packageId=change["package"]["packageId"], tags=[tag]) for change, tag in zip(proposed["versions"], proposed["distTags"])])
            receipt = dict(schemaVersion=2, registryOrigin=self.plan["registryOrigin"], artifactSourceCommit="a" * 40,
                releaseId=42, request=proposed, result=result)
            pipeline.receipts.check(receipt)
            request_path = self.directory / pipeline.REQUEST
            receipt_path = self.directory / ("agent-family-cohort-" + pipeline.cohort.request_hash(proposed) + ".json")
            pipeline.cohort.store(request_path, proposed); pipeline.cohort.store(receipt_path, receipt)
            pipeline.persist(self.directory / r.PUBLICATIONS, r.canonical(self.publications) + b"\n")
            self.targets[0][1].upload(42, [request_path, receipt_path, self.directory / r.PUBLICATIONS])
            for _, store in self.targets: store.value["draft"] = False
            with patch.object(pipeline.receipts, "download_bytes", return_value=receipt), \
                 patch.object(pipeline.cohort, "confirm") as confirm, patch.object(pipeline.cohort, "apply") as apply, \
                 patch.object(pipeline, "registry_versions"):
                pipeline.visibility(self.core, self.directory, self.plan)
            confirm.assert_called_once()
            apply.assert_not_called()


if __name__ == "__main__": unittest.main()
