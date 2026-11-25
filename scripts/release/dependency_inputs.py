#!/usr/bin/env python3
"""Resolve published dependencies and admit qualified six-native owner candidates.

No operator-supplied source SHA, asset locator or approval manifest is involved.
Committed dependency identities remain authoritative and are never rewritten here.
"""
from pathlib import Path
import os
import shutil

import agent_release as r
import public_platform_catalog
import release_inputs as inputs

AGENT = "Younics/sunder-agent-package"


def releases(repository):
    result, identifiers, tags = [], set(), set()
    for page in range(1, 101):
        values = inputs.gh_json(f"repos/{repository}/releases?per_page=100&page={page}")
        r.require(type(values) is list and len(values) <= 100, "Invalid published release page")
        for value in values:
            r.positive(value["id"])
            r.require(type(value["draft"]) is bool and value["id"] not in identifiers and value["tag_name"] not in tags,
                      "Ambiguous dependency release")
            identifiers.add(value["id"]); tags.add(value["tag_name"])
            if not value["draft"]:
                result.append(value)
        if len(values) < 100:
            return result
    raise r.Invalid("Release pagination limit exceeded")


def locate(repository, published, name, digest=None):
    matches = [(release, asset) for release in published if not release["draft"] for asset in release["assets"] if asset["name"] == name
               and (digest is None or asset.get("digest") == "sha256:" + digest)]
    if not matches:
        return None
    # Coordinators may retain the same archive as its individual package release.
    # Equal bytes are fine; conflicting publications are not.
    r.require(len({(asset.get("digest"), asset["size"]) for _, asset in matches}) == 1, "Conflicting dependency publications: " + name)
    release, asset = min(matches, key=lambda pair: pair[0]["id"])
    r.require(asset["state"] == "uploaded" and (asset.get("digest") or "").startswith("sha256:"), "Dependency asset has no digest")
    return release, inputs.locator(dict(repository=repository, releaseId=release["id"], assetId=asset["id"],
        name=name, size=asset["size"], sha256=asset["digest"][7:]))


def record(path):
    path = inputs.regular(path)
    return dict(name=path.name, size=path.stat().st_size, sha256=r.file_sha(path))


def resolve(source, destination, *, packages=None, allow_missing=False):
    r.require(not Path(destination).is_symlink(), "Linked dependency output")
    source, destination = Path(source).resolve(), Path(destination).resolve()
    r.require(not destination.exists() and not destination.is_relative_to(source), "Dependency output must be a new external directory")
    before = inputs.source_snapshot(source)
    try:
        return resolve_dependencies(source, destination, packages, allow_missing=allow_missing)
    finally:
        r.require(inputs.source_snapshot(source) == before, "Dependency preparation changed authored source/locks")


def resolve_dependencies(source, destination, packages=None, *, allow_missing=False):
    identities = {(package_id, entry["selectedPackageVersion"], entry["selectedPackageContentIdentity"], entry["publicInventorySha256"])
                  for _, lock in inputs.lock_documents(source, packages) for package_id, entry in lock["packageArtifacts"].items()}
    r.require(len({item[0] for item in identities}) == len(identities), "Conflicting exact dependency selections for the same package")
    destination.mkdir(parents=True)
    catalog_path = public_platform_catalog.fetch(source, destination, packages)
    agent_releases = releases(AGENT) if identities else []
    paths, pending = {}, []
    for package_id, version, _, _ in sorted(identities):
        name = f"{package_id}.{version}.sunderpkg"
        found = locate(AGENT, agent_releases, name)
        if found:
            path = destination / name
            inputs.download(found[1], path)
            key, _ = inputs.verify_candidate_file(source, path, packages)
            r.require(key[0] == package_id, "Downloaded dependency has another package identity")
            paths[package_id] = path
        else:
            pending.append((package_id, version))
    authored = {row["packageId"]: row for row in r.inventory(source)["packages"]}
    for package_id, version in pending:
        row = authored.get(package_id)
        r.require(row and row["version"] == version, f"Missing published dependency {package_id}@{version}; source cannot substitute another version")
        lock = r.load(source / Path(row["projectPath"]).parent / "sunder-contracts.lock.json")
        r.require(not lock["packageArtifacts"], "Unpublished dependency needs an already-qualified owner candidate")
    r.require(not pending or allow_missing,
              "Unpublished dependencies need exact six-native composed candidates; run dependency preparation with --allow-missing, then complete-inputs")
    # This checks full contract/content identities, not just matching filenames/versions.
    if not pending: inputs.verify_selected_files(source, list(paths.values()), catalog_path, packages)
    result = dict(schemaVersion=4, sourceCommit=r.git(source, "rev-parse", "HEAD").decode().strip(),
                  lockSetSha256=r.locks(source), packageIds=inputs.package_scope(source, packages),
                  candidates=[record(path) for _, path in sorted(paths.items())], platformCatalog=record(catalog_path))
    if pending:
        result = dict(schemaVersion=1, sourceCommit=r.git(source, "rev-parse", "HEAD").decode().strip(),
            sourceInputs=inputs.source_snapshot(source), inputs=result, pending=[package_id for package_id, _ in pending])
    r_path = destination / ("preparation.json" if pending else "ci-inputs.json")
    r_path.write_bytes(r.canonical(result) + b"\n")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write("pending=" + r.canonical([package_id for package_id, _ in pending]).decode() + "\n")
            output.write("packages=" + r.canonical(inputs.package_scope(source, packages)).decode() + "\n")
    return result


def complete(source, directory, candidates):
    """Admit composed native owners against unchanged production selections."""
    source, directory, candidates = map(lambda path: Path(path).resolve(), (source, directory, candidates))
    prepared = r.load(directory / "preparation.json")
    r.closed(prepared, "schemaVersion sourceCommit sourceInputs inputs pending")
    r.require(prepared["schemaVersion"] == 1 and prepared["sourceCommit"] == r.git(source, "rev-parse", "HEAD").decode().strip()
        and prepared["sourceInputs"] == inputs.source_snapshot(source), "Dependency preparation source/selection changed")
    value = prepared["inputs"]
    r.require(value["schemaVersion"] == 4 and value["sourceCommit"] == prepared["sourceCommit"]
              and value["lockSetSha256"] == r.locks(source), "Dependency preparation inputs changed")
    for asset in [*value["candidates"], value["platformCatalog"]]:
        inputs.input_asset(asset, value["schemaVersion"])
        r.require(record(directory / asset["name"]) == asset, "Prepared dependency bytes changed")
    rows = {row["packageId"]: row for row in r.inventory(source)["packages"]}
    r.require(prepared["pending"] and len(set(prepared["pending"])) == len(prepared["pending"]), "Invalid missing dependency selection")
    from native_release_inputs import RIDS
    for package_id in prepared["pending"]:
        path = inputs.regular(candidates / (package_id + "." + rows[package_id]["version"] + ".sunderpkg"))
        key, _ = inputs.verify_candidate_file(source, path, value.get("packageIds"))
        manifest, _, _ = inputs.archive_metadata(path)
        r.require(key[0] == package_id and sorted(target["rid"] for target in manifest["targets"] if target["role"] == "runtime") == sorted(RIDS),
                  "Dependency owner must be the composed six-native candidate")
        target = directory / path.name
        r.require(not target.exists(), "Dependency candidates are immutable")
        shutil.copyfile(path, target)
        value["candidates"].append(record(target))
    value["candidates"].sort(key=lambda item: item["name"])
    inputs.verify_selected_files(source, [directory / item["name"] for item in value["candidates"]],
        directory / value["platformCatalog"]["name"], value.get("packageIds"))
    (directory / "ci-inputs.json").write_bytes(r.canonical(value) + b"\n")
    (directory / "preparation.json").unlink()
    return value
