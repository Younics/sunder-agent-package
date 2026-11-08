#!/usr/bin/env python3
"""The single version-selected Agent publication pipeline."""
import argparse
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
import bash_runtime
import agent_cohort as cohort
import agent_cohort_receipt as receipts
import agent_publication as publication
import agent_release as r
import agent_selection as selection

ROOT = Path(__file__).resolve().parents[2]
COMMON = {selection.PLAN, "native-test-evidence.json", "package-output-audit.json", "release-provenance.json", "toolchain.txt"}
PER_RELEASE = {"SHA256SUMS", "release-sbom.spdx.json"}
REQUEST = publication.REQUEST


def source_guard(core, plan):
    source = r.digest(os.environ["GITHUB_SHA"], 40)
    r.require(os.environ["GITHUB_REPOSITORY"] == selection.REPOSITORY
        and os.environ["GITHUB_EVENT_NAME"] in ("push", "workflow_dispatch")
        and os.environ["GITHUB_REF"] == "refs/heads/main" and os.environ["GITHUB_REF_NAME"] == "main"
        and os.environ["GITHUB_WORKFLOW_SHA"] == source and os.environ["GITHUB_REF_PROTECTED"] == "true"
        and os.environ["GITHUB_WORKFLOW_REF"] == f"{selection.REPOSITORY}/{publication.WORKFLOW}@refs/heads/main",
        "Selective publication requires the exact protected-main workflow")
    r.require(plan["artifactSourceCommit"] == source and plan["lockSetSha256"] == r.locks(ROOT)
        and plan["registryOrigin"] == r.origin(os.environ["REGISTRY_API_URL"]), "Release source/locks/destination differ")
    for root, sha in ((ROOT, source), (core, r.digest(os.environ["CORE_COMMIT"], 40))):
        r.require(r.git(root, "rev-parse", "HEAD").decode().strip() == sha
            and not r.git(root, "status", "--porcelain", "--untracked-files=no").strip(), "Release checkout is not the exact clean source")
    r.require(os.environ["CORE_TAG"] in ("sdk/v" + os.environ["CORE_BUILD_VERSION"],
        "nuget-sunder-package-build/v" + os.environ["CORE_BUILD_VERSION"]), "Core build-tool release coordinates differ")
    selection.validate(plan, ROOT, source)


def stores(core, plan):
    return [(row, publication.GitHubRelease(selection.REPOSITORY, row["tag"])) for row in selection.selected(plan)]


def primary_names(plan, row, coordinator=False):
    packages = selection.selected(plan) if coordinator else [row]
    return COMMON | {selection.archive_name(package) for package in packages}


def persist(path, data):
    path = Path(path)
    if path.exists():
        r.require(r.regular(path).read_bytes() == data, "Immutable publication input changed: " + str(path))
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("xb") as output:
            output.write(data)


def verify_primary(core, directory, plan):
    source_guard(core, plan)
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/release/native-family.py"), "verify",
        str(directory / "native-test-evidence.json"), str(directory), str(ROOT / "packages.json"), str(directory / selection.PLAN),
        plan["artifactSourceCommit"], os.environ["CORE_TAG"], os.environ["CORE_COMMIT"], os.environ["CORE_BUILD_VERSION"]], check=True)
    with tempfile.TemporaryDirectory() as temporary:
        audit = Path(temporary) / "audit.json"
        subprocess.run(bash_runtime.command(ROOT / "scripts/release/generate-package-output-audit.sh", directory,
            directory / "native-test-evidence.json", ROOT, audit), check=True)
        r.require(audit.read_bytes() == (directory / "package-output-audit.json").read_bytes(), "Native output audit changed")
    return r.verify_family(ROOT, directory, plan["registryOrigin"])


def provenance(directory, plan, release_id, family):
    destination = dict(registryOrigin=plan["registryOrigin"])
    if plan["schemaVersion"] == 1:
        destination["ownerPrincipalId"] = plan["ownerPrincipalId"]
    return dict(schemaVersion=plan["schemaVersion"] + 1, buildType="https://sunderapp.io/provenance/github-actions/v1",
        invocation=dict(repository=selection.REPOSITORY, workflowPath=publication.WORKFLOW,
            workflowRef=os.environ["GITHUB_WORKFLOW_REF"], workflowSha=os.environ["GITHUB_WORKFLOW_SHA"],
            event=os.environ["GITHUB_EVENT_NAME"], repositoryId=int(os.environ["GITHUB_REPOSITORY_ID"]),
            runId=os.environ["GITHUB_RUN_ID"], runAttempt=os.environ["GITHUB_RUN_ATTEMPT"], releaseId=int(release_id)),
        artifactSourceCommit=plan["artifactSourceCommit"], planSha256=r.file_sha(directory / selection.PLAN),
        lockSetSha256=plan["lockSetSha256"], publication=destination,
        materials=[dict(name="Sunder.Package.Build", tag=os.environ["CORE_TAG"], sha=os.environ["CORE_COMMIT"], packageVersion=os.environ["CORE_BUILD_VERSION"])],
        artifacts=family["artifacts"])


def verify_provenance(directory, plan, release_id, family):
    value = r.load(directory / "release-provenance.json")
    expected = provenance(directory, plan, release_id, family)
    inv = value["invocation"]
    for field in ("releaseId", "repositoryId"):
        r.positive(inv[field])
    for field in ("runId", "runAttempt"):
        r.require(type(inv[field]) is str and re.fullmatch(r"[1-9][0-9]*", inv[field]), "Invalid original run coordinate")
        r.positive(int(inv[field]))
    r.require(inv["event"] in ("push", "workflow_dispatch"), "Unexpected original release event")
    # Recovery may be a later run of the same protected source, never a new source.
    expected["invocation"].update({key: inv[key] for key in ("runId", "runAttempt", "event")})
    r.require(value == expected and (directory / "release-provenance.json").read_bytes() == r.canonical(value) + b"\n",
              "Retained release source, invocation, materials or family bytes differ")
    return value


def files_for(directory, plan, row, coordinator=False):
    names = primary_names(plan, row, coordinator)
    records = [dict(path=name, size=(directory / name).stat().st_size, sha256=r.file_sha(directory / name)) for name in sorted(names)]
    evidence = directory / ".github" / row["key"]
    persist(evidence / "SHA256SUMS", "".join(f"{item['sha256']}  {item['path']}\n" for item in records).encode())
    sbom = dict(spdxVersion="SPDX-2.3", dataLicense="CC0-1.0", SPDXID="SPDXRef-DOCUMENT", name=row["tag"],
        documentNamespace="https://sunderapp.io/spdx/" + r.file_sha(directory / selection.PLAN) + "/" + row["key"],
        files=[dict(SPDXID=f"SPDXRef-File-{i}", fileName="./" + item["path"],
            checksums=[dict(algorithm="SHA256", checksumValue=item["sha256"])]) for i, item in enumerate(records)])
    persist(evidence / "release-sbom.spdx.json", r.canonical(sbom) + b"\n")
    files = {name: directory / name for name in names} | {name: evidence / name for name in PER_RELEASE}
    if coordinator and r.read_publications(directory, r.load(directory / "release-provenance.json")["artifacts"]):
        files[r.PUBLICATIONS] = directory / r.PUBLICATIONS
    if coordinator and (directory / REQUEST).exists():
        proposed = cohort.request(r.load(directory / REQUEST))
        files[REQUEST] = directory / REQUEST
        receipt = "agent-family-cohort-" + cohort.request_hash(proposed) + ".json"
        if (directory / receipt).exists():
            files[receipt] = directory / receipt
    return files


def stage(core, directory, plan):
    family = verify_primary(core, directory, plan)
    targets = stores(core, plan)
    r.require(targets, "Nothing selected for publication")
    for index, (row, store) in enumerate(targets):
        # Explicitly create the tag only after the tested-byte gates. A lost reply
        # is reconciled by the exact commit, never by force-updating a reference.
        try:
            store.api("git/refs", "POST", dict(ref="refs/tags/" + row["tag"], sha=plan["artifactSourceCommit"]))
        except (ValueError, subprocess.TimeoutExpired):
            pass
        publication.remote_tag(store, plan["artifactSourceCommit"])
        info = store.create(plan["artifactSourceCommit"], row["tag"])
        if index == 0:
            old = {asset["name"]: asset for asset in info["assets"]}
            for name in ("release-provenance.json", "toolchain.txt", r.PUBLICATIONS, REQUEST):
                if name in old:
                    store.download(old[name], directory / name)
            if REQUEST in old:
                receipt_name = "agent-family-cohort-" + cohort.request_hash(r.load(directory / REQUEST)) + ".json"
                if receipt_name in old:
                    store.download(old[receipt_name], directory / receipt_name)
            if not (directory / "release-provenance.json").exists():
                persist(directory / "release-provenance.json", r.canonical(provenance(directory, plan, info["id"], family)) + b"\n")
            if not (directory / "toolchain.txt").exists():
                persist(directory / "toolchain.txt", subprocess.check_output(["dotnet", "--info"]))
            verify_provenance(directory, plan, info["id"], family)
        files = files_for(directory, plan, row, index == 0)
        if info["draft"]:
            # The selection must become durable before any tested payload. A new
            # dispatch may recover it, but must never infer a fresh build instead.
            names = [selection.PLAN, *sorted(set(files) - {selection.PLAN})]
            store.upload(info["id"], [files[name] for name in names])
        store.verify(info["id"], files)
    # The first selected package also retains all selected archives. Thus a new
    # run can recover the complete tested batch without rebuilding any archive.


def recover(core, directory, plan):
    source_guard(core, plan)
    selection.retain(plan, directory)
    row, store = stores(core, plan)[0]
    info = store.resolve()
    r.require(info is not None and info["target_commitish"] == plan["artifactSourceCommit"], "Missing original coordinator release")
    info = store.get(info["id"])
    required = primary_names(plan, row, True) | PER_RELEASE
    r.require(required <= {asset["name"] for asset in info["assets"]},
        "Interrupted staging is incomplete; rerun the original failed job with its retained tested artifact, never rebuild it")
    for asset in info["assets"]:
        if asset["name"] not in PER_RELEASE:
            store.download(asset, directory / asset["name"])
    family = verify_primary(core, directory, plan)
    verify_provenance(directory, plan, info["id"], family)
    stage(core, directory, plan)


def verified(core, directory, plan):
    family = verify_primary(core, directory, plan)
    targets = stores(core, plan)
    r.require(targets, "No selected release")
    infos = []
    for index, (row, store) in enumerate(targets):
        info = store.resolve()
        r.require(info is not None, "Missing staged package release")
        publication.remote_tag(store, plan["artifactSourceCommit"])
        r.require(info["target_commitish"] == plan["artifactSourceCommit"]
            and info["prerelease"] is (not r.version(row["version"])[1]), "Package release source/version changed")
        infos.append(store.verify(info["id"], files_for(directory, plan, row, index == 0)))
    value = verify_provenance(directory, plan, infos[0]["id"], family)
    return targets, infos, value


def registry_versions(directory, plan, value, availability=None):
    selected_ids = {row["packageId"] for row in selection.selected(plan)}
    order = r.verify_family(ROOT, directory, plan["registryOrigin"])["order"]
    records = {item["packageId"]: item for item in value["artifacts"]}
    retained = r.read_publications(directory, value["artifacts"], required=plan["schemaVersion"] == 2 and availability is not None)
    observed = []
    with tempfile.TemporaryDirectory() as temporary:
        for package_id in order:
            record = records[package_id]
            expected = Path(temporary) / "expected.json"
            expected.write_bytes(r.canonical(retained.get(package_id, r.publication(record, plan.get("ownerPrincipalId")))) + b"\n")
            result = Path(temporary) / (package_id + ".json")
            current = availability if package_id in selected_ids else "Listed"
            command = bash_runtime.command(ROOT / "scripts/release/publish-registry-package.sh")
            if current:
                command += ["--verify-only", "--current-availability", current]
            subprocess.run([*command, "--initial-yanked", str(expected), "--publication-output", str(result),
                plan["registryOrigin"], str(directory / record["path"]),
                package_id, record["version"]], check=True)
            observed.append(r.publication_evidence(r.load(result), record))
    # Ownership is observed from the Registry, per package. A later run compares
    # the same immutable evidence, including owners, instead of supplying an owner.
    persist(directory / r.PUBLICATIONS, r.canonical(sorted(observed, key=lambda item: item["package"]["packageId"])) + b"\n")


def publish_versions(core, directory, plan):
    targets, infos, value = verified(core, directory, plan)
    if (directory / REQUEST).exists():
        visibility(core, directory, plan)
        return
    r.require(all(info["draft"] for info in infos), "New immutable publication requires drafts")
    registry_versions(directory, plan, value)
    row, store = targets[0]
    store.upload(infos[0]["id"], [directory / r.PUBLICATIONS])
    store.verify(infos[0]["id"], files_for(directory, plan, row, True))


def cohort_request(plan, value, revision, retained_tags=None):
    ids = {row["packageId"] for row in selection.selected(plan)}
    versions = [dict(package={key: item[key] for key in ("packageId", "version", "packageContentIdentity")},
        expectedAvailability="Yanked", availability="Listed") for item in value["artifacts"] if item["packageId"] in ids]
    versions.sort(key=lambda item: item["package"]["packageId"] + "/" + item["package"]["version"])
    tags = [dict(packageId=item["package"]["packageId"], tag="latest" if r.version(item["package"]["version"])[1] else "preview",
                 target=item["package"]) for item in versions]
    if retained_tags is not None:
        # Older supported majors can become Listed without moving a newer
        # channel backwards. Recovery keeps that exact original tag subset.
        r.require(all(tag in tags for tag in retained_tags), "Retained cohort contains a foreign channel/target")
        tags = retained_tags
    return cohort.request(dict(idempotencyKey="agent-release-" + str(value["invocation"]["releaseId"]),
        expectedPackagePolicyRevision=revision, versions=versions,
        distTags=tags))


def advancing_tags(proposed, observed):
    ids = {item["package"]["packageId"] for item in proposed["versions"]}
    current = cohort.snapshots(observed, ids)
    return [tag for tag in proposed["distTags"] if tag["tag"] not in current[tag["packageId"]]
            or r.version(tag["target"]["version"]) >= r.version(current[tag["packageId"]][tag["tag"]]["target"]["version"])]


def visibility(core, directory, plan):
    targets, infos, value = verified(core, directory, plan)
    r.read_publications(directory, value["artifacts"], required=plan["schemaVersion"] == 2)
    _, store = targets[0]
    info = infos[0]
    path = directory / REQUEST
    if not path.exists():
        r.require(all(info["draft"] for info in infos), "An announcement has no retained cohort request")
        registry_versions(directory, plan, value, "Yanked")
        proposed = cohort_request(plan, value, 1)
        tags = advancing_tags(proposed, cohort.current_tags(plan["registryOrigin"], {item["package"]["packageId"] for item in proposed["versions"]}))
        subjects = sorted([change["package"] for change in proposed["versions"]],
                          key=lambda item: (item["packageId"], item["version"], item["packageContentIdentity"]))
        synchronization = dict(nonce=secrets.token_hex(16), packages=subjects, platformCatalogs=[], minimumPackagePolicy=None)
        status, data, failed = cohort.http(plan["registryOrigin"], "/api/v1/policy/synchronize", r.encoded(synchronization))
        r.require(not failed and status == 200, "Cannot obtain the current Registry policy fence")
        policy = cohort.policy_snapshot(r.decode(data), plan["registryOrigin"], synchronization)
        r.require(all(item["packageState"] == "Active" and item["versionAvailability"] == "Yanked" for item in policy["packages"]),
            "Selected cohort is not active and Yanked")
        cohort.store(path, cohort_request(plan, value, policy["packagePolicy"]["revision"], tags))
        store.upload(info["id"], [path])
    proposed = cohort.request(r.load(path))
    r.require(proposed == cohort_request(plan, value, proposed["expectedPackagePolicyRevision"], proposed["distTags"]), "Retained selected cohort differs")
    receipt_name = "agent-family-cohort-" + cohort.request_hash(proposed) + ".json"
    with tempfile.TemporaryDirectory() as temporary:
        result_path = Path(temporary) / "result.json"
        if (directory / receipt_name).exists():
            receipt = receipts.download_bytes(selection.REPOSITORY, str(info["id"]), store.tag, cohort.request_hash(proposed))
            r.require(receipt["request"] == proposed and receipt["registryOrigin"] == plan["registryOrigin"]
                and receipt["artifactSourceCommit"] == plan["artifactSourceCommit"], "Retained cohort result differs")
            cohort.store(result_path, receipt["result"])
        else:
            r.require(info["draft"], "Published release has no retained result")
            os.environ["SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON"] = r.encoded([sys.executable, "-B", str(Path(__file__).resolve()),
                "guard", "--core", str(core), "--directory", str(directory)]).decode()
            cohort.apply(plan["registryOrigin"], path, result_path)
            receipt = dict(schemaVersion=2, registryOrigin=plan["registryOrigin"], artifactSourceCommit=plan["artifactSourceCommit"],
                releaseId=info["id"], request=proposed, result=r.load(result_path))
            receipts.check(receipt)
            cohort.store(directory / receipt_name, receipt)
            store.upload(info["id"], [directory / receipt_name])
            r.require(receipts.download_bytes(selection.REPOSITORY, str(info["id"]), store.tag, cohort.request_hash(proposed)) == receipt,
                "Durable cohort readback differs")
        cohort.confirm(plan["registryOrigin"], path, result_path, directory)
    registry_versions(directory, plan, value, "Listed")


def finalize(core, directory, plan):
    visibility(core, directory, plan)
    targets, infos, value = verified(core, directory, plan)
    publication.install_plans(directory, value)  # all fifteen exact tuples, every RID
    # Keep the coordinator draft until the other package announcements finish.
    for index in [*range(1, len(targets)), 0]:
        row, store = targets[index]
        info = infos[index]
        source_guard(core, plan)
        publication.remote_tag(store, plan["artifactSourceCommit"])
        current = store.get(info["id"])
        r.require(current["assets"] == info["assets"] and current["target_commitish"] == plan["artifactSourceCommit"], "Release changed before announcement")
        if current["draft"]:
            try:
                store.api("releases/" + str(info["id"]), "PATCH", dict(draft=False, make_latest="false"))
            except (ValueError, subprocess.TimeoutExpired):
                pass
        store.verify(info["id"], files_for(directory, plan, row, index == 0), draft=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("stage", "recover", "publish", "visibility", "guard", "finalize"))
    parser.add_argument("--core", type=Path, required=True)
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    directory, core = args.directory.resolve(), args.core.resolve(strict=True)
    plan = selection.load(directory / selection.PLAN, ROOT)
    if args.action == "stage": stage(core, directory, plan)
    elif args.action == "recover": recover(core, directory, plan)
    elif args.action == "publish": publish_versions(core, directory, plan)
    elif args.action == "visibility": visibility(core, directory, plan)
    elif args.action == "finalize": finalize(core, directory, plan)
    else:
        targets, infos, value = verified(core, directory, plan)
        r.require(infos[0]["draft"], "Cohort mutation requires its coordinator draft")
        proposed = cohort.request(r.load(directory / REQUEST))
        r.require(proposed == cohort_request(plan, value, proposed["expectedPackagePolicyRevision"], proposed["distTags"]), "Cohort source binding differs")


if __name__ == "__main__":
    try: main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("Selective publication rejected: " + str(error))
