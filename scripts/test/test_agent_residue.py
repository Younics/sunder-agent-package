"""Real temporary Git/worktree guards for physical residue cleanup; no builds."""
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).absolute().parents[1] / "dev"))
import agent_residue as cleanup


class AgentResidueTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="agent-residue-guard-")
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name).resolve()
        self.root = self.work / "agent"
        self.root.mkdir()
        cleanup.git(self.root, "init", "--quiet")
        self.write(".gitignore", "bin/\nobj/\nartifacts/\n__pycache__/\n")
        self.write("packages.json", json.dumps({"packages": [
            {"projectPath": f"src/{f}/{f}.csproj"} for f in cleanup.FAMILIES]}))
        for family in cleanup.FAMILIES:
            self.write(f"src/{family}/{family}.csproj", "<Project />\n")
        for app in cleanup.RETAINED_APPS:
            self.write(f"{app}/Retained.cs", "// retained App source\n")
        self.write("compatibility/agent-current-inventory.json", json.dumps({"packages": [
            {"projectPath": f"src/{f}/{f}.csproj"} for f in cleanup.FAMILIES]}))
        self.write(cleanup.FROZEN + "/README.md", "frozen history\n")
        cleanup.git(self.root, "add", ".")
        self.repo = cleanup.Repository(self.root)
        self.addCleanup(self.repo.close)
        self.retired = "src/Sunder.Package.Agent.Skills/App"
        self.app = "src/Sunder.Package.Agent/App"
        self.rid = cleanup.RID_LEAVES[0]

    def write(self, path, value="residue\n"):
        destination = self.root / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(value)
        return destination

    def directory(self, path):
        destination = self.root / path
        destination.mkdir(parents=True, exist_ok=True)
        return destination

    def run_tool(self, *args):
        with patch("sys.stdout", new_callable=io.StringIO), patch("sys.stderr", new_callable=io.StringIO):
            return cleanup.main(["--repo", str(self.root), *map(str, args)])

    def assert_refused(self, path, reason):
        self.repo.refresh_git()
        entry = cleanup.candidate(self.repo, path)
        self.assertEqual("refused", entry["state"], entry)
        self.assertIn(reason, entry["reason"])

    def qualified_rid(self):
        family = cleanup.FAMILIES[0]
        self.write(f"{self.rid}/{family}.dll", "old worker bytes")
        self.write(f"{self.rid}/{family}.deps.json", json.dumps({
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/osx-arm64"},
            "libraries": {"Sunder.Package.Agent.OwnerProtocol/2.0.0": {}}}))
        old = cleanup.RID_BEFORE_NS - 2_000_000_000
        for path in (self.root / self.rid).iterdir():
            os.utime(path, ns=(old, old))
        base = self.rid.rsplit("/", 1)[0]
        self.write(f"{base}/sunder-roles/worker/osx-arm64/current.dll", "retained role")
        self.write(f"{base}/sunder-dev/current.dll", "retained aggregate")
        qualification = f"{base}/sunder-targets/osx-arm64/role-inputs.v1.json"
        raw = json.dumps({"inputs": {"project": f"src/{family}/{family}.csproj",
                                    "configuration": "Debug", "targetFramework": "net10.0",
                                    "runtimeIdentifier": "osx-arm64"}})
        self.write(qualification, raw)
        self.write(qualification + ".sha256", hashlib.sha256(raw.encode()).hexdigest())
        return qualification

    def consolidate_source(self):
        # Model the CURRENT tree: the seven reviewed deletions remain unstaged.
        # Fixture-only Retained.cs files are not part of the real consolidated Apps.
        cleanup.git(self.root, "rm", "--cached", "--", *[p + "/Retained.cs" for p in cleanup.CONSOLIDATED_APPS])
        for app in cleanup.CONSOLIDATED_APPS:
            (self.root / app / "Retained.cs").unlink()
        for properties in cleanup.CONSOLIDATED_PROPERTIES:
            family = properties.split("/")[1]
            canonical = f"src/{family}/Properties/AssemblyInfo.cs"
            self.write(canonical, "// merged assembly attributes\n")
            original = properties + "/AssemblyInfo.cs"
            self.write(original, "// former App assembly attributes\n")
            cleanup.git(self.root, "add", "--", canonical, original)
            (self.root / original).unlink()
        marker = "src/Sunder.Package.Agent.Subagents/App/RuntimeNamespaceMarker.cs"
        self.write(marker, "// former namespace marker\n")
        cleanup.git(self.root, "add", "--", marker)
        (self.root / marker).unlink()
        self.directory("src/Sunder.Package.Agent/App/Runtime")
        self.repo.refresh_git()

    def test_default_dry_run_and_check_are_read_only(self):
        artifact = self.write(f"{self.retired}/bin/old.dll")
        before = cleanup.preservation(self.repo, cleanup.inventory(self.repo))
        self.assertEqual(0, self.run_tool())
        self.assertEqual(1, self.run_tool("--check"))
        self.assertTrue(artifact.exists())
        self.assertEqual(before, cleanup.preservation(self.repo, cleanup.inventory(self.repo)))

    def test_exact_allowlist_rejects_traversal_and_active_output_roots(self):
        for path in ("../sunder-core", "/tmp/x", "src/../src", "src//bad", "src\\bad", "src/bad:stream",
                     "src/Sunder.Package.Agent/bin", self.app, self.rid + "/child",
                     "src/Sunder.Package.Agent/obj", "src/Sunder.Agent.Execution.Common/bin",
                     cleanup.FROZEN, self.retired + "-other"):
            with self.subTest(path=path), self.assertRaises(cleanup.Refusal):
                cleanup.candidate(self.repo, path)

    def test_tracked_ignored_file_and_restored_head_file_are_protected(self):
        path = f"{self.retired}/bin/tracked.dll"
        self.write(path)
        cleanup.git(self.root, "add", "-f", path)
        self.assert_refused(self.retired, "Tracked path")
        # HEAD inventory protects a restored staged-deletion file as well.
        cleanup.git(self.root, "rm", "--cached", "-f", "--", path)
        self.repo.refresh_git()
        self.repo.head.add(path)
        self.assertIn("HEAD-tracked", cleanup.candidate(self.repo, self.retired)["reason"])

    def test_nonignored_files_and_ignored_source_outside_bin_obj_are_protected(self):
        self.write(f"{self.retired}/Keep.cs")
        self.assert_refused(self.retired, "nonignored")
        self.write(".gitignore", "bin/\nobj/\n*.cs\n")
        self.assert_refused(self.retired, "outside retired immediate bin/obj")

    def test_active_project_in_ignored_output_or_retained_app_is_protected(self):
        self.write(f"{self.retired}/obj/Active.csproj", "<Project />")
        self.assert_refused(self.retired, "Active project")
        self.write(f"{self.app}/bin/old.dll")
        self.write(f"{self.app}/Nested/New.csproj", "<Project />")
        self.assert_refused(self.app + "/bin", "active project")

    def test_symlink_root_descendant_and_ancestor_never_follow_target(self):
        outside = self.work / "outside"
        outside.mkdir()
        sentinel = outside / "keep.dll"
        sentinel.write_text("outside bytes")
        root = self.directory("src/Sunder.Package.Agent.Skills") / "App"
        root.symlink_to(outside, target_is_directory=True)
        self.assert_refused(self.retired, "ordinary directory")
        root.unlink()
        self.directory(self.retired)
        (root / "bin").symlink_to(outside, target_is_directory=True)
        self.assert_refused(self.retired, "Link, reparse")
        (self.root / self.app / "bin").symlink_to(outside, target_is_directory=True)
        self.assert_refused(self.app + "/bin", "ordinary directory")
        # The candidate root is ordinary-looking but an ancestor is a symlink.
        (self.root / "src/Sunder.Package.Agent/bin").symlink_to(outside, target_is_directory=True)
        self.assertEqual("refused", cleanup.candidate(self.repo, self.rid)["state"])
        self.assertEqual("outside bytes", sentinel.read_text())

    def test_special_reparse_and_hardlinked_files_are_refused(self):
        directory = self.directory(f"{self.retired}/bin")
        os.mkfifo(directory / "pipe")
        self.assert_refused(self.retired, "special file")
        (directory / "pipe").unlink()
        keeper = self.write("artifacts/qualified.dll", "qualified bytes")
        os.link(keeper, directory / "copy.dll")
        self.assert_refused(self.retired, "Hard-linked")
        self.assertEqual("link-or-reparse", cleanup.kind(SimpleNamespace(
            st_mode=stat.S_IFDIR, st_file_attributes=0x400)))

    def test_qualified_and_release_evidence_blocks_entire_apply(self):
        good = self.write(f"{self.retired}/bin/old.dll")
        for name in ("role-inputs.v1.json", "package.sunderpkg", "native-leaf-evidence-osx-arm64.json",
                     "sunder-targets/keep.dll"):
            with self.subTest(name=name):
                bad = self.write("src/Sunder.Package.Agent/OwnerProtocol/bin/" + name)
                plan = cleanup.inventory(self.repo)
                with self.assertRaisesRegex(cleanup.Refusal, "Preflight refused"):
                    cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
                self.assertTrue(good.exists())
                bad.unlink()

    def test_retired_app_is_removed_but_retained_app_source_and_other_outputs_survive(self):
        self.write(f"{self.retired}/obj/stale.cache")
        self.write(f"{self.app}/bin/stale.dll")
        self.write(f"{self.app}/obj/stale.cache")
        retained_empty = self.directory(f"{self.app}/Runtime")
        active = self.write("src/Sunder.Package.Agent/bin/Debug/net10.0/sunder-dev/keep.dll")
        support = self.write("src/Sunder.Agent.Execution.Common/bin/Debug/keep.dll")
        frozen = self.directory(cleanup.FROZEN + "/KeepEmpty")
        plan = cleanup.inventory(self.repo)
        before = cleanup.preservation(self.repo, plan)
        removed = {"files": [], "directories": [], "bytes": 0}
        cleanup.apply_plan(self.repo, plan, removed)
        self.assertFalse((self.root / self.retired).exists())
        self.assertFalse((self.root / self.app / "bin").exists())
        self.assertTrue((self.root / self.app / "Retained.cs").is_file())
        self.assertTrue(all(p.exists() for p in (retained_empty, active, support, frozen)))
        self.assertEqual(before, cleanup.preservation(self.repo, plan))

    def test_direct_rid_classification_and_preserved_role_bytes(self):
        qualification = self.qualified_rid()
        plan = cleanup.inventory(self.repo)
        entry = next(c for c in plan["candidates"] if c["path"] == self.rid)
        self.assertEqual("eligible", entry["state"], entry)
        self.assertEqual(qualification, entry["classification"]["preservedQualification"])
        before = cleanup.preservation(self.repo, plan)
        cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
        self.assertFalse((self.root / self.rid).exists())
        self.assertEqual(before, cleanup.preservation(self.repo, plan))

    def test_recent_direct_rid_and_invalid_or_missing_qualification_are_refused(self):
        qualification = self.qualified_rid()
        self.write(f"{self.rid}/new.dll")
        self.assert_refused(self.rid, "newer than")
        (self.root / self.rid / "new.dll").unlink()
        self.write(qualification + ".sha256", "wrong digest")
        self.assert_refused(self.rid, "digest mismatch")
        (self.root / qualification).unlink()
        self.assertEqual("refused", cleanup.candidate(self.repo, self.rid)["state"])

    def test_direct_rid_without_legacy_dependency_or_wrong_identity_is_refused(self):
        qualification = self.qualified_rid()
        deps = self.root / self.rid / (cleanup.FAMILIES[0] + ".deps.json")
        original = deps.stat()
        value = json.loads(deps.read_text())
        value["libraries"] = {"Sunder.Package.Agent/2.0.0": {}}
        deps.write_text(json.dumps(value))
        os.utime(deps, ns=(original.st_atime_ns, original.st_mtime_ns))
        self.assert_refused(self.rid, "no retired protocol dependency")
        self.qualified_rid()
        raw = json.loads((self.root / qualification).read_text())
        raw["inputs"]["project"] = "src/Other/Other.csproj"
        content = json.dumps(raw)
        self.write(qualification, content)
        self.write(qualification + ".sha256", hashlib.sha256(content.encode()).hexdigest())
        self.assert_refused(self.rid, "project mismatch")

    def test_empty_current_rid_obj_original_archives_and_identity_evidence_survive_repeated_check(self):
        rid = self.directory(self.rid)
        obj = self.directory("src/Sunder.Package.Agent/obj")
        archive = self.write(self.rid.rsplit("/", 1)[0] + "/Sunder.Package.Agent.2.0.0.sunderpkg", "original archive")
        evidence = self.write("compatibility/identity-migration/v1/predecessor/archive.sunderpkg", "predecessor bytes")
        receipt = self.write("compatibility/identity-migration/v1/fork-receipts/Sunder.Package.Agent.json", "identity receipt")
        plan = cleanup.inventory(self.repo)
        entry = next(c for c in plan["candidates"] if c["path"] == self.rid)
        self.assertEqual("preserved", entry["state"], entry)
        before = cleanup.preservation(self.repo, plan)
        removed = {"files": [], "directories": [], "bytes": 0}
        cleanup.apply_plan(self.repo, plan, removed)
        self.assertEqual([], removed["directories"])
        for _ in range(2):
            self.assertEqual(0, self.run_tool("--check"))
        self.assertEqual(before, cleanup.preservation(self.repo, plan))
        self.assertTrue(all(p.exists() for p in (rid, obj, archive, evidence, receipt)))

    def test_current_nonlegacy_rid_requires_current_identity_and_is_preserved(self):
        qualification = self.qualified_rid()
        manifest = json.loads((self.root / "packages.json").read_text())
        manifest["packages"][0]["packageId"] = "younics.ai.agent"
        self.write("packages.json", json.dumps(manifest))
        raw = json.loads((self.root / qualification).read_text())
        raw["inputs"].update(packageId="younics.ai.agent", project="Sunder.Package.Agent/Sunder.Package.Agent.csproj")
        content = json.dumps(raw)
        self.write(qualification, content)
        self.write(qualification + ".sha256", hashlib.sha256(content.encode()).hexdigest())
        self.write(f"{self.rid}/{cleanup.FAMILIES[0]}.deps.json", json.dumps({
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/osx-arm64"},
            "libraries": {"Sunder.Package.Agent/2.0.0": {}}}))
        plan = cleanup.inventory(self.repo)
        entry = next(c for c in plan["candidates"] if c["path"] == self.rid)
        self.assertEqual("preserved", entry["state"], entry)
        self.assertEqual(0, self.run_tool("--check"))
        before = cleanup.preservation(self.repo, plan)
        cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
        self.assertEqual(before, cleanup.preservation(self.repo, plan))
        manifest["packages"][0]["packageId"] = "another.identity"
        self.write("packages.json", json.dumps(manifest))
        self.assert_refused(self.rid, "current package identity mismatch")

    def test_consolidated_empty_allowlist_preserves_current_source_and_unstaged_deletions(self):
        self.consolidate_source()
        plan = cleanup.inventory(self.repo)
        self.assertEqual(cleanup.CONSOLIDATED_EMPTY_PATHS, {e["path"] for e in plan["emptyDirectories"]})
        self.assertEqual(11, plan["counts"]["emptyOnlyDirectories"])
        before = cleanup.preservation(self.repo, plan)
        removed = {"files": [], "directories": [], "bytes": 0}
        cleanup.apply_plan(self.repo, plan, removed)
        self.assertEqual([], removed["files"])
        self.assertEqual(cleanup.CONSOLIDATED_EMPTY_PATHS, set(removed["directories"]))
        self.assertEqual(before, cleanup.preservation(self.repo, plan))
        self.assertTrue(cleanup.CONSOLIDATED_MISSING <= self.repo.tracked)
        self.assertTrue(all((self.root / app).exists() for app in cleanup.RETAINED_APPS
                            if app not in cleanup.CONSOLIDATED_APPS))
        self.assertEqual(0, self.run_tool("--check"))

    def test_consolidation_does_not_relax_unreviewed_missing_tracked_or_nonempty_protection(self):
        self.consolidate_source()
        unreviewed = self.write(f"{self.app}/Unreviewed.cs", "new staged work")
        cleanup.git(self.root, "add", "--", f"{self.app}/Unreviewed.cs")
        unreviewed.unlink()
        plan = cleanup.inventory(self.repo)
        self.assertTrue(any("Unreviewed tracked path" in reason for reason in plan["refusals"]))
        with self.assertRaisesRegex(cleanup.Refusal, "Preflight refused"):
            cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
        cleanup.git(self.root, "rm", "--cached", "--", f"{self.app}/Unreviewed.cs")
        restored = self.write(f"{self.app}/Properties/AssemblyInfo.cs", "restored tracked source")
        unknown = self.write("src/Sunder.Package.Agent.Mcp/App/Properties/Unreviewed.txt", "unknown work")
        plan = cleanup.inventory(self.repo)
        before = cleanup.preservation(self.repo, plan)
        cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
        self.assertTrue(restored.is_file())
        self.assertTrue(unknown.is_file())
        self.assertEqual(before, cleanup.preservation(self.repo, plan))

    def test_consolidation_refuses_symlinks_and_requires_canonical_attributes(self):
        self.consolidate_source()
        outside = self.work / "outside"
        outside.mkdir()
        sentinel = outside / "keep.cs"
        sentinel.write_text("keep")
        properties = self.root / self.app / "Properties"
        properties.rmdir()
        properties.symlink_to(outside, target_is_directory=True)
        plan = cleanup.inventory(self.repo)
        self.assertTrue(any("link/reparse/special" in reason for reason in plan["refusals"]))
        with self.assertRaisesRegex(cleanup.Refusal, "Preflight refused"):
            cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
        self.assertEqual("keep", sentinel.read_text())
        properties.unlink()
        properties.mkdir()
        (self.root / "src/Sunder.Package.Agent/Properties/AssemblyInfo.cs").unlink()
        plan = cleanup.inventory(self.repo)
        self.assertTrue(any("Missing retained canonical assembly attributes" in reason for reason in plan["refusals"]))

    def test_empty_pruning_ignores_active_output_and_history_trees_and_nonempty_extras(self):
        removable = [self.directory(p) for p in (
            "src/Sunder.Package.Agent/Unused/Child", "fixtures/PackedProtocolConsumer",
            ".circleci", "scripts/__pycache__")]
        protected = [self.directory(p) for p in (
            "src/Sunder.Package.Agent/obj/Empty", "src/Sunder.Package.Agent/bin/Release/Empty",
            "src/Sunder.Package.Agent/PreCanonicalKeep/Empty", f"{self.app}/Empty")]
        self.write("fixtures/keep.txt", "fixture source")
        plan = cleanup.inventory(self.repo)
        removed = {"files": [], "directories": [], "bytes": 0}
        cleanup.apply_plan(self.repo, plan, removed)
        self.assertTrue(all(not p.exists() for p in removable))
        self.assertTrue(all(p.exists() for p in protected))
        self.assertTrue((self.root / "fixtures/keep.txt").is_file())

    def test_complete_recheck_detects_new_file_or_new_tracking_before_any_removal(self):
        first = self.write("src/Sunder.Package.Agent.Contracts/bin/first.dll")
        self.write(f"{self.retired}/bin/old.dll")
        plan = cleanup.inventory(self.repo)
        new = self.write(f"{self.retired}/Keep.cs")
        removed = {"files": [], "directories": [], "bytes": 0}
        with self.assertRaisesRegex(cleanup.Refusal, "Inventory changed"):
            cleanup.apply_plan(self.repo, plan, removed)
        self.assertTrue(first.exists())
        self.assertEqual([], removed["files"])
        new.unlink()
        plan = cleanup.inventory(self.repo)
        cleanup.git(self.root, "add", "-f", f"{self.retired}/bin/old.dll")
        with self.assertRaisesRegex(cleanup.Refusal, "Inventory changed"):
            cleanup.apply_plan(self.repo, plan, removed)
        self.assertTrue(first.exists())

    def test_per_root_recheck_detects_symlink_replacement(self):
        path = self.write(f"{self.retired}/bin/old.dll")
        reviewed = cleanup.candidate(self.repo, self.retired)
        path.unlink()
        outside = self.work / "sentinel"
        outside.write_text("outside")
        path.symlink_to(outside)
        with self.assertRaisesRegex(cleanup.Refusal, "changed since review"):
            cleanup.delete_candidate(self.repo, reviewed, {"files": [], "directories": [], "bytes": 0})
        self.assertEqual("outside", outside.read_text())

    def test_cli_requires_matching_review_preserves_dirty_index_and_proves_removal(self):
        self.write(f"{self.retired}/bin/old.dll")
        source = self.write(f"{self.app}/Retained.cs", "staged source")
        cleanup.git(self.root, "add", "--", str(source.relative_to(self.root)))
        source.write_text("unstaged source")
        self.write("tests/new-source.cs", "untracked source")
        review = self.work / "before.json"
        after = self.work / "after.json"
        self.assertEqual(0, self.run_tool("--report", review))
        self.assertEqual(0, self.run_tool("--apply", "--review", review, "--report", after))
        proof = json.loads(after.read_text())
        self.assertTrue(proof["preservationUnchanged"])
        self.assertEqual(1, len(proof["removed"]["files"]))
        self.assertEqual(0, proof["after"]["counts"]["eligibleRoots"])
        self.assertEqual("unstaged source", source.read_text())
        self.assertEqual("staged source", cleanup.git(self.root, "show", ":" + str(source.relative_to(self.root))).decode())
        self.assertEqual(0, self.run_tool("--check"))
        # A subsequent build-like regeneration is detected, never automatically deleted.
        regenerated = self.write(f"{self.retired}/bin/old.dll", "regenerated")
        self.assertEqual(1, self.run_tool("--check"))
        self.assertTrue(regenerated.exists())

    def test_stale_or_tampered_review_cannot_widen_cleanup(self):
        artifact = self.write(f"{self.retired}/bin/old.dll")
        review = self.work / "before.json"
        self.assertEqual(0, self.run_tool("--report", review))
        original_report = review.read_bytes()
        self.assertEqual(2, self.run_tool("--report", review))
        self.assertEqual(original_report, review.read_bytes())
        data = json.loads(review.read_text())
        data["before"]["emptyDirectories"].append({"path": "src/Sunder.Package.Agent/bin", "device": 0, "inode": 0})
        review.write_text(json.dumps(data))
        self.assertEqual(2, self.run_tool("--apply", "--review", review, "--report", self.work / "tampered.json"))
        self.assertTrue(artifact.exists())
        fresh = self.work / "fresh.json"
        self.assertEqual(0, self.run_tool("--report", fresh))
        self.write("src/Sunder.Package.Agent/Concurrent.cs", "new source")
        self.assertEqual(2, self.run_tool("--apply", "--review", fresh, "--report", self.work / "stale.json"))
        self.assertTrue(artifact.exists())

    def test_empty_directory_filled_after_review_is_not_removed(self):
        self.directory("src/Sunder.Package.Agent/Unused")
        plan = cleanup.inventory(self.repo)
        keeper = self.write("src/Sunder.Package.Agent/Unused/Keep.cs", "new work")
        with self.assertRaisesRegex(cleanup.Refusal, "Inventory changed"):
            cleanup.apply_plan(self.repo, plan, {"files": [], "directories": [], "bytes": 0})
        self.assertTrue(keeper.exists())


if __name__ == "__main__":
    unittest.main()
