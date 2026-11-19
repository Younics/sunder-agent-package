"""Probe two macOS failure boundaries; never produce release qualification."""
import argparse
from pathlib import Path, PurePosixPath
import os
import platform
import shutil
import subprocess
import sys
import zipfile

source = Path.cwd().resolve()
diagnostic = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(source / "scripts/ci"))
import agent_ci as ci
import test_lanes

PROJECT = "Sunder.Package.Agent.Integration.Tests"
TEST_ROOT = f"tests/{PROJECT}"
BASE = "181f3ec7c0e0622a858d2bd1b71fca98c75c31ff"
CORE = "5a51f2040634bfef166516b04342f5115fb1be6a"
PROFILES = {
    "osx-arm64-memory": (
        "arm64", 14559122330, "8ac79107fd43d619fc46c258e20a53be086e5e1f2e71e570c18a267edfdc8964",
        "AgentSourceArtifactMigrationTests.Diagnostic_MemoryArtifact_StatusDelivery",
        ("AgentSourceArtifactMigrationTests.MemoryModels.cs", "AgentSourceArtifactMigrationTests.DiagnosticMemoryStatus.cs")),
    "osx-x64-owner": (
        "x86_64", 14560122175, "b9a89bff126e5fd8f0885609cbffbc49f109bb04dcb769b1f6ae9b3186232906",
        "AgentOwnerSourceMigrationTests.OwnerSource_MatchesCurrentContractsWithRoleHandoff",
        ("AgentOwnerSourceMigrationTests.StackImport.cs",)),
}


def acquire(artifact, expected, directory):
    archive = directory.with_suffix(".zip")
    with archive.open("xb") as output:
        subprocess.run(["gh", "api", f"repos/Younics/sunder-agent-package/actions/artifacts/{artifact}/zip"],
                       stdout=output, check=True)
    if ci.inputs.file_sha(archive) != expected:
        raise ValueError("Pinned archive hash differs")
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        if len(names) != len(set(names)) or any(
            PurePosixPath(name).is_absolute() or ".." in PurePosixPath(name).parts or "\\" in name or ":" in name for name in names
        ):
            raise ValueError("Unexpected archive paths")
        package.extractall(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", choices=PROFILES)
    parser.add_argument("candidate")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    evidence = output / "evidence"
    evidence.mkdir(parents=True)
    arch, tools_id, tools_digest, suffix, overlays = PROFILES[args.profile]
    core = source.parent / "sunder-core"
    if sys.platform != "darwin" or platform.machine() != arch:
        raise ValueError("Incorrect native diagnostic runner")
    if args.candidate != BASE or ci.head(source) != BASE or ci.head(core) != CORE:
        raise ValueError("Unexpected candidate/Core checkout")
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=source):
        raise ValueError("Unexpected source changes before diagnostic overlay")
    acquire(14561616437, "6a599ffa502387724d77cc01ef916b31f00d47ff7ed0223bdca63184a2dc2dbd", output / "inputs")
    acquire(tools_id, tools_digest, output / "tools")
    for key in ("GH_TOKEN", "GITHUB_TOKEN"):
        os.environ.pop(key, None)
    changes = {}
    for name in overlays:
        relative = Path(TEST_ROOT) / name
        authored = source / relative
        before = ci.inputs.file_sha(authored) if authored.is_file() else None
        shutil.copyfile(diagnostic / relative, authored)
        changes[relative.as_posix()] = dict(before=before, after=ci.inputs.file_sha(authored))
    receipt = dict(scope="diagnostic only, not qualification", candidate=BASE, core=CORE, diagnostic=ci.head(diagnostic),
                   profile=args.profile, nativeArchitecture=arch, overlay=changes,
                   dependencyArtifact=14561616437, toolsArtifact=tools_id, test="Sunder.Package.Agent.Tests." + suffix)
    (evidence / "recipe.json").write_bytes(ci.inputs.canonical(receipt) + b"\n")

    project = f"{TEST_ROOT}/{PROJECT}.csproj"
    prepared = output / "prepared"
    ci.configure_console()
    ci.prepare(argparse.Namespace(source=source, core=core, work=prepared, mode="source", packages=None,
                                  project=[project], inputs=output / "inputs", candidate=None, catalog=None, tools=output / "tools"))
    state = prepared / "ci-state.json"
    shutil.copyfile(state, evidence / "ci-state.json")
    status = ci.execute(state, ["dotnet", "build", project, "-c", "Release", "--no-restore", f"-bl:{evidence / 'build.binlog'}"],
                        log=evidence / "build.log")
    if status:
        return status

    results = evidence / "test-results"
    results.mkdir()
    os.environ.update(SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES="1", SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION="1",
                      SUNDER_AGENT_OWNER_TEST_TRACE=str(evidence / "owner-phase.log"),
                      SUNDER_AGENT_OWNER_COMMAND_TRACE_DIR=str(evidence / "owner-commands"),
                      SUNDER_AGENT_MEMORY_STATUS_TRACE=str(evidence / "memory-status.tsv"),
                      SUNDER_AGENT_SOURCE_ARTIFACT_COMMAND_EVIDENCE=str(evidence / "source-commands"))
    base = ["dotnet", "test", project, "-c", "Release", "--no-build", "--no-restore", "--filter", "FullyQualifiedName=" + receipt["test"]]
    log = results / (PROJECT + ".discovery.log")
    if ci.execute(state, [*base, "--list-tests", "--verbosity", "quiet"], log=log):
        return 1
    cases = test_lanes.discovered_tests(log.read_text(encoding="utf-8"))
    if cases != [receipt["test"]]:
        raise ValueError("Expected one exact diagnostic case")
    discovery = dict(schemaVersion=1, project=PROJECT, filter=base[-1], report=PROJECT + ".trx", tests=cases, prerequisites={})
    (results / (PROJECT + ".discovery.json")).write_bytes(ci.inputs.canonical(discovery) + b"\n")
    watchdog = ["--blame"] if args.profile == "osx-arm64-memory" else ["--blame-hang", "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "mini"]
    status = ci.execute(state, [*base, "--logger", f"trx;LogFileName={PROJECT}.trx", "--results-directory", str(results),
                                "--diag", str(results / (PROJECT + ".vstest.log")), *watchdog], log=results / (PROJECT + ".run.log"))
    for path, identity in changes.items():
        if ci.inputs.file_sha(source / path) != identity["after"]:
            raise ValueError("Diagnostic source changed while testing")
    if not status:
        rows = test_lanes.project_results(results, PROJECT, discovery)
        if [(row["test"], row["outcome"]) for row in rows] != [(receipt["test"], "passed")]:
            raise ValueError("Diagnostic pass does not match exact discovery")
    return status


if __name__ == "__main__":
    raise SystemExit(main())
