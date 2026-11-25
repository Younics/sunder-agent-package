"""Authoring must use an import-free project and admit only exact reproduced bytes."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import patch
from zipfile import ZipFile

SCRIPTS = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SCRIPTS / "ci"))
import owner_authoring as author
from owner_authoring import inputs

fixture_setup = runpy.run_path(str(Path(__file__).with_name("release-inputs.fixture.py")))["ColdInputsTests"].setUp


class HostedAttemptTests(unittest.TestCase):
    def test_authoring_uses_the_complete_unchanged_attempt(self):
        def list_page(route):
            page = int(route.rsplit("=", 1)[-1])
            return dict(total_count=45, jobs=[dict(id=value) for value in range((page - 1) * 20 + 1, min(45, page * 20) + 1)])

        with patch.object(author.hosted, "api_json", side_effect=list_page):
            self.assertEqual(list(range(1, 46)), [job["id"] for job in author.hosted.attempt_jobs(17, 5)])

        def duplicate(route):
            result = list_page(route)
            if route.endswith("page=2"):
                result["jobs"][0]["id"] = 1
            return result

        with patch.object(author.hosted, "api_json", side_effect=duplicate), self.assertRaisesRegex(ValueError, "Duplicate Actions job"):
            author.hosted.attempt_jobs(17, 5)


class OwnerAuthoringTests(unittest.TestCase):
    def setUp(self):
        fixture_setup(self)
        self.owner = self.rows[0]["packageId"]
        self.consumer = "younics.ai.agent.execution.local"
        command = ["git", "-C", str(self.source)]
        subprocess.run([*command, "init", "-q"], check=True)
        subprocess.run([*command, "add", "--all"], check=True)
        subprocess.run([*command, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                        "-c", "commit.gpgSign=false", "commit", "-qm", "Import-free fixture"], check=True)
        self.source_sha = subprocess.check_output([*command, "rev-parse", "HEAD"], text=True).strip()
        self.output = self.root / "authoring"
        for name, patched in (
            ("evaluated", patch.object(author.agent_selection, "evaluated_packages")),
            ("graph", patch.object(author.project_scope, "packages", return_value=[self.owner])),
        ):
            setattr(self, name, patched.start())
            self.addCleanup(patched.stop)

    def prepare(self, package=None):
        return author.prepare(self.source, package or self.owner, self.source_sha, self.output)

    def test_uses_only_the_published_catalog_and_scoped_locks(self):
        with patch.object(author.dependency_inputs, "releases") as releases, \
             patch.object(author.dependency_inputs.public_platform_catalog, "download", return_value=self.catalog_path.read_bytes()) as catalog, \
             redirect_stdout(io.StringIO()):
            result = self.prepare()
        self.assertEqual(self.owner, result["package"])
        value = inputs.verify(self.source, self.output, result["sha256"], self.source_sha, packages=[self.owner])
        self.assertEqual([], value["candidates"])
        self.assertEqual([self.owner], value["packageIds"])
        self.assertEqual([self.rows[0]["projectPath"]], self.graph.call_args.args[1])
        self.evaluated.assert_called_once_with(self.source.resolve(), [self.owner])
        releases.assert_not_called()
        catalog.assert_called_once()
        with self.assertRaisesRegex(ValueError, "does not cover"):
            inputs.verify(self.source, self.output, result["sha256"], self.source_sha, packages=[self.consumer])

    def test_cannot_author_from_a_consumer_or_implicit_catalog(self):
        with patch.object(author.dependency_inputs, "releases", return_value=[]) as releases, \
             patch.object(author.dependency_inputs.public_platform_catalog, "download",
                          side_effect=subprocess.CalledProcessError(22, "curl")) as catalog:
            with self.assertRaisesRegex(ValueError, "import-free"):
                self.prepare(self.consumer)
            with self.assertRaisesRegex(ValueError, "cannot reach"):
                with patch.object(author.project_scope, "packages", return_value=[self.owner, self.consumer]):
                    self.prepare()
            releases.assert_not_called()
            catalog.assert_not_called()
            with self.assertRaises(subprocess.CalledProcessError):
                self.prepare()
            catalog.assert_called_once()

    def test_handoff_reproduces_raw_owner_bytes_not_just_the_locked_inventory(self):
        transport = self.root / "authoring.zip"
        with ZipFile(transport, "w") as archive:
            archive.write(self.archive, self.archive.name)
        self.assertTrue(author.same_archive(transport, self.archive))
        self.archive.write_bytes(self.archive.read_bytes() + b"different ZIP bytes")
        self.assertFalse(author.same_archive(transport, self.archive))


if __name__ == "__main__":
    unittest.main()
