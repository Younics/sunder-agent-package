#!/usr/bin/env python3
"""Bounded native A/B experiments on a diagnostic branch; never package or publish."""
import argparse
from collections import Counter
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/ci"))
import agent_ci as ci
import project_scope
import test_lanes

POLICY = json.loads(Path(__file__).with_name("owning-interference.json").read_text())


def acquire(reference, directory):
    directory.mkdir(parents=True)
    endpoint = f"repos/Younics/sunder-agent-package/actions/artifacts/{reference['id']}"
    metadata = json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))
    directory.with_suffix(".metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    ci.inputs.require(metadata["id"] == reference["id"] and metadata["workflow_run"]["id"] == POLICY["inputRun"]
                      and not metadata["expired"] and metadata["digest"] == "sha256:" + reference["sha256"],
                      "Diagnostic artifact identity/run differs or expired")
    archive = directory.with_suffix(".zip")
    with archive.open("xb") as stream:
        subprocess.run(["gh", "api", endpoint + "/zip"], stdout=stream, check=True)
    ci.inputs.require(ci.inputs.file_sha(archive) == reference["sha256"], "Diagnostic input ZIP hash differs")
    with zipfile.ZipFile(archive) as package:
        seen = set()
        for entry in package.infolist():
            path = PurePosixPath(entry.filename)
            ci.inputs.require(path.parts and not path.is_absolute() and ".." not in path.parts
                              and "\\" not in entry.filename and ":" not in entry.filename
                              and path.as_posix() == entry.filename.rstrip("/")
                              and (entry.external_attr >> 16) & 0o170000 != 0o120000
                              and path.as_posix().casefold() not in seen, "Unsafe diagnostic ZIP membership")
            seen.add(path.as_posix().casefold())
            target = directory.joinpath(*path.parts)
            if entry.is_dir(): target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("xb") as stream: stream.write(package.read(entry))


def processes():
    command = ([
        shutil.which("pwsh") or "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
        "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name | ConvertTo-Json -Compress",
    ] if sys.platform == "win32" else ["ps", "-axo", "pid=,ppid=,comm="])
    try:
        result = subprocess.run(command, text=True, capture_output=True, errors="replace")
        return dict(command=command, exitCode=result.returncode, output=result.stdout, stderr=result.stderr)
    except OSError as error:
        return dict(command=command, exitCode=None, error=ci.describe_error(error))


def payloads(source, rid, projects):
    files = {}
    roots = list((source / "src").glob(f"*/bin/Release/net10.0/sunder-targets/{rid}/sunder-dev"))
    ci.inputs.require(roots, "Missing diagnostic role outputs")
    roots += [source / Path(project).parent / "bin/Release/net10.0" for project in projects]
    for root in roots:
        ci.inputs.require(root.is_dir(), "Missing diagnostic prepared output: " + str(root))
        for path in sorted(root.rglob("*")):
            if path.is_file(): files[str(path.relative_to(source))] = ci.inputs.file_sha(path)
    return files


def authored_inputs(source):
    names = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=source).decode().split("\0")
    return {name: ({"link": os.readlink(source / name)} if (source / name).is_symlink()
                   else ci.inputs.file_sha(source / name)) for name in sorted(set(names) - {""})}


def case_identity(results):
    keys = [(row["testId"], row["test"]) for row in results]
    ci.inputs.require(keys and all(all(key) for key in keys), "Diagnostic group selected no identifiable cases")
    # Neither test IDs nor truncated names are individually unique for all xUnit
    # theories. Their pair distinguishes every retained case in these projects.
    ci.inputs.require(len(keys) == len(set(keys)), "Diagnostic group duplicated a case")
    return dict(count=len(keys), sha256=ci.inputs.sha(ci.inputs.canonical(sorted(keys)) + b"\n"))


def require_cases(profile, key, results):
    identity = case_identity(results)
    ci.inputs.require(identity == profile["expectedCaseIdentities"][key],
                      "Diagnostic case identity differs for " + key + ": " + json.dumps(identity))
    return identity


def collect_results(directory):
    results = []
    for path in sorted(directory.rglob("*.discovery.json")):
        discovery = ci.inputs.load(path)
        if not discovery["tests"]:
            continue
        name = path.name.removesuffix(".discovery.json")
        rows = test_lanes.project_results(path.parent, name, discovery)
        nodes = ET.parse(path.with_name(name + ".trx")).findall(".//{*}UnitTestResult")
        ci.inputs.require(len(rows) == len(nodes), "Diagnostic TRX result identity count differs")
        for row, node in zip(rows, nodes):
            ci.inputs.require(row["test"] == node.get("testName"), "Diagnostic TRX result order differs")
            results.append(dict(row, testId=node.get("testId")))
    return results


def trial_groups(profile, variant):
    project = profile["project"]
    if variant == "mixed":
        return [(path, "", Path(path).stem) for path in [*profile["prelude"], project]]
    if variant == "partitioned":
        return [(project, profile["partitionExclude"], "partitionExclude"), (project, profile["partition"], "partition")]
    if variant == "focused":
        return [(project, profile["target"], "focused")]
    if variant == "mixed-targets":
        return [(project, profile["mixedTargets"], "mixedTargets")]
    ci.inputs.require(variant == "project-alone", "Unknown diagnostic variant")
    return [(project, "", Path(project).stem)]


def verify_traces(directory, required):
    paths = sorted((directory / "trace").glob("*.jsonl"))
    ci.inputs.require(paths or not required, "Diagnostic observer produced no traces")
    samples = 0
    for path in paths:
        rows = [json.loads(line) for line in path.read_text().splitlines()]
        ci.inputs.require(rows and rows[0]["stage"] == "observer-start" and rows[-1]["stage"] == "observer-stop"
                          and not any(row["stage"] == "observer-error" for row in rows),
                          "Diagnostic observer failed or is incomplete: " + str(path))
        observed = [row for row in rows if row["stage"] == "sample"]
        ci.inputs.require(all(not row["poolThread"] for row in observed), "Diagnostic observer ran on a pool thread")
        samples += len(observed)
    return dict(files=len(paths), samples=samples)


def execute_group(state_path, profile, project, selection, key, directory, inventory, group):
    os.environ["SUNDER_TEST_INTERFERENCE_TRACE"] = str(directory / "trace")
    group.update(project=project, filter=selection, identityKey=key, error=None, processBefore=processes())
    try:
        try:
            test_lanes.run_project_with_filesystems(state_path, ROOT, project,
                ["dotnet", "test", "solution", "-c", "Release", "--no-build", "--no-restore"],
                selection, directory, inventory, "source")
        except test_lanes.TestProjectFailure as failure:
            group["error"] = ci.describe_error(failure)
        results = collect_results(directory)
        group.update(counts=dict(Counter(row["outcome"] for row in results)), results=results)
        group["identity"] = require_cases(profile, key, results)
        trace_required = project == profile["project"] and not (profile["id"] == "view" and key == "partitionExclude")
        group["observer"] = verify_traces(directory, trace_required)
        return results
    except BaseException as error:
        group["infrastructureError"] = ci.describe_error(error)
        raise
    finally:
        group["processAfter"] = processes()


def run_profile(profile, repetitions, output):
    ci.inputs.require(1 <= repetitions <= 3, "Diagnostic repetitions are bounded to 1..3")
    ci.inputs.require(not output.exists() and not output.is_relative_to(ROOT)
                      and not output.is_relative_to(ROOT.parent / "sunder-core"), "Diagnostic output must be new and outside sources")
    output.mkdir(parents=True)
    original = authored_inputs(ROOT)
    (output / "authored-input-hashes.json").write_text(json.dumps(original, indent=2) + "\n")
    (output / "recipe.json").write_text(json.dumps(POLICY, indent=2) + "\n")
    projects = [*profile["prelude"], profile["project"]]
    owner = output / "inputs/younics.ai.agent.2.0.0.sunderpkg"
    variants = ["mixed", "partitioned", "focused", "mixed-targets" if "mixedTargets" in profile else "project-alone"]
    summary = dict(scope=POLICY["scope"], profile=profile["id"], sourceCommit=ci.head(ROOT), coreCommit=POLICY["coreCommit"],
        repetitions=repetitions, expectedTrials=repetitions * len(variants), preparedProjects=projects,
        threadPoolPolicy="unmodified", status="preparing", trials=[])
    def save():
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    save()
    frozen = None
    previous = dict(os.environ)
    try:
        acquire(POLICY["inputs"], output / "inputs")
        acquire(profile["tools"], output / "tools")
        # The read-only Actions token is needed only for acquisition, including
        # before inventory evaluation (prepare/test contexts also strip it).
        for key in ("GH_TOKEN", "GITHUB_TOKEN"):
            os.environ.pop(key, None)
        catalog = output / "inputs/sunder-platform-contracts.v1.json"
        ci.inputs.require(ci.inputs.file_sha(owner) == POLICY["ownerSha256"] and ci.inputs.file_sha(catalog) == POLICY["catalogSha256"],
                          "Approved archive/catalog bytes differ")
        inventory_path = ci.generate_inventory(ROOT, [profile["owner"]], "source")
        inventory = ci.inputs.load(inventory_path)
        shutil.copyfile(inventory_path, output / "test-inventory.json")
        ci.inputs.require(set(projects) == {row["path"] for row in inventory["projects"]}, "Diagnostic owned project inventory differs")
        args = argparse.Namespace(source=ROOT, core=ROOT.parent / "sunder-core", work=output / "prepared", mode="source",
            packages=json.dumps([profile["owner"]]), project=project_scope.projects(ROOT, [profile["owner"]], inventory, "source"),
            inputs=None, candidate=[owner], catalog=catalog, tools=output / "tools")
        ci.inputs.require(ci.head(args.core) == POLICY["coreCommit"], "Diagnostic Core source differs")
        ci.prepare(args)
        state_path = args.work / "ci-state.json"
        state, _ = ci.context(state_path)
        command = ["dotnet", "build", state["buildProject"], "-c", "Release", "--no-restore", "-bl:" + str(output / "build.binlog")]
        status = ci.execute(state_path, command, log=output / "build.log")
        ci.inputs.require(status == 0, "Diagnostic preparation build failed")
        frozen = payloads(ROOT, profile["rid"], projects)
        (output / "prepared-payload-hashes.json").write_text(json.dumps(frozen, indent=2) + "\n")
        for name, digest in frozen.items():
            target = output / "prepared-binaries" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
            ci.inputs.require(ci.inputs.file_sha(target) == digest, "Diagnostic binary retention changed bytes")
        summary["status"] = "running"
        save()
        os.environ.update(SUNDER_REQUIRE_FILESYSTEM_TEST_PREREQUISITES="1", SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION="1")
        for repetition in range(repetitions):
            order = variants[repetition:] + variants[:repetition]
            for variant in order:
                trial = output / f"{repetition + 1}-{variant}"
                trial.mkdir()
                trial_state = json.loads(json.dumps(state))
                home, cache = trial / "home", trial / "home/contracts/v1/sha256"
                shutil.copytree(state["cache"], cache)
                graph = project_scope.write_build_graph(ROOT, state["projects"], trial)
                trial_state.update(home=str(home), cache=str(cache), buildProject=str(graph))
                trial_state["properties"]["SunderContractCacheRoot"] = str(cache)
                trial_state["inputHashes"][str(graph)] = ci.inputs.file_sha(graph)
                trial_state_path = trial / "ci-state.json"
                trial_state_path.write_bytes(ci.inputs.canonical(trial_state) + b"\n")
                record = dict(repetition=repetition + 1, variant=variant, processBefore=processes(), status="running", groups=[])
                summary["trials"].append(record)
                save()
                clock = time.monotonic()
                main_results = []
                try:
                    for index, (project, selection, key) in enumerate(trial_groups(profile, variant)):
                        directory = trial / f"{index + 1}-{Path(project).stem}"
                        directory.mkdir()
                        group = {}
                        record["groups"].append(group)
                        try:
                            results = execute_group(trial_state_path, profile, project, selection, key, directory, inventory, group)
                        finally:
                            save()
                        if project == profile["project"]:
                            main_results += results
                    if variant in ("mixed", "partitioned", "project-alone"):
                        record["mainIdentity"] = require_cases(profile, Path(profile["project"]).stem, main_results)
                    record["payloadsUnchanged"] = payloads(ROOT, profile["rid"], projects) == frozen
                    record["authoredInputsUnchanged"] = authored_inputs(ROOT) == original
                    ci.inputs.require(record["payloadsUnchanged"] and record["authoredInputsUnchanged"], "Prepared diagnostic bytes changed")
                    record["status"] = "failed" if any(group["error"] or group["counts"].get("skipped", 0) for group in record["groups"]) else "passed"
                except BaseException as error:
                    record.update(status="infrastructure-error", infrastructureError=ci.describe_error(error))
                    raise
                finally:
                    record.update(elapsedSeconds=time.monotonic() - clock, processAfter=processes())
                    save()
                print(json.dumps(dict(profile=profile["id"], repetition=repetition + 1, variant=variant,
                    counts={outcome:sum(group["counts"].get(outcome, 0) for group in record["groups"]) for outcome in ("passed", "failed", "skipped")})), flush=True)
        ci.inputs.require(len(summary["trials"]) == summary["expectedTrials"], "Diagnostic trials are incomplete")
        summary["status"] = "failed" if any(trial["status"] != "passed" for trial in summary["trials"]) else "passed"
    except BaseException as error:
        summary.update(status="infrastructure-error", infrastructureError=ci.describe_error(error))
        raise
    finally:
        os.environ.clear()
        os.environ.update(previous)
        # Persist the original exception even if one final integrity check fails.
        for key, check in (
            ("payloadsUnchanged", lambda: frozen is not None and payloads(ROOT, profile["rid"], projects) == frozen),
            ("authoredInputsUnchanged", lambda: authored_inputs(ROOT) == original),
            ("approvedOwnerUnchanged", lambda: ci.inputs.file_sha(owner) == POLICY["ownerSha256"]),
        ):
            try:
                summary[key] = check()
            except Exception as error:
                summary[key] = False
                summary.setdefault("integrityErrors", {})[key] = ci.describe_error(error)
        if not all(summary[key] for key in ("payloadsUnchanged", "authoredInputsUnchanged", "approvedOwnerUnchanged")):
            summary["status"] = "infrastructure-error"
        save()
    return 0 if summary["status"] == "passed" else 1


if __name__ == "__main__":
    ci.configure_console()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", action="store_true")
    parser.add_argument("--profile", choices=[row["id"] for row in POLICY["profiles"]])
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.plan:
        value = dict(include=[{key:row[key] for key in ("id", "rid", "runner")} for row in POLICY["profiles"]])
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write("matrix=" + json.dumps(value, separators=(",", ":")) + "\n")
    else:
        if args.profile is None or args.output is None:
            parser.error("--profile and --output are required unless --plan is used")
        profile = next(row for row in POLICY["profiles"] if row["id"] == args.profile)
        raise SystemExit(run_profile(profile, args.repetitions, args.output.resolve()))
