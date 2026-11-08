#!/usr/bin/env python3
"""Read-only version selection. Tags alone are never publication evidence."""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from urllib.parse import quote

sys.dont_write_bytecode = True
import agent_cohort as cohort
import agent_release as r
import release_inputs as inputs

REPOSITORY = "Younics/sunder-agent-package"
PLAN = "release-plan.json"


def evaluated_packages(source, package_ids=None):
    rows = r.inventory(source)["packages"]
    if package_ids is not None:
        r.require(set(package_ids) <= {row["packageId"] for row in rows}, "Unknown version validation selection")
        rows = [row for row in rows if row["packageId"] in package_ids]
    env = {key: value for key, value in os.environ.items() if key not in
        ("GH_TOKEN", "GITHUB_TOKEN", "SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_PUBLISH_TOKEN")}
    for row in rows:
        project = r.source_file(source, row["projectPath"])
        result = subprocess.check_output(["dotnet", "msbuild", str(project), "-p:Configuration=Release",
            "-p:ContinuousIntegrationBuild=true", "-p:SunderUseSourceSdk=false",
            "-getProperty:Version,PackageVersion,InformationalVersion,SunderAgentVersion"], cwd=source, env=env)
        props = r.decode(result)["Properties"]
        r.require(not props["SunderAgentVersion"] and all(props[key] == row["version"] for key in
            ("Version", "PackageVersion", "InformationalVersion")), "Evaluated Release version differs from authored Version: " + row["packageId"])
    return rows


def releases():
    result, identifiers, tags = [], set(), set()
    for page in range(1, 101):
        values = inputs.gh_json(f"repos/{REPOSITORY}/releases?per_page=100&page={page}")
        r.require(type(values) is list and len(values) <= 100, "Invalid GitHub release page")
        for value in values:
            identifier = r.positive(value["id"])
            r.require(identifier not in identifiers and value["tag_name"] not in tags
                      and type(value["draft"]) is bool, "Duplicate/ambiguous GitHub release")
            identifiers.add(identifier); tags.add(value["tag_name"])
            result.append(value)
        if len(values) < 100:
            return result
    raise r.Invalid("Release pagination limit exceeded")


def archive_locator(release, row):
    name = f"{row['packageId']}.{row['version']}.sunderpkg"
    return asset_locator(release, name)


def asset_locator(release, name):
    matches = [asset for asset in release["assets"] if asset["name"] == name]
    r.require(len(matches) == 1, "Published release has no exact canonical archive: " + name)
    asset = matches[0]
    r.require(asset["state"] == "uploaded" and type(asset.get("digest")) is str
              and asset["digest"].startswith("sha256:"), "Published archive has no immutable digest")
    return inputs.locator(dict(repository=REPOSITORY, releaseId=release["id"], assetId=asset["id"],
        name=name, size=asset["size"], sha256=asset["digest"][7:]))


def registry_version(registry, row):
    route = "/api/v1/packages/" + quote(row["packageId"], safe="") + "/versions/" + quote(row["version"], safe="")
    status, data, failed = cohort.http(registry, route)
    r.require(not failed and status in (200, 404), "Registry version reconciliation failed: " + row["packageId"])
    if status == 404:
        return None
    value = r.decode(data)
    r.require(value["packageId"] == row["packageId"] and value["version"] == row["version"]
              and not value.get("isSecurityBlocked", False), "Registry version is blocked or has another identity")
    return value


def validate(plan, source, source_sha=None):
    r.require(type(plan) is dict and type(plan.get("schemaVersion")) is int
              and plan["schemaVersion"] in (1, 2) and plan.get("repository") == REPOSITORY,
              "Unknown release selection")
    r.closed(plan, "schemaVersion repository artifactSourceCommit lockSetSha256 registryOrigin packages"
             + (" ownerPrincipalId" if plan["schemaVersion"] == 1 else ""))
    if plan["schemaVersion"] == 1:
        # Historical plans retain their original identity assertion byte-for-byte.
        r.digest(plan["ownerPrincipalId"], 32)
        r.require(plan["ownerPrincipalId"] != "0" * 32, "Missing historical release owner")
    r.digest(plan["artifactSourceCommit"], 40)
    r.require(plan["lockSetSha256"] == r.locks(source)
              and (source_sha is None or plan["artifactSourceCommit"] == source_sha), "Release selection source/locks differ")
    r.origin(plan["registryOrigin"])
    expected = sorted(r.inventory(source)["packages"], key=lambda row: row["packageId"])
    rows = plan["packages"]
    r.require(type(rows) is list and len(rows) == len(expected), "Release selection must cover all fifteen packages")
    for row, authored in zip(rows, expected):
        r.closed(row, "key packageId projectPath version tag selected retainedArchive retainedEvidence")
        r.require(all(row[key] == authored[key] for key in ("key", "packageId", "projectPath"))
                  and row["tag"] == row["key"] + "/v" + row["version"] and type(row["selected"]) is bool,
                  "Release selection differs from authored package versions")
        r.require(not row["selected"] or row["version"] == authored["version"], "Selected release version differs from the project")
        if row["retainedArchive"] is not None:
            locator = inputs.locator(row["retainedArchive"])
            r.require(not row["selected"] and locator["repository"] == REPOSITORY
                      and locator["name"] == archive_name(row), "Invalid retained package selection")
            evidence = inputs.locator(row["retainedEvidence"])
            r.require(evidence["repository"] == REPOSITORY and evidence["name"] == "native-test-evidence.json"
                      and evidence["releaseId"] == locator["releaseId"], "Retained package needs its exact native evidence")
        else:
            r.require(row["selected"] and row["retainedEvidence"] is None, "Unchanged packages require retained inputs")
    return plan


def load(path, source, source_sha=None):
    return validate(r.load(path), source, source_sha)


def archive_name(row):
    return f"{row['packageId']}.{row['version']}.sunderpkg"


def selected(plan):
    return [row for row in plan["packages"] if row["selected"]]


def plan(source, source_sha, registry, published=None):
    r.digest(source_sha, 40)
    # Selection is deliberately toolchain-free. Evaluate only selected projects
    # later, in validation, before trusting their build output.
    rows = r.inventory(source)["packages"]
    for row in rows:
        r.source_file(source, row["projectPath"])
    published = releases() if published is None else published
    # A draft, or a partially announced batch, keeps its original selection and
    # immutable inputs across workflow reruns. Never turn it into a fresh build.
    retained, pending_drafts = [], []
    for row in rows:
        tag = row["key"] + "/v" + row["version"]
        for release in published:
            if release["tag_name"] != tag:
                continue
            if release["draft"]:
                r.require(release["target_commitish"] == source_sha, "Pending draft belongs to another source; resume that release first")
                pending_drafts.append(release)
            if release["target_commitish"] == source_sha:
                assets = [asset for asset in release["assets"] if asset["name"] == PLAN]
                r.require(len(assets) <= 1, "Ambiguous retained release plan")
                if assets:
                    asset = assets[0]
                    locator = inputs.locator(dict(repository=REPOSITORY, releaseId=release["id"], assetId=asset["id"],
                        name=PLAN, size=asset["size"], sha256=(asset.get("digest") or "")[7:]))
                    with tempfile.TemporaryDirectory() as temporary:
                        path = Path(temporary) / PLAN
                        inputs.download(locator, path)
                        retained.append(load(path, source, source_sha))
    r.require(retained or not pending_drafts,
              "Interrupted staging has no retained plan; rerun the original failed job with its tested artifact, never rebuild it")
    if retained:
        r.require(all(value == retained[0] for value in retained), "Pending package releases disagree on their batch selection")
        value = retained[0]
        r.require(value["registryOrigin"] == r.origin(registry), "Retained release destination changed")
        by_tag = {release["tag_name"]: release for release in published}
        pending = any(row["tag"] not in by_tag or by_tag[row["tag"]]["draft"] for row in selected(value))
        if not pending:
            for row in selected(value):
                current = registry_version(registry, row)
                if current is None or current.get("isYanked") is not False:
                    pending = True
        if pending:
            return value
    result = []
    for row in rows:
        matches = []
        prefix = row["key"] + "/v"
        for release in published:
            if not release["tag_name"].startswith(prefix):
                continue
            text = release["tag_name"][len(prefix):]
            r.version(text)  # malformed matching tags are errors, not silently skipped
            r.require(release.get("prerelease") is (not r.version(text)[1]), "GitHub prerelease flag differs from SemVer")
            if not release["draft"]:
                matches.append((r.version(text), text, release))
        # A maintained major can advance while a newer major coexists.
        major = r.version(row["version"])[0][0]
        matches = [item for item in matches if item[0][0][0] == major]
        latest = max(matches, default=None, key=lambda item: item[0])
        is_new = latest is None or r.version(row["version"]) > latest[0]
        exact = [release for _, text, release in matches if text == row["version"]]
        retained_row = row
        retained_release = exact[0] if exact else latest[2] if latest else None
        if not is_new and not exact:
            # Lower authored versions never become releases. Use published bytes
            # for the unchanged member of the integration-test family instead.
            retained_row = dict(row, version=latest[1])
        current = registry_version(registry, retained_row)
        if is_new:
            r.require(current is None or current.get("isYanked") is True,
                      "Registry version is already Listed without retained release evidence; reconcile before releasing")
        else:
            r.require(current is not None and current.get("isYanked") is False, "Published GitHub version is not Listed in Registry; reconcile before releasing")
            locator = archive_locator(retained_release, retained_row)
            r.require(current.get("canonicalArtifact", {}).get("sha256") == locator["sha256"], "GitHub and Registry archive bytes differ")
        result.append({key: retained_row[key] for key in ("key", "packageId", "projectPath", "version")} | dict(
            tag=prefix + retained_row["version"], selected=is_new, retainedArchive=None if is_new else locator,
            retainedEvidence=None if is_new else asset_locator(retained_release, "native-test-evidence.json")))
    return validate(dict(schemaVersion=2, repository=REPOSITORY, artifactSourceCommit=source_sha,
        lockSetSha256=r.locks(source), registryOrigin=r.origin(registry),
        packages=sorted(result, key=lambda row: row["packageId"])), source, source_sha)


def retain(plan, directory):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    for row in plan["packages"]:
        locator = row["retainedArchive"]
        if locator is None:
            continue
        path = directory / locator["name"]
        if not path.exists():
            inputs.download(locator, path)
        r.require(r.file_sha(path) == locator["sha256"] and path.stat().st_size == locator["size"], "Retained archive changed")
        manifest, _, _ = r.archive_metadata(path)
        r.require((manifest["id"], manifest["version"]) == (row["packageId"], row["version"]), "Retained archive identity differs")
        evidence = row["retainedEvidence"]
        path = directory / evidence_name(evidence)
        if not path.exists():
            inputs.download(evidence, path)
    retained_role_uses(plan, directory)


def evidence_name(locator):
    return f"native-evidence-{locator['sha256']}.json"


def retained_role_uses(plan, directory):
    """Carry prior source-backed consumer evidence, never infer grants from output."""
    from native_release_inputs import json_file, RIDS
    result, documents = {}, {}
    for row in plan["packages"]:
        if row["selected"]:
            continue
        locator = row["retainedEvidence"]
        path = Path(directory) / evidence_name(locator)
        if locator["sha256"] not in documents:
            r.require(path.stat().st_size == locator["size"] and r.file_sha(path) == locator["sha256"], "Retained native evidence changed")
            documents[locator["sha256"]] = json_file(path.resolve(), 256 * 1024 * 1024, 24 * 1024 * 1024)
        evidence = documents[locator["sha256"]]
        archive = row["retainedArchive"]
        r.require(dict(path=archive["name"], size=archive["size"], sha256=archive["sha256"]) in evidence["finalArtifacts"],
                  "Retained native evidence belongs to other archive bytes")
        runs = evidence["nativeRuns"]
        r.require(len(runs) == len(RIDS) and {run["rid"] for run in runs} == set(RIDS), "Retained archive lacks six-RID evidence")
        values = []
        for run in runs:
            packages = [package for package in run["packages"] if package["packageId"] == row["packageId"]]
            if packages:
                r.require(len(packages) == 1, "Duplicate retained source-use package")
                values.append(packages[0]["sourceRoleUses"])
            else:
                values.append(evidence["retainedSourceRoleUses"][row["packageId"]])
        r.require(all(value == values[0] for value in values), "Retained source uses differ across native RIDs")
        result[row["packageId"]] = values[0]
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("plan", "retain", "rows"))
    parser.add_argument("--source", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--directory", type=Path)
    args = parser.parse_args()
    source = args.source.resolve()
    if args.action == "plan":
        remote = releases()
        value = plan(source, os.environ["GITHUB_SHA"], os.environ["REGISTRY_API_URL"], remote)
        r.require(not args.plan.exists(), "Release plan destination already exists")
        args.plan.parent.mkdir(parents=True, exist_ok=True)
        args.plan.write_bytes(r.canonical(value) + b"\n")
        count = len(selected(value))
        resume = any(release["tag_name"] in {row["tag"] for row in selected(value)}
            and release["target_commitish"] == value["artifactSourceCommit"]
            and any(asset["name"] == PLAN for asset in release["assets"]) for release in remote)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                package_ids = [row["packageId"] for row in selected(value)]
                output.write(f"selected_count={count}\nplan_sha256={r.file_sha(args.plan)}\nresume={str(resume).lower()}\n"
                             + "packages=" + r.canonical(package_ids).decode() + "\n")
        print(f"Selected {count} version-bumped packages." if count else "No package version is newer; nothing to release.")
    else:
        value = load(args.plan, source)
        if args.action == "retain":
            r.require(args.directory is not None, "Retained archive directory required")
            retain(value, args.directory)
        else:
            print(r.encoded(selected(value)).decode())


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("Release selection rejected: " + str(error))
