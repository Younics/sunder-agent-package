import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/ci"))
from required_test_evidence import verify


class RequiredCasesTests(unittest.TestCase):
    def test_both_windows_abis_require_live_unique_containment_cases(self):
        policy = json.loads((ROOT / "scripts/test/required-platform-cases.json").read_bytes())
        row, = [row for row in policy["requirements"] if row["className"].endswith(".WindowsSecureMutationTests")]
        self.assertEqual(34, row["expectedCount"])
        self.assertEqual({"win-x64", "win-arm64"}, set(row["rids"]))
        inventory = dict(projects=[dict(path="Fixture.Tests.csproj", owner=row["owner"])])
        results = [dict(project="fixture.tests", fullyQualifiedName=row["className"] + "." + name.split("(", 1)[0],
                        test=row["className"] + "." + name, outcome="passed") for name in row["cases"]]
        for rid in row["rids"]:
            self.assertEqual([], verify(results, inventory, "source", rid))
            for defect in ([], results[:-1], [*results[:-1], results[0]],
                           [dict(results[0], test="another test"), *results[1:]],
                           [dict(results[0], outcome="skipped"), *results[1:]],
                           [dict(results[0], outcome="failed"), *results[1:]]):
                with self.subTest(rid=rid, count=len(defect)):
                    self.assertTrue(verify(defect, inventory, "source", rid))

    def test_linux_docker_assurance_needs_its_actual_case_not_a_passing_count(self):
        owner = "younics.ai.agent.execution.docker"
        inventory = dict(projects=[dict(path="Docker.Tests.csproj", owner=owner)])
        wrong = dict(project="docker.tests", fullyQualifiedName="Docker.Tests.SomePassingCase", outcome="passed")
        correct = dict(wrong, fullyQualifiedName="Docker.Tests.DockerExecutionTarget_RealDaemon_VerifiesMountAndContainerCanReadNestedHostCreation")
        self.assertTrue(verify([wrong], inventory, "source", "linux-arm64"))
        self.assertEqual([], verify([correct], inventory, "source", "linux-arm64"))


if __name__ == "__main__":
    unittest.main()
