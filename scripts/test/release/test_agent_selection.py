"""Version planning and recovery: synthetic source, mocked GitHub/Registry/MSBuild."""
import copy
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_release as r
import agent_selection as selection


class SelectionTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory()
        self.addCleanup(scope.cleanup)
        self.source = Path(scope.name)
        self.rows = copy.deepcopy(r.inventory(Path(__file__).resolve().parents[3])["packages"])
        (self.source / "packages.json").write_bytes(r.canonical(dict(releaseVersion="2.0.0", packages=self.rows)))
        for row in self.rows:
            path = self.source / row["projectPath"]
            path.parent.mkdir(parents=True)
            path.write_text('<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>')
            (path.parent / "sunder-contracts.lock.json").write_text('{}')
        self.remote = []
        self.registry = {}

    def release(self, row, version="2.0.0", **changes):
        value = dict(id=self.rows.index(row) + 1, tag_name=row["key"] + "/v" + version,
            target_commitish="b" * 40, draft=False, prerelease=not r.version(version)[1],
            assets=[dict(id=100 + self.rows.index(row), name=f"{row['packageId']}.{version}.sunderpkg",
                size=123, digest="sha256:" + "c" * 64, state="uploaded"),
                dict(id=200 + self.rows.index(row), name="native-test-evidence.json", size=123, digest="sha256:" + "e" * 64, state="uploaded")])
        value.update(changes)
        return value

    def published(self):
        self.remote = [self.release(row) for row in self.rows]
        self.registry = {row["packageId"]: dict(packageId=row["packageId"], version="2.0.0", isYanked=False,
            canonicalArtifact=dict(sha256="c" * 64)) for row in self.rows}

    def plan(self):
        with patch.object(selection, "evaluated_packages", side_effect=AssertionError("selection must not invoke MSBuild")), \
             patch.object(selection.subprocess, "check_output", side_effect=AssertionError("selection must not start a toolchain")), \
             patch.object(selection, "releases", return_value=self.remote), \
             patch.object(selection, "registry_version", side_effect=lambda _, row: self.registry.get(row["packageId"])):
            return selection.plan(self.source, "a" * 40, "https://registry.test")

    def test_first_release_selects_all_but_noop_selects_none(self):
        first = self.plan()
        self.assertEqual(15, len(selection.selected(first)))
        self.assertEqual(2, first["schemaVersion"])
        self.assertNotIn("ownerPrincipalId", first)
        with self.assertRaises(r.Invalid):
            selection.validate(dict(first, ownerPrincipalId="d" * 32), self.source)
        self.published()
        plan = self.plan()
        self.assertEqual([], selection.selected(plan))
        self.assertTrue(all(row["retainedArchive"] is not None for row in plan["packages"]))

    def test_cli_plans_first_release_and_noop_with_only_source_and_registry_configuration(self):
        for published in (False, True):
            with self.subTest(published=published):
                if published: self.published()
                destination = self.source / ("noop.json" if published else "first.json")
                output = self.source / "outputs"
                with patch.dict(os.environ, dict(GITHUB_SHA="a" * 40, REGISTRY_API_URL="https://registry.test", GITHUB_OUTPUT=str(output)), clear=True), \
                     patch.object(sys, "argv", ["agent_selection.py", "plan", "--source", str(self.source), "--plan", str(destination)]), \
                     patch.object(selection, "evaluated_packages", side_effect=AssertionError("selection must not invoke MSBuild")), \
                     patch.object(selection, "releases", return_value=self.remote), \
                     patch.object(selection, "registry_version", side_effect=lambda _, row: self.registry.get(row["packageId"])):
                    selection.main()
                self.assertEqual(0 if published else 15, len(selection.selected(r.load(destination))))
                self.assertNotIn("ownerPrincipalId", r.load(destination))
                self.assertIn(f"selected_count={0 if published else 15}\n", output.read_text())

    def test_one_bump_selects_only_that_package_with_mixed_versions(self):
        self.published()
        row = self.rows[9]
        (self.source / row["projectPath"]).write_text('<Project><PropertyGroup><Version>2.1.0</Version></PropertyGroup></Project>')
        self.registry.pop(row["packageId"])
        result = self.plan()
        self.assertEqual([(row["packageId"], "2.1.0")], [(p["packageId"], p["version"]) for p in selection.selected(result)])
        self.assertEqual(14, sum(p["retainedArchive"] is not None for p in result["packages"]))

    def test_newer_major_does_not_block_a_supported_major_patch(self):
        self.published()
        row = self.rows[0]
        self.remote.append(self.release(row, "3.0.0", id=999))
        self.assertEqual([], selection.selected(self.plan()))
        (self.source / row["projectPath"]).write_text('<Project><PropertyGroup><Version>2.0.1</Version></PropertyGroup></Project>')
        self.registry.pop(row["packageId"])
        self.assertEqual([row["packageId"]], [item["packageId"] for item in selection.selected(self.plan())])

    def test_missing_release_is_not_inferred_from_a_tag_or_registry_upload(self):
        self.registry = {row["packageId"]: dict(isYanked=True) for row in self.rows}
        self.assertEqual(15, len(selection.selected(self.plan())))
        self.registry[self.rows[0]["packageId"]] = dict(isYanked=False)
        with self.assertRaisesRegex(r.Invalid, "already Listed"):
            self.plan()

    def test_prerelease_precedence_is_semver_not_listing_order(self):
        row = self.rows[0]
        (self.source / row["projectPath"]).write_text('<Project><PropertyGroup><Version>2.0.0-rc.10</Version></PropertyGroup></Project>')
        self.remote = [self.release(row, "2.0.0-rc.2"), self.release(row, "2.0.0-rc.9", id=1000)]
        self.assertEqual(15, len(selection.selected(self.plan())))
        self.remote.append(self.release(row, "2.0.0", id=1001))
        self.registry[row["packageId"]] = dict(isYanked=False, canonicalArtifact=dict(sha256="c" * 64))
        result = self.plan()
        self.assertEqual(14, len(selection.selected(result)))
        retained = next(p for p in result["packages"] if p["packageId"] == row["packageId"])
        self.assertFalse(retained["selected"])
        self.assertEqual("2.0.0", retained["version"])
        # An exact older publication can be reused, but can never be republished.
        self.remote.append(self.release(row, "2.0.0-rc.10", id=1002))
        result = self.plan()
        self.assertEqual(14, len(selection.selected(result)))
        self.assertEqual("2.0.0-rc.10", next(p for p in result["packages"] if not p["selected"])["version"])

    def test_published_registry_mismatch_and_incomplete_assets_block_selection(self):
        self.published()
        for change in ("missing", "yanked", "digest", "asset"):
            with self.subTest(change=change):
                original, remote = copy.deepcopy(self.registry), copy.deepcopy(self.remote)
                if change == "missing": self.registry.pop(self.rows[0]["packageId"])
                if change == "yanked": self.registry[self.rows[0]["packageId"]]["isYanked"] = True
                if change == "digest": self.registry[self.rows[0]["packageId"]]["canonicalArtifact"]["sha256"] = "f" * 64
                if change == "asset": self.remote[0]["assets"] = []
                with self.assertRaises(r.Invalid): self.plan()
                self.registry, self.remote = original, remote

    def test_draft_from_another_commit_is_never_adopted(self):
        self.remote = [self.release(self.rows[0], draft=True)]
        with self.assertRaisesRegex(r.Invalid, "another source"):
            self.plan()

    def test_draft_without_retained_plan_never_falls_back_to_a_fresh_native_build(self):
        self.remote = [self.release(self.rows[0], draft=True, target_commitish="a" * 40)]
        with self.assertRaisesRegex(r.Invalid, "original failed job.*never rebuild"):
            self.plan()

    def test_partial_announcement_recovers_the_original_selection(self):
        original = self.plan()
        self.remote = [self.release(self.rows[0], target_commitish="a" * 40, draft=False,
            assets=[dict(id=999, name=selection.PLAN, size=len(r.canonical(original)), digest="sha256:" + r.sha(r.canonical(original)))])]
        def download(_, destination): destination.write_bytes(r.canonical(original))
        with patch.object(selection.inputs, "download", side_effect=download):
            self.assertEqual(original, self.plan())
        self.remote[0]["target_commitish"] = "b" * 40
        with self.assertRaises(r.Invalid): self.plan()

    def test_historical_plan_is_resumed_without_rewriting_its_recorded_owner(self):
        original = dict(self.plan(), schemaVersion=1, ownerPrincipalId="d" * 32)
        self.remote = [self.release(self.rows[0], target_commitish="a" * 40, draft=True,
            assets=[dict(id=999, name=selection.PLAN, size=len(r.canonical(original)), digest="sha256:" + r.sha(r.canonical(original)))])]
        with patch.object(selection.inputs, "download", side_effect=lambda _, destination: destination.write_bytes(r.canonical(original))):
            self.assertEqual(original, self.plan())

    def test_completed_same_source_batch_is_a_successful_noop_not_an_endless_resume(self):
        original = self.plan()
        self.published()
        for remote in self.remote:
            remote["target_commitish"] = "a" * 40
            remote["assets"].append(dict(id=900 + remote["id"], name=selection.PLAN,
                size=len(r.canonical(original)), digest="sha256:" + r.sha(r.canonical(original))))
        with patch.object(selection.inputs, "download", side_effect=lambda _, destination: destination.write_bytes(r.canonical(original))):
            self.assertEqual([], selection.selected(self.plan()))

    def test_all_release_pages_are_read_and_duplicate_release_tags_are_rejected(self):
        rows = [dict(id=i + 1, tag_name=f"unrelated/v{i}.0.0", draft=False) for i in range(100)]
        last = self.release(self.rows[0], id=1001)
        with patch.object(selection.inputs, "gh_json", side_effect=[rows, [last]]) as request:
            self.assertEqual(101, len(selection.releases()))
            self.assertTrue(request.call_args.args[0].endswith("page=2"))
        with patch.object(selection.inputs, "gh_json", return_value=[last, last]), self.assertRaises(r.Invalid):
            selection.releases()

    def test_release_msbuild_versions_must_match_the_authored_literal_without_overrides(self):
        def evaluate(command, **kwargs):
            self.assertIn("-p:Configuration=Release", command)
            self.assertFalse(any("SunderAgentVersion=" in part for part in command))
            self.assertNotIn("GH_TOKEN", kwargs["env"])
            self.assertNotIn("SUNDER_REGISTRY_PUBLISH_TOKEN", kwargs["env"])
            return r.encoded(dict(Properties=dict(Version="2.0.0", PackageVersion="2.0.0", InformationalVersion="2.0.0", SunderAgentVersion="")))
        with patch.object(selection.subprocess, "check_output", side_effect=evaluate):
            self.assertEqual(15, len(selection.evaluated_packages(self.source)))
            self.assertEqual([self.rows[0]], selection.evaluated_packages(self.source, [self.rows[0]["packageId"]]))
        with patch.object(selection.subprocess, "check_output") as evaluate:
            self.assertEqual([], selection.evaluated_packages(self.source, []))
            evaluate.assert_not_called()
        with patch.object(selection.subprocess, "check_output", return_value=r.encoded(dict(Properties=dict(
            Version="9.0.0", PackageVersion="9.0.0", InformationalVersion="9.0.0", SunderAgentVersion="9.0.0")))), self.assertRaises(r.Invalid):
            selection.evaluated_packages(self.source)


if __name__ == "__main__": unittest.main()
