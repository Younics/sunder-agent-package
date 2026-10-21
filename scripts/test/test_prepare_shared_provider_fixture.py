"""Generation-free checks for preparation isolation and evidence rejection."""
import copy
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('prepare_fixture', Path(__file__).with_name('prepare-shared-provider-fixture.py'))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class PreparationEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.no_generation = patch.object(prepare.subprocess, 'run', side_effect=AssertionError('Generation is forbidden'))
        self.no_generation.start()
        self.addCleanup(self.no_generation.stop)
        self.hashes = {field: 'a' * 64 for field in prepare.HASH_FIELDS}
        self.sources = {name: 'b' * 64 for name, _, _ in prepare.FAMILIES}
        self.manifest = dict(schemaVersion=prepare.SCHEMA_VERSION, **self.hashes, fixtureSources=self.sources.copy(), commands=[
            dict(label=label, command=['dotnet', 'msbuild'], exitCode=0, elapsedSeconds=0.1)
            for label in prepare.COMMAND_LABELS
        ])

    def validate(self, manifest):
        prepare.validate_manifest(manifest, self.hashes, self.sources)

    def test_complete_evidence_is_accepted(self):
        self.validate(self.manifest)

    def test_manifest_is_closed_and_version_is_integer(self):
        for field in self.manifest:
            with self.subTest(missing=field):
                changed = copy.deepcopy(self.manifest)
                del changed[field]
                with self.assertRaises(ValueError):
                    self.validate(changed)
        for field, value in [('extra', None), ('schemaVersion', True), ('schemaVersion', 1), ('schemaVersion', 3)]:
            changed = copy.deepcopy(self.manifest)
            changed[field] = value
            with self.assertRaises(ValueError):
                self.validate(changed)

    def test_every_input_and_output_identity_is_bound(self):
        for field in prepare.HASH_FIELDS:
            with self.subTest(field=field):
                changed = copy.deepcopy(self.manifest)
                changed[field] = 'c' * 64
                with self.assertRaises(ValueError):
                    self.validate(changed)

    def test_seventeen_current_providers_and_resolver_operation_are_required(self):
        self.assertEqual(17, len(prepare.FAMILIES))
        self.assertNotIn('WorkspacePath', {name for name, _, _ in prepare.FAMILIES})
        self.assertEqual(('WorkspaceResolver', 'workspace.execution.resolver', 'workspaceResolver'), prepare.FAMILIES[-1])
        self.assertEqual(22, len(prepare.COMMAND_LABELS))
        self.assertEqual(['20-implement-WorkspaceResolver', '21-build'], prepare.COMMAND_LABELS[-2:])
        for change_source in (True, False):
            stale = copy.deepcopy(self.manifest)
            if change_source:
                del stale['fixtureSources']['WorkspaceResolver']
            else:
                stale['commands'].pop(-2)
            with self.assertRaises(ValueError):
                self.validate(stale)

    def test_source_graph_matches_loader_and_real_fixture_files(self):
        import re
        test_root = Path(__file__).resolve().parents[2] / 'tests/Sunder.Package.Agent.Tests'
        loader = (test_root / 'RegressionTestGeneratedProviders.cs').read_text()
        inventory = re.search(r'FixtureSources\s*=\s*\[(.*?)\];', loader, re.S).group(1)
        self.assertEqual([name for name, _, _ in prepare.FAMILIES], re.findall(r'"([A-Za-z]+)"', inventory))
        self.assertIn('Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32())', loader)
        self.assertEqual({f'RegressionTestGeneratedProviders.{name}.cs.txt' for name, _, _ in prepare.FAMILIES},
                         {path.name for path in (test_root / 'Fixtures').glob('RegressionTestGeneratedProviders.*.cs.txt')})
        for name, _, _ in prepare.FAMILIES:
            source = test_root / 'Fixtures' / f'RegressionTestGeneratedProviders.{name}.cs.txt'
            self.assertTrue(source.is_file(), source)
            self.assertIn(f'class {name}Provider', source.read_text())
        extension_loader = (test_root / 'AgentPublicSourceMigrationTests.ExtensionFixture.cs').read_text()
        self.assertIn('RegressionTestGeneratedProviders.CreatePreparedProvider', extension_loader)
        for forbidden in ('PackSunderPackage', 'BuildExtensionProvidersAsync', 'BuilderSourceMigrationTests.Run', 'Process.Start'):
            self.assertNotIn(forbidden, extension_loader)

    def test_exact_source_inventory_and_each_source_hash_are_bound(self):
        for name in self.sources:
            changed = copy.deepcopy(self.manifest)
            changed['fixtureSources'][name] = 'c' * 64
            with self.assertRaises(ValueError):
                self.validate(changed)
        for extra in (True, False):
            changed = copy.deepcopy(self.manifest)
            if extra:
                changed['fixtureSources']['Unknown'] = 'b' * 64
            else:
                del changed['fixtureSources']['Chat']
            with self.assertRaises(ValueError):
                self.validate(changed)

    def test_command_records_are_closed_ordered_and_successful(self):
        mutations = [lambda record: record.update(extra=True), lambda record: record.pop('command'),
                     lambda record: record.update(exitCode=1), lambda record: record.update(exitCode=False),
                     lambda record: record.update(elapsedSeconds=float('nan')),
                     lambda record: record.update(elapsedSeconds=-1), lambda record: record.update(command=['dotnet']),
                     lambda record: record.update(command=['shell', 'build']), lambda record: record.update(label='wrong')]
        for mutate in mutations:
            changed = copy.deepcopy(self.manifest)
            mutate(changed['commands'][0])
            with self.assertRaises(ValueError):
                self.validate(changed)
        changed = copy.deepcopy(self.manifest)
        changed['commands'].pop()
        with self.assertRaises(ValueError):
            self.validate(changed)

    def test_output_cannot_reuse_or_nest_in_protected_locations(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            repository, core = root / 'agent', root / 'core'
            repository.mkdir()
            core.mkdir()
            fresh = root / 'fresh'
            self.assertEqual(fresh, prepare.validate_output(fresh, repository, core))
            self.assertFalse(fresh.exists())
            prepared = root / 'prepared'
            prepared.mkdir()
            (prepared / 'prepared-inputs.json').write_text('{}')
            checkout = root / 'checkout'
            checkout.mkdir()
            (checkout / '.git').write_text('gitdir: elsewhere')
            alias = root / 'alias'
            alias.symlink_to(repository, target_is_directory=True)
            for output in (repository, repository / 'new', core / 'new', prepared / 'new', checkout / 'new',
                           alias / 'new', root / 'absent' / 'new', Path.home() / 'fixture-preparation-rejected'):
                with self.subTest(output=output), self.assertRaises(ValueError):
                    prepare.validate_output(output, repository, core)


if __name__ == '__main__':
    unittest.main()
