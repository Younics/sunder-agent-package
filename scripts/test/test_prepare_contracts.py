"""Local preparation delegates exact input admission and all cache writes to owners."""
import importlib.util
import io
from pathlib import Path
import runpy
import subprocess
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("prepare_contracts", SCRIPTS / "dev/prepare-contracts.py")
local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(local)
fixture_setup = runpy.run_path(str(SCRIPTS / "test/release/release-inputs.fixture.py"))["ColdInputsTests"].setUp


class LocalContractPreparationTests(unittest.TestCase):
    setUp = fixture_setup

    def prepare(self, **kwargs):
        with patch.object(local.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run, \
                patch("sys.stdout", new_callable=io.StringIO):
            local.prepare(self.source, [self.archive], self.catalog_path, **kwargs)
        return [call.args[0] for call in run.call_args_list]

    def test_inventory_preparation_uses_exact_inputs_then_checks_all_projects_in_the_same_cache(self):
        before = local.inputs.source_snapshot(self.source)
        cache = self.root / "developer cache"
        commands = self.prepare(cache_root=cache)
        hydration = [command for command in commands if "-t:RestoreSunderContracts" in command]
        checks = [command for command in commands if "-t:CheckSunderContractCache" in command]
        self.assertEqual(15, len(hydration))
        self.assertEqual(15, len(checks))
        self.assertEqual(14, sum(any(arg.startswith("-p:SunderPackageCandidatePath=") for arg in command) for command in hydration))
        self.assertEqual(commands, hydration + checks)
        self.assertTrue(all("-p:SunderContractCacheRoot=" + str(cache.resolve()) in command for command in commands))
        self.assertTrue(all(command[:2] == ["dotnet", "msbuild"] for command in commands))
        self.assertFalse(cache.exists())  # The Python caller never creates cache evidence.
        self.assertEqual(before, local.inputs.source_snapshot(self.source))

    def test_wrong_candidate_fails_before_any_core_invocation(self):
        with patch.object(local.subprocess, "run") as run:
            with self.assertRaisesRegex(ValueError, "exact committed package selection"):
                local.prepare(self.source, [], self.catalog_path)
            run.assert_not_called()

    def test_check_needs_no_candidate_and_never_invokes_restore(self):
        with patch.object(local.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run, \
                patch("sys.stdout", new_callable=io.StringIO):
            local.prepare(self.source, check=True)
        self.assertEqual(15, run.call_count)
        self.assertTrue(all("-t:CheckSunderContractCache" in call.args[0] for call in run.call_args_list))
        self.assertTrue(all(not any(arg.startswith("-p:SunderContractCacheRoot=") for arg in call.args[0]) for call in run.call_args_list))

    def test_core_failure_stops_preparation_and_authored_mutation_is_not_accepted_or_repaired(self):
        with patch.object(local.subprocess, "run", side_effect=subprocess.CalledProcessError(7, [])) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                local.prepare(self.source, [self.archive], self.catalog_path)
            self.assertEqual(1, run.call_count)
        changed = self.source / "src/P1/sunder-contracts.lock.json"

        def mutate(*args, **kwargs):
            changed.write_bytes(b"{}")
            raise subprocess.CalledProcessError(7, args[0])

        with patch.object(local.subprocess, "run", side_effect=mutate):
            with self.assertRaisesRegex(ValueError, "changed authored locks"):
                local.prepare(self.source, [self.archive], self.catalog_path)
        self.assertEqual(b"{}", changed.read_bytes())


if __name__ == "__main__":
    unittest.main()
