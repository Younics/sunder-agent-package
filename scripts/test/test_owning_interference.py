"""Diagnostic-driver guards; no release admission or native acceptance is supplied."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("owning_interference", Path(__file__).with_name("run-owning-interference.py"))
driver = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(driver)


class OwningInterferenceTests(unittest.TestCase):
    def row(self, test_id="one", name="case", outcome="passed"):
        return dict(testId=test_id, test=name, outcome=outcome)

    def test_identity_accepts_truncated_names_and_theory_method_ids_without_merging_cases(self):
        rows = [self.row("one", "truncated"), self.row("two", "truncated"), self.row("two", "other data")]
        self.assertEqual(3, driver.case_identity(rows)["count"])
        self.assertEqual(driver.case_identity(rows), driver.case_identity(list(reversed(rows))))

    def test_identity_rejects_empty_or_exact_duplicate_cases(self):
        for rows in ([], [self.row(), self.row()], [self.row("")]):
            with self.subTest(rows=rows), self.assertRaises(driver.ci.inputs.Invalid):
                driver.case_identity(rows)

    def test_same_count_substitution_is_not_accepted(self):
        profile = dict(expectedCaseIdentities=dict(full=driver.case_identity([self.row()])))
        with self.assertRaises(driver.ci.inputs.Invalid):
            driver.require_cases(profile, "full", [self.row("substitute")])

    def test_partition_identity_rejects_an_omission_or_cross_process_duplicate(self):
        rows = [self.row("one"), self.row("two")]
        profile = dict(expectedCaseIdentities=dict(full=driver.case_identity(rows)))
        self.assertEqual(2, driver.require_cases(profile, "full", rows)["count"])
        for partitioned in (rows[:1], [*rows, rows[0]]):
            with self.subTest(partitioned=partitioned), self.assertRaises(driver.ci.inputs.Invalid):
                driver.require_cases(profile, "full", partitioned)

    def test_only_mixed_view_variant_runs_the_original_owning_project_prelude(self):
        profile = next(row for row in driver.POLICY["profiles"] if row["id"] == "view")
        self.assertEqual([*profile["prelude"], profile["project"]],
                         [row[0] for row in driver.trial_groups(profile, "mixed")])
        for variant in ("partitioned", "focused", "project-alone"):
            self.assertTrue(all(row[0] == profile["project"] for row in driver.trial_groups(profile, variant)))
        self.assertEqual(["partitionExclude", "partition"],
                         [row[2] for row in driver.trial_groups(profile, "partitioned")])

    def test_completed_assertion_failure_is_retained_without_aborting_comparisons(self):
        rows = [self.row(outcome="failed")]
        profile = dict(id="skills", project="test.csproj", expectedCaseIdentities=dict(focused=driver.case_identity(rows)))
        group = {}
        failure = driver.test_lanes.TestProjectFailure(1, ["dotnet", "test"])
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ), \
             patch.object(driver.test_lanes, "run_project_with_filesystems", side_effect=failure), \
             patch.object(driver, "collect_results", return_value=rows), \
             patch.object(driver, "verify_traces", return_value=dict(files=1, samples=1)):
            result = driver.execute_group("state", profile, profile["project"], "selection", "focused", Path(root), {}, group)
        self.assertEqual(rows, result)
        self.assertEqual(dict(failed=1), group["counts"])
        self.assertIn("TestProjectFailure", group["error"])
        self.assertNotIn("infrastructureError", group)

    def test_infrastructure_failure_is_recorded_and_not_reclassified_as_assertion_failure(self):
        profile = dict(id="skills", project="test.csproj")
        group = {}
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ), \
             patch.object(driver.test_lanes, "run_project_with_filesystems", side_effect=subprocess.CalledProcessError(2, ["dotnet", "test"])):
            with self.assertRaises(subprocess.CalledProcessError):
                driver.execute_group("state", profile, profile["project"], "", "full", Path(root), {}, group)
        self.assertIn("CalledProcessError", group["infrastructureError"])

    def test_observer_errors_or_pool_execution_invalidate_diagnostics(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            trace = directory / "trace"
            trace.mkdir()
            path = trace / "one.jsonl"
            for middle in (dict(stage="observer-error"), dict(stage="sample", poolThread=True)):
                rows = [dict(stage="observer-start"), middle, dict(stage="observer-stop")]
                path.write_text("\n".join(json.dumps(row) for row in rows))
                with self.subTest(middle=middle), self.assertRaises(driver.ci.inputs.Invalid):
                    driver.verify_traces(directory, True)
            path.write_text("\n".join(json.dumps(row) for row in (
                dict(stage="observer-start"), dict(stage="sample", poolThread=False), dict(stage="observer-stop"))))
            self.assertEqual(dict(files=1, samples=1), driver.verify_traces(directory, True))

    def test_repetition_bounds_stop_before_preparation_or_output_creation(self):
        with tempfile.TemporaryDirectory() as root, patch.object(driver.ci, "prepare") as prepare:
            for repetitions in (0, 4):
                path = Path(root) / str(repetitions)
                with self.assertRaises(driver.ci.inputs.Invalid):
                    driver.run_profile({}, repetitions, path)
                self.assertFalse(path.exists())
            prepare.assert_not_called()

    def test_acquisition_tokens_are_removed_before_inventory_evaluation_and_restored_on_error(self):
        def inventory(*_):
            self.assertNotIn("GH_TOKEN", os.environ)
            self.assertNotIn("GITHUB_TOKEN", os.environ)
            raise RuntimeError("fixture stops at sanitized inventory")

        def digest(path):
            return driver.POLICY["ownerSha256" if Path(path).suffix == ".sunderpkg" else "catalogSha256"]

        with tempfile.TemporaryDirectory() as root, \
             patch.dict(os.environ, GH_TOKEN="fixture-actions-only", GITHUB_TOKEN="fixture-github-only"), \
             patch.object(driver, "authored_inputs", return_value={}), \
             patch.object(driver.ci, "head", return_value="fixture"), \
             patch.object(driver, "acquire") as acquire, \
             patch.object(driver.ci.inputs, "file_sha", side_effect=digest), \
             patch.object(driver.ci, "generate_inventory", side_effect=inventory):
            output = Path(root) / "diagnostic"
            with self.assertRaisesRegex(RuntimeError, "sanitized inventory"):
                driver.run_profile(driver.POLICY["profiles"][0], 1, output)
            self.assertEqual(2, acquire.call_count)
            self.assertEqual("fixture-actions-only", os.environ["GH_TOKEN"])
            self.assertEqual("fixture-github-only", os.environ["GITHUB_TOKEN"])
            self.assertEqual("infrastructure-error", json.loads((output / "summary.json").read_text())["status"])


if __name__ == "__main__":
    unittest.main()
