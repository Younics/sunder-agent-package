"""Diagnostic-only: verify both consumers of the transcript geometry test fixture."""
import argparse
from collections import Counter
from pathlib import Path, PurePosixPath
import os
import platform
import re
import subprocess
import sys
import zipfile

source = Path.cwd().resolve()
sys.path.insert(0, str(source / "scripts/ci"))
import agent_ci as ci
import test_lanes

projects = {
    "Sunder.Package.Agent.View.Tests": ("64ae62577c76154b9bebe5e45562fb578e82dd231ae72f144f2de0f019994470", 180),
    "Sunder.Package.Agent.Presentation.Tests": ("0fb99bb4e3bc641476c5c8c99cf2ad95e5a5edce291598593bc92859f7632b35", 124),
}


def acquire(artifact, expected, directory):
    archive = directory.with_suffix(".zip")
    with archive.open("xb") as output:
        subprocess.run(["gh", "api", f"repos/Younics/sunder-agent-package/actions/artifacts/{artifact}/zip"],
                       stdout=output, check=True)
    if ci.inputs.file_sha(archive) != expected:
        raise ValueError("Pinned input archive hash differs")
    with zipfile.ZipFile(archive) as package:
        members = [PurePosixPath(item.filename) for item in package.infolist()]
        if any(item.is_absolute() or ".." in item.parts or "\\" in str(item) or ":" in str(item) for item in members):
            raise ValueError("Invalid pinned archive member path")
        package.extractall(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("commit")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir()
    if sys.platform != "win32" or platform.machine().lower() not in ("amd64", "x86_64"):
        raise ValueError("Use a native Windows x64 runner")
    core = source.parent / "sunder-core"
    if not re.fullmatch(r"[a-f0-9]{40}", args.commit) or ci.head(source) != args.commit:
        raise ValueError("Candidate checkout does not match the exact requested commit")
    if ci.head(core) != "5a51f2040634bfef166516b04342f5115fb1be6a":
        raise ValueError("Core checkout differs")
    paths = {name: f"tests/{name}/{name}.csproj" for name in projects}
    acquire(14550325211, "6bc37c73fd358f8aaffda46dd469981f39bcce2a01dafa5accb9278509942cdc", output / "inputs")
    acquire(14543859063, "f2c96a40e3b8cc419e0d6bcf72b0f03a18065f5082a2fcdd602ca375365c0ed9", output / "tools")
    for key in ("GH_TOKEN", "GITHUB_TOKEN"):
        os.environ.pop(key, None)
    owner = output / "inputs/younics.ai.agent.2.0.0.sunderpkg"
    catalog = output / "inputs/sunder-platform-contracts.v1.json"
    if ci.inputs.file_sha(owner) != "6af4a0de4a0ef336d0ae647b2fcd3296233016ee9fba618645bc270e4ef453fb" or \
            ci.inputs.file_sha(catalog) != "7c79ee38af47a3b7c45fb9b9715233887a9f581bea354d6d874144e545fa6210":
        raise ValueError("Owner or catalog bytes differ")
    prepared = output / "prepared"
    ci.configure_console()
    ci.prepare(argparse.Namespace(source=source, core=core, work=prepared, mode="source",
        packages=None, project=list(paths.values()), inputs=None, candidate=[owner],
        catalog=catalog, tools=output / "tools"))
    state = prepared / "ci-state.json"
    build = ci.inputs.load(state)["buildProject"]
    status = ci.execute(state, ["dotnet", "build", build, "-c", "Release", "--no-restore",
        f"-bl:{output / 'build.binlog'}"], log=output / "build.log")
    if status:
        return status
    results = output / "tests"
    results.mkdir()
    os.environ.update(SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES="1", SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION="1")
    selection = "FullyQualifiedName!~" + test_lanes.POLICY["artifactProducer"]
    report = dict(scope="affected suites only, not qualification", candidate=args.commit, rid="win-x64", projects={})
    for name, project in paths.items():
        test_lanes.run_project(state, source, project,
            ["dotnet", "test", project, "-c", "Release", "--no-build", "--no-restore"], selection, results)
        discovered = ci.inputs.load(results / (name + ".discovery.json"))
        rows = test_lanes.project_results(results, name, discovered)
        outcomes = Counter(row["outcome"] for row in rows)
        identities = sorted(row["test"] for row in rows)
        actual = ci.inputs.sha(ci.inputs.canonical(identities) + b"\n")
        expected, passed = projects[name]
        if actual != expected or len(set(identities)) != passed or outcomes != Counter(passed=passed):
            raise ValueError(f"{name}: exact case inventory or outcomes differ")
        report["projects"][name] = dict(outcomes=dict(outcomes), identity=actual)
    (output / "summary.json").write_bytes(ci.inputs.canonical(report) + b"\n")
    return 0


raise SystemExit(main())
