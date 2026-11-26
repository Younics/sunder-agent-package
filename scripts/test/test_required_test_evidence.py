import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/ci"))
sys.path.insert(0, str(ROOT / "scripts/release"))
import agent_cutover_200 as cutover
import agent_selection as selection
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

    def test_initial_cutover_does_not_grant_current_windows_credit(self):
        digest = "b" * 64
        for rid in cutover.RIDS:
            marker = dict(className=cutover.CLASS, rid=rid, planSha256=digest, status="not-selected", caseCount=34)
            inventory = {"projects": [dict(path=cutover.PROJECT, owner=cutover.OWNER)]}
            self.assertTrue(verify([], inventory, "source", rid))
            inventory[cutover.DISPOSITION] = marker
            self.assertEqual([], verify([], inventory, "source", rid))
            observed = dict(project=cutover.SUITE.casefold(), fullyQualifiedName=cutover.CLASS + ".OneCase",
                            test=cutover.CLASS + ".OneCase", outcome="passed")
            self.assertTrue(verify([observed], inventory, "source", rid))
            with self.assertRaisesRegex(ValueError, "case disposition"):
                verify([], inventory, "source", "linux-x64")
            inventory["projects"].append(dict(path="Builder.Tests.csproj", owner="younics.sunder.package.maker"))
            self.assertTrue(all("Builder" in error for error in verify([], inventory, "source", rid)))
            self.assertTrue(verify([], inventory, "source", rid))

    def test_cutover_disposition_is_exactly_bound_and_later_versions_never_enter_it(self):
        def plan(version="2.0.0"):
            return dict(schemaVersion=2, registryOrigin="https://hub.sunderapp.io", lockSetSha256=cutover.LOCKS,
                        packages=[dict(packageId=key, version=version, selected=True)
                                  for key in sorted(cutover.r.FAMILY_PACKAGE_IDS)])

        self.assertTrue(cutover.applies(plan()))
        for version in ("2.0.1", "2.1.0", "3.0.0", "2.0.0-rc.1"):
            self.assertFalse(cutover.applies(plan(version)), version)
        partial = plan()
        partial["packages"][0]["selected"] = False
        self.assertFalse(cutover.applies(partial))
        mixed = plan()
        mixed["packages"][0]["version"] = "2.0.1"
        self.assertFalse(cutover.applies(mixed))

        source = "a" * 40
        objects = [f"{i:040x}".encode() for i in range(1, len(cutover.SCOPE) + 1)]
        fingerprint = cutover.hashlib.sha256(b"".join(objects)).hexdigest()
        maker = [b"f" * 40, *objects[1:]]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "release-plan.json"
            path.write_text("{}")
            with patch.object(selection, "load", return_value=plan()) as load, \
                 patch.object(cutover, "SCOPE_SHA256", fingerprint), \
                 patch.object(cutover, "MAKER_UPDATER_SCOPE_SHA256", cutover.hashlib.sha256(b"".join(maker)).hexdigest()):
                def git(tree=objects, dirty=b""):
                    return [source.encode(), b"\n".join(tree), dirty]

                with patch.object(cutover.r, "git", side_effect=git()):
                    result = cutover.disposition(ROOT, path, "win-arm64", [cutover.OWNER], "source")
                self.assertEqual(dict(className=cutover.CLASS, caseCount=34, rid="win-arm64",
                                      status="not-selected", planSha256=cutover.r.file_sha(path)), result)
                load.assert_called_once_with(path, ROOT, source)
                for rid in cutover.RIDS:
                    with patch.object(cutover.r, "git", side_effect=git(maker)):
                        disposition = cutover.disposition(ROOT, path, rid, [cutover.OWNER], "source")
                    self.assertEqual(dict(result, rid=rid), disposition)
                for steps in (git(dirty=b" M dirty"), git(objects[::-1]), git(maker, dirty=b"?? extra"), git([*maker[:-1], b"e"*40])):
                    with patch.object(cutover.r, "git", side_effect=steps), self.assertRaisesRegex(ValueError, "scope changed"):
                        cutover.disposition(ROOT, path, "win-x64", [cutover.OWNER], "source")
                with patch.object(selection, "load", return_value=dict(plan(), lockSetSha256="a"*64)), \
                     patch.object(cutover.r, "git", side_effect=git(maker)), self.assertRaisesRegex(ValueError, "scope changed"):
                    cutover.disposition(ROOT, path, "win-x64", [cutover.OWNER], "source")
                for rid, packages, mode in (("win-x64", ["younics.ai.agent"], "source"),
                                            ("osx-arm64", [cutover.OWNER], "source"),
                                            ("win-x64", [cutover.OWNER], "package")):
                    with patch.object(cutover.r, "git", side_effect=git()), self.assertRaisesRegex(ValueError, "Windows Local"):
                        cutover.disposition(ROOT, path, rid, packages, mode)


if __name__ == "__main__":
    unittest.main()
