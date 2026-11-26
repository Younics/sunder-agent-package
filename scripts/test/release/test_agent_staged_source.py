"""Single-package recovery only: compare original data, never create a replacement release."""
import copy
import os
from pathlib import Path
from types import SimpleNamespace
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/release"))
import agent_pipeline as pipeline
import agent_release as r
import agent_staged_source as staged
import package_release


class StagedSourceTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.directory = Path(temp.name).resolve()
        self.producer, self.controller = "a" * 40, "b" * 40
        package = "younics.ai.agent"
        self.plan = dict(schemaVersion=3, artifactSourceCommit=self.producer, releasePackage=package,
                         packages=[dict(packageId=package, version="2.0.0", tag="agent/v2.0.0", selected=True)])
        self.info = dict(id=12, tag_name="agent/v2.0.0", target_commitish=self.producer, assets=[
            dict(name=name, id=index+1, size=len(name), state="uploaded", digest="sha256:" + r.sha(name.encode()))
            for index, name in enumerate(sorted(staged.primary(self.plan)))])

    def test_binding_pins_original_asset_ids_bytes_and_release_but_allows_later_receipts(self):
        before = staged.fingerprint(self.info, self.plan, self.controller)
        self.info["assets"].append(dict(name=r.PUBLICATIONS))
        self.assertEqual(before, staged.fingerprint(self.info, self.plan, self.controller))
        for mutate in (lambda i: i.update(id=77), lambda i: i["assets"][0].update(id=99),
                       lambda i: i["assets"][0].update(digest="sha256:"+"c"*64)):
            info = copy.deepcopy(self.info); mutate(info)
            with patch.dict(os.environ, GITHUB_SHA=self.controller, SUNDER_STAGED_INPUTS_SHA256=before), self.assertRaises(r.Invalid):
                pipeline.require_staged_binding(info, self.plan)
        self.info["assets"].pop(0)
        with self.assertRaises(r.Invalid):
            staged.fingerprint(self.info, self.plan, self.controller)

    def test_original_qualified_bytes_must_match_every_tested_asset(self):
        names = staged.primary(self.plan) - {"release-provenance.json", "toolchain.txt", "SHA256SUMS", "release-sbom.spdx.json"}
        archive = self.directory/"qualified.zip"
        for changes in ({}, {"native-test-evidence.json": b"different"}, {"release-plan.json": None}, {"extra": b"x"}):
            with ZipFile(archive, "w") as z:
                for n, data in ({n: n.encode() for n in names} | changes).items():
                    if data is not None: z.writestr(n, data)
            if not changes: staged.compare_qualified(archive, self.info, self.plan)
            else:
                with self.assertRaises(r.Invalid): staged.compare_qualified(archive, self.info, self.plan)

    def test_only_a_complete_draft_can_cross_a_controller_and_it_cannot_restage(self):
        with patch.dict(os.environ, GITHUB_SHA=self.controller), patch.object(pipeline, "source_guard"), \
             patch.object(pipeline.selection, "retain") as retain, patch.object(pipeline, "stores") as stores:
            with self.assertRaisesRegex(r.Invalid, "complete original draft"):
                pipeline.recover(None, self.directory, self.plan, resume_staging=True)
            with self.assertRaisesRegex(r.Invalid, "original source can stage"):
                pipeline.stage(None, self.directory, self.plan, family={})
            retain.assert_not_called(); stores.assert_not_called()

    def test_recovery_checks_original_checkout_and_all_non_controller_inputs(self):
        current = self.directory/"control"; current.mkdir()
        original = self.directory/"sunder-agent-producer"
        def git(root,*cmd):return subprocess.check_output(["git","-C",str(root),*cmd],text=True,stderr=subprocess.DEVNULL).strip()
        git(current,"init","-q");git(current,"config","user.name","Fixture");git(current,"config","user.email","fixture@example.invalid")
        (current/"worker").write_text("original");git(current,"add",".");git(current,"commit","-qm","producer")
        self.plan["artifactSourceCommit"] = git(current,"rev-parse","HEAD")
        git(current,"worktree","add","-q","--detach",str(original),"HEAD")
        (current/"docs").mkdir();(current/"docs/RELEASES.md").write_text("controller change")
        git(current,"add",".");git(current,"commit","-qm","controller")
        with patch.dict(os.environ, GITHUB_SHA=git(current,"rev-parse","HEAD"), SUNDER_STAGED_INPUTS_SHA256="b"*64):
            self.assertEqual(original,staged.checkout(current,self.plan))
            (original/"worker").write_text("dirty")
            with self.assertRaises(r.Invalid):staged.checkout(current,self.plan)
            git(original,"restore","worker")
            (current/"worker").write_text("new product");git(current,"add",".");git(current,"commit","-qm","worker")
            with patch.dict(os.environ,GITHUB_SHA=git(current,"rev-parse","HEAD")),self.assertRaises(r.Invalid):
                staged.checkout(current,self.plan)

    def test_recovered_install_uses_controller_after_authenticating_the_producer(self):
        context = self.directory/"context"
        context.mkdir()
        (context/"release-plan.json").write_bytes(b"bound plan")
        (self.directory/"artifacts/release").mkdir(parents=True)
        package = self.plan["releasePackage"]
        args = SimpleNamespace(action="installed", context=context, package=package, rid="linux-arm64",
                               plan_sha256=r.file_sha(context/"release-plan.json"))
        self.plan["registryOrigin"] = "https://registry.example"
        producer = self.directory/"producer"
        with patch.dict(os.environ), patch.object(package_release, "ROOT", self.directory), \
             patch.object(package_release.ci, "head", return_value=self.controller), \
             patch.object(package_release.selection, "load", return_value=self.plan), \
             patch.object(pipeline, "source_guard", return_value=producer) as guard, \
             patch.object(package_release.subprocess, "run") as run, \
             patch.object(staged.native, "export_installed") as export:
            package_release.main(args)
        guard.assert_called_once_with(None, self.plan)
        run.assert_called_once()
        self.assertEqual(self.directory, run.call_args.kwargs["cwd"])
        self.assertEqual([str(self.directory/"scripts/release/run-native-runtime-smoke.sh"),
                          str(self.directory/"artifacts/release"), str(self.directory/"artifacts/installed-metrics/linux-arm64"),
                          "published", package], run.call_args.args[0][-5:])
        self.assertEqual(self.plan["registryOrigin"], run.call_args.kwargs["env"]["SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN"])
        export.assert_called_once()

    def test_publication_credential_check_never_prints_a_value_or_contacts_hub(self):
        script=ROOT/"scripts/release/publish-registry-package.sh"
        import bash_runtime
        for value,expected in (("",1),("wrong-format",1),("sunder_pub_v1_"+"X"*43,0)):
            process=subprocess.run(bash_runtime.command(script,"--check-credential"),capture_output=True,text=True,
                                   env={**os.environ,"SUNDER_REGISTRY_API_KEY":value},timeout=5)
            self.assertEqual(expected,process.returncode)
            if value:self.assertNotIn(value,process.stderr+process.stdout)

    def test_hub_writer_recovers_github_release_bytes_before_publication(self):
        workflows=ROOT/".github/workflows"
        caller=(workflows/"sunder-package-release.yml").read_text()
        callee=(workflows/"sunder-package-ship.yml").read_text()
        for section in (caller.split("\n  owner:\n",1)[1].split("\n  extensions:\n",1)[0],
                        caller.split("\n  extensions:\n",1)[1]):
            self.assertIn("uses: ./.github/workflows/sunder-package-ship.yml",section)
            self.assertIn("    secrets:\n      SUNDER_REGISTRY_API_KEY: ${{ secrets.SUNDER_REGISTRY_API_KEY }}",section)
            self.assertNotIn("secrets: inherit",section)
        self.assertIn("    secrets:\n      SUNDER_REGISTRY_API_KEY: { required: true }",callee.split("permissions:",1)[0])
        stage=callee.split("\n  stage:\n",1)[1].split("\n  publish:\n",1)[0]
        self.assertIn("agent_pipeline.py stage",stage)
        publish=callee.split("\n  publish:\n",1)[1].split("\n  installed:\n",1)[0]
        self.assertIn("needs: [prepare, stage]",publish)
        self.assertLess(publish.index("--check-credential"),publish.index("agent_pipeline.py publish"))
        self.assertLess(publish.index("agent_pipeline.py recover"),publish.index("agent_pipeline.py publish"))
        self.assertNotIn("secrets.SUNDER_REGISTRY_API_KEY",callee.split("\n  publish:\n",1)[0])
        self.assertNotIn("secrets.SUNDER_REGISTRY_API_KEY",callee.split("\n  installed:\n",1)[1].split("\n  release:\n",1)[0])


if __name__ == "__main__": unittest.main()
