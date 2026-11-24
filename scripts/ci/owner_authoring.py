#!/usr/bin/env python3
"""Prepare inputs for composing one import-free root, not for qualification."""
import argparse
import os
from pathlib import Path
import subprocess
import sys

import agent_ci
import agent_selection
import dependency_inputs
import project_scope
import release_inputs as inputs

REPRODUCTION = "compatibility/agent-owner-authoring.json"


def prepare(source, package, source_sha, core_sha, output):
    source = Path(source).resolve(strict=True)
    core = source.parent / "sunder-core"
    inputs.clean_source(source, inputs.digest(source_sha, 40))
    inputs.require(agent_ci.head(core) == inputs.digest(core_sha, 40), "Core checkout differs from the published Build source")
    selected = inputs.package_scope(source, [package])
    (row, lock), = inputs.lock_documents(source, selected)
    inputs.require(not lock["packageImports"] and not lock["packageArtifacts"],
                   "Authoring requires an import-free canonical project")
    properties = dict(Configuration="Release", SunderUseSourceSdk="false")
    env = inputs.isolated_env(Path(output) / "evaluation", Path(output) / "evaluation" / "contracts")
    inputs.require(project_scope.packages(source, [row["projectPath"]], properties, env) == selected,
                   "An authoring root cannot reach other canonical package projects")
    agent_selection.evaluated_packages(source, selected)
    dependency_inputs.resolve(source, core, output, packages=selected, published_catalog_only=True)
    value = inputs.fetch(source, output, source_sha, core_sha, packages=selected, prepared=True)
    inputs.require(not value["candidates"], "An authoring root cannot consume a candidate package")
    return dict(package=package, sha256=inputs.file_sha(Path(output) / inputs.MANIFEST))


def verify_reproduction(source, bundle):
    record = inputs.source_file(source, REPRODUCTION, optional=True)
    if record is None:
        return
    review = inputs.load(record)
    inputs.closed(review, "schemaVersion packageId version authoredSource coreCommit authoringRunId authoringArtifactId authoringZipSha256 archive")
    inputs.require(type(review["schemaVersion"]) is int and review["schemaVersion"] == 1, "Unknown owner authoring record")
    package = inputs.package_scope(source, [review["packageId"]])[0]
    inputs.version(review["version"])
    inputs.digest(review["authoredSource"], 40)
    inputs.positive(review["authoringRunId"])
    inputs.positive(review["authoringArtifactId"])
    inputs.digest(review["authoringZipSha256"])
    expected = review["archive"]
    inputs.closed(expected, "size sha256")
    inputs.positive(expected["size"])
    inputs.digest(expected["sha256"])
    value = inputs.load(Path(bundle) / inputs.MANIFEST)
    if not any(package in lock["packageArtifacts"] for _, lock in inputs.lock_documents(source, value.get("packageIds"))):
        return
    inputs.require(value["core"]["artifactSourceCommit"] == inputs.digest(review["coreCommit"], 40),
                   "Reproduced owner must use the same Core source as lock authoring")
    candidates = [item for item in value["candidates"] if item["packageId"] == package]
    inputs.require(len(candidates) == 1 and candidates[0]["version"] == review["version"],
                   "Missing exact owner authored for the consumer locks")
    asset = candidates[0]["asset"]
    name = f"{package}.{review['version']}.sunderpkg"
    inputs.require(asset["name"] == name and all(asset[key] == item for key, item in expected.items()),
                   "Rebuilt owner bytes differ from the archive used to author the consumer locks")
    path = inputs.regular(Path(bundle) / name)
    inputs.require(path.stat().st_size == expected["size"] and inputs.file_sha(path) == expected["sha256"],
                   "Rebuilt owner archive on disk did not reproduce the authored bytes")
    print(f"{package}@{review['version']}: reproduced the lock-authoring archive byte for byte before consumer builds.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="operation", required=True)
    command = commands.add_parser("prepare")
    command.add_argument("package")
    command.add_argument("source_sha")
    command.add_argument("core_sha")
    command.add_argument("output", type=Path)
    command = commands.add_parser("verify-reproduction")
    command.add_argument("inputs", type=Path)
    args = parser.parse_args()
    if args.operation == "prepare":
        result = prepare(Path.cwd(), args.package, args.source_sha, args.core_sha, args.output)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.writelines(f"{key}={value}\n" for key, value in result.items())
        print("Prepared exact inputs for lock authoring only; no tests or publication.")
    else:
        verify_reproduction(Path.cwd(), args.inputs)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        raise SystemExit("Owner authoring inputs rejected: " + str(error))
