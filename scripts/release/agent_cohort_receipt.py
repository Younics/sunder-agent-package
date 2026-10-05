#!/usr/bin/env python3
"""Immutable numeric-release storage for the actual Registry cohort request/result."""

import os
from pathlib import Path
import re
import subprocess
import sys

import agent_cohort as cohort
from agent_release import canonical, closed, decode, digest, file_sha, load, origin, positive, require, sha


def gh(route):
    return subprocess.check_output(["gh", "api", "-H", "Accept: application/vnd.github+json",
                                    "-H", "X-GitHub-Api-Version: 2022-11-28", route])


def check(value):
    closed(value, "schemaVersion registryOrigin preVisibilityAcceptanceSha256 request result")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1, "Unknown cohort receipt schema")
    origin(value["registryOrigin"])
    digest(value["preVisibilityAcceptanceSha256"])
    cohort.result(value["result"], value["request"])
    return cohort.request_hash(value["request"])


def release_info(repository, release_id, tag):
    require(repository == "Younics/sunder-agent-package", "Wrong receipt repository")
    positive(int(release_id))
    value = decode(gh(f"repos/{repository}/releases/{release_id}"))
    require(value["id"] == int(release_id) and value["tag_name"] == tag and type(value["draft"]) is bool, "Numeric release changed")
    return value


def download_bytes(repository, release_id, tag, request_sha):
    digest(request_sha)
    info = release_info(repository, release_id, tag)
    name = f"agent-family-cohort-{request_sha}.json"
    assets = [a for a in info["assets"] if a["name"] == name]
    require(len(assets) == 1, "Missing/duplicate retained cohort receipt")
    asset = assets[0]
    positive(asset["id"])
    require(asset["state"] == "uploaded" and 0 < asset["size"] <= 1048576, "Invalid cohort receipt metadata")
    route = f"repos/{repository}/releases/assets/{asset['id']}"
    require(asset["url"] == "https://api.github.com/" + route, "Cohort asset URL is not numeric-identity bound")
    data = subprocess.check_output(["gh", "api", "-H", "Accept: application/octet-stream", route])
    require(len(data) == asset["size"] and asset["digest"] == "sha256:" + sha(data), "Cohort receipt bytes differ")
    value = decode(data)
    require(check(value) == request_sha and data == canonical(value) + b"\n", "Cohort receipt request/canonical bytes differ")
    return value


def upload(repository, release_id, tag, registry, acceptance_path, result_path):
    acceptance = load(acceptance_path)
    require(acceptance["schemaVersion"] == 4 and acceptance["phase"] == "pre-visibility"
            and acceptance["draftRelease"]["id"] == int(release_id) and acceptance["tag"] == tag,
            "Cohort storage requires the accepted pre-visibility identity")
    value = {"schemaVersion": 1, "registryOrigin": origin(registry),
             "preVisibilityAcceptanceSha256": file_sha(acceptance_path),
             "request": acceptance["cohort"]["request"], "result": load(result_path)}
    request_sha = check(value)
    name = f"agent-family-cohort-{request_sha}.json"
    info = release_info(repository, release_id, tag)
    require(info["draft"] is True, "Cohort cannot be stored after announcement")
    existing = [a for a in info["assets"] if a["name"] == name]
    if not existing:
        token = os.environ["GH_TOKEN"]
        # The Registry has already durably committed. An ambiguous GitHub upload is
        # reconciled by exact readback; neither it nor a retry can mutate Registry.
        subprocess.run(["curl", "--disable", "--silent", "--show-error", "--proto", "=https", "--tlsv1.2",
                        "--max-redirs", "0", "--max-time", "60", "--request", "POST",
                        "--header", "Authorization: Bearer " + token, "--header", "Content-Type: application/octet-stream",
                        "--data-binary", "@-", f"https://uploads.github.com/repos/{repository}/releases/{release_id}/assets?name={name}"],
                       input=canonical(value) + b"\n", stdout=subprocess.DEVNULL, check=False)
    require(download_bytes(repository, release_id, tag, request_sha) == value, "Conflicting durable cohort result; never replace it")
    print(request_sha)


if __name__ == "__main__":
    try:
        command, *args = sys.argv[1:]
        if command == "upload":
            upload(*args)
        elif command == "download":
            repository, release_id, tag, request_sha, output = args
            cohort.store(output, download_bytes(repository, release_id, tag, request_sha))
        else:
            raise ValueError("Unknown cohort receipt operation")
    except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(f"Agent cohort receipt rejected: {error}")
