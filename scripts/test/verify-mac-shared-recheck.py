"""Recheck just the changed macOS boundaries, then their complete source lanes."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import platform
import subprocess
import sys
import zipfile

SOURCE = Path.cwd().resolve()
DIAGNOSTIC = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SOURCE / "scripts/ci"))
import agent_ci as ci
import test_lanes

BASE = "181f3ec7c0e0622a858d2bd1b71fca98c75c31ff"
CORE = "5a51f2040634bfef166516b04342f5115fb1be6a"
PINS = {
    "inputs": (14561616437, "6a599ffa502387724d77cc01ef916b31f00d47ff7ed0223bdca63184a2dc2dbd"),
    "osx-arm64": (14559122330, "8ac79107fd43d619fc46c258e20a53be086e5e1f2e71e570c18a267edfdc8964"),
    "osx-x64": (14560122175, "b9a89bff126e5fd8f0885609cbffbc49f109bb04dcb769b1f6ae9b3186232906"),
}
PROJECT = "Sunder.Package.Agent.Integration.Tests"
TEST_ROOT = f"tests/{PROJECT}"
CHANGED = [f"{TEST_ROOT}/AgentOwnerSourceMigrationTests.StackImport.cs",
           f"{TEST_ROOT}/AgentSourceArtifactMigrationTests.MemoryModels.cs"]


def acquire(pin, directory):
    artifact, digest = PINS[pin]
    archive = directory.with_suffix(".zip")
    with archive.open("xb") as output:
        subprocess.run(["gh", "api", f"repos/Younics/sunder-agent-package/actions/artifacts/{artifact}/zip"],
                       stdout=output, check=True)
    if ci.inputs.file_sha(archive) != digest:
        raise ValueError(f"Pinned archive differs: {artifact}")
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        if len(names) != len(set(names)) or any(
                PurePosixPath(name).is_absolute() or ".." in PurePosixPath(name).parts or "\\" in name or ":" in name
                for name in names):
            raise ValueError(f"Unexpected paths: {artifact}")
        package.extractall(directory)


def git(*args):
    return subprocess.check_output(["git", *args], cwd=SOURCE, text=True).strip()


def run_focus(args, root, evidence, core, case):
    project = f"{TEST_ROOT}/{PROJECT}.csproj"
    prepared = root / "prepared"
    ci.prepare(argparse.Namespace(source=SOURCE, core=core, work=prepared, mode="source", packages=None,
                                  project=[project], inputs=root / "inputs", candidate=None, catalog=None, tools=root / "tools"))
    state = prepared / "ci-state.json"
    status = ci.execute(state, ["dotnet", "build", project, "-c", "Release", "--no-restore", f"-bl:{evidence / 'build.binlog'}"],
                        log=evidence / "build.log")
    if status:
        return status
    tests = evidence / "test-results"
    tests.mkdir()
    os.environ.update(SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION="1", SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES="1",
                      SUNDER_AGENT_SOURCE_ARTIFACT_COMMAND_EVIDENCE=str(evidence / "source-commands"))
    base = ["dotnet", "test", project, "-c", "Release", "--no-build", "--no-restore", "--filter", f"FullyQualifiedName={case}"]
    log = tests / (PROJECT + ".discovery.log")
    status = ci.execute(state, [*base, "--list-tests", "--verbosity", "quiet"], log=log)
    if status:
        return status
    cases = test_lanes.discovered_tests(log.read_text(encoding="utf-8"))
    if cases != [case]:
        raise ValueError(f"Unexpected focused test selection: {cases}")
    discovery = dict(schemaVersion=1, project=PROJECT, filter=base[-1], report=PROJECT + ".trx", tests=cases, prerequisites={})
    (tests / (PROJECT + ".discovery.json")).write_bytes(ci.inputs.canonical(discovery) + b"\n")
    watchdog = ["--blame"] if args.rid == "osx-arm64" else ["--blame-hang", "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "mini"]
    status = ci.execute(state, [*base, "--logger", f"trx;LogFileName={PROJECT}.trx", "--results-directory", str(tests),
                                "--diag", str(tests / (PROJECT + ".vstest.log")), *watchdog], log=tests / (PROJECT + ".run.log"))
    if not status:
        rows = test_lanes.project_results(tests, PROJECT, discovery)
        if [(row["test"], row["outcome"]) for row in rows] != [(case, "passed")]:
            raise ValueError("The focused case did not pass exactly once")
    return status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["focused", "shared"])
    parser.add_argument("rid", choices=["osx-arm64", "osx-x64"])
    parser.add_argument("candidate")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = args.output.resolve()
    evidence = root / "evidence"
    evidence.mkdir(parents=True)
    core = SOURCE.parent / "sunder-core"
    arch = "arm64" if args.rid == "osx-arm64" else "x86_64"
    if sys.platform != "darwin" or platform.machine() != arch:
        raise ValueError("Incorrect native runner")
    if ci.head(core) != CORE or git("rev-parse", "HEAD") != args.candidate or git("rev-parse", "HEAD^") != BASE:
        raise ValueError("Incorrect candidate/Core checkout")
    if git("status", "--porcelain") or sorted(git("diff", "--name-only", BASE, "HEAD").splitlines()) != sorted(CHANGED):
        raise ValueError("Candidate did not contain exactly the two reviewed fixture changes")
    acquire("inputs", root / "inputs")
    acquire(args.rid, root / "tools")
    for key in ("GH_TOKEN", "GITHUB_TOKEN"):
        os.environ.pop(key, None)
    owner = root / "inputs/younics.ai.agent.2.0.0.sunderpkg"
    if (owner.stat().st_size, ci.inputs.file_sha(owner)) != (
            32002626, "6af4a0de4a0ef336d0ae647b2fcd3296233016ee9fba618645bc270e4ef453fb"):
        raise ValueError("Incorrect approved owner bytes")
    package_inputs = json.loads((root / "inputs/cold-inputs.json").read_text())
    if package_inputs["lockSetSha256"] != "80871aed82e828c552288bfd210fac2558cfa44f50ca93e14e8187dfff9348c3":
        raise ValueError("Incorrect all-family lock set")
    os.environ["SUNDER_AGENT_OWNER_TEST_TRACE"] = str(evidence / "owner-phase.log")
    ci.configure_console()
    case = overlay = None
    if args.stage == "focused":
        case = "Sunder.Package.Agent.Tests.AgentOwnerSourceMigrationTests.OwnerSource_MatchesCurrentContractsWithRoleHandoff"
        if args.rid == "osx-arm64":
            case = "Sunder.Package.Agent.Tests.AgentSourceArtifactMigrationTests.Diagnostic_MemoryArtifact_StatusDelivery"
            path = SOURCE / TEST_ROOT / "MacSharedRecheckMemory.cs"
            with path.open("xb") as dest:
                dest.write(Path(__file__).with_name("mac-shared-recheck-memory.cs.txt").read_bytes())
            overlay = dict(path=path.relative_to(SOURCE).as_posix(), sha256=ci.inputs.file_sha(path))
    recipe = dict(scope="only macOS source; not all-fifteen qualification", stage=args.stage, rid=args.rid,
                  candidate=args.candidate, diagnostic=ci.head(DIAGNOSTIC), core=CORE, architecture=arch,
                  inputsArtifact=PINS["inputs"][0], toolsArtifact=PINS[args.rid][0], case=case, overlay=overlay,
                  changed={path: ci.inputs.file_sha(SOURCE / path) for path in CHANGED})
    (evidence / "recipe.json").write_bytes(ci.inputs.canonical(recipe) + b"\n")
    if case:
        result = run_focus(args, root, evidence, core, case)
    else:
        result = subprocess.run([sys.executable, "-B", "scripts/ci/agent_ci.py", "validate",
                                 "--mode", "source", "--shared", "--stress", "--core", str(core),
                                 "--inputs", str(root / "inputs"), "--tools", str(root / "tools")], check=False).returncode
    if git("diff", "--name-only", "HEAD") or (overlay is None and git("status", "--porcelain")):
        raise ValueError("Authored checkout changed while testing")
    return result


if __name__ == "__main__":
    raise SystemExit(main())
