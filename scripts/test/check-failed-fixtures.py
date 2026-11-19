"""Diagnostic-only: run three previously affected projects, without publishing."""
import argparse
from collections import Counter
from pathlib import Path, PurePosixPath
import os
import re
import subprocess
import sys
import zipfile

source = Path.cwd().resolve()
sys.path.insert(0, str(source / "scripts/ci"))
import agent_ci as ci
import test_lanes

profiles = {
    "linux-arm64-shared": ("Sunder.Package.Agent.Tests", 14502574216,
        "89bc1702d29d3b10b32d0f0bbb3752137c33b9559d078776e6611aed66eeb54d",
        "a6ca033e37208dbad75b4a09ee707897ef0e625bd1bd7440fd9b53f1fc3ecf02", 762, [
            "Sunder.Package.Agent.Execution.Local.Tests.LocalFileSystemSymlinkContainmentTests.ResolveFileResource_WindowsOutsideScopeUsesLexicalExclusion",
            "Sunder.Package.Agent.Tests.DockerFilesAcceptanceTests.RealDaemon_FilesReadWriteEditPatchAndDeleteUseTheVerifiedHostObject",
            "Sunder.Package.Agent.Execution.Local.Tests.LocalFileSystemSymlinkContainmentTests.ResolveFileResource_MacCaseAliasUsesOpenedConfiguredRootIdentity",
            "Sunder.Package.Agent.Execution.Local.Tests.LocalFileSystemSymlinkContainmentTests.ResolveFileResource_MacCaseAliasClassifiesMissingDescendants",
        ]),
    "win-arm64-memory": ("Sunder.Package.Agent.Memory.Semantic.Tests", 14502409525,
        "b3b338a9493ea4412e112b52d55d2d9915005b503b66e4e3f48d214af384e976",
        "ed1daf0aa350e4967547e13314a7b2237a8494affe1ca495d6e5bd4584d5ab26", 152, []),
    "osx-arm64-view": ("Sunder.Package.Agent.View.Tests", 14502039393,
        "05e23a6e9fcb26be4328462189757abe08852803e3651a4ed090f04341125b69",
        "64ae62577c76154b9bebe5e45562fb578e82dd231ae72f144f2de0f019994470", 180, []),
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
    parser.add_argument("profile", choices=profiles)
    parser.add_argument("commit")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir()
    core = source.parent / "sunder-core"
    if not re.fullmatch(r"[a-f0-9]{40}", args.commit) or ci.head(source) != args.commit:
        raise ValueError("Candidate checkout does not match the exact requested commit")
    if ci.head(core) != "5a51f2040634bfef166516b04342f5115fb1be6a":
        raise ValueError("Core checkout differs")
    name, tools_id, tools_digest, identity, passed, skipped = profiles[args.profile]
    project = f"tests/{name}/{name}.csproj"
    acquire(14503916919, "d03fb5b71ab46b9ae5d1a65b6f88f626c15fb04214cca3a586dc84e40a25396a", output / "inputs")
    acquire(tools_id, tools_digest, output / "tools")
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
        packages=None, project=[project], inputs=None, candidate=[] if args.profile == "osx-arm64-view" else [owner],
        catalog=catalog, tools=output / "tools"))
    state = prepared / "ci-state.json"
    status = ci.execute(state, ["dotnet", "build", project, "-c", "Release", "--no-restore",
        f"-bl:{output / 'build.binlog'}"], log=output / "build.log")
    if status:
        return status
    results = output / "tests"
    results.mkdir()
    os.environ.update(SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES="1", SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION="1")
    exclusions = [test_lanes.POLICY["artifactProducer"]]
    if args.profile == "linux-arm64-shared":
        exclusions += test_lanes.POLICY["requiredLinux"]
    selection = "&".join("FullyQualifiedName!~" + case for case in exclusions)
    test_lanes.run_project(state, source, project,
        ["dotnet", "test", project, "-c", "Release", "--no-build", "--no-restore"], selection, results)
    discovered = ci.inputs.load(results / (name + ".discovery.json"))
    rows = test_lanes.project_results(results, name, discovered)
    outcomes = Counter(row["outcome"] for row in rows)
    actual_skips = sorted(row["test"] for row in rows if row["outcome"] == "skipped")
    actual_identity = ci.inputs.sha(ci.inputs.canonical(sorted(row["test"] for row in rows)) + b"\n")
    report = dict(scope="affected suite only, not qualification", profile=args.profile, candidate=args.commit,
                  outcomes=dict(outcomes), identity=actual_identity, skipped=actual_skips)
    (output / "summary.json").write_bytes(ci.inputs.canonical(report) + b"\n")
    if actual_identity != identity or outcomes != Counter(passed=passed, skipped=len(skipped)) or actual_skips != sorted(skipped):
        raise ValueError("Exact case inventory, outcomes or reviewed platform skips differ")
    return 0


raise SystemExit(main())
