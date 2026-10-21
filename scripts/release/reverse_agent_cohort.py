#!/usr/bin/env python3
"""Explicit reviewed, non-security reverse transition for an unannounced cohort."""

import os
from pathlib import Path
import subprocess
import sys
from urllib.parse import quote

import agent_cohort as cohort
import agent_cohort_receipt as receipts
from agent_release import Invalid, decode, digest, git, inventory, load, origin, require


def guard(repository, release_id, tag, source, source_sha, forward_path, request_path):
    policy = Path(__file__).resolve().parents[2]
    require(os.environ["GITHUB_REF"] == "refs/heads/main"
            and git(policy, "rev-parse", "HEAD").decode().strip() == os.environ["GITHUB_SHA"], "Rollback policy must be exact protected main")
    require(git(source, "rev-parse", "HEAD").decode().strip() == source_sha, "Rollback source checkout differs")
    digest(source_sha, 40)
    require(tag == "agent/v" + inventory(source)["releaseVersion"], "Rollback tag/version differs")
    info = receipts.release_info(repository, release_id, tag)
    require(info["draft"] is True and info["target_commitish"] == source_sha,
            "Non-security reverse requires the exact unannounced draft")
    ref = decode(receipts.gh(f"repos/{repository}/git/ref/tags/{quote(tag, safe='')}"))
    require(ref["ref"] == "refs/tags/" + tag, "Rollback tag ref differs")
    target = ref["object"]
    for _ in range(8):
        if target["type"] == "commit":
            break
        require(target["type"] == "tag", "Unsupported tag target")
        digest(target["sha"], 40)
        target = decode(receipts.gh(f"repos/{repository}/git/tags/{target['sha']}"))["object"]
    require(target["type"] == "commit" and target["sha"] == source_sha, "Rollback tag no longer selects artifact source")
    forward = load(forward_path)
    require(forward["artifactSourceCommit"] == source_sha and forward["releaseId"] == int(release_id), "Forward receipt source differs")
    require(forward["registryOrigin"] == origin(os.environ["REGISTRY_API_URL"].rstrip("/")), "Rollback Registry differs from protected configuration")
    request_sha = receipts.check(forward)
    require(receipts.download_bytes(repository, release_id, tag, request_sha) == forward, "Retained forward cohort changed")
    rows = inventory(source)["packages"]
    require(sorted(v["package"]["packageId"] for v in forward["request"]["versions"]) == sorted(p["packageId"] for p in rows)
            and all(v["package"]["version"] == inventory(source)["releaseVersion"] for v in forward["request"]["versions"]),
            "Forward cohort does not identify this exact family")
    proposed = cohort.request(load(request_path))
    require(proposed == cohort.reverse(forward["request"], forward["result"], proposed["idempotencyKey"],
                                      proposed["expectedPackagePolicyRevision"]), "Reverse is not the captured prior tag map")
    current = cohort.current_tags(forward["registryOrigin"], {p["packageId"] for p in rows})
    # An exact retry can observe the already restored map. Only the Registry's
    # retained (owner,key,request hash) result can confirm that it was this request.
    require(current in (forward["result"]["previousTags"], forward["result"]["resultingTags"]), "Concurrent tag change invalidates reverse approval")


def run(repository, release_id, tag, source, source_sha, forward_path, key, revision, output):
    forward = load(forward_path)
    receipts.check(forward)
    output = Path(output)
    require(not output.exists() and not output.is_symlink(), "Reverse evidence destination already exists")
    output.mkdir()
    request_path, result_path = output / "request.json", output / "result.json"
    proposed = cohort.reverse(forward["request"], forward["result"], key, int(revision))
    cohort.store(request_path, proposed)
    args = [repository, release_id, tag, source, source_sha, forward_path, str(request_path)]
    os.environ["SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON"] = cohort.encoded([sys.executable, "-B", str(Path(__file__).resolve()), "guard", *args]).decode()
    guard(*args)
    cohort.apply(forward["registryOrigin"], str(request_path), str(result_path))
    committed = load(result_path)
    require(committed["previousTags"] == forward["result"]["resultingTags"]
            and committed["resultingTags"] == forward["result"]["previousTags"], "Reverse did not restore the exact captured map")
    print("Applied reviewed atomic reverse. Cohort is Yanked; draft remains unannounced.")


if __name__ == "__main__":
    try:
        command, *args = sys.argv[1:]
        if command == "guard":
            guard(*args)
        elif command == "apply":
            run(*args)
        else:
            raise Invalid("Unknown reverse operation")
    except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(f"Agent cohort reverse rejected: {error}")
