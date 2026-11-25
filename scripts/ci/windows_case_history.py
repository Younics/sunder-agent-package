#!/usr/bin/env python3
"""Separate original Windows reports for an unfinished release, not fresh test results."""
import argparse
import copy
import os
from pathlib import Path
import subprocess
from urllib.parse import quote
import xml.etree.ElementTree as ET
from zipfile import ZipFile

import hosted_evidence as hosted
import required_test_evidence as evidence
import release_inputs as inputs

ROOT = Path(__file__).resolve().parents[2]
OWNER = "younics.ai.agent.execution.local"
AGENT = "younics.ai.agent"
PROJECT = "tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj"
CLASS = "Sunder.Package.Agent.Execution.Local.Tests.WindowsSecureMutationTests"
TRX = "test-results/full/Sunder.Package.Agent.Execution.Local.Tests.trx"
RIDS = {"win-x64", "win-arm64"}
WORKFLOW = "sunder-package-release.yml"
REPORT = "originals.json"


def git(source, *args):
    return subprocess.check_output(["git", "-C", str(source), *args])


def is_historical_group(row):
    return (row["owner"], row["mode"], row["className"], row.get("methodName")) == (OWNER, "source", CLASS, None)


def policy_row(source, document=None):
    rows = evidence.requirements(source, document if document is not None else inputs.load(source / "scripts/test/required-platform-cases.json"), [OWNER])
    matches = [row for row in rows if is_historical_group(row)]
    inputs.require(len(matches) == 1 and "cases" in matches[0] and set(matches[0]["rids"]) == RIDS,
                   "Recovery requires the exact Windows case definitions")
    return matches[0]


def contract_projection(lock):
    projected = copy.deepcopy(lock)
    owner = projected["packageArtifacts"].get(AGENT)
    if owner:
        old = owner.pop("selectedPackageContentIdentity")
        owner.pop("publicInventorySha256")
        for item in [*owner["contracts"].values(), *projected["packageImports"].values()]:
            if item.get("authorityId") == AGENT and item.get("selectedAuthorityContentIdentity") == old:
                del item["selectedAuthorityContentIdentity"]
    return projected


def same_case_scope(source, prior, row, worker):
    # Do not compare a whole tree or a whole lock set: the unchanged test, local
    # implementation, common execution code, and selected RPC tuples are the scope.
    lock = f"{worker}/sunder-contracts.lock.json"
    changed = git(source, "diff", "--name-only", prior, "HEAD", "--", str(Path(PROJECT).parent), worker,
                  "src/Sunder.Agent.Execution.Common",
                  f":(exclude){lock}", f":(exclude){worker}/packages.release.lock.json")
    if changed:
        return False
    previous_policy = inputs.decode(git(source, "show", f"{prior}:scripts/test/required-platform-cases.json"))
    return policy_row(source, previous_policy) == row and contract_projection(inputs.load(source / lock)) == contract_projection(
        inputs.decode(git(source, "show", f"{prior}:{lock}")))


def original_observations(path, pin):
    hosted.verify_zip(pin, path)
    with ZipFile(path) as archive:
        entries = [entry for entry in archive.infolist() if entry.filename == TRX]
        inputs.require(len(entries) == 1 and 0 < entries[0].file_size <= 8 * 1024 * 1024,
                       "Missing or ambiguous original Windows TRX")
        data = archive.read(entries[0])
    inputs.require(not any(marker in data.upper().replace(b"\0", b"") for marker in (b"<!DOCTYPE", b"<!ENTITY")),
                   "Unsupported original Windows TRX markup")
    document = ET.fromstring(data)
    counters = document.find(".//{*}Counters")
    inputs.require(counters is not None and counters.get("failed") == counters.get("notExecuted") == "0",
                   "Original Windows report has failures or skipped cases")
    return evidence.observations(document), inputs.sha(data)


def eligible_release(source, row):
    """Discover a protected-main failure with an unfinished, same authored cohort."""
    from dependency_inputs import releases

    current = {item["packageId"]: item for item in inputs.inventory(source)["packages"]}
    cohort = [current[key] for key in (AGENT, OWNER)]
    if any(release["tag_name"] == cohort[0]["key"] + "/v" + cohort[0]["version"]
           for release in releases(hosted.REPOSITORY)):
        return None
    route = f"workflows/{WORKFLOW}/runs?branch=main&event=push&status=failure&per_page=15"
    for listed in hosted.api_json(route)["workflow_runs"]:
        prior = inputs.digest(listed["head_sha"], 40)
        if str(listed["id"]) == os.environ.get("GITHUB_RUN_ID") or listed["run_attempt"] <= 1:
            continue
        if subprocess.run(["git", "-C", str(source), "merge-base", "--is-ancestor", prior, "HEAD"],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode:
            continue
        if not all(ET.fromstring(git(source, "show", f"{prior}:{item['projectPath']}")).findtext("./PropertyGroup/Version")
                   == item["version"] for item in cohort):
            continue
        run = hosted.api_json(f"runs/{inputs.positive(listed['id'])}")
        inputs.require(hosted.run_origin(run, WORKFLOW, "push") and run["head_sha"] == prior and run["head_branch"] == "main"
                       and run["conclusion"] == "failure", "Original release verdict or source changed")
        def final_failure(job):
            return (job["name"].startswith("Test final archives · ") and job["conclusion"] == "failure"
                    and job["runner_id"] and job["run_attempt"] == run["run_attempt"]
                    and job["run_id"] == run["id"] and job["head_sha"] == prior)

        if not hosted.attempt_jobs(run["id"], run["run_attempt"], reverse=True, stop_after_first=final_failure):
            continue
        # Different relevant inputs need ordinary validation; historical reports never cover them.
        return run if same_case_scope(source, prior, row, str(Path(current[OWNER]["projectPath"]).parent)) else None
    return None


def prepare(source, selected, directory):
    if OWNER not in selected:
        return ""
    row = policy_row(source)
    run = eligible_release(source, row)
    if run is None:
        return ""
    run_id = run["id"]
    jobs = hosted.attempt_jobs(run_id, 1)
    directory.mkdir(parents=True, exist_ok=False)
    windows = {}
    for rid in sorted(RIDS):
        name = f"agent-component-{OWNER}-source-{rid}-1"
        job, = [job for job in jobs if job["name"].endswith(f"{OWNER} · source · {rid}")
                and job["head_sha"] == run["head_sha"] and job["run_id"] == run_id and job["run_attempt"] == 1
                and job["status"] == "completed" and job["conclusion"] == "success" and job["started_at"] and job["completed_at"]]
        artifact, = hosted.entries(f"runs/{run_id}/artifacts?name={quote(name, safe='')}", "artifacts")
        inputs.require(job["started_at"] <= artifact["created_at"] <= job["completed_at"], "Wrong original Windows job artifact")
        pin = hosted.artifact_receipt(run, artifact, name, 16 * 1024 * 1024)
        path = directory / f"{pin['artifactId']}.zip"
        hosted.download(pin, path)
        observed, trx = original_observations(path, pin)
        evidence.verify_cases(row, rid, observed, historical=True)
        windows[rid] = dict(pin, jobId=job["id"], trxSha256=trx)
    record = dict(schemaVersion=1, destinationSource=git(source, "rev-parse", "HEAD").decode().strip(),
                  originalRun=run_id, originalAttempt=1, originalReleaseAttempt=run["run_attempt"],
                  originalSource=run["head_sha"], originalReleaseVerdict="failure", windows=windows, row=row)
    path = directory / REPORT
    path.write_bytes(inputs.canonical(record) + b"\n")
    return inputs.file_sha(path)


def checked_history(source, directory, sha):
    path = inputs.regular(Path(directory) / REPORT)
    inputs.require(inputs.file_sha(path) == inputs.digest(sha), "Windows recovery receipt changed")
    value = inputs.load(path)
    inputs.require(value["schemaVersion"] == 1 and value["originalReleaseVerdict"] == "failure"
                   and value["destinationSource"] == git(source, "rev-parse", "HEAD").decode().strip()
                   and value["row"] == policy_row(source) and set(value["windows"]) == RIDS,
                   "Windows recovery receipt has another scope or verdict")
    return value


def filter_project(project, current_filter):
    if project != PROJECT:
        return current_filter
    prefix = f"({current_filter})&" if current_filter else ""
    return prefix + f"FullyQualifiedName!~{CLASS}."


def audit(reviewed, rows, results, directory):
    row, = [row for row in rows if is_historical_group(row)]
    inputs.require(row == reviewed["row"], "Historical case selection changed")
    directory = inputs.directory(directory)
    originals = reviewed["windows"]
    inputs.require({path.name for path in directory.iterdir()} == {REPORT, *(f"{pin['artifactId']}.zip" for pin in originals.values())},
                   "Missing or duplicate original Windows artifacts")
    records = []
    for rid, pin in originals.items():
        fresh = evidence.read_scope(results, (OWNER, "source", rid))
        inputs.require(fresh and not evidence.case_results(row, fresh), f"{rid}: current evidence is missing or repeats historical execution")
        observed, trx = original_observations(directory / f"{pin['artifactId']}.zip", pin)
        inputs.require(trx == pin["trxSha256"], "Original Windows TRX digest differs")
        result = evidence.verify_cases(row, rid, observed, historical=True)
        records.append(dict(result, **pin, skipped=0, failed=0))
    receipt = {key: item for key, item in reviewed.items() if key not in ("row", "windows")}
    receipt.update(groups=records, executedOnDestination=False)
    return [value for value in rows if value != row], receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True)
    parser.add_argument("--prepare", type=Path, required=True)
    args = parser.parse_args()
    selected = evidence.version_selection.package_ids(ROOT, inputs.decode(args.packages.encode()))
    sha = prepare(ROOT, selected, args.prepare)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"sha256={sha}\n")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError, KeyError) as error:
        raise SystemExit("Windows history rejected: " + str(error))
