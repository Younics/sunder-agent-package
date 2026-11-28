"""Exercise the approval and per-package job/artifact joins without publishing."""
import copy
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/release"))
import agent_release as r
import agent_release_wave as wave
import agent_selection as selection


class ReleaseWaveTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.context = Path(temporary.name)
        self.source = "a" * 40
        self.hub = "https://hub.sunderapp.io"
        self.owner = selection.OWNER
        self.extension = "younics.ai.agent.tools.web"
        self.env = dict(GITHUB_RUN_ID="12", GITHUB_REPOSITORY_ID="34", GITHUB_SHA=self.source,
                        REGISTRY_API_URL=self.hub, PACKAGE=self.owner, GITHUB_ENV=str(self.context / "env"),
                        GITHUB_OUTPUT=str(self.context / "outputs"), GITHUB_STEP_SUMMARY=str(self.context / "summary"))
        environment = patch.dict(os.environ, self.env)
        environment.start()
        self.addCleanup(environment.stop)
        self.rows = r.inventory(ROOT)["packages"]
        self.scope = selection.release_scope(self.rows, [])

    def test_dispatch_displays_exact_scope_before_review_and_skips_completed_versions(self):
        published = [dict(tag_name=row["key"] + "/v" + row["version"], draft=False, prerelease=False) for row in self.rows]
        earlier = "d" * 40
        mixed = [dict(item, draft=True, target_commitish=earlier) if row["packageId"] == self.extension else item
                 for item, row in zip(published, self.rows) if row["packageId"] != self.owner]
        for remote, expected in ((published, {}), (mixed, {self.owner: self.source, self.extension: earlier})):
            with self.subTest(remote=len(remote)), patch.object(selection, "releases", return_value=remote), \
                 patch.object(sys, "argv", ["agent_selection", "dispatch", "--plan", str(self.context / "cutover")]):
                selection.main()
                outputs = dict(line.split("=", 1) for line in (self.context / "outputs").read_text().splitlines())
                plan = r.decode(outputs["plan"].encode())
                self.assertEqual(r.sha(r.canonical(plan)), outputs["plan_sha"])
                self.assertEqual(expected, {p: item["source"] for p, item in plan["packages"].items()})
                summary = (self.context / "summary").read_text()
                for value in (self.hub, self.source, *expected.values()):
                    self.assertIn(value, summary)
                for package, item in plan["packages"].items():
                    self.assertIn(f"`{package}@{item['version']}`", summary)
                self.assertEqual(not expected, "No approval" in summary)
                self.assertEqual(str(bool(expected)).lower(), outputs["owner"])
                self.assertEqual(sorted(set(expected) - {self.owner}), r.decode(outputs["extensions"].encode()))
                (self.context / "summary").unlink()
                (self.context / "outputs").unlink()

    def test_handoff_cannot_change_reviewed_run_hub_source_subject_or_version(self):
        with patch.object(selection, "registry_version", return_value=None), patch.object(selection.inputs, "gh_json", return_value=[]):
            plan, _ = selection.package_plan(ROOT, self.source, self.hub, self.owner, [])
        path = self.context / selection.PLAN
        (self.context / "staged.sha256").write_text("")
        path.write_bytes(r.canonical(plan))
        os.environ["SUNDER_AGENT_APPROVED_PLAN"] = r.canonical(self.scope).decode()
        wave.bind_handoff(self.context)
        self.assertIn("PLAN_SHA=" + r.file_sha(path), (self.context / "env").read_text())
        for values in (dict(GITHUB_RUN_ID="21"), dict(GITHUB_SHA="b" * 40),
                       dict(REGISTRY_API_URL="https://elsewhere.test"), dict(PACKAGE=self.extension)):
            with self.subTest(values=values), patch.dict(os.environ, values), self.assertRaises(r.Invalid):
                wave.bind_handoff(self.context)
        for entry in (None, dict(version="99.0.0", source=self.source), dict(version=self.scope["packages"][self.owner]["version"], source="b"*40)):
            wrong = copy.deepcopy(self.scope)
            if entry is None:
                del wrong["packages"][self.owner]
            else:
                wrong["packages"][self.owner] = entry
            with patch.dict(os.environ, SUNDER_AGENT_APPROVED_PLAN=r.canonical(wrong).decode()), self.assertRaises(r.Invalid):
                wave.bind_handoff(self.context)
        producer = "b" * 40
        plan["artifactSourceCommit"] = producer
        path.write_bytes(r.canonical(plan))
        self.scope["packages"][self.owner]["source"] = producer
        with patch.dict(os.environ, SUNDER_AGENT_APPROVED_PLAN=r.canonical(self.scope).decode()):
            with self.assertRaises(r.Invalid):
                wave.bind_handoff(self.context)
            (self.context / "staged.sha256").write_text("c" * 64)
            wave.bind_handoff(self.context)
            self.assertIn("original=" + producer, (self.context / "outputs").read_text())

    def test_full_rerun_can_only_finish_the_original_successfully_reviewed_plan(self):
        original = self.scope
        remaining = dict(original, packages={self.owner: original["packages"][self.owner]})
        job = dict(name="Approve release plan · " + r.sha(r.canonical(original)), run_id=12, run_attempt=1,
                   head_sha=self.source, status="completed", conclusion="success", started_at="10", completed_at="20")
        artifact = dict(id=5, expired=False, name=wave.APPROVAL, size_in_bytes=1234, digest="sha256:"+"a"*64,
                        created_at="15", workflow_run=dict(id=12, head_sha=self.source, head_branch="main",
                                                          repository_id=34, head_repository_id=34))
        def download(_, path):
            with ZipFile(path, "w") as archive:
                archive.writestr("review.json", r.canonical(dict(plan=original, attempt=1)))
        with patch.object(wave.hosted, "entries", return_value=[artifact]), patch.object(wave.hosted, "download", side_effect=download), \
             patch.object(wave.hosted, "attempt_jobs", return_value=[job]):
            self.assertTrue(wave.previous_approval(remaining, 3))
            for expanded in (dict(remaining, registryOrigin="https://another.test"),
                             dict(remaining, packages={"other": original["packages"][self.owner]}),
                             dict(remaining, packages={self.owner: dict(original["packages"][self.owner], source="c" * 40)})):
                with self.assertRaisesRegex(r.Invalid, "outside the original approval"):
                    wave.previous_approval(expanded, 3)
            for change in (dict(conclusion="failure"), dict(run_attempt=2), dict(name="some other job"), dict(completed_at="12")):
                with patch.object(wave.hosted, "attempt_jobs", return_value=[dict(job, **change)]), self.assertRaises(ValueError):
                    wave.previous_approval(remaining, 3)
        with patch.object(wave.hosted, "entries", return_value=[]):
            self.assertFalse(wave.previous_approval(remaining, 2))

    def test_routes_only_successful_uploads_and_complete_per_package_six_rid_sets(self):
        other = "younics.ai.agent.provider.openai"
        packages = [self.extension, other]
        jobs, artifacts = [], []
        def success(name, prefix):
            jobs.append(dict(name=name, status="completed", conclusion="success", started_at="10", completed_at="20"))
            artifacts.append(dict(name=prefix+"-1", id=len(artifacts)+1, expired=False, created_at="15"))
            return artifacts[-1]["id"]
        uploads = {}
        for p in packages:
            uploads[p] = success(p + " / Upload the GitHub release archive to Hub", "hub-ready-" + p)
        jobs[1]["conclusion"] = "failure"
        ready, errors = wave.eligible_packages("uploads", "extensions", dict.fromkeys(packages), jobs, artifacts, 2)
        self.assertEqual([dict(package=self.extension, handoff=uploads[self.extension])], ready)
        self.assertEqual(1, len(errors))
        receipts = {}
        for p in packages:
            receipts[p] = [success(f"Accept extensions / Install from Hub · {p} · {rid}", f"installed-{p}-{rid}") for rid in wave.RIDS]
        self.assertEqual({"win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"}, set(wave.RIDS))
        first = dict(package=self.extension, handoff=uploads[self.extension], receipts=receipts[self.extension])
        second = dict(package=other, handoff=uploads[other], receipts=receipts[other])
        self.assertEqual(([first, second], []), wave.eligible_packages("installs", "extensions", uploads, jobs, artifacts, 2))
        # A failed current RID must not be credited with its older successful artifact.
        for result, available in (([*jobs[:-1], dict(jobs[-1], conclusion="failure")], artifacts),
                                  (jobs[:-1], artifacts), (jobs, artifacts[:-1]),
                                  (jobs, [*artifacts[:-1], dict(artifacts[-1], expired=True)]),
                                  (jobs, [*artifacts[:-1], dict(artifacts[-1], created_at="05")]),
                                  (jobs, [*artifacts, dict(artifacts[-1], id=999)])):
            ready, errors = wave.eligible_packages("installs", "extensions", uploads, result, available, 2)
            self.assertEqual([first], ready)
            self.assertEqual(1, len(errors))
            self.assertIn(other, errors[0])

    def test_workflow_gates_only_nonempty_plans_and_orders_owner_upload_install_and_promotion(self):
        workflow = (ROOT / ".github/workflows/sunder-package-release.yml").read_text()
        select, review = workflow.split("\n  review:\n", 1)
        self.assertNotIn("\n    environment:", select)
        self.assertIn("steps.work.outputs.plan", select)
        self.assertEqual(1, workflow.count("\n    environment:"))
        self.assertIn("needs.select.outputs.owner == 'true' || needs.select.outputs.extensions != '[]'", review)
        self.assertIn("needs.approve.result == 'success'", workflow.split("\n  owner:\n")[1].split("\n  owner-accept:")[0])
        self.assertIn("needs: [select, approve, owner-accept]", workflow)
        self.assertIn("needs: [select, approve, extensions]", workflow)
        self.assertIn("needs.extensions.result == 'failure'", workflow)
        self.assertEqual(4, workflow.count("approval: ${{ needs.approve.outputs.plan }}"))

    def test_owner_wave_uses_this_attempts_success_and_only_this_runs_artifact_ids(self):
        workflows = ROOT / ".github/workflows"
        caller = (workflows / "sunder-package-release.yml").read_text()
        ship = (workflows / "sunder-package-ship.yml").read_text()
        def job_name(workflow, job):
            return workflow.split(f"\n  {job}:\n    name: ", 1)[1].splitlines()[0]
        job = dict(name=job_name(caller, "owner") + " / " + job_name(ship, "publish"),
                   run_id=12, run_attempt=2, head_sha=self.source, status="completed", conclusion="success",
                   started_at="2026-11-27T10:15:00Z", completed_at="2026-11-27T10:16:00Z")
        origin = dict(id=12, head_sha=self.source)
        artifact = dict(id=123, workflow_run=origin, name=f"hub-ready-{self.owner}-1",
                        created_at="2026-11-27T10:15:55Z", expired=False)
        # GitHub credits a carried job to attempt 2 but keeps its original execution times.
        with patch.dict(os.environ, WAVE="owner", GITHUB_RUN_ATTEMPT="2", SUNDER_AGENT_APPROVED_PLAN=r.canonical(self.scope).decode()), \
             patch.object(wave.hosted, "attempt_jobs", return_value=[dict(job, run_attempt=1), job]), \
             patch.object(wave.hosted, "entries", return_value=[artifact, dict(artifact, id=124, workflow_run=dict(origin, id=13)),
                                                                dict(artifact, id=125, workflow_run=dict(origin, head_sha="b" * 40))]):
            wave.route("uploads")
        outputs = dict(line.split("=", 1) for line in (self.context / "outputs").read_text().splitlines())
        self.assertEqual("false", outputs["incomplete"])
        self.assertEqual([dict(package=self.owner, handoff=123)], r.decode(outputs["packages"].encode()))


if __name__ == "__main__":
    unittest.main()
