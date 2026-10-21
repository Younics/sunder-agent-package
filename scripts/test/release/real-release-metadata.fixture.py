#!/usr/bin/env python3
"""Read-only integration with retained real archives and the existing Core inspector.

Usage: python3 -B real-release-metadata.fixture.py <agent-root> <core-cli.dll>
       python3 -B real-release-metadata.fixture.py --local-handoff <agent-root> <handoff.json>
No project build or source/lock mutation is performed. Temporary hardlinks and
inspection outputs are confined to a new temporary directory.
The local-handoff mode reads retained Core inspections/qualifications only. It
does not launch .NET, contact a Registry, or claim native/release qualification.
"""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_release as release


def main(source, cli):
    source, cli = Path(source).resolve(), release.regular(cli).resolve()
    family = release.inventory(source)
    before = release.locks(source)
    with tempfile.TemporaryDirectory(prefix="agent-real-metadata-") as temporary:
        output = Path(temporary).resolve()
        for package in family["packages"]:
            project = Path(package["projectPath"])
            archive = source / project.parent / "bin/Debug/net10.0" / f"{project.stem}.{family['releaseVersion']}.sunderpkg"
            release.regular(archive)
            expected_name = f"{package['packageId']}.{family['releaseVersion']}.sunderpkg"
            os.link(archive, output / expected_name)
        plan = release.family_plan(source, output, "https://hub.sunderapp.io/", "1" * 32)
        release.require(plan["order"][0] == "younics.ai.agent", "Real family owner/order differs")
        inspected = []
        for package_id in ("younics.ai.agent", "younics.ai.agent.provider.anthropic", "younics.ai.agent.tools.shell"):
            archive = output / f"{package_id}.{family['releaseVersion']}.sunderpkg"
            inspection = output / (package_id + "-api")
            subprocess.run(["dotnet", str(cli), "dev", "package", "validate", str(archive), "--api-output", str(inspection)], check=True)
            receipt = release.load(inspection / "inspection.json")
            manifest, public, inventory_sha = release.archive_metadata(archive)
            release.require(receipt["archiveSha256"] == release.file_sha(archive)
                            and receipt["inventory"] == public and receipt["publicInventorySha256"] == inventory_sha,
                            "Release metadata differs from the actual Core Format inspection")
            runtime = [t for t in manifest["targets"] if t["role"] == "runtime"]
            app = [t for t in manifest["targets"] if t["role"] == "app"]
            inspected.append({"packageId": package_id, "publicContractCount": public["publicContractCount"],
                              "packageContentIdentity": public["packageContentIdentity"], "publicInventorySha256": inventory_sha,
                              "workerFiles": [t["entryPoint"] for t in runtime], "appFiles": [t["entryPoint"] for t in app],
                              "sdkVersions": sorted({t["sdkVersion"] for t in runtime})})
        release.require(release.locks(source) == before, "Read-only metadata inspection changed a lock")
        print(release.encoded({"familyArchives": 15, "coreInspections": inspected, "lockSetUnchanged": before}).decode())


def local_handoff(source, handoff_path):
    source, handoff_path = Path(source).resolve(), release.regular(handoff_path).resolve()
    handoff = release.load(handoff_path)
    release.require(handoff["agentRoot"] == str(source) and handoff["scope"] == "local-unpublished-current-RID",
                    "Expected an explicit current local unpublished handoff")
    validation_path = release.regular(handoff["validation"])
    validation = release.load(validation_path)
    release.require(validation["nativeReleaseQualification"] is False and validation["registryOrPublicationMutation"] is False,
                    "Local handoff cannot substitute for release or publication authority")
    inventory_path = source / "compatibility/agent-current-inventory.json"
    expected, family = release.load(inventory_path), release.inventory(source)
    release.require(release.file_sha(inventory_path) == validation["inventorySha256"]
                    and release.file_sha(source / "packages.json") == validation["packageInventorySha256"], "Current inventory changed after handoff")
    reviewed = {row["key"]: row for row in expected["packages"]}
    rows = {row["packageId"]: row for row in validation["packages"]}
    release.require(len(rows) == len(validation["packages"]) == len(reviewed) == 15
                    and set(rows) == {row["packageId"] for row in family["packages"]}, "Handoff must contain the exact current 15-package family")
    archives = Path(handoff["archiveRoot"])
    release.require({path.name for path in archives.glob("*.sunderpkg")} == {
        row["packageId"] + "." + family["releaseVersion"] + ".sunderpkg" for row in family["packages"]}, "Unexpected local archive inventory")
    before = release.locks(source)
    pinned = {path: release.file_sha(path) for path in (handoff_path, validation_path, inventory_path, source / "packages.json")}

    def pin(path, expected):
        release.require(path not in pinned or pinned[path] == expected, "Conflicting qualified input hashes across local family")
        pinned[path] = expected

    evidence = {}
    for package in family["packages"]:
        package_id, project = package["packageId"], Path(package["projectPath"])
        row, identity = rows[package_id], reviewed[package["key"]]
        release.require(identity["packageId"] == package_id and identity["projectPath"] == package["projectPath"], "Current package coordinates differ")
        archive = archives / f"{package_id}.{family['releaseVersion']}.sunderpkg"
        pin(archive, row["archiveSha256"])
        release.require(release.file_sha(archive) == pinned[archive], "Local archive changed after Core inspection")
        manifest, public, public_sha = release.archive_metadata(archive)
        inspection = release.load(validation_path.parent / "inspections" / package["key"] / "inspection.json")
        release.require(inspection["archiveSha256"] == pinned[archive] and inspection["inventory"] == public
                        and inspection["publicInventorySha256"] == public_sha == row["publicInventorySha256"]
                        and public["packageContentIdentity"] == row["packageContentIdentity"]
                        and public["publicContractCount"] == row["publicContractCount"], "Retained Core inspection differs")
        qualification_path = validation_path.parent / "final" / package["key"] / "role-inputs.v1.json"
        pin(qualification_path, row["roleInputsSha256"])
        release.require(release.file_sha(qualification_path) == pinned[qualification_path], "Qualified source-use evidence changed after handoff")
        qualification = release.load(qualification_path)
        frame = qualification["inputs"]
        release.require(frame["packageId"] == package_id and frame["packageVersion"] == family["releaseVersion"]
                        and frame["project"] == project.relative_to("src").as_posix()
                        and frame["configuration"] == handoff["configuration"] and frame["runtimeIdentifier"] == handoff["rid"],
                        "Local source qualification coordinates differ")
        for field in ("ownedBaseline", "contractLock"):
            path = release.qualified_owned_baseline(source / "src", frame) if field == "ownedBaseline" else release.source_file(source / "src", frame[field])
            if path is not None:
                pin(path, frame[field + "Sha256"])
        # Authenticate the complete current source frame, including sibling inputs.
        tree = {item["path"]: item for item in frame["sources"]}
        for name, item in tree.items():
            pin(release.source_file(source / "src", name), item["sha256"])
        pin(release.source_file(source / "src", frame["project"]), tree[frame["project"]]["sha256"])
        tracked = {item["path"]: item for item in frame["externalInputs"]}
        for item in frame["items"]:
            if item["kind"] in ("compile", "resource"):
                key = item["kind"] + ":" + item["identity"]
                release.require(key in tracked, "Evaluated local input is absent from qualified external inputs: " + key)
                pin(release.source_file(source / "src", item["identity"]), tracked[key]["sha256"])
        evidence[package_id] = release.source_role_use_evidence(qualification)
        with zipfile.ZipFile(archive) as packed:
            content = packed.read("manifest/sunder-package.json")
        qualified_manifest = [item for item in qualification["files"] if item["path"] == "manifest/sunder-package.json"]
        release.require(len(qualified_manifest) == 1 and qualified_manifest[0]["sha256"] == release.sha(content),
                        "Archive manifest differs from source-qualified bytes")
        release.require(manifest["name"] == package["releaseTitle"] and {target["rid"] for target in manifest["targets"]} == {handoff["rid"]},
                        "Local manifest display/RID differs")
    release.require(all(release.file_sha(path) == expected for path, expected in pinned.items()), "Local source/evidence bytes differ from qualification")
    verified = release.verify_family(source, archives, registry=None, source_role_uses=evidence)
    release.require(release.locks(source) == before and all(release.file_sha(path) == expected for path, expected in pinned.items()),
                    "Read-only verification inputs changed")
    print(release.encoded(dict(scope="local-unpublished-current-RID", rid=handoff["rid"], familyArchives=len(verified["artifacts"]),
                              coreInspections=len(rows), lockSetUnchanged=before, order=verified["order"],
                              sourceRoleUses={key: value["roleUses"] for key, value in evidence.items() if value["roleUses"]},
                               fullSourceTreeRequalification=True, nativeReleaseQualification=False, registryOrPublicationMutation=False)).decode())


if __name__ == "__main__":
    if sys.argv[1:2] == ["--local-handoff"]:
        local_handoff(*sys.argv[2:])
    else:
        main(*sys.argv[1:])
