#!/usr/bin/env python3
"""Resolve published dependencies and admit qualified six-native owner candidates.

No operator-supplied source SHA, asset locator or approval manifest is involved.
Committed dependency identities remain authoritative and are never rewritten here.
"""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
from urllib.parse import quote

import agent_release as r
import release_inputs as inputs

CORE = "Younics/sunder-core"
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


def core_packages(source, packages=None):
    """Read exact NuGet resolutions; different Core packages may have different versions."""
    selected = set()
    scope = inputs.package_scope(source, packages)
    for row in r.inventory(source)["packages"]:
        if row["packageId"] not in scope:
            continue
        path = Path(source) / Path(row["projectPath"]).parent / "packages.release.lock.json"
        for framework in r.load(path)["dependencies"].values():
            for name, entry in framework.items():
                if name in ("Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build"):
                    r.version(entry["resolved"])
                    r.require(entry["type"] != "Project" and entry.get("contentHash"), "Core package must have a real locked NuGet identity")
                    selected.add((name, entry["resolved"]))
    return sorted(selected)


def coordinates(source, published=None):
    published = releases(CORE) if published is None else published
    versions = {version for name, version in core_packages(source) if name == "Sunder.Package.Build"}
    r.require(len(versions) == 1, "Native composition needs one declared Sunder.Package.Build version")
    version = next(iter(versions))
    found = locate(CORE, published, f"Sunder.Package.Build.{version}.nupkg")
    r.require(found is not None, f"Publish the declared Sunder.Package.Build {version} dependency first")
    release, asset = found
    r.require(release["tag_name"] in ("sdk/v" + version, "nuget-sunder-package-build/v" + version)
              and release["prerelease"] is (not r.version(version)[1]), "Core build-tool release version/tag differs")
    source_sha = release["target_commitish"]
    # A release's display target can be a branch; the actual immutable tag is authoritative.
    target = inputs.gh_json(f"repos/{CORE}/git/ref/tags/" + quote(release["tag_name"], safe=""))["object"]
    for _ in range(9):
        r.digest(target["sha"], 40)
        if target["type"] == "commit":
            source_sha = target["sha"]
            break
        r.require(target["type"] == "tag", "Invalid Core dependency tag")
        target = inputs.gh_json(f"repos/{CORE}/git/tags/{target['sha']}")["object"]
    r.require(target["type"] == "commit", "Excessive Core tag indirection")
    # Bind the source checkout to the actual tested Build archive, not a mutable
    # release display target or a tag that no longer matches its provenance.
    proof = locate(CORE, [release], "release-provenance.json")
    r.require(proof, "Core build-tool release has no source provenance")
    with tempfile.TemporaryDirectory(prefix="sunder-core-source-") as temporary:
        path = Path(temporary) / "release-provenance.json"
        inputs.download(proof[1], path, inputs.MAX_MANIFEST)
        value = r.load(path)
    r.require(value["schemaVersion"] == 1 and value["sources"] == [dict(name="sunder-core", sha=source_sha)]
              and value["invocation"]["repository"] == CORE and value["invocation"]["releaseId"] == release["id"]
              and value["invocation"]["sha"] == source_sha, "Core build-tool provenance names another source/release")
    records = [item for item in value["artifacts"] if item["path"] == asset["name"]]
    r.require(len(records) == 1 and records[0]["sha256"] == asset["sha256"] and records[0]["size"] == asset["size"],
              "Core build-tool archive differs from its tested provenance")
    return dict(core_commit=source_sha, core_tag=release["tag_name"], core_version=version)


def record(path):
    path = inputs.regular(path)
    return dict(name=path.name, size=path.stat().st_size, sha256=r.file_sha(path))


def run(command, source, home):
    env = inputs.isolated_env(home, Path(home) / "contracts/v1/sha256")
    subprocess.run(command, cwd=source, env=env, check=True)


def catalog(source, core, destination, published, packages=None):
    required = {(item["authorityId"], item["selectedAuthorityRelease"], item["selectedAuthorityContentIdentity"])
                for _, lock in inputs.lock_documents(source, packages) for item in inputs.catalog_requirements(lock)}
    r.require(len(required) == 1, "Expected one exact Core catalog dependency")
    authority, version, digest = next(iter(required))
    r.require(authority == "sunder.core", "Unknown platform catalog authority")
    path = destination / "sunder-platform-contracts.v1.json"
    found = locate(CORE, published, path.name, digest)
    if found:
        inputs.download(found[1], path, 67108864)
    else:
        # Source CI can export an unpublished catalog through the real owning tool.
        project = core / "languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj"
        with tempfile.TemporaryDirectory(prefix="sunder-catalog-home-") as home:
            run(["dotnet", "restore", str(project), "--locked-mode"], source, home)
            run(["dotnet", "build", str(project), "-c", "Release", "--no-restore", "-p:GeneratePackageOnBuild=false"], source, home)
            tool = subprocess.check_output(["dotnet", "msbuild", str(project), "-p:Configuration=Release", "-getProperty:TargetPath"],
                cwd=core, env=inputs.isolated_env(home, Path(home) / "contracts/v1/sha256"), text=True).strip()
            run(["dotnet", str(tool), "platform-contracts", "export", "--core-root", str(core), "--catalog-release", version, "--output", str(path)], source, home)
    r.require(r.file_sha(path) == digest, "Core catalog does not match the committed dependency; update the authored selection explicitly")
    return path


def resolve(source, core, destination, *, packages=None, allow_missing=False):
    r.require(not Path(destination).is_symlink(), "Linked dependency output")
    source, core, destination = Path(source).resolve(), Path(core).resolve(), Path(destination).resolve()
    r.require(not destination.exists() and not destination.is_relative_to(source) and not destination.is_relative_to(core), "Dependency output must be a new external directory")
    before = inputs.source_snapshot(source)
    try:
        return resolve_dependencies(source, core, destination, packages, allow_missing=allow_missing)
    finally:
        r.require(inputs.source_snapshot(source) == before, "Dependency preparation changed authored source/locks")


def resolve_dependencies(source, core, destination, packages=None, *, allow_missing=False):
    identities = {(package_id, entry["selectedPackageVersion"], entry["selectedPackageContentIdentity"], entry["publicInventorySha256"])
                  for _, lock in inputs.lock_documents(source, packages) for package_id, entry in lock["packageArtifacts"].items()}
    r.require(len({item[0] for item in identities}) == len(identities), "Conflicting exact dependency selections for the same package")
    destination.mkdir(parents=True)
    core_releases = releases(CORE)
    catalog_path = catalog(source, core, destination, core_releases, packages)
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
    result = dict(schemaVersion=2, coreCommit=r.git(core, "rev-parse", "HEAD").decode().strip(),
                  candidates=[record(path) for _, path in sorted(paths.items())], platformCatalog=record(catalog_path))
    if packages is not None:
        result.update(schemaVersion=3, packageIds=inputs.package_scope(source, packages))
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


def complete(source, core, directory, candidates):
    """Admit composed native owners against unchanged production selections."""
    source, core, directory, candidates = map(lambda path: Path(path).resolve(), (source, core, directory, candidates))
    prepared = r.load(directory / "preparation.json")
    r.closed(prepared, "schemaVersion sourceCommit sourceInputs inputs pending")
    r.require(prepared["schemaVersion"] == 1 and prepared["sourceCommit"] == r.git(source, "rev-parse", "HEAD").decode().strip()
        and prepared["sourceInputs"] == inputs.source_snapshot(source), "Dependency preparation source/selection changed")
    value = prepared["inputs"]
    r.require(value["coreCommit"] == r.git(core, "rev-parse", "HEAD").decode().strip(), "Dependency preparation Core source changed")
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
