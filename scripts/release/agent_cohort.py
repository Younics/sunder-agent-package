#!/usr/bin/env python3
"""One retained Registry cohort request. No per-package mutation/compensation path."""

from datetime import timedelta
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import tempfile

from agent_release import (Invalid, archive_metadata, canonical, closed, decode, digest, encoded, load,
                           origin, positive, require, sha, timestamp, version)


def identity(value):
    closed(value, "packageId version packageContentIdentity")
    require(re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)*", value["packageId"]), "Invalid cohort package ID")
    version(value["version"])
    digest(value["packageContentIdentity"])
    return {key: value[key] for key in ("packageId", "version", "packageContentIdentity")}


def tag_change(value, allow_null=True):
    closed(value, "packageId tag target")
    require(re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,63}", value["tag"]), "Invalid cohort tag")
    target = value["target"]
    require(allow_null or target is not None, "Tag snapshot cannot contain a deletion")
    if target is not None:
        identity(target)
        require(target["packageId"] == value["packageId"], "Tag targets another package")
    return {"packageId": value["packageId"], "tag": value["tag"], "target": None if target is None else identity(target)}


def ordered_unique(values, key):
    keys = [key(value) for value in values]
    require(keys == sorted(set(keys)), "Cohort entries must be sorted and unique")


def request(value):
    closed(value, "idempotencyKey expectedPackagePolicyRevision versions distTags")
    require(re.fullmatch(r"[A-Za-z0-9._-]{1,128}", value["idempotencyKey"]), "Invalid retained idempotency key")
    positive(value["expectedPackagePolicyRevision"])
    require(type(value["versions"]) is list and 0 < len(value["versions"]) <= 64, "Cohort version bound exceeded")
    require(type(value["distTags"]) is list and len(value["distTags"]) <= 64, "Cohort tag bound exceeded")
    versions = []
    for change in value["versions"]:
        closed(change, "package expectedAvailability availability")
        require({change["expectedAvailability"], change["availability"]} == {"Yanked", "Listed"}, "Invalid availability transition")
        versions.append({"package": identity(change["package"]), "expectedAvailability": change["expectedAvailability"],
                         "availability": change["availability"]})
    tags = [tag_change(t) for t in value["distTags"]]
    ordered_unique(versions, lambda v: v["package"]["packageId"] + "/" + v["package"]["version"])
    ordered_unique(tags, lambda t: t["packageId"] + "/" + t["tag"])
    ids = {v["package"]["packageId"] for v in versions}
    require(all(t["packageId"] in ids for t in tags), "Tag outside cohort")
    return {"idempotencyKey": value["idempotencyKey"], "expectedPackagePolicyRevision": value["expectedPackagePolicyRevision"],
            "versions": versions, "distTags": tags}


def request_bytes(value):
    # RegistryPolicyCanonical.Encode's declared DTO order and default System.Text.Json
    # ASCII encoder. '+' in SemVer build metadata is the only escapable grammar byte.
    return encoded(request(value)).replace(b"+", b"\\u002B")


def request_hash(value):
    return sha(b"sunder.package-cohort-request.v1\0" + request_bytes(value))


def snapshots(values, package_ids):
    require(type(values) is list and [v["packageId"] for v in values] == sorted(package_ids), "Incomplete cohort tag snapshot")
    for snapshot in values:
        closed(snapshot, "packageId tags")
        require(type(snapshot["tags"]) is list and len(snapshot["tags"]) <= 1024, "Snapshot tag bound exceeded")
        for tag in snapshot["tags"]:
            tag_change(tag, False)
            require(tag["packageId"] == snapshot["packageId"], "Snapshot contains foreign tag")
        ordered_unique(snapshot["tags"], lambda t: t["tag"])
    return {v["packageId"]: {t["tag"]: t for t in v["tags"]} for v in values}


def result(value, proposed):
    proposed = request(proposed)
    closed(value, "idempotencyKey requestSha256 packagePolicy committedAtUtc versions previousTags resultingTags")
    require(value["idempotencyKey"] == proposed["idempotencyKey"] and value["requestSha256"] == request_hash(proposed),
            "Cohort result is not the exact retained request")
    require(value["versions"] == proposed["versions"], "Cohort result changed an exact identity/state")
    closed(value["packagePolicy"], "revision fingerprint")
    require(positive(value["packagePolicy"]["revision"]) == proposed["expectedPackagePolicyRevision"] + 1, "Wrong committed policy revision")
    digest(value["packagePolicy"]["fingerprint"])
    timestamp(value["committedAtUtc"])
    ids = {v["package"]["packageId"] for v in proposed["versions"]}
    previous = snapshots(value["previousTags"], ids)
    resulting = snapshots(value["resultingTags"], ids)
    for tag in proposed["distTags"]:
        if tag["target"] is None:
            previous[tag["packageId"]].pop(tag["tag"], None)
        else:
            previous[tag["packageId"]][tag["tag"]] = tag
    require(previous == resulting, "Result did not preserve the complete prior tag map plus exact requested changes")
    return value


def policy_snapshot(value, registry, proposed):
    """Validate a nonce-bound DB policy observation against retained client consistency."""
    closed(value, "nonce registryOrigin packagePolicy observedAtUtc validUntilUtc packages platformCatalogs")
    require(value["nonce"] == proposed["nonce"] and value["registryOrigin"] == origin(registry), "Policy nonce/origin mismatch")
    policy = value["packagePolicy"]
    closed(policy, "revision fingerprint")
    positive(policy["revision"])
    digest(policy["fingerprint"])
    minimum = proposed["minimumPackagePolicy"]
    if minimum is not None:
        closed(minimum, "revision fingerprint")
        positive(minimum["revision"])
        digest(minimum["fingerprint"])
        require(policy["revision"] >= minimum["revision"]
                and (policy["revision"] != minimum["revision"] or policy == minimum),
                "Policy is older than or conflicts with the retained package-policy identity")
    times = []
    fractions = []
    for field in ("observedAtUtc", "validUntilUtc"):
        text = value[field]
        match = re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.([0-9]{1,7}))?(?:Z|\+00:00)", text) if type(text) is str else None
        require(match, "Policy observation requires a UTC timestamp")
        times.append(timestamp(text))
        # Preserve Core's 100 ns precision when Python's datetime truncates it.
        fractions.append((match[1] or "").ljust(7, "0"))
    require(times[1] - times[0] == timedelta(hours=24) and fractions[0] == fractions[1], "Invalid fixed policy lease interval")
    for field, member, fields in (
            ("packages", "package", "package packageState versionAvailability disposition errorCode publicReason platformDependencies"),
            ("platformCatalogs", "catalog", "catalog lifecycleState disposition errorCode revokedAtUtc")):
        require(type(value[field]) is list and len(value[field]) <= 128, "Invalid policy subject collection")
        for decision in value[field]:
            closed(decision, fields)
        require([decision[member] for decision in value[field]] == proposed[field], "Policy response changed the exact requested subjects")
    return value


def store(path, value):
    path = Path(path)
    data = canonical(value) + b"\n"
    if path.exists() or path.is_symlink():
        require(not path.is_symlink() and path.is_file() and path.read_bytes() == data, "Retained evidence cannot be replaced")
        return
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".cohort-", delete=False) as stream:
        staged = Path(stream.name)
        try:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
            os.link(staged, path)  # Atomic create without overwrite.
        finally:
            staged.unlink()


def http(registry, route, payload=None, token=None):
    registry = origin(registry)
    command = ["curl", "--disable", "--silent", "--show-error", "--proto", "=https", "--tlsv1.2",
               "--max-redirs", "0", "--max-time", "60", "--max-filesize", "1048576", "--write-out", "\n%{http_code}"]
    if payload is not None:
        command += ["--request", "POST", "--header", "Content-Type: application/json", "--data-binary", "@-"]
    if token is not None:
        command += ["--header", "Authorization: Bearer " + token]
    command.append(registry + route)
    clean = {k: v for k, v in os.environ.items() if k not in ("SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_PUBLISH_TOKEN")}
    response = subprocess.run(command, input=payload, stdout=subprocess.PIPE, env=clean, check=False)
    body, separator, status = response.stdout.rpartition(b"\n")
    require(separator and re.fullmatch(b"[0-9]{3}", status), "Registry response has no HTTP status; retain the same request")
    return int(status), body, response.returncode


def get(registry, route):
    status, body, transport = http(registry, route)
    require(not transport and status == 200, f"Registry read failed ({status}); no cohort mutation is safe")
    return decode(body)


def current_tags(registry, package_ids):
    answer = []
    for package_id in sorted(package_ids):
        document = get(registry, f"/api/v1/packages/{package_id}/dist-tags")
        require(document["packageId"] == package_id, "Registry tag subject changed")
        tags = []
        for tag in document["distTags"]:
            # Read content identity too: same tag/version text cannot hide changed immutable bytes.
            version(tag["version"])
            details = get(registry, f"/api/v1/packages/{package_id}/versions/{tag['version']}")
            target = identity(details["publication"]["package"])
            require(target["packageId"] == package_id and target["version"] == tag["version"], "Tag identity changed")
            tags.append({"packageId": package_id, "tag": tag["tag"], "target": target})
        answer.append({"packageId": package_id, "tags": sorted(tags, key=lambda t: t["tag"])})
    snapshots(answer, package_ids)
    return answer


def verify_current(registry, proposed, committed):
    result(committed, proposed)
    ids = {v["package"]["packageId"] for v in proposed["versions"]}
    require(current_tags(registry, ids) == committed["resultingTags"], "Complete resulting tag map drifted")
    for change in proposed["versions"]:
        package = change["package"]
        details = get(registry, f"/api/v1/packages/{package['packageId']}/versions/{package['version']}")
        require(details["publication"]["package"] == package
                and type(details["isYanked"]) is bool
                and details["isYanked"] == (change["availability"] == "Yanked"), "Committed cohort availability/content drifted")


def confirm(registry, request_path, result_path, archives):
    """Authenticate the retained result before announcement without a new mutation.

    A fresh anonymous synchronization checks the DB revision against the retained
    committed identity, which is newer than the request's expected revision. The
    owner POST uses that original stale fence to read back the exact operation.
    This is cached consistency, not independent proof against database rollback.
    """
    registry = origin(registry)
    proposed = request(load(request_path))
    committed = result(load(result_path), proposed)
    verify_current(registry, proposed, committed)
    token = os.environ.get("SUNDER_REGISTRY_CLI_TOKEN", "")
    require(re.fullmatch(r"sunder_cli_[A-Za-z0-9_-]{43}", token), "Owner credential required to confirm the retained cohort")
    nonce = secrets.token_hex(16)
    minimum = committed["packagePolicy"]
    platforms = {}
    for change in proposed["versions"]:
        package = change["package"]
        manifest, public, _ = archive_metadata(Path(archives) / f"{package['packageId']}.{package['version']}.sunderpkg")
        require(public["packageContentIdentity"] == package["packageContentIdentity"], "Policy input archive identity differs")
        authorities = [*manifest.get("platformImports", []), *manifest.get("contractBundles", [])]
        for imported in manifest.get("packageArtifactImports", []):
            authorities.extend(imported.get("authorityConstraints", []))
        for authority in authorities:
            if authority.get("authorityKind") == "platform":
                item = {"authorityId": authority["authorityId"], "releaseVersion": authority["selectedAuthorityRelease"],
                        "catalogSha256": digest(authority["selectedAuthorityContentIdentity"])}
                require(item["authorityId"] == "sunder.core", "Unknown platform authority")
                version(item["releaseVersion"])
                platforms[tuple(item.values())] = item
    subjects = sorted([v["package"] for v in proposed["versions"]], key=lambda p: (p["packageId"], p["version"], p["packageContentIdentity"]))
    synchronization = {"nonce": nonce, "packages": subjects,
                       "platformCatalogs": [platforms[key] for key in sorted(platforms)], "minimumPackagePolicy": minimum}
    status, body, transport = http(registry, "/api/v1/policy/synchronize", encoded(synchronization))
    require(not transport and status == 200, "Fresh cohort policy synchronization failed")
    current = policy_snapshot(decode(body), registry, synchronization)
    require(all(p["packageState"] == "Active" and p["versionAvailability"] == "Listed" and p["disposition"] == "Allowed"
                    and p["errorCode"] is None and type(p["platformDependencies"]) is list
                    and all(c in synchronization["platformCatalogs"] for c in p["platformDependencies"])
                    for p in current["packages"]), "Fresh policy does not allow the complete Listed cohort")
    require(all(p["lifecycleState"] == "Supported" and p["disposition"] == "Allowed"
                    and p["errorCode"] is None and p["revokedAtUtc"] is None for p in current["platformCatalogs"]),
            "Fresh policy does not allow the complete platform catalog set")
    for attempt in range(2):
        status, body, transport = http(registry, "/api/v1/me/package-cohorts/availability", request_bytes(proposed), token)
        if not transport and status == 200:
            require(result(decode(body), proposed) == committed, "Registry retained result differs from accepted cohort evidence")
            return
        require(attempt == 0 and (transport or status >= 500), "Registry did not confirm the exact retained cohort; no announcement")


def guard():
    value = decode(os.environ.get("SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON", "null").encode())
    require(type(value) is list and 0 < len(value) <= 16 and all(type(v) is str and v and not any(c in v for c in "\r\n\0") for v in value),
            "A trusted pre-mutation guard is required")
    clean = {k: v for k, v in os.environ.items() if k not in ("SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_PUBLISH_TOKEN")}
    subprocess.run(value, env=clean, check=True)


def apply(registry, request_path, result_path):
    proposed = request(load(request_path))
    body = request_bytes(proposed)
    if Path(result_path).exists() or Path(result_path).is_symlink():
        verify_current(registry, proposed, load(result_path))
        return
    token = os.environ.get("SUNDER_REGISTRY_CLI_TOKEN", "")
    require(re.fullmatch(r"sunder_cli_[A-Za-z0-9_-]{43}", token), "Interactive owner credential required")
    for attempt in range(2):
        observed = current_tags(registry, {v["package"]["packageId"] for v in proposed["versions"]})
        if all(v["availability"] == "Listed" for v in proposed["versions"]):
            before = snapshots(observed, {v["package"]["packageId"] for v in proposed["versions"]})
            for tag in proposed["distTags"]:
                prior = before[tag["packageId"]].get(tag["tag"])
                require(tag["target"] is not None and (prior is None or version(tag["target"]["version"]) >= version(prior["target"]["version"])),
                        "Forward promotion cannot regress/delete a public tag")
        guard()
        require(request_bytes(load(request_path)) == body, "Retained request changed during guard")
        status, response, transport = http(registry, "/api/v1/me/package-cohorts/availability", body, token)
        try:
            document = decode(response)
        except (ValueError, UnicodeError):
            document = None
        if not transport and status == 200 and document is not None:
            committed = result(document, proposed)
            store(result_path, committed)
            verify_current(registry, proposed, committed)
            return
        require(transport or status >= 500 or status == 200,
                f"Registry rejected retained cohort ({status}); do not alter its revision/key on retry")
        # Exactly the same serialized body/key/revision after an ambiguous response.
        require(attempt == 0, "Cohort outcome remains unknown; retain this request for exact retry/administrator recovery")


def reverse(proposed, committed, key, revision):
    result(committed, proposed)
    require(key != proposed["idempotencyKey"] and positive(revision) >= committed["packagePolicy"]["revision"], "Reverse requires a new reviewed key/revision")
    require(all(v["expectedAvailability"] == "Yanked" and v["availability"] == "Listed" for v in proposed["versions"]), "Reverse needs a forward promotion result")
    previous = snapshots(committed["previousTags"], {v["package"]["packageId"] for v in proposed["versions"]})
    after = snapshots(committed["resultingTags"], set(previous))
    tags = []
    for package_id in sorted(previous):
        for tag in sorted(set(previous[package_id]) | set(after[package_id])):
            tags.append(previous[package_id].get(tag, {"packageId": package_id, "tag": tag, "target": None}))
    tags.sort(key=lambda t: t["packageId"] + "/" + t["tag"])
    return request({"idempotencyKey": key, "expectedPackagePolicyRevision": revision,
                    "versions": [{"package": v["package"], "expectedAvailability": "Listed", "availability": "Yanked"}
                                 for v in proposed["versions"]], "distTags": tags})


def main():
    command, *args = sys.argv[1:]
    if command == "hash":
        print(request_hash(load(args[0])))
    elif command == "validate":
        result(load(args[1]), load(args[0]))
    elif command == "apply":
        apply(*args)
    elif command == "verify":
        registry, req, res = args
        verify_current(registry, request(load(req)), load(res))
    elif command == "confirm":
        confirm(*args)
    elif command == "reverse":
        req, res, key, revision, output = args
        store(output, reverse(load(req), load(res), key, int(revision)))
    else:
        raise Invalid("Unknown cohort command")


if __name__ == "__main__":
    try:
        main()
    except (Invalid, ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(f"Agent cohort rejected: {error}")
