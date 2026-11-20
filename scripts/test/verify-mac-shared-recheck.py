"""Run both complete macOS shared-source lanes after focused verification."""
import argparse
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import subprocess
import sys
import zipfile

SOURCE = Path.cwd().resolve()
DIAGNOSTIC = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(SOURCE / "scripts/ci"))
import agent_ci as ci

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


def bind_inputs(root, candidate):
    original, prepared = root / "original-inputs", root / "inputs"
    original_sha = ci.inputs.file_sha(original / ci.inputs.MANIFEST)
    intake = ci.inputs.verify(SOURCE, original, original_sha, BASE, CORE)
    if intake["lockSetSha256"] != "80871aed82e828c552288bfd210fac2558cfa44f50ca93e14e8187dfff9348c3":
        raise ValueError("Incorrect all-family lock set")
    if len(intake["packageIds"]) != 15:
        raise ValueError("Expected the full authored lock-set scope")
    prepared.mkdir()
    index = dict(schemaVersion=3, coreCommit=CORE, packageIds=intake["packageIds"],
                 candidates=[item["asset"] for item in intake["candidates"]], platformCatalog=intake["platformCatalog"]["asset"])
    for item in [*index["candidates"], index["platformCatalog"]]:
        name = item["name"]
        shutil.copyfile(original / name, prepared / name)
    (prepared / "ci-inputs.json").write_bytes(ci.inputs.canonical(index) + b"\n")
    sealed = ci.inputs.fetch(SOURCE, prepared, candidate, CORE, packages=intake["packageIds"], prepared=True)
    if sealed != dict(intake, artifactSourceCommit=candidate):
        raise ValueError("Scoped input reseal must change only the Agent source binding")
    return dict(previous=original_sha, candidate=ci.inputs.file_sha(prepared / ci.inputs.MANIFEST))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
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
    # MSBuild imports environment variables as case-insensitive properties.
    # Do not allow workflow routing to override the test's fresh archive path.
    if any(key.casefold() == "candidate" for key in os.environ):
        raise ValueError("Diagnostic environment must not supply the MSBuild Candidate property")
    acquire("inputs", root / "original-inputs")
    acquire(args.rid, root / "tools")
    for key in ("GH_TOKEN", "GITHUB_TOKEN", "SUNDER_MAC_RECHECK_SHA", "SUNDER_MAC_RECHECK_RID"):
        os.environ.pop(key, None)
    owner = root / "original-inputs/younics.ai.agent.2.0.0.sunderpkg"
    if (owner.stat().st_size, ci.inputs.file_sha(owner)) != (
            32002626, "6af4a0de4a0ef336d0ae647b2fcd3296233016ee9fba618645bc270e4ef453fb"):
        raise ValueError("Incorrect approved owner bytes")
    input_manifests = bind_inputs(root, args.candidate)
    os.environ["SUNDER_AGENT_OWNER_TEST_TRACE"] = str(evidence / "owner-phase.log")
    ci.configure_console()
    recipe = dict(scope="only macOS source; not all-fifteen qualification", stage="shared", rid=args.rid,
                  candidate=args.candidate, diagnostic=ci.head(DIAGNOSTIC), core=CORE, architecture=arch,
                  inputsArtifact=PINS["inputs"][0], toolsArtifact=PINS[args.rid][0], case=None, overlay=None,
                  inputManifests=input_manifests,
                  changed={path: ci.inputs.file_sha(SOURCE / path) for path in CHANGED})
    (evidence / "recipe.json").write_bytes(ci.inputs.canonical(recipe) + b"\n")
    result = subprocess.run([sys.executable, "-B", "scripts/ci/agent_ci.py", "validate",
                             "--mode", "source", "--shared", "--stress", "--core", str(core),
                             "--inputs", str(root / "inputs"), "--tools", str(root / "tools")], check=False).returncode
    if git("status", "--porcelain"):
        raise ValueError("Authored checkout changed while testing")
    return result


if __name__ == "__main__":
    raise SystemExit(main())
