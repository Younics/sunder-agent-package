#!/usr/bin/env python3
"""Integrated immutable publication, atomic family visibility and announcement."""
import argparse
import importlib
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
from urllib.parse import quote, urlsplit

sys.dont_write_bytecode = True
import agent_cohort as cohort
import agent_cohort_receipt as receipts
import agent_release as r

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ".github/workflows/sunder-package-release.yml"
REPOSITORY = "Younics/sunder-agent-package"
REQUEST = "agent-family-cohort-request.json"
EVIDENCE = {"SHA256SUMS", "release-provenance.json", "release-sbom.spdx.json", "toolchain.txt"}


def source_guard(core):
    source, core_sha = r.digest(os.environ["GITHUB_SHA"], 40), r.digest(os.environ["CORE_COMMIT"], 40)
    tag = "agent/v" + r.inventory(ROOT)["releaseVersion"]
    r.require(os.environ["GITHUB_REPOSITORY"] == REPOSITORY and os.environ["GITHUB_EVENT_NAME"] == "push"
              and os.environ["GITHUB_REF"] == "refs/tags/" + tag and os.environ["GITHUB_REF_NAME"] == tag
              and os.environ["GITHUB_WORKFLOW_SHA"] == source
              and os.environ["GITHUB_WORKFLOW_REF"] == f"{REPOSITORY}/{WORKFLOW}@refs/tags/{tag}"
              and os.environ["GITHUB_REF_PROTECTED"] == "true", "Publication requires the exact protected tag workflow")
    r.require(source == os.environ["SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT"]
              and r.locks(ROOT) == os.environ["SUNDER_AGENT_LOCK_SET_SHA256"], "Approved source or lock set differs")
    approval = urlsplit(os.environ["SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE"])
    r.require(approval.scheme == "https" and approval.hostname and approval.path not in ("", "/")
              and not approval.username and not approval.password and not approval.fragment, "Release approval reference is missing")
    for root, sha in ((ROOT, source), (core, core_sha)):
        r.require(r.git(root, "rev-parse", "HEAD").decode().strip() == sha
                  and not r.git(root, "status", "--porcelain", "--untracked-files=no").strip(), "Release checkout is not the exact clean source")
    r.require(os.environ["CORE_TAG"] == "sdk/v" + os.environ["CORE_SDK_VERSION"], "Core release coordinates differ")
    return source, tag


def transport(core):
    # The workflow checks out the verified final Core SDK commit. Share its one
    # numeric-ID, immutable-byte transport rather than maintaining another client.
    sys.path.insert(0, str(Path(core).resolve() / "scripts/release"))
    return importlib.import_module("github_release")


def remote_tag(store, source):
    target = store.api("git/ref/tags/" + quote(store.tag, safe=""))["object"]
    for _ in range(9):
        r.digest(target["sha"], 40)
        if target["type"] == "commit":
            r.require(target["sha"] == source, "Agent tag moved away from its artifact source")
            return
        r.require(target["type"] == "tag", "Invalid tag target")
        target = store.api("git/tags/" + target["sha"])["object"]
    raise r.Invalid("Excessive tag indirection")


def primary_names():
    inventory = r.inventory(ROOT)
    return {f"{p['packageId']}.{inventory['releaseVersion']}.sunderpkg" for p in inventory["packages"]} | {
        "native-test-evidence.json", "package-output-audit.json"}


def verify_primary(directory):
    directory = Path(directory)
    version = r.inventory(ROOT)["releaseVersion"]
    subprocess.run([sys.executable, "-B", str(ROOT / "scripts/release/native-family.py"), "verify",
                    str(directory / "native-test-evidence.json"), str(directory), str(ROOT / "packages.json"), version,
                    os.environ["GITHUB_SHA"], os.environ["CORE_TAG"], os.environ["CORE_COMMIT"], os.environ["CORE_SDK_VERSION"]], check=True)
    with tempfile.TemporaryDirectory() as work:
        audit = Path(work) / "audit.json"
        subprocess.run(["bash", str(ROOT / "scripts/release/generate-package-output-audit.sh"), str(directory),
                        str(directory / "native-test-evidence.json"), str(ROOT), str(audit)], check=True)
        r.require(audit.read_bytes() == (directory / "package-output-audit.json").read_bytes(), "Native output audit changed")
    return r.family_plan(ROOT, directory, r.origin(os.environ["REGISTRY_API_URL"].rstrip("/")), os.environ["SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID"])


def verify_evidence(directory, release_id):
    directory = Path(directory)
    value = r.validate_provenance(ROOT, directory)
    inv = value["invocation"]
    r.require(inv["releaseId"] == int(release_id) and inv["sha"] == os.environ["GITHUB_SHA"]
              and inv["runId"] == os.environ["GITHUB_RUN_ID"]
              and int(inv["runAttempt"]) <= int(os.environ["GITHUB_RUN_ATTEMPT"])
              and value["controlPlane"]["repositoryId"] == int(os.environ["GITHUB_REPOSITORY_ID"])
              and value["materials"] == [dict(name="sunder-core-sdk", tag=os.environ["CORE_TAG"], sha=os.environ["CORE_COMMIT"], packageVersion=os.environ["CORE_SDK_VERSION"])]
              and value["publication"] == dict(registryOrigin=r.origin(os.environ["REGISTRY_API_URL"].rstrip("/")),
                                                ownerPrincipalId=os.environ["SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID"]), "Release invocation/materials differ")
    expected = primary_names()
    sums = sorted(f"{r.file_sha(directory / name)}  {name}" for name in expected)
    r.require(sorted((directory / "SHA256SUMS").read_text().splitlines()) == sums, "Checksum inventory differs")
    sbom = r.load(directory / "release-sbom.spdx.json")
    r.require(sbom["spdxVersion"] == "SPDX-2.3" and sbom["dataLicense"] == "CC0-1.0"
              and len(sbom["files"]) == len(expected), "Invalid SBOM")
    for name in expected:
        matches = [item for item in sbom["files"] if item["fileName"] == "./" + name]
        r.require(len(matches) == 1 and matches[0]["checksums"] == [dict(algorithm="SHA256", checksumValue=r.file_sha(directory / name))], "SBOM bytes differ")
    return value


def stage(core, directory):
    source, tag = source_guard(core)
    helper = transport(core); store = helper.GitHubRelease(REPOSITORY, tag)
    remote_tag(store, source)
    directory = Path(directory).resolve()
    files = helper.local_files(directory)
    r.require(set(files) - EVIDENCE == primary_names(), "Expected exactly the 15 tested archives and two native reports")
    verify_primary(directory)
    info = store.create(source, "AI Agent Family " + r.inventory(ROOT)["releaseVersion"])
    r.require(info["draft"], "Published Agent versions cannot be restaged")
    old = {item["name"]: item for item in info["assets"]}
    r.require(old.keys() <= primary_names() | EVIDENCE, "Unexpected retained publication assets")
    for name in sorted(old.keys() & EVIDENCE): store.download(old[name], directory / name)
    files = helper.local_files(directory)
    if not EVIDENCE <= files.keys():
        env = dict(os.environ, GITHUB_RELEASE_ID=str(info["id"]), SUNDER_CORE_TAG=os.environ["CORE_TAG"],
                   SUNDER_CORE_COMMIT=os.environ["CORE_COMMIT"], SUNDER_CORE_PACKAGE_VERSION=os.environ["CORE_SDK_VERSION"])
        if "release-provenance.json" in files: env["GITHUB_RUN_ATTEMPT"] = r.load(files["release-provenance.json"])["invocation"]["runAttempt"]
        with tempfile.TemporaryDirectory(dir=directory) as temporary:
            for name in sorted(primary_names()): os.link(files[name], Path(temporary) / name)
            subprocess.run(["bash", str(ROOT / "scripts/release/generate-release-evidence.sh"), temporary,
                            "sunder-agent-package=" + source, "sunder-core=" + os.environ["CORE_COMMIT"]], cwd=ROOT, env=env, check=True)
            for name in EVIDENCE - files.keys():
                with (directory / name).open("xb") as output: output.write((Path(temporary) / name).read_bytes())
    verify_evidence(directory, info["id"])
    files = helper.local_files(directory)
    store.upload(info["id"], list(files.values()))
    store.verify(info["id"], files, draft=True)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output: output.write(f"release_id={info['id']}\n")
    return info["id"]


def verified(core, directory, release_id):
    source, tag = source_guard(core)
    helper = transport(core); store = helper.GitHubRelease(REPOSITORY, tag)
    remote_tag(store, source)
    files = helper.local_files(directory)
    base = primary_names() | EVIDENCE
    extras = set(files) - base
    r.require(base <= files.keys() and extras <= {REQUEST} | {name for name in extras if name.startswith("agent-family-cohort-") and name.endswith(".json")}, "Unexpected publication files")
    if extras:
        r.require(REQUEST in extras, "Cohort result has no retained request")
        request_sha = cohort.request_hash(r.load(files[REQUEST]))
        r.require(extras <= {REQUEST, f"agent-family-cohort-{request_sha}.json"}, "Cohort asset does not bind the retained request")
    value = verify_evidence(directory, release_id)
    verify_primary(directory)
    info = store.verify(release_id, files)
    r.require(info["target_commitish"] == source and info["prerelease"] is ("-" in tag), "Release source/version changed")
    return store, info, value


def registry_versions(directory, provenance, availability=None):
    registry = provenance["publication"]["registryOrigin"]
    plan = r.family_plan(ROOT, directory, registry, provenance["publication"]["ownerPrincipalId"])
    with tempfile.TemporaryDirectory() as temporary:
        for package_id in plan["order"]:
            publication = next(item for item in plan["publications"] if item["package"]["packageId"] == package_id)
            expected = Path(temporary) / "expected.json"
            expected.write_bytes(r.canonical(publication) + b"\n")
            command = ["bash", str(ROOT / "scripts/release/publish-registry-package.sh")]
            if availability: command += ["--verify-only", "--current-availability", availability]
            version = publication["package"]["version"]
            subprocess.run([*command, "--initial-yanked", str(expected), registry,
                            str(Path(directory) / f"{package_id}.{version}.sunderpkg"), package_id, version], check=True)


def cohort_request(provenance, policy_revision):
    versions = [dict(package={key: item[key] for key in ("packageId", "version", "packageContentIdentity")},
                     expectedAvailability="Yanked", availability="Listed") for item in provenance["artifacts"]]
    versions.sort(key=lambda item: item["package"]["packageId"] + "/" + item["package"]["version"])
    tag = "latest" if r.version(versions[0]["package"]["version"])[1] else "preview"
    return cohort.request(dict(idempotencyKey="agent-release-" + str(provenance["invocation"]["releaseId"]),
        expectedPackagePolicyRevision=policy_revision, versions=versions,
        distTags=[dict(packageId=item["package"]["packageId"], tag=tag, target=item["package"]) for item in versions]))


def visibility(core, directory, release_id):
    directory = Path(directory).resolve()
    store, info, provenance = verified(core, directory, release_id)
    registry = provenance["publication"]["registryOrigin"]
    path = directory / REQUEST
    if not info["draft"]:
        # A final PATCH/readback can lose its acknowledgement. A rerun of the
        # release job reaches this stage first; validate its retained transition
        # without issuing a new visibility mutation before final verification.
        proposed = cohort.request(r.load(path))
        r.require(proposed == cohort_request(provenance, proposed["expectedPackagePolicyRevision"]), "Published cohort request differs")
        receipt = receipts.download_bytes(REPOSITORY, str(release_id), store.tag, cohort.request_hash(proposed))
        r.require(receipt["request"] == proposed and receipt["artifactSourceCommit"] == provenance["artifactSourceCommit"]
                  and receipt["registryOrigin"] == registry, "Published cohort binding differs")
        cohort.verify_current(registry, proposed, receipt["result"])
        return
    if not path.exists():
        registry_versions(directory, provenance, "Yanked")
        nonce = secrets.token_hex(16)
        subjects = sorted([{key: item[key] for key in ("packageId", "version", "packageContentIdentity")}
                           for item in provenance["artifacts"]], key=lambda item: (item["packageId"], item["version"], item["packageContentIdentity"]))
        synchronization = dict(nonce=nonce, packages=subjects, platformCatalogs=[], minimumPackagePolicy=None)
        status, data, failed = cohort.http(registry, "/api/v1/policy/synchronize", r.encoded(synchronization))
        r.require(not failed and status == 200, "Cannot obtain the current Registry policy fence")
        policy = cohort.policy_snapshot(r.decode(data), registry, synchronization)
        r.require(all(item["packageState"] == "Active" and item["versionAvailability"] == "Yanked" for item in policy["packages"]),
                  "The complete family is not active and Yanked")
        cohort.store(path, cohort_request(provenance, policy["packagePolicy"]["revision"]))
        store.upload(release_id, [path])
    proposed = cohort.request(r.load(path))
    r.require(proposed == cohort_request(provenance, proposed["expectedPackagePolicyRevision"]), "Retained request differs from the complete family")
    result_path = directory / ("agent-family-cohort-" + cohort.request_hash(proposed) + ".json")
    with tempfile.TemporaryDirectory() as temporary:
        result = Path(temporary) / "result.json"
        if result_path.exists():
            receipt = r.load(result_path); receipts.check(receipt)
            cohort.store(result, receipt["result"])
        os.environ["SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON"] = r.encoded([sys.executable, "-B", str(Path(__file__).resolve()),
            "guard", "--core", str(core), "--directory", str(directory), "--release-id", str(release_id)]).decode()
        cohort.apply(registry, path, result)
        receipts.upload(REPOSITORY, str(release_id), store.tag, registry, provenance["artifactSourceCommit"], str(path), str(result))
        retained = receipts.download_bytes(REPOSITORY, str(release_id), store.tag, cohort.request_hash(proposed))
        cohort.store(result_path, retained)
        # Fresh policy synchronization plus an idempotent owner readback proves
        # this exact request committed. No synthetic acceptance decision is issued.
        cohort.confirm(registry, path, result, directory)
    registry_versions(directory, provenance, "Listed")


def install_plans(directory, provenance):
    """Check anonymous current-RID projections for every exact Listed family tuple."""
    manifests = {item["packageId"]: r.archive_metadata(Path(directory) / item["path"])[0] for item in provenance["artifacts"]}
    identities = {(item["packageId"], item["version"], item["packageContentIdentity"]) for item in provenance["artifacts"]}
    capabilities = sorted({"core.v1", "worker-protocol.v1", "sdk-baseline-1-1.v1"} |
                          {capability for manifest in manifests.values() for target in manifest["targets"] for capability in target.get("requiredHostCapabilities", [])})
    for rid in ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"):
        targets = {key: [dict(role=t["role"], rid=rid) for t in manifest["targets"] if t["rid"] == rid] for key, manifest in manifests.items()}
        payload = dict(packages=[dict(packageId=key, version=manifest["version"], desiredTargets=targets[key]) for key, manifest in sorted(manifests.items())],
                       installedPackages=[], includePrerelease=not r.version(r.inventory(ROOT)["releaseVersion"])[1],
                       compatibility=dict(manifestVersions=[1], runtimeTargetKinds=["worker"], workerProtocol="worker-protocol.v1",
                                          workerCapabilities=capabilities, sdkBaseline="sdk-baseline-1-1.v1", rid=rid))
        status, data, failed = cohort.http(provenance["publication"]["registryOrigin"], "/api/v1/packages/resolve-package-changes", r.encoded(payload))
        r.require(not failed and status == 200, "Public install-plan resolution failed for " + rid)
        value = r.decode(data)
        r.require(value["success"] is True and not value["errors"] and not value["conflicts"]
                  and len(value["items"]) == 15
                  and {(item["packageId"], item["version"], item["packageContentIdentity"]) for item in value["items"]} == identities,
                  "Install plan does not select the exact Listed family")
        for item in value["items"]:
            expected = {(target["role"], rid) for target in targets[item["packageId"]]}
            r.require({(target["role"], target["rid"]) for target in item["targets"]} == expected
                      and {(artifact["kind"], artifact["rid"]) for artifact in item["artifacts"]} == expected | {("shared", None)},
                      "Install plan omitted a role or selected another RID")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("stage", "publish", "visibility", "guard", "finalize"))
    parser.add_argument("--core", type=Path, required=True)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--release-id")
    args = parser.parse_args()
    core = args.core.resolve(strict=True)
    if args.action == "stage": stage(core, args.directory); return
    if args.action == "visibility": visibility(core, args.directory, args.release_id); return
    store, info, provenance = verified(core, args.directory, args.release_id)
    if args.action == "publish":
        r.require(info["draft"], "Immutable publication requires a draft")
        registry_versions(args.directory, provenance)
    elif args.action == "guard":
        r.require(info["draft"], "Cohort mutation requires a draft")
        proposed = r.load(args.directory / REQUEST)
        r.require(proposed == cohort_request(provenance, proposed["expectedPackagePolicyRevision"]), "Cohort source binding differs")
    else:
        proposed = r.load(args.directory / REQUEST)
        receipt = receipts.download_bytes(REPOSITORY, args.release_id, store.tag, cohort.request_hash(proposed))
        r.require(receipt["artifactSourceCommit"] == provenance["artifactSourceCommit"] and receipt["request"] == proposed
                  and receipt["registryOrigin"] == provenance["publication"]["registryOrigin"], "Cohort release binding differs")
        with tempfile.TemporaryDirectory() as temporary:
            result = Path(temporary) / "result.json"
            cohort.store(result, receipt["result"])
            cohort.confirm(receipt["registryOrigin"], args.directory / REQUEST, result, args.directory)
        registry_versions(args.directory, provenance, "Listed")
        install_plans(args.directory, provenance)
        remote_tag(store, provenance["artifactSourceCommit"])
        current = store.get(args.release_id)
        r.require(current["assets"] == info["assets"] and current["target_commitish"] == info["target_commitish"], "Release changed before announcement")
        if current["draft"]:
            try: store.api("releases/" + str(args.release_id), "PATCH", dict(draft=False, make_latest="false"))
            except (ValueError, subprocess.TimeoutExpired): pass
        store.verify(args.release_id, transport(core).local_files(args.directory), draft=False)


if __name__ == "__main__":
    try: main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("Agent publication rejected: " + str(error))
