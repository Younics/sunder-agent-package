#!/usr/bin/env python3
"""One-cohort carry-forward: original Windows filesystem results, never a new verdict."""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile

import required_test_evidence as evidence
import release_inputs as inputs

ROOT = Path(__file__).resolve().parents[2]
ACTIVATION = "compatibility/agent-2.0.0-windows-case-carry.json"
POLICY = "scripts/test/required-platform-cases.json"
POLICY_SHA256 = "48ce435045f109724f8dbf5c4951d96263004953091a16075a76a4ec421da213"
OWNER = "younics.ai.agent.execution.local"
PROJECT = "tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj"
CLASS = "Sunder.Package.Agent.Execution.Local.Tests.WindowsSecureMutationTests"
TRX = "test-results/full/Sunder.Package.Agent.Execution.Local.Tests.trx"
REPOSITORY = "Younics/sunder-agent-package"
REPOSITORY_ID = 1232186250
WORKFLOW_ID = 275269183
RUN = 45150131370
SOURCE = "5cb5cc1ed76a52eeb6fb358772bd28b2d93e20cb"
# These are the two original successful source jobs, not reruns or archive smokes.
ORIGINALS = {
    "win-x64": dict(jobId=137619825202, artifactId=14807852067, size=2479873,
        zipSha256="a4e6eae54c6562f0922729c9bc4de27472dd9e601731630c293443b5ccc973fe",
        trxSha256="71608fc05e9bc7e9026d1efcb6583c301f9aec555e698d7dc5746d99b7de3a6e"),
    "win-arm64": dict(jobId=137619825418, artifactId=14808352147, size=2481459,
        zipSha256="84702971df1324a8810c18e2f02b067fc57c7e29c8e89ee49eadd91dd272bdc8",
        trxSha256="475194b30c817e7a4920d7203ce1e29d5023276e9ca6437f27e3773dc6c517a7"),
}
require = inputs.require


def git(source, *args):
    return subprocess.check_output(["git", "-C", str(source), *args])


def source_tree(source):
    """Hash the committed projection, omitting only the file that names that hash."""
    entries = git(source, "ls-tree", "-r", "-z", "--full-tree", "HEAD").split(b"\0")[:-1]
    ledger = [item for item in entries if item.split(b"\t", 1)[1] == ACTIVATION.encode()]
    return inputs.sha(b"\0".join(item for item in entries if item not in ledger)), ledger


def is_historical_group(row):
    return (row["owner"], row["mode"], row["className"], row.get("methodName")) == (OWNER, "source", CLASS, None)


def policy_row(source):
    path = inputs.source_file(source, POLICY)
    require(inputs.file_sha(path) == POLICY_SHA256, "Windows carry is not valid for a revised evidence policy")
    rows = evidence.requirements(source, inputs.load(path), [OWNER])
    matches = [row for row in rows if is_historical_group(row)]
    require(len(matches) == 1 and "cases" in matches[0] and set(matches[0]["rids"]) == set(ORIGINALS),
            "Windows carry does not cover the exact historical case scope")
    return matches[0]


def active(source, selected, core_commit):
    if selected is not None and OWNER not in selected:
        return None
    tree, ledger = source_tree(source)
    path = Path(source) / ACTIVATION
    if not ledger and not path.exists() and not path.is_symlink():
        return None
    require(len(ledger) == 1 and ledger[0].startswith(b"100644 blob "),
            "Windows carry must use a committed regular review record")
    path = inputs.source_file(source, ACTIVATION)
    require(subprocess.run(["git", "-C", str(source), "diff", "--quiet", "HEAD", "--"]).returncode == 0,
            "Windows carry checkout differs from the reviewed commit")
    require(not git(source, "ls-files", "--others", "--exclude-standard", "-z", "--", "src", "tests", "scripts", "compatibility"),
            "Windows carry cannot use untracked build or test inputs")
    value = inputs.load(path)
    inputs.closed(value, "schemaVersion sourceTreeSha256 coreCommit lockSetSha256")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1 and value["sourceTreeSha256"] == tree
            and value["coreCommit"] == inputs.digest(core_commit, 40) and value["lockSetSha256"] == inputs.locks(source),
            "Windows carry approval does not bind this source, Build, and lock set")
    versions = {row["packageId"]: row["version"] for row in inputs.inventory(source)["packages"]}
    require(versions.get(OWNER) == versions.get("younics.ai.agent") == "2.0.0", "Windows carry is only for the corrected 2.0.0 cohort")
    return dict(value, destinationSource=git(source, "rev-parse", "HEAD").decode().strip(), row=policy_row(source))


def filter_project(reviewed, project, current_filter):
    if reviewed is None or project != PROJECT:
        return current_filter
    prefix = f"({current_filter})&" if current_filter else ""
    return prefix + f"FullyQualifiedName!~{CLASS}."


def file_name(pin):
    return f"{pin['artifactId']}.zip"


def original_observations(path, pin):
    path = inputs.regular(path)
    require(path.stat().st_size == pin["size"] and inputs.file_sha(path) == pin["zipSha256"],
            "Original Windows artifact size or ZIP digest differs")
    with ZipFile(path) as archive:
        entries = [entry for entry in archive.infolist() if entry.filename == TRX]
        require(len(entries) == 1 and 0 < entries[0].file_size <= 8 * 1024 * 1024, "Missing or ambiguous original Windows TRX")
        data = archive.read(entries[0])
    require(inputs.sha(data) == pin["trxSha256"], "Original Windows TRX digest differs")
    require(not any(marker in data.upper().replace(b"\0", b"") for marker in (b"<!DOCTYPE", b"<!ENTITY")),
            "Unsupported original Windows TRX markup")
    document = ET.fromstring(data)
    counters = document.find(".//{*}Counters")
    require(counters is not None and counters.get("failed") == counters.get("notExecuted") == "0",
            "Original Windows report has failures or skipped cases")
    return evidence.observations(document)


def retrieve(reviewed, directory):
    prefix = f"repos/{REPOSITORY}/actions"
    run = inputs.gh_json(f"{prefix}/runs/{RUN}")
    require(run["id"] == RUN and run["workflow_id"] == WORKFLOW_ID and run["repository"]["id"] == REPOSITORY_ID
            and run["head_sha"] == SOURCE and run["head_branch"] == "main" and run["event"] == "push"
            and run["status"] == "completed" and run["conclusion"] == "failure" and run["run_attempt"] == 3,
            "Historical carry does not name the original failed release")
    directory.mkdir(parents=True, exist_ok=False)
    for rid, pin in ORIGINALS.items():
        job = inputs.gh_json(f"{prefix}/jobs/{pin['jobId']}")
        require(job["id"] == pin["jobId"] and job["run_id"] == RUN and job["run_attempt"] == 1
                and job["head_sha"] == SOURCE and job["name"].endswith(f"{OWNER} · source · {rid}")
                and job["started_at"] and job["completed_at"] and job["status"] == "completed" and job["conclusion"] == "success",
                "Wrong original Windows source job")
        route = f"{prefix}/artifacts/{pin['artifactId']}"
        artifact = inputs.gh_json(route)
        origin = dict(id=RUN, repository_id=REPOSITORY_ID, head_repository_id=REPOSITORY_ID, head_branch="main", head_sha=SOURCE)
        require(artifact["id"] == pin["artifactId"] and artifact["name"] == f"agent-component-{OWNER}-source-{rid}-1"
                and artifact["size_in_bytes"] == pin["size"] and artifact["digest"] == "sha256:" + pin["zipSha256"]
                and all(artifact["workflow_run"][key] == value for key, value in origin.items()),
                "Wrong original Windows artifact")
        require(artifact["expired"] is False, "Original Windows artifact expired; review authenticated retained transport, never rerun")
        path = directory / file_name(pin)
        with path.open("xb") as output:
            subprocess.run(["gh", "api", "--allow-escape-sequences", route + "/zip"], stdout=output, check=True, timeout=120)
        evidence.verify_cases(reviewed["row"], rid, original_observations(path, pin), historical=True)


def audit(reviewed, rows, results, directory):
    row, = [row for row in rows if is_historical_group(row)]
    require(row == reviewed["row"], "Historical case selection changed")
    directory = inputs.directory(directory)
    require({path.name for path in directory.iterdir()} == {file_name(pin) for pin in ORIGINALS.values()},
            "Missing or duplicate original Windows artifacts")
    records = []
    for rid, pin in ORIGINALS.items():
        fresh = evidence.read_scope(results, (OWNER, "source", rid))
        require(fresh and not evidence.case_results(row, fresh),
                f"{rid}: current Windows evidence is missing or repeats historical case execution")
        observed = original_observations(directory / file_name(pin), pin)
        result = evidence.verify_cases(row, rid, observed, historical=True)
        records.append(dict(result, **pin, skipped=0, failed=0))
    receipt = dict(originalRun=RUN, originalAttempt=1, originalSource=SOURCE, originalReleaseVerdict="failure",
                   destinationSource=reviewed["destinationSource"], destinationTreeSha256=reviewed["sourceTreeSha256"],
                   destinationLockSetSha256=reviewed["lockSetSha256"], destinationCoreCommit=reviewed["coreCommit"],
                   policySha256=POLICY_SHA256, groups=records, executedOnDestination=False)
    return [value for value in rows if value != row], receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True)
    parser.add_argument("--core", required=True)
    parser.add_argument("--prepare", type=Path, required=True)
    args = parser.parse_args()
    selected = evidence.version_selection.package_ids(ROOT, inputs.decode(args.packages.encode()))
    reviewed = active(ROOT, selected, args.core)
    if reviewed:
        retrieve(reviewed, args.prepare)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"ready={str(reviewed is not None).lower()}\n")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError, KeyError) as error:
        raise SystemExit("Windows carry rejected: " + str(error))
