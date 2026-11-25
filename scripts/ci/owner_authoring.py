#!/usr/bin/env python3
"""Compose an import-free owner and authenticate its independent lock-authoring bytes."""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from urllib.parse import quote
from zipfile import ZipFile

import agent_ci  # Adds scripts/release to the direct CLI module path.
import agent_selection
import dependency_inputs
import hosted_evidence as hosted
import project_scope
import release_inputs as inputs

WORKFLOW = "sunder-package-qualify.yml"
AUTHORING_TITLE = "Compose for lock authoring (not qualification)"


def prepare(source, package, source_sha, output):
    source = Path(source).resolve(strict=True)
    inputs.clean_source(source, inputs.digest(source_sha, 40))
    selected = inputs.package_scope(source, [package])
    (row, lock), = inputs.lock_documents(source, selected)
    inputs.require(not lock["packageImports"] and not lock["packageArtifacts"],
                   "Authoring requires an import-free canonical project")
    properties = dict(Configuration="Release")
    env = inputs.isolated_env(Path(output) / "evaluation", Path(output) / "evaluation" / "contracts")
    inputs.require(project_scope.packages(source, [row["projectPath"]], properties, env) == selected,
                   "An authoring root cannot reach other canonical package projects")
    agent_selection.evaluated_packages(source, selected)
    value = inputs.fetch(source, output, source_sha, packages=selected)
    inputs.require(not value["candidates"], "An authoring root cannot consume a candidate package")
    return dict(package=package, sha256=inputs.file_sha(Path(output) / inputs.MANIFEST))


def same_archive(transport, candidate):
    with ZipFile(transport) as archive:
        members = [member for member in archive.infolist() if member.filename == candidate.name]
        if not members:
            return False  # Another released version of this package.
        inputs.require(len(members) == 1, "Ambiguous authoring archive")
        if members[0].file_size != candidate.stat().st_size:
            return False
        with archive.open(members[0]) as original, candidate.open("rb") as rebuilt:
            while block := rebuilt.read(65536):
                if original.read(len(block)) != block:
                    return False
            return not original.read(1)


def authored_archive(package, candidate, repository_run):
    name = f"agent-candidate-{package}"
    for item in hosted.entries("artifacts?name=" + quote(name, safe=""), "artifacts"):
        origin = item.get("workflow_run")
        if item["expired"] or not origin or str(origin["id"]) == repository_run:
            continue
        run_id = inputs.positive(origin["id"])
        run = hosted.api_json(f"runs/{run_id}")
        if (run["id"] != run_id or not hosted.run_origin(run, WORKFLOW, "workflow_dispatch")
                or run["conclusion"] != "success" or run["head_branch"] == "main"
                or run["display_title"] != AUTHORING_TITLE):
            continue
        attempt = inputs.positive(run["run_attempt"])
        jobs = hosted.attempt_jobs(run_id, attempt)
        matches = [job for job in jobs if job["name"].endswith(f"{package} · compose once")
                   and job["run_id"] == run_id and job["run_attempt"] == attempt and job["head_sha"] == run["head_sha"]
                   and job["conclusion"] == "success" and job["started_at"] and job["completed_at"]]
        inputs.require(len(matches) == 1 and matches[0]["started_at"] <= item["created_at"] <= matches[0]["completed_at"],
                       "Authoring artifact is not from the successful compose job")
        record = hosted.artifact_receipt(run, item, name, 384 * 1024 * 1024)
        with tempfile.TemporaryDirectory(prefix="sunder-owner-authoring-") as directory:
            transport = Path(directory) / "artifact.zip"
            hosted.download(record, transport)
            if same_archive(transport, candidate):
                return dict(record, runId=run_id, runAttempt=attempt, jobId=matches[0]["id"], source=run["head_sha"])
    raise ValueError(f"{candidate.name}: no authenticated authoring archive reproduced byte for byte. "
                     "Use the owning compose workflow to author or recover the exact dependency.")


def verify_reproduction(source, bundle, pending, output=None):
    bundle = Path(bundle)
    value = inputs.load(bundle / inputs.MANIFEST)
    candidates = {item["packageId"]: item for item in value["candidates"]}
    inputs.require(len(pending) == len(set(pending)) and set(pending) <= candidates.keys(), "Invalid pending authoring scope")
    records = []
    for package in inputs.package_scope(source, pending) if pending else ():
        asset = candidates[package]["asset"]
        candidate = inputs.regular(bundle / asset["name"])
        inputs.require(dependency_inputs.record(candidate) == asset, "Rebuilt owner changed after lock admission")
        origin = authored_archive(package, candidate, os.environ.get("GITHUB_RUN_ID", ""))
        records.append(dict(packageId=package, archive=asset, authoring=origin))
        print(f"{candidate.name}: reproduced the lock-authoring archive byte for byte before consumer builds.")
    if output:
        with Path(output).open("xb") as stream:
            stream.write(inputs.canonical(dict(schemaVersion=1, source=value["artifactSourceCommit"], owners=records)) + b"\n")
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="operation", required=True)
    command = commands.add_parser("prepare")
    command.add_argument("package")
    command.add_argument("source_sha")
    command.add_argument("output", type=Path)
    command = commands.add_parser("verify-reproduction")
    command.add_argument("inputs", type=Path)
    command.add_argument("pending")
    command.add_argument("receipt", type=Path)
    args = parser.parse_args()
    if args.operation == "prepare":
        result = prepare(Path.cwd(), args.package, args.source_sha, args.output)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.writelines(f"{key}={value}\n" for key, value in result.items())
        print("Prepared exact inputs for lock authoring only; no tests or publication.")
    else:
        verify_reproduction(Path.cwd(), args.inputs, inputs.decode(args.pending.encode()), args.receipt)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        raise SystemExit("Owner authoring inputs rejected: " + str(error))
