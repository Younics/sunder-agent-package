#!/usr/bin/env python3
"""Explicit, serialized preparation of the Main tests' generated provider module.

Run only after the parent releases the normal Core producer inputs, before tests.
This uses the existing AgentSourceArtifactTestTask authoring pipeline. It never
builds Core or Agent product projects and never edits their source or locks.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import subprocess
import tempfile
import time
from xml.sax.saxutils import quoteattr


FAMILIES = [
    ('Chat', 'chat.provider', 'chatProvider'),
    ('ToolSource', 'tool.source', 'toolSource'),
    ('ExecutionTarget', 'execution.target', 'executionTarget'),
    ('RuntimeCatalog', 'runtime.catalog', 'runtimeCatalog'),
    ('Embedding', 'embedding.provider', 'embeddingProvider'),
    ('SessionCleaner', 'session.cleaner', 'sessionCleaner'),
    ('DurableLifecycle', 'durable.lifecycle.observer', 'durableLifecycle'),
    ('PromptContext', 'prompt.context.contributor', 'promptContext'),
    ('SystemPrompt', 'system.prompt.contributor', 'systemPrompt'),
    ('Permission', 'permission.surface', 'permission'),
    ('ProfileCapability', 'profile.capability.consumer', 'profileCapability'),
    ('SelectableCapability', 'selectable.capability.provider', 'selectableCapability'),
    ('WorkspaceEditor', 'workspace.editor', 'workspaceEditor'),
    ('WorkspacePath', 'workspace.path.migrator', 'workspacePath'),
    ('BehaviorLoop', 'behavior.loop', 'behaviorLoop'),
    ('ChildRun', 'child.run.executor', 'childRun'),
    ('ObservedTool', 'tool.execution.observed', 'observedTool'),
    ('WorkspaceResolver', 'workspace.execution.resolver', 'workspaceResolver'),
]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


HASH_FIELDS = ('candidateSha256', 'sdkSha256', 'workerSdkSha256', 'driverSha256',
               'taskSha256', 'diAbstractionsSha256', 'moduleSha256', 'receiptSha256', 'seedLockSha256', 'platformCatalogSha256')
SCHEMA_VERSION = 2
COMMAND_LABELS = ['00-restore', '01-accept', '02-freeze', '03-add'] + [
    f'{number:02d}-implement-{name}' for number, (name, _, _) in enumerate(FAMILIES, 4)
] + [f'{len(FAMILIES) + 4:02d}-build']


def validate_manifest(manifest, expected_hashes, expected_sources):
    if (set(manifest) != {'schemaVersion', 'fixtureSources', 'commands', *HASH_FIELDS}
            or type(manifest['schemaVersion']) is not int or manifest['schemaVersion'] != SCHEMA_VERSION):
        raise ValueError('Unexpected prepared manifest schema')
    if set(expected_hashes) != set(HASH_FIELDS):
        raise ValueError('Incomplete expected input/output identities')
    for field, expected in expected_hashes.items():
        if len(expected) != 64 or any(c not in '0123456789abcdef' for c in expected) or manifest[field] != expected:
            raise ValueError(f'Prepared identity mismatch: {field}')
    if set(expected_sources) != {name for name, _, _ in FAMILIES} or manifest['fixtureSources'] != expected_sources:
        raise ValueError('Prepared fixture source inventory mismatch')
    if any(not isinstance(value, str) or len(value) != 64 or any(c not in '0123456789abcdef' for c in value)
           for value in expected_sources.values()):
        raise ValueError('Invalid fixture source identity')
    commands = manifest['commands']
    if not isinstance(commands, list) or len(commands) != len(COMMAND_LABELS):
        raise ValueError('Incomplete preparation commands')
    for record, label in zip(commands, COMMAND_LABELS):
        if set(record) != {'label', 'command', 'exitCode', 'elapsedSeconds'} or record['label'] != label:
            raise ValueError('Unexpected command record schema/order')
        command = record['command']
        elapsed = record['elapsedSeconds']
        if (not isinstance(command, list) or len(command) < 2 or command[0] != 'dotnet'
                or not all(isinstance(arg, str) and arg for arg in command)
                or type(record['exitCode']) is not int or record['exitCode'] != 0
                or type(elapsed) not in (int, float) or not math.isfinite(elapsed) or elapsed < 0):
            raise ValueError('Invalid or unsuccessful preparation command')


def validate_output(output, repository, core_root):
    output = output.resolve()
    if output.exists() or not output.parent.is_dir():
        raise ValueError('Preparation requires a fresh directory under an existing dedicated temporary parent')
    if not output.is_relative_to(Path(tempfile.gettempdir()).resolve()):
        raise ValueError('Preparation output must be in temporary storage')
    if any(output.is_relative_to(root.resolve()) for root in (repository, core_root)):
        raise ValueError('Preparation output cannot be inside a repository')
    for parent in output.parents:
        if any((parent / marker).exists() for marker in ('.git', 'prepared-inputs.json', 'Imported.csproj')):
            raise ValueError('Preparation output cannot be nested inside a checkout or prepared module')
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('candidate', 'driver', 'sdk', 'worker-sdk', 'di-abstractions', 'platform-catalog'):
        parser.add_argument('--' + name, type=Path, required=True)
        parser.add_argument('--' + name + '-sha256', required=True)
    parser.add_argument('--task-sha256', required=True)
    parser.add_argument('--seed-lock-sha256', required=True)
    parser.add_argument('--core-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    fixtures = repository / 'tests/Sunder.Package.Agent.Tests/Fixtures'
    inputs = {}
    for name in ('candidate', 'driver', 'sdk', 'worker_sdk', 'di_abstractions', 'platform_catalog'):
        path = getattr(args, name).resolve(strict=True)
        expected = getattr(args, name + '_sha256').lower()
        if digest(path) != expected:
            raise ValueError(f'Unqualified {name}: {path}')
        setattr(args, name, path)
        inputs[path] = expected
    task = args.driver.parent / 'Sunder.Package.Build.dll'
    if digest(task) != args.task_sha256.lower():
        raise ValueError(f'Unqualified Core task beside driver: {task}')
    inputs[task] = args.task_sha256.lower()
    sources = {name: fixtures / f'RegressionTestGeneratedProviders.{name}.cs.txt' for name, _, _ in FAMILIES}
    source_hashes = {name: digest(path) for name, path in sources.items()}
    inputs.update({path: source_hashes[name] for name, path in sources.items()})
    seed_lock = args.core_root.resolve(strict=True) / 'specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json'
    if digest(seed_lock) != args.seed_lock_sha256.lower():
        raise ValueError(f'Unqualified seed lock: {seed_lock}')
    inputs[seed_lock] = args.seed_lock_sha256.lower()
    output = validate_output(args.output, repository, args.core_root)
    output.mkdir()
    records = []

    def verify_inputs():
        for path, expected in inputs.items():
            if digest(path) != expected:
                raise ValueError(f'Preparation input changed: {path}')

    def run(label, *arguments):
        verify_inputs()
        command = ['dotnet', *arguments, '-p:BuildProjectReferences=false', '-p:Configuration=Debug',
                   '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false',
                   '-p:ImportDirectoryPackagesProps=false',
                   '-nodeReuse:false', '-nologo']
        started = time.monotonic()
        with (output / f'{label}.log').open('x') as log:
            process = subprocess.run(command, cwd=output, stdout=log, stderr=subprocess.STDOUT)
        record = dict(label=label, command=command, exitCode=process.returncode, elapsedSeconds=time.monotonic() - started)
        (output / f'{label}.result.json').write_text(json.dumps(record, indent=2) + '\n')
        records.append(record)
        verify_inputs()
        if process.returncode:
            raise RuntimeError(f'Preparation failed at {label}; see {output / (label + ".log")}')

    references = '\n'.join(f'<Reference Include={quoteattr(path.stem)} HintPath={quoteattr(str(path))} />'
                           for path in (args.sdk, args.worker_sdk, args.di_abstractions))
    project = f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>RegressionTestGeneratedProviders</AssemblyName>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>{references}</ItemGroup>
  <UsingTask TaskName="Sunder.Package.Build.Tests.AgentSourceArtifactTestTask" AssemblyFile={quoteattr(str(args.driver))} />
  <UsingTask TaskName="Sunder.Package.Build.Tasks.ManageSunderPackageApiTask" AssemblyFile={quoteattr(str(task))} />
  <Target Name="FixtureOperation" BeforeTargets="CoreCompile" DependsOnTargets="ResolveReferences;GenerateGlobalUsings">
    <PropertyGroup><Operation Condition="'$(Operation)' == ''">Generate</Operation></PropertyGroup>
    <PropertyGroup><ApiPresence Condition="'$(Operation)' == 'Add'">required</ApiPresence></PropertyGroup>
    <ItemGroup><_Source Include="@(Compile)" Condition="'%(Compile.AutoGen)' != 'true'" /></ItemGroup>
    <ManageSunderPackageApiTask Condition="'$(Operation)' == 'Add' or '$(Operation)' == 'Implement'"
      Operation="$(Operation)" ProjectDirectory="$(MSBuildProjectDirectory)" PackageId="sunder.package.agent"
      PackageVersion="2.0.0" VersionRange="&gt;=2.0.0 &lt;3.0.0" Presence="$(ApiPresence)"
      Candidate={quoteattr(str(args.candidate))} PlatformCatalogPath={quoteattr(str(args.platform_catalog))}
      ContractCacheRoot={quoteattr(str(output / 'cache'))} ContractId="$(ContractId)" LocalName="$(ProviderKey)"
      AssemblyName="$(AssemblyName)" ProjectIdentity="Imported.csproj" Configuration="Debug" TargetFramework="net10.0"
      OutputType="Library" IntermediateOutputPath="$(IntermediateOutputPath)" SourceFiles="@(_Source)" ReferencePaths="@(ReferencePath)" />
    <AgentSourceArtifactTestTask Condition="'$(Operation)' != 'Add' and '$(Operation)' != 'Implement'"
      Operation="$(Operation)" ProjectDirectory="$(MSBuildProjectDirectory)"
      IntermediateOutputPath="$(IntermediateOutputPath)" AssemblyName="$(AssemblyName)" ProjectIdentity="Imported.csproj" OutputType="Library"
      PackageId="example.agent.regression.providers" CacheRoot={quoteattr(str(output / 'cache'))}
      Candidate={quoteattr(str(args.candidate))} ContractId="$(ContractId)" LocalName="$(ProviderKey)"
      SourceFiles="@(_Source)" ReferencePaths="@(ReferencePath)">
      <Output TaskParameter="CompileItems" ItemName="Compile" />
    </AgentSourceArtifactTestTask>
  </Target>
</Project>
'''
    (output / 'Imported.csproj').write_text(project)
    (output / 'sunder-contracts.lock.json').write_bytes(seed_lock.read_bytes())
    source = output / 'Source.cs'
    source.write_text('public sealed class Seed;\n')
    run('00-restore', 'restore', 'Imported.csproj', '--disable-parallel')
    for number, operation in enumerate(('Accept', 'Freeze', 'Add'), 1):
        run(f'{number:02d}-{operation.lower()}', 'msbuild', 'Imported.csproj', '-t:FixtureOperation', '-p:Operation=' + operation,
            '-p:ProviderKey=agent')
    for number, (name, contract, key) in enumerate(FAMILIES, 4):
        source.write_bytes(sources[name].read_bytes())
        run(f'{number:02d}-implement-{name}', 'msbuild', 'Imported.csproj', '-t:FixtureOperation',
            '-p:Operation=Implement', '-p:ContractId=sunder.agent.' + contract, '-p:ProviderKey=' + key)
    source.unlink()
    for name, path in sources.items():
        (output / (name + '.cs')).write_bytes(path.read_bytes())
    run(COMMAND_LABELS[-1], 'build', 'Imported.csproj', '--no-restore')
    module = output / 'bin/Debug/net10.0/RegressionTestGeneratedProviders.dll'
    receipt = output / 'obj/Debug/net10.0/sunder-rpc/authoring-receipt.v1.json'
    verify_inputs()
    manifest = dict(schemaVersion=SCHEMA_VERSION, candidateSha256=digest(args.candidate), sdkSha256=digest(args.sdk),
                    workerSdkSha256=digest(args.worker_sdk), driverSha256=digest(args.driver), taskSha256=digest(task),
                    diAbstractionsSha256=digest(args.di_abstractions), moduleSha256=digest(module),
                    receiptSha256=digest(receipt), seedLockSha256=digest(seed_lock), platformCatalogSha256=digest(args.platform_catalog),
                    fixtureSources=source_hashes, commands=records)
    expected_hashes = {field: manifest[field] for field in HASH_FIELDS}
    validate_manifest(manifest, expected_hashes, source_hashes)
    manifest_path = output / 'prepared-inputs.json'
    manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
    environment = {}
    for name, path in (('MODULE', module), ('INPUT_MANIFEST', manifest_path), ('TASK', task), ('RECEIPT', receipt),
                       ('SEED_LOCK', seed_lock), ('PLATFORM_CATALOG', args.platform_catalog)):
        environment['SUNDER_AGENT_SHARED_PROVIDER_FIXTURE_' + name] = str(path)
        environment['SUNDER_AGENT_SHARED_PROVIDER_FIXTURE_' + name + '_SHA256'] = digest(path)
    environment['SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE'] = str(args.candidate)
    environment['SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE_SHA256'] = digest(args.candidate)
    (output / 'test-environment.json').write_text(json.dumps(environment, indent=2) + '\n')
    verify_inputs()
    expected_hashes.update(moduleSha256=digest(module), receiptSha256=digest(receipt))
    validate_manifest(json.loads(manifest_path.read_text()), expected_hashes, source_hashes)
    print(json.dumps(environment, indent=2))


if __name__ == '__main__':
    main()
