#!/usr/bin/env python3
"""Read-only integration with retained real archives and the existing Core inspector.

Usage: python3 -B real-release-metadata.fixture.py <agent-root> <core-cli.dll>
No project build or source/lock mutation is performed. Temporary hardlinks and
inspection outputs are confined to a new temporary directory.
"""

import os
from pathlib import Path
import subprocess
import sys
import tempfile

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
        release.require(plan["order"][0] == "sunder.package.agent", "Real family owner/order differs")
        inspected = []
        for package_id in ("sunder.package.agent", "sunder.package.agent.provider.anthropic", "sunder.package.agent.tools.shell"):
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


if __name__ == "__main__":
    main(*sys.argv[1:])
