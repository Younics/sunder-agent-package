#!/usr/bin/env python3
"""Fresh schema-3 handoff for the promotion fixture's synthetic archive bytes.

Runs the native owner's real capture and final verification. Core compiler/role
receipt semantics remain owned by Core's tests; these are explicitly mock bytes.
"""

import importlib.util
from pathlib import Path
import sys
import zipfile

import native_release_inputs as native

SPEC = importlib.util.spec_from_file_location("native_family", Path(__file__).with_name("native-family.py"))
family = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(family)


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else native.compact(data))


def seed(source, assets, work, agent_sha, core_sha):
    source, assets, work = Path(source).resolve(), Path(assets).resolve(), Path(work).resolve()
    rows = native.json_file(source / "packages.json")["packages"]
    version = native.json_file(source / "packages.json")["releaseVersion"]
    runs = []
    for rid in native.RIDS:
        for row in rows:
            package_id, project = row["packageId"], row["projectPath"]
            directory = source / Path(project).parent
            write(source / project, b"<Project />")
            write(directory / "Source.cs", b"// native fixture source\n")
            write(directory / "sunder-contracts.owned.json", {"packageId": package_id, "contracts": {}})
            write(directory / "sunder-contracts.lock.json", {})
            with zipfile.ZipFile(assets / f"{package_id}.{version}.sunderpkg") as archive:
                manifest = native.parse(archive.read("manifest/sunder-package.json"))
                manifest["targets"] = [t for t in manifest["targets"] if t["rid"] == rid]
                files = {name: archive.read(name) for name in archive.namelist()
                         if name.startswith((f"payload/runtime/{rid}/", "payload/runtime/shared/", "payload/shared/", f"payload/app/{rid}/", "payload/app/shared/"))}
            files["manifest/sunder-package.json"] = native.compact(manifest)
            files["manifest/content-index.json"] = native.compact({"schemaVersion": 1, "files": [
                {"path": name, "sha256": native.digest(data), "size": len(data)} for name, data in sorted(files.items())]})
            bundle = directory / "bin/Release/net10.0/sunder-targets" / rid
            for name, data in files.items():
                write(bundle / "sunder-dev" / name, data)
            fingerprint = native.digest(b"synthetic native fixture frame")
            receipt = {"schemaVersion": 1, "producer": {"name": "Sunder.Package.Build", "version": "1.1.0", "language": "csharp"},
                       "inputFingerprint": fingerprint, "lockSha256": native.digest((directory / "sunder-contracts.lock.json").read_bytes()),
                       "packageId": package_id, "packageVersion": version, "contracts": [], "packageArtifacts": [], "bundles": [],
                       "packageArtifactImports": [], "packageApiImports": [], "providerImports": [], "platformImports": [], "providers": [], "uses": []}
            evidence = {"schemaVersion": 1, "inputs": {"packageId": package_id, "packageVersion": version,
                "project": project, "projectIdentity": Path(project).stem, "configuration": "Release", "targetFramework": "net10.0",
                "runtimeIdentifier": rid, "assemblyName": Path(project).stem,
                "ownedBaseline": str(Path(project).parent / "sunder-contracts.owned.json"),
                "ownedBaselineSha256": native.digest((directory / "sunder-contracts.owned.json").read_bytes()),
                "contractLock": str(Path(project).parent / "sunder-contracts.lock.json"), "contractLockSha256": receipt["lockSha256"],
                "properties": {}, "items": [], "sources": [{"path": str(Path(project).parent / "Source.cs"),
                    "bytes": len(b"// native fixture source\n"), "sha256": native.digest(b"// native fixture source\n")}], "toolset": [], "externalInputs": []},
                "rolePlan": {"inputFingerprint": fingerprint, "nativePlanSha256": native.digest(b"fixture plan"), "hasApp": False,
                    "roles": [{"role": "worker", "libraries": [], "generatedInputs": [], "references": [], "runtimeReferences": [], "receipt": native.compact(receipt).decode()}]},
                "leaf": "sunder-dev", "files": [{"path": name, "bytes": len(data), "sha256": native.digest(data)} for name, data in sorted(files.items())]}
            raw = native.compact(evidence)
            write(bundle / native.ROLE_INPUTS, raw)
            write(bundle / (native.ROLE_INPUTS + ".sha256"), (native.digest(raw) + "\n").encode())
            archive_path = work / rid / "archives" / f"{package_id}.{version}.sunderpkg"
            archive_path.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(archive_path, "w") as archive:
                for name, data in sorted(files.items()):
                    archive.writestr(zipfile.ZipInfo(name), data)
            metric = {"measurement": {"formatVersion": 1, "profile": "dotnetWorker", "rid": rid, "archive": archive_path.name,
                "archiveBytes": archive_path.stat().st_size, "capturedAtUtc": "2026-10-05T00:00:00Z", "currentRidInstalledBytes": 1000,
                "startupMilliseconds": 25, "workerProcessCount": 1, "workerRssBytes": 4096},
                "thresholds": {"expectedWorkerProcessCount": 1, "maximumArchiveBytes": 268435456, "maximumCurrentRidInstalledBytes": 671088640,
                "maximumStartupMilliseconds": 10000, "maximumWorkerRssBytes": 1073741824}}
            write(work / rid / "metrics" / f"{package_id}.{rid}.json", metric)
        family.capture([rid, version, agent_sha, "sdk/v1.1.0", core_sha, "1.1.0", str(work / rid / "archives"),
                        str(work / rid / "metrics"), str(work / "handoff"), str(source)])
        runs.append(native.json_file(work / "handoff" / f"native-leaf-evidence-{rid}.json"))
    expected = family.identity(version, agent_sha, "sdk/v1.1.0", core_sha, "1.1.0")
    document = {**expected, "nativeRuns": runs,
                "finalArtifacts": [native.file_record(assets / f"{row['packageId']}.{version}.sunderpkg") for row in rows]}
    family.verify_document(document, assets, rows, expected)
    write(assets / "native-test-evidence.json", document)


if __name__ == "__main__":
    seed(*sys.argv[1:])
