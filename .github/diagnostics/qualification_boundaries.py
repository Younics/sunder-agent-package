"""Non-publishing diagnostics on the failed qualification's native labels and exact inputs."""
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time
from zipfile import ZipFile
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
SOURCE = Path.cwd().resolve()
CORE = SOURCE.parent / "sunder-core"
ARTIFACTS = SOURCE / "artifacts"
OUT = ARTIFACTS / "boundary"
INPUTS = Path(os.environ["RUNNER_TEMP"]) / "agent-inputs"
TOOLS = INPUTS.with_name("core-tools")
API = "repos/Younics/sunder-agent-package/actions/artifacts"
ATTEMPT = 44946054660
SOURCE_SHA = "670ea57dc4eb4752ae130f7232d5fe91dd802060"
CORE_SHA = "5a51f2040634bfef166516b04342f5115fb1be6a"
OWNER_SHA = "6af4a0de4a0ef336d0ae647b2fcd3296233016ee9fba618645bc270e4ef453fb"
CATALOG_SHA = "7c79ee38af47a3b7c45fb9b9715233887a9f581bea354d6d874144e545fa6210"
HANDOFF_SHA = "62e92e7cbd7348df54d7be28d967bb34b6eac04f370081de21523d22709b4368"
TOOL_ARTIFACT = {
    "linux-arm64": (14723329870, "9e52f0257ddb99c10371834f254ae144ed96980e119d6f99dfab62eaed730b49"),
    "osx-arm64": (14723422928, "2ca91a6308156aafa0b725e12b8c0e9c5ce51fde2c3e2e28fd8b596c0706ef35"),
    "osx-x64": (14723344909, "090860793d60660cdca1f365f9d42da179e9c74b9e4cfe765270fdf766687f80"),
}
PROJECT = "tests/Sunder.Package.Agent.Runtime.Tests/Sunder.Package.Agent.Runtime.Tests.csproj"
INTEGRATION = "tests/Sunder.Package.Agent.Integration.Tests/Sunder.Package.Agent.Integration.Tests.csproj"
FIXTURE = "tests/Sunder.Package.Agent.Runtime.Tests/WorkspacePersistenceTests.cs"
OWNER_PROJECT = "src/Sunder.Package.Agent/Sunder.Package.Agent.csproj"
ARTIFACT_CASE = "CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload"
READ_CASES = (
    "AgentLocalStore_AsyncWorkspaceRead_DoesNotBlockCallerAndObservesCancellation",
    "AgentLocalStore_AsyncWorkspaceReads_UseIndependentConcurrentOperations",
)
CI = [sys.executable, "-B", "scripts/ci/agent_ci.py"]
LOCKED = ["--mode", "source", "--core", str(CORE), "--inputs", str(INPUTS), "--tools", str(TOOLS)]


def run(args, **options):
    return subprocess.run([str(a) for a in args], check=True, **options)


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_checkout(rid):
    for root, commit in ((SOURCE, SOURCE_SHA), (CORE, CORE_SHA)):
        assert run(["git", "-C", root, "rev-parse", "HEAD"], text=True, capture_output=True).stdout.strip() == commit
        run(["git", "-C", root, "diff", "--exit-code", "HEAD", "--"])
    assert (platform.system(), platform.machine()) == {
        "osx-x64": ("Darwin", "x86_64"), "osx-arm64": ("Darwin", "arm64"), "linux-arm64": ("Linux", "aarch64")
    }[rid]
    assert run(["dotnet", "--version"], text=True, capture_output=True).stdout.strip() == "10.0.403"


def fetch(artifact, digest, name, destination):
    meta = json.loads(run(["gh", "api", f"{API}/{artifact}"], capture_output=True, timeout=40).stdout)
    assert meta["name"] == name and meta["digest"] == "sha256:" + digest and not meta["expired"]
    assert (meta["workflow_run"]["id"], meta["workflow_run"]["head_sha"]) == (ATTEMPT, SOURCE_SHA)
    archive = destination.with_suffix(".zip")
    with archive.open("xb") as stream:
        run(["gh", "api", f"{API}/{artifact}/zip"], stdout=stream, timeout=180)
    assert archive.stat().st_size == meta["size_in_bytes"] and sha(archive) == digest
    destination.mkdir()
    with ZipFile(archive) as z:
        files = z.namelist()
        assert len(files) == len(set(files))
        for member in z.infolist():
            assert (destination / member.filename).resolve().is_relative_to(destination)
            assert (member.external_attr >> 16) & 0o170000 != 0o120000
        z.extractall(destination)
    print(f"Verified retained input bytes: {artifact} {name} {digest}")


def inputs(rid):
    fetch(14724475628, "355b6889c3166fbea43643ad6b52d62ff9a330b8fc069bf9d65555f92febb9ac",
          "agent-dependency-inputs", INPUTS)
    fetch(*TOOL_ARTIFACT[rid], "agent-core-tools-" + rid, TOOLS)
    owner = INPUTS / "younics.ai.agent.2.0.0.sunderpkg"
    assert owner.stat().st_size == 32002626 and sha(owner) == OWNER_SHA
    assert sha(INPUTS / "sunder-platform-contracts.v1.json") == CATALOG_SHA
    assert sha(INPUTS / "cold-inputs.json") == HANDOFF_SHA


def workspace():
    state = INPUTS.with_name("workspace-prepared") / "ci-state.json"
    run([*CI, "prepare", *LOCKED, "--work", state.parent, "--project", PROJECT])
    dotnet = [*CI, "run", "--state", state, "--", "dotnet"]
    build = [*dotnet, "build", PROJECT, "-c", "Release", "--no-restore"]
    run(build)
    bin_dir = SOURCE / Path(PROJECT).parent / "bin/Release/net10.0"
    assembly = bin_dir / Path(PROJECT).stem
    root = Path(os.environ["DOTNET_ROOT"])
    csc = root / "sdk/10.0.403/Roslyn/bincore/csc.dll"
    refs = sorted((root / "packs/Microsoft.NETCore.App.Ref").glob("10.0.*/ref/net10.0"))[-1]
    probe = bin_dir / "WorkspaceProbeEntry.dll"
    settings = {"DOTNET_PROCESSOR_COUNT": "1", "DOTNET_ThreadPool_DisableStarvationDetection": "1"}
    for phase, expected in (("starved", 1), ("compensating", 0)):
        patch = HERE / f"workspace-{phase}.patch"
        run(["git", "apply", "--check", patch])
        run(["git", "apply", patch])
        try:
            run([*build, "-p:BuildProjectReferences=false"])
            run(["dotnet", csc, "/nologo", "/target:exe", "/out:" + str(probe),
                 "/reference:" + str(assembly) + ".dll", *[
                     "/reference:" + str(refs / (name + ".dll")) for name in ("mscorlib", "System.Runtime", "System.Console")
                 ], HERE / "WorkspaceProbeEntry.cs"])
            controlled = ["dotnet", "exec", "--runtimeconfig", str(assembly) + ".runtimeconfig.json",
                          "--depsfile", str(assembly) + ".deps.json", probe]
            for i in range(1, 4):
                trace = OUT / f"{phase}-{i}.schedule.log"
                with (OUT / f"{phase}-{i}.run.log").open("xb") as log:
                    result = subprocess.run(controlled, timeout=35, stdout=log, stderr=subprocess.STDOUT,
                                            env=os.environ | settings | {"SUNDER_WORKSPACE_READ_DIAGNOSTIC": str(trace)})
                text = trace.read_text()
                print(phase, i, result.returncode, "\n" + text)
                assert result.returncode == expected
                assert sorted(re.findall(r"(get|list) callback entered", text)) == ["get", "list"]
                if expected == 1:
                    assert "external observer deadline; active=1 max=1" in text
                    assert "System.TimeoutException:" in (OUT / f"{phase}-{i}.run.log").read_text()
                else:
                    assert "max=2" in text and "external observer deadline" not in text
        finally:
            run(["git", "apply", "--reverse", patch])

    final_patch = HERE / "workspace-proposed.patch"
    run(["git", "apply", "--check", final_patch])
    run(["git", "apply", final_patch])
    try:
        assert run(["git", "diff", "--name-only"], text=True, capture_output=True).stdout.splitlines() == [FIXTURE]
        print("Exact proposed fixture SHA-256:", sha(SOURCE / FIXTURE))
        run(build)
        # Execute the normal owning project with the same CI filters, discovery and reporting.
        sys.path.insert(0, str(SOURCE / "scripts/ci"))
        import agent_ci
        import test_lanes
        inventory = json.loads(agent_ci.generate_inventory(SOURCE).read_text())
        out = ARTIFACTS / "test-results/full"
        out.mkdir(parents=True)
        common = ["dotnet", "test", "Sunder.AgentPackage.slnx", "-c", "Release", "--no-build", "--no-restore"]
        flags = [ARTIFACT_CASE, *(test_lanes.POLICY["requiredLinux"] if sys.platform == "linux" else [])]
        os.environ["SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES"] = "1"
        count = test_lanes.run_project_with_filesystems(
            state, SOURCE, PROJECT, common, "&".join("FullyQualifiedName!~" + x for x in flags), out, inventory, "source")
        rows = [row for file in out.rglob("*.trx") for row in ET.parse(file).findall(".//{*}UnitTestResult")]
        assert count > 0 and len(rows) == 707 and Counter(v.get("outcome") for v in rows) == {"Passed": 707}
        for case in READ_CASES:
            name = "Sunder.Package.Agent.Tests.WorkspacePersistenceTests." + case
            assert [row.get("outcome") for row in rows if row.get("testName") == name] == ["Passed"]
        print("Owning Runtime: 707 passed, zero failures or skips; exact cancellation and overlap cases passed.")
    finally:
        run(["git", "apply", "--reverse", final_patch])


def process_table():
    raw = run(["ps", "-ww", "-axo", "pid=,ppid=,pcpu=,time=,lstart=,stat=,command="],
              text=True, capture_output=True, timeout=5).stdout
    table = {}
    for line in raw.splitlines():
        fields = line.split(None, 10)
        if len(fields) == 11 and fields[0].isdigit() and fields[1].isdigit():
            pid, ppid, cpu, cpu_time, *rest = fields
            table[int(pid)] = (int(ppid), cpu, cpu_time, " ".join(rest[:5]), rest[5], rest[6])
    return table


def descendant_ids(table, parents):
    known = set(parents)
    while True:
        extra = {p for p, row in table.items() if row[0] in known} - known
        if not extra:
            return known - set(parents)
        known.update(extra)


def sample_pack(process, trace):
    starts, sampled = {}, {}
    while True:
        try:
            return process.wait(timeout=12)
        except subprocess.TimeoutExpired:
            pass
        try:
            table = process_table()
            children = descendant_ids(table, [process.pid])
            packs = [p for p in children if OWNER_PROJECT in table[p][-1] and "-t:PackSunderPackage" in table[p][-1]]
            for pid in packs:
                pack = table[pid]
                key = (pid, pack[3])
                age = time.monotonic() - starts.setdefault(key, time.monotonic())
                children = sorted(descendant_ids(table, [pid]))
                trace.write(json.dumps(dict(sequence=len(starts), secondsObserved=round(age, 1),
                                            parent=list(pack[:5]), pid=pid, descendants=[
                    [p, *table[p][:5], ("csc" if "/csc.dll " in table[p][-1] else Path(table[p][-1].split()[0]).name)]
                    for p in children])) + "\n")
                trace.flush()
                if age < 630 or age - sampled.get(key, -900) < 120:
                    continue
                sampled[key] = age
                for target in [pid, *[p for p in children if "/csc.dll " in table[p][-1]]]:
                    now = process_table().get(target)
                    if now is None or (now[0], now[3], now[5]) != (table[target][0], table[target][3], table[target][5]):
                        continue
                    with (OUT / f"pid-{target}-{int(age)}.sample-command.log").open("xb") as status:
                        subprocess.run(["/usr/bin/sample", str(target), "1", "10", "-file",
                                        str(OUT / f"pid-{target}-{int(age)}.sample.txt")], stdout=status,
                                       stderr=subprocess.STDOUT, timeout=8)
                    lsof = subprocess.run(["/usr/sbin/lsof", "-p", str(target), "-Fn"], stdout=subprocess.PIPE,
                                          stderr=subprocess.DEVNULL, text=True, timeout=5)
                    paths = [row[1:] for row in lsof.stdout.splitlines() if row.startswith("n")
                             and row.endswith(".sunder-output.lock") and str(SOURCE) in row]
                    trace.write(json.dumps(dict(pid=target, secondsObserved=round(age, 1), openOutputLocks=paths)) + "\n")
                    trace.flush()
        except (OSError, subprocess.SubprocessError) as failure:
            trace.write(json.dumps({"processObservationError": type(failure).__name__}) + "\n")
            trace.flush()


def intel(family):
    args = [*CI, "validate", *LOCKED]
    if not family:
        run([*args, "--packages", '["younics.ai.agent.subagents"]', "--stress"])
        return
    # Isolate the all-family fixture from unrelated ordinary shared tests.
    # The production C# fixture and every child command remain unmodified.
    state = INPUTS.with_name("intel-prepared") / "ci-state.json"
    run([*CI, "prepare", *LOCKED, "--work", state.parent, "--project", INTEGRATION])
    run([*CI, "run", "--state", state, "--", "dotnet", "build", INTEGRATION, "-c", "Release", "--no-restore"])
    sys.path.insert(0, str(SOURCE / "scripts/ci"))
    import agent_ci
    agent_ci.generate_inventory(SOURCE)
    diagnostics = {
        "SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES": "1",
        "SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION": "1",
        "SUNDER_AGENT_SOURCE_ARTIFACT_EVIDENCE": str(ARTIFACTS / "source-family"),
        "SUNDER_AGENT_SOURCE_ARTIFACT_COMMAND_EVIDENCE": str(ARTIFACTS / "source-commands"),
    }
    with subprocess.Popen([*CI, "test", "--state", str(state), "--lane", "artifact"],
                          env=os.environ | diagnostics, start_new_session=True) as process:
        with (OUT / "owner-pack-process.jsonl").open("x") as trace:
            status = sample_pack(process, trace)
        if status:
            raise SystemExit(status)
    command_dir = ARTIFACTS / "source-commands"
    receipts = sorted(command_dir.glob("*.before.json"))
    packs = [p for p in receipts if "-t:PackSunderPackage" in json.loads(p.read_text())["arguments"]]
    assert len(packs) == 16 and sum("-Sunder.Package.Agent-msbuild." in p.name for p in packs) == 2
    for receipt in packs:
        command = json.loads(receipt.read_text())
        assert "-p:RuntimeIdentifier=osx-x64" in command["arguments"]
        after = command_dir / receipt.name.replace(".before.json", ".after.json")
        assert json.loads(after.read_text())["exitCode"] == 0
    attempt = json.loads((command_dir / "attempt-artifacts.json").read_text())
    assert attempt["completed"] is True and len(attempt["archives"]) == 15
    report = ARTIFACTS / "test-results/artifact/Sunder.Package.Agent.Integration.Tests.trx"
    [row] = ET.parse(report).findall(".//{*}UnitTestResult")
    assert row.get("testName") == "Sunder.Package.Agent.Tests.AgentSourceArtifactMigrationTests." + ARTIFACT_CASE
    assert row.get("outcome") == "Passed"
    actual = {}
    for item in attempt["archives"]:
        archive = Path(item["retainedPath"])
        assert archive.parent == command_dir / "attempt-archives"
        assert sha(archive) == item["sha256"] and item["packageId"] not in actual
        actual[item["packageId"]] = item["sha256"]
    assert actual == json.loads((HERE / "intel-expected-source-hashes.json").read_text())
    (OUT / "intel-boundary-receipt.json").write_text(json.dumps(
        {"rid": "osx-x64", "packs": len(packs), "sourceArchiveHashes": actual}, indent=2) + "\n")
    print("Intel owner and all 14 consumers repacked; 16 successful exact-RID packs and matching archive bytes.")


def main(mode, rid):
    verify_checkout(rid)
    if mode == "inputs":
        inputs(rid)
        return
    OUT.mkdir(parents=True)
    assert sha(INPUTS / "cold-inputs.json") == HANDOFF_SHA
    if mode == "workspace":
        workspace()
    elif mode in ("shared-source", "subagents-retention"):
        assert rid == "osx-x64"
        intel(mode == "shared-source")
    else:
        raise ValueError(mode)
    verify_checkout(rid)


if __name__ == "__main__":
    main(sys.argv[1], os.environ["RID"])
