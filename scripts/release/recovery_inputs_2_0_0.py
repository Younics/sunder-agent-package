#!/usr/bin/env python3
"""Offline input preparation for the one failed Agent 2.0.0 release.

This is not a promotion verifier. It never queries a Registry, downloads
artifacts, builds packages, or issues a native/publishable receipt.
"""
import argparse
from pathlib import Path
import stat
import subprocess
from xml.etree import ElementTree
from zipfile import ZipFile

import agent_release as release
import agent_selection as selection
from native_release_inputs import RIDS, compact, digest, file_record, parse, read_bytes, regular, require


INVENTORY_SHA256 = "83d1676fd85b53b95d6788c12705920bbbc382bbfc17d48b905fe7a40a516260"
BUILD_TREE = "0cf5b4d2a9013eeb7fbd91b6b0143f913dfb004b"
CATALOG = "sunder-platform-contracts.v1.json"
WINDOWS_TRX = "test-results/full/Sunder.Package.Agent.Execution.Local.Tests.trx"
WINDOWS_CLASS = "Sunder.Package.Agent.Execution.Local.Tests.WindowsSecureMutationTests"
TEST_ASSEMBLY = "Sunder.Package.Agent.Archive.Tests.dll"
NUGET_IDS = {"Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker",
             "Sunder.Package.Build", "Sunder.Package.Templates"}


def checked_file(path, size, sha256, ceiling=2 * 1024**3):
    require(file_record(path, maximum=ceiling) == dict(path=Path(path).name, size=size, sha256=sha256),
            "Retained input differs from its pinned size/hash: " + str(path))
    return path


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], timeout=45, text=True).rstrip("\n")


def checkout(root, commit, tree):
    regular(root, directory=True)
    root = Path(root).resolve(strict=True)
    require(Path(git(root, "rev-parse", "--show-toplevel")).resolve(strict=True) == root
            and git(root, "rev-parse", "HEAD", "HEAD^{tree}") == commit + "\n" + tree
            and not git(root, "status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none"),
            "Expected the exact clean checkout: " + str(root))
    return root


def read_inventory(path):
    data = read_bytes(path, 64 * 1024)
    require(digest(data) == INVENTORY_SHA256, "Not the pinned Agent 2.0.0 recovery review inventory")
    value = parse(data)
    require(value["approved"] is False and value["testExecuted"] is False and value["publishable"] is False,
            "The inventory is input lineage only")
    return value


def archive_set(directory, rows, plan):
    regular(directory, directory=True)
    expected = {selection.archive_name(row): row["packageId"] for row in selection.selected(plan)}
    require(len(expected) == len(rows) == 15 and {row["archive"]: row["packageId"] for row in rows} == expected
            and {path.name for path in directory.iterdir()} == set(expected),
            "Expected exactly the fifteen selected original archives")
    for row in rows:
        checked_file(directory / row["archive"], row["size"], row["sha256"])


def verify_core_assets(directory, test):
    """Verify both released plans and the audited asset bytes (not an execution claim)."""
    regular(directory, directory=True)
    for role in ("host", "app"):
        root, pinned = directory / role, test["releases"][role]
        regular(root, directory=True)
        require({path.name for path in root.iterdir()} == set(pinned["assets"]),
                "Repaired Core release inventory differs for " + role)
        for name, asset in pinned["assets"].items():
            checked_file(root / name, asset["size"], asset["digest"].removeprefix("sha256:"))
        plan = release.load(root / "native-release-plan.json")
        provenance = release.load(root / "release-provenance.json")
        invocation = provenance["invocation"]
        require((plan["component"], plan["tag"], plan["artifactSourceCommit"], plan["runId"], plan["workflowRef"])
                == (role, pinned["tag"], test["commit"], str(test["run"]), invocation["workflowRef"])
                and (invocation["repository"], invocation["sha"], invocation["runId"], invocation["runAttempt"],
                     invocation["releaseId"], invocation["ref"])
                == (test["repository"], test["commit"], str(test["run"]), str(test["attempt"]), pinned["id"], "refs/heads/main"),
                "Host and App must use the same pinned release source and attempt")


def member(zip_file, name, ceiling, size=None):
    entry = zip_file.getinfo(name)
    kind = stat.S_IFMT(entry.external_attr >> 16)
    require(not entry.is_dir() and kind in (0, stat.S_IFREG) and not entry.flag_bits & 1
            and 0 < entry.file_size <= ceiling and (size is None or size == entry.file_size),
            "Invalid retained artifact member: " + name)
    with zip_file.open(entry) as stream:
        data = stream.read(entry.file_size + 1)
    require(len(data) == entry.file_size, "Retained artifact member length changed: " + name)
    return data


def windows_report(zip_path, pin):
    checked_file(zip_path, pin["size"], pin["zipDigest"].removeprefix("sha256:"))
    with ZipFile(zip_path) as bundle:
        raw = member(bundle, WINDOWS_TRX, 8 * 1024**2)
    require(digest(raw) == pin["originalTrxSha256"], "Original Windows report differs")
    text = raw.decode("utf-8-sig")
    require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(), "Invalid Windows TRX")
    root = ElementTree.fromstring(text)
    ids = [definition.get("id") for definition in root.findall(".//{*}TestDefinitions/{*}UnitTest")
           if definition.find("{*}TestMethod") is not None
           and definition.find("{*}TestMethod").get("className") == WINDOWS_CLASS]
    results = [row for row in root.findall(".//{*}UnitTestResult")
               if row.get("testId") in ids or row.get("testName", "").startswith(WINDOWS_CLASS + ".")]
    require(len(ids) == len(set(ids)) == len(results) == len({row.get("executionId") for row in results}) == 34
            and {row.get("testId") for row in results} == set(ids)
            and len({row.get("testName") for row in results}) == 34
            and all(row.get("outcome") == "Passed" for row in results)
            and pin["cases"] == {"passed": 34, "failed": 0, "skipped": 0} and pin["nativeExecutionCount"] == 1,
            "Expected the 34 original passing Windows cases exactly once")


def maker_feed(zip_path, pinned, build):
    checked_file(zip_path, pinned["zipSize"], pinned["zipDigest"].removeprefix("sha256:"))
    with ZipFile(zip_path) as bundle:
        names = bundle.namelist()
        require(len(names) == len({name.casefold() for name in names}), "Duplicate retained Maker member")
        raw = member(bundle, "maker-inputs/inputs.json", 1024**2)
        require(digest(raw) == pinned["makerInputsDigest"], "Wrong original Maker preparation manifest")
        manifest = parse(raw)
        rows = pinned["feed"]
        require(manifest["schemaVersion"] == 2 and manifest["coreCommit"] == build["commit"]
                and manifest["workingTreeClean"] is True and manifest["packages"] == rows
                and len(rows) == 6 and {row["packageId"] for row in rows} == NUGET_IDS,
                "Maker must retain the six original locked developer packages")
        files = {}
        prefix = "maker-inputs/feed/"
        for row in rows:
            name = row["name"]
            require(name == f"{row['packageId']}.{row['version']}.nupkg" and name not in files, "Wrong retained NuGet identity")
            data = member(bundle, prefix + name, 32 * 1024**2, row["size"])
            require(digest(data) == row["sha256"], "Retained NuGet bytes differ: " + name)
            files[name] = data
        require({name for name in names if name.startswith(prefix) and name != prefix}
                == {prefix + name for name in files}, "Missing/extra original Maker feed bytes")
        catalog = member(bundle, "maker-inputs/" + CATALOG, 1024**2, manifest["platformCatalog"]["size"])
        require(manifest["platformCatalog"]["path"] == CATALOG and digest(catalog)
                == pinned["catalogSha256"] == manifest["platformCatalog"]["sha256"] == build["platformCatalog"]["sha256"],
                "Maker catalog differs from the original Build selection")
    return files, catalog


def prepare(args):
    inventory = read_inventory(args.inventory)
    source, build, test = (inventory[key] for key in ("archiveSource", "buildInputs", "testInputs"))
    original = checkout(args.agent, source["commit"], source["tree"])
    build_core = checkout(args.build_core, build["commit"], BUILD_TREE)
    test_core = checkout(args.test_core, test["commit"], test["tree"])
    require(len({original, build_core, test_core}) == 3, "Archive, Build and test checkouts must be separate")
    require(release.locks(original) == source["lockSetSha256"], "Original Agent locks changed")
    for name, sha256 in test["originalAgentFixtureHashes"].items():
        require(release.file_sha(release.source_file(original, name)) == sha256, "Original native fixture changed: " + name)
    plan_bytes = read_bytes(args.plan)
    require(digest(plan_bytes) == source["selectionSha256"], "Original Agent selection changed")
    plan = selection.load(args.plan, original, source["commit"])
    require(plan["registryOrigin"] == inventory["registryOrigin"] and plan["lockSetSha256"] == source["lockSetSha256"],
            "Original Hub/lock selection changed")
    archive_set(args.archives, inventory["archives"], plan)
    verify_core_assets(args.core_assets, test)
    select_pin, dependencies = source["selectionArtifact"], source["dependencyArtifact"]
    select_zip = checked_file(args.original_artifacts / select_pin["name"], select_pin["size"], select_pin["sha256"])
    with ZipFile(select_zip) as bundle:
        require(bundle.namelist() == [selection.PLAN] and member(bundle, selection.PLAN, 1024**2) == plan_bytes,
                "Selection does not match the original release ZIP")
    checked_file(args.original_artifacts / dependencies["name"], dependencies["size"], dependencies["sha256"])
    require([pin["rid"] for pin in inventory["originalWindows"]] == ["win-arm64", "win-x64"]
            and all(len({pin[key] for pin in inventory["originalWindows"]}) == 2
                    for key in ("artifactId", "originalJobId", "zipDigest", "originalTrxSha256")),
            "Both Windows ABIs need distinct original execution evidence")
    for pin in inventory["originalWindows"]:
        windows_report(args.original_artifacts / f"{pin['artifactId']}-verified.zip", pin)
    binaries = inventory["originalArchiveTestBinaries"]
    checked_file(args.test_binaries, binaries["size"], binaries["zipDigest"].removeprefix("sha256:"))
    with ZipFile(args.test_binaries) as bundle:
        require(digest(member(bundle, TEST_ASSEMBLY, 32 * 1024**2)) == binaries["testAssemblySha256"],
                "Original archive test assembly differs")
    pinned = build["makerFeeds"][args.rid]
    files, catalog = maker_feed(args.final_artifacts / f"{pinned['artifact']}-verified.zip", pinned, build)

    parent = args.output.parent.resolve(strict=True)
    output = parent / args.output.name
    require(not any(output == path or path in output.parents for path in (original, build_core, test_core)),
            "The preparation output must stay outside the exact source checkouts")
    output.mkdir(mode=0o700, exist_ok=False)
    (output / "feed").mkdir()
    for name, data in files.items():
        (output / "feed" / name).write_bytes(data)
    (output / CATALOG).write_bytes(catalog)
    (output / "recovery-preparation.json").write_bytes(compact(dict(
        purpose="offline-input-preparation", inventorySha256=INVENTORY_SHA256, rid=args.rid,
        archiveSourceCommit=source["commit"], buildCommit=build["commit"], testCommit=test["commit"],
        sourceRun=source["releaseRun"], makerArtifact=pinned["artifact"], catalogSha256=digest(catalog),
        retainedWindows=[{"rid": row["rid"], "originalArtifact": row["artifactId"], "cases": row["cases"]}
                         for row in inventory["originalWindows"]],
        retainedArchiveTestsArtifact=binaries["artifactId"],
        testExecuted=False, publishable=False)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("inventory", "agent", "build-core", "test-core", "plan", "archives", "core-assets",
                 "original-artifacts", "final-artifacts", "test-binaries", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--rid", choices=RIDS, required=True)
    prepare(parser.parse_args())


if __name__ == "__main__":
    main()
