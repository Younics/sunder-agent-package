"""Read-only collection from the exact Agent 2.0.0 protected build."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from shutil import copyfileobj
import subprocess
import sys
from tempfile import TemporaryDirectory
from zipfile import BadZipFile, ZipFile

import agent_release as release
import agent_selection as selection
from native_release_inputs import RIDS, compact, digest, file_record, read_bytes, relative, require
import recovery_inputs_2_0_0 as recovery


ROOT = Path(__file__).resolve().parents[2]
INVENTORY = ROOT / "compatibility/recovery/agent-2.0.0.json"
AGENT = "repos/Younics/sunder-agent-package"
CORE = "repos/Younics/sunder-core"
CORE_TAG = "nuget-sunder-package-build/v1.1.0"
VERSION = "2.0.0"
AGENT_REPO_ID = 1232186250
RELEASE_WORKFLOW = ".github/workflows/sunder-package-release.yml"
WIN_PACKAGE = "younics.ai.agent.execution.local"
WIN_RUNNERS = {"win-arm64": "windows-11-arm", "win-x64": "windows-2022"}
INTEL_REF = "refs/heads/fix/agent-2.0.0-release-recovery"
INTEL_WORKFLOW = ".github/workflows/sunder-package-qualify.yml"


def gh(route, output=None):
    # The Actions ZIP endpoint negotiates JSON on GitHub before sending the
    # storage redirect. Octet-stream is for *release assets*, not Actions ZIPs.
    args = ["gh", "api", "--hostname", "github.com", "-H", "Accept: application/vnd.github+json", route]
    result = subprocess.run(args, stdout=output if output is not None else subprocess.PIPE,
                            stderr=subprocess.DEVNULL, timeout=900 if output is not None else 30)
    require(result.returncode == 0, "Cannot read pinned GitHub input: " + route)
    if output is None:
        return release.decode(result.stdout)


def validate_artifact(meta, artifact_id, name, sha, source, size, expires=None):
    run = meta["workflow_run"]
    require(
        (meta["id"], meta["name"], meta["digest"], meta["expired"]) == (artifact_id, name, "sha256:" + sha, False)
        and (run["id"], run["head_sha"], run["head_branch"], run["repository_id"], run["head_repository_id"])
        == (source["releaseRun"], source["commit"], "main", AGENT_REPO_ID, AGENT_REPO_ID)
        and type(meta["size_in_bytes"]) is int
        and 0 < meta["size_in_bytes"] <= 512 * 1024**2
        and (size is None or meta["size_in_bytes"] == size)
        and (expires is None or meta["expires_at"] == expires),
        "Original Actions artifact coordinates differ or expired: " + name,
    )


def artifact(directory, artifact_id, name, sha, source, size=None, expires=None):
    route = f"{AGENT}/actions/artifacts/{artifact_id}"
    meta = gh(route)
    validate_artifact(meta, artifact_id, name, sha, source, size, expires)
    path = directory / f"{artifact_id}.zip"
    with path.open("xb") as stream:
        gh(route + "/zip", stream)
    return recovery.checked_file(path, meta["size_in_bytes"], sha)


def check_runs(source, test, build):
    run = gh(f"{AGENT}/actions/runs/{source['releaseRun']}/attempts/{source['releaseAttempt']}")
    require(tuple(run[k] for k in ("id", "run_attempt", "workflow_id", "path", "head_sha", "head_branch", "event", "status", "conclusion")) ==
            (source["releaseRun"], source["releaseAttempt"], 275269183, RELEASE_WORKFLOW, source["commit"], "main", "push", "completed", "failure")
            and run["repository"]["full_name"] == run["head_repository"]["full_name"] == source["repository"],
            "Original failed release execution changed")
    qualified = gh(f"{AGENT}/actions/runs/{source['qualificationRun']}")
    require(tuple(qualified[k] for k in ("id", "run_attempt", "workflow_id", "path", "head_sha", "event", "status", "conclusion")) ==
            (source["qualificationRun"], 1, 425318496, ".github/workflows/sunder-package-qualify.yml",
             "16fd4793ff65d15cf313a6146efc3d33272906ba", "workflow_dispatch", "completed", "success")
            and qualified["repository"]["full_name"] == qualified["head_repository"]["full_name"] == source["repository"]
            and gh(f"{AGENT}/git/commits/{qualified['head_sha']}")["tree"]["sha"] == source["tree"],
            "Original Agent release tree was not qualified")
    branch = gh(f"{AGENT}/branches/main")
    relation = gh(f"{AGENT}/compare/{source['commit']}...{branch['commit']['sha']}") if branch["commit"]["sha"] != source["commit"] else None
    require(branch["protected"] is True and (relation is None or
            (relation["status"] == "ahead" and relation["merge_base_commit"]["sha"] == source["commit"])),
            "Original release must be on protected Agent main ancestry")
    require(gh(f"{CORE}/git/commits/{build['commit']}")["tree"]["sha"] == recovery.BUILD_TREE,
            "Original Build tree differs")
    check_tag(CORE_TAG, build["commit"])
    repaired = gh(f"{CORE}/actions/runs/{test['run']}/attempts/{test['attempt']}")
    require(tuple(repaired[k] for k in ("id", "run_attempt", "workflow_id", "path", "head_sha", "head_branch", "event", "status", "conclusion")) ==
            (test["run"], test["attempt"], 275259847, ".github/workflows/sunder-sdk-release.yml", test["commit"], "main", "push", "completed", "success")
            and repaired["repository"]["full_name"] == repaired["head_repository"]["full_name"] == test["repository"],
            "Repaired Core test release is not the recorded passing execution")
    for role, pin in test["releases"].items():
        visible = gh(f"{CORE}/releases/{pin['id']}")
        assets = {row["name"]: {k: row[k] for k in ("id", "size", "digest")} for row in visible["assets"]}
        require(visible["id"] == pin["id"] and visible["tag_name"] == pin["tag"] and visible["draft"] is False
                and visible["prerelease"] is False and len(assets) == len(visible["assets"]) and assets == pin["assets"],
                "Repaired " + role + " release inventory changed")
        check_tag(pin["tag"], test["commit"])


def check_tag(tag, commit):
    ref = gh(f"{CORE}/git/ref/tags/{tag}")["object"]
    if ref["type"] == "tag":
        ref = gh(f"{CORE}/git/tags/{ref['sha']}")["object"]
    require(ref["type"] == "commit" and ref["sha"] == commit, "Pinned Core tag moved: " + tag)


def checkouts(original, repaired, pins):
    source, test = pins["archiveSource"], pins["testInputs"]
    original = recovery.checkout(original, source["commit"], source["tree"])
    repaired = recovery.checkout(repaired, test["commit"], test["tree"])
    require(original != repaired and release.locks(original) == source["lockSetSha256"], "Wrong recovered checkout/locks")
    for name, value in test["originalAgentFixtureHashes"].items():
        require(release.file_sha(release.source_file(original, name)) == value, "Original native fixture changed: " + name)
    return original, repaired


def unpack(zip_path, directory, entries):
    """Only the closed root members produced by the original CI may be copied."""
    with ZipFile(zip_path) as bundle:
        names = bundle.namelist()
        require(len(names) == len(entries) and set(names) == set(entries), "Unexpected original artifact inventory")
        for name, ceiling in entries.items():
            relative(name, depth=1)
            data = recovery.member(bundle, name, ceiling)
            with (directory / name).open("xb") as output:
                output.write(data)


def candidate(row, source, downloads, candidates):
    package_id = row["packageId"]
    archive = row["archive"]
    require(package_id in release.FAMILY_PACKAGE_IDS and archive == f"{package_id}.{VERSION}.sunderpkg",
            "Invalid original composition member")
    path = artifact(downloads, row["artifact"], "agent-candidate-" + package_id,
                    row["artifactDigest"].removeprefix("sha256:"), source)
    unpack(path, candidates, ({f"native-leaves-{package_id}-{rid}.tar.gz": 128 * 1024**2 for rid in RIDS}
                              | {package_id + ".candidate.json": 4 * 1024**2, archive: row["size"]}))
    recovery.checked_file(candidates / archive, row["size"], row["sha256"])


def gather(original, pins, candidates, output):
    source, build = pins["archiveSource"], pins["buildInputs"]
    plan = output / selection.PLAN
    require(digest(read_bytes(plan)) == source["selectionSha256"], "Original Agent release plan bytes changed")
    version_selection = selection.load(plan, original, source["commit"])
    require(version_selection["registryOrigin"] == pins["registryOrigin"]
            and version_selection["lockSetSha256"] == source["lockSetSha256"]
            and len(selection.selected(version_selection)) == 15
            and all(row["version"] == VERSION for row in version_selection["packages"]),
            "Recovery only applies to the exact complete Agent 2.0.0 selection")
    # Use the exact original program and its sibling readers, not an import
    # which could resolve similarly named modules from this controller.
    archives = output / "family"
    subprocess.run([sys.executable, "-B", str(original / "scripts/release/native-family.py"), "gather",
                    str(candidates), str(archives), str(plan), source["commit"], CORE_TAG,
                    build["commit"], build["packageBuildVersion"], str(original)], cwd=original, check=True, timeout=600)
    recovery.archive_set(archives, pins["archives"], version_selection)


def retain_original_tests(downloads, output, pins):
    """Keep the original Windows reports and test binaries. Do not execute them here."""
    source = pins["archiveSource"]
    for pin in pins["originalWindows"]:
        rid = pin["rid"]
        jobs = [gh(f"{AGENT}/actions/jobs/{job}") for job in (pin["originalJobId"], *pin["carriedJobIds"])]
        name = f"Validate selected packages / Validate component ({WIN_PACKAGE}) / {WIN_PACKAGE} · source · {rid}"
        require([job["id"] for job in jobs] == [pin["originalJobId"], *pin["carriedJobIds"]]
                and [job["run_attempt"] for job in jobs] == [1, 2, 3]
                and jobs[0]["runner_name"] and all(
                    (job["run_id"], job["head_sha"], job["name"], job["labels"], job["status"], job["conclusion"],
                     job["runner_id"], job["runner_name"], job["started_at"], job["completed_at"])
                    == (source["releaseRun"], source["commit"], name, [WIN_RUNNERS[rid]], "completed", "success",
                        jobs[0]["runner_id"], jobs[0]["runner_name"], pin["originalStartedAt"], pin["originalCompletedAt"])
                    and job["steps"] == jobs[0]["steps"] for job in jobs),
                "Windows report is not the same original execution across attempts: " + rid)
        path = artifact(downloads, pin["artifactId"], f"agent-component-{WIN_PACKAGE}-source-{rid}-1",
                        pin["zipDigest"].removeprefix("sha256:"), source, pin["size"], pin["expiresAt"])
        recovery.windows_report(path, pin)
        with ZipFile(path) as bundle:
            raw = recovery.member(bundle, recovery.WINDOWS_TRX, 8 * 1024**2)
        with (output / f"original-windows-{rid}.trx").open("xb") as stream:
            stream.write(raw)
    pin = pins["originalArchiveTestBinaries"]
    path = artifact(downloads, pin["artifactId"], "agent-package-test-binaries",
                    pin["zipDigest"].removeprefix("sha256:"), source, pin["size"], pin["expiresAt"])
    with ZipFile(path) as bundle:
        require(digest(recovery.member(bundle, recovery.TEST_ASSEMBLY, 32 * 1024**2)) == pin["testAssemblySha256"],
                "Original archive test assembly differs")
    # Preserve the transport as-is. The fan-in owns running the archive
    # contract tests from this binary set, separate from the Windows TRXs.
    with (output / "original-archive-test-binaries.zip").open("xb") as stream, path.open("rb") as source_stream:
        copyfileobj(source_stream, stream)


def prepare(original, repaired, output, rid):
    pins = recovery.read_inventory(INVENTORY)
    source, build, test = (pins[key] for key in ("archiveSource", "buildInputs", "testInputs"))
    original, repaired = checkouts(original, repaired, pins)
    check_runs(source, test, build)
    parent = output.parent.resolve(strict=True)
    output = parent / output.name
    require(not any(path == output or path in output.parents for path in (ROOT, original, repaired)),
            "Recovery evidence belongs outside the source checkouts")
    output.mkdir(mode=0o700)
    candidates = output / "candidates"
    candidates.mkdir()
    with TemporaryDirectory(dir=output) as temporary:
        downloads = Path(temporary)
        pin = source["selectionArtifact"]
        plan_zip = artifact(downloads, pin["id"], "agent-release-selection", pin["sha256"], source, pin["size"])
        unpack(plan_zip, output, {selection.PLAN: 1024**2})
        pin = source["dependencyArtifact"]
        catalog_zip = artifact(downloads, pin["id"], "agent-dependency-inputs", pin["sha256"], source, pin["size"])
        with ZipFile(catalog_zip) as bundle:
            catalog = recovery.member(bundle, recovery.CATALOG, 1024**2)
        require(digest(catalog) == build["platformCatalog"]["sha256"], "Wrong dependency catalog")
        retain_original_tests(downloads, output, pins)
        with ThreadPoolExecutor(max_workers=4) as workers:
            list(workers.map(lambda row: candidate(row, source, downloads, candidates), pins["archives"]))
        gather(original, pins, candidates, output)
        pin = build["makerFeeds"][rid]
        feed_zip = artifact(downloads, pin["artifact"], f"agent-native-diagnostics-{rid}-{source['releaseAttempt']}",
                            pin["zipDigest"].removeprefix("sha256:"), source, pin["zipSize"], pin["expiresAt"])
        files, maker_catalog = recovery.maker_feed(feed_zip, pin, build)
        require(maker_catalog == catalog, "Original Maker and dependency catalogs differ")
        (output / "feed").mkdir()
        for name, data in files.items():
            (output / "feed" / name).write_bytes(data)
        (output / recovery.CATALOG).write_bytes(catalog)
    (output / "recovery-preparation.json").write_bytes(compact(dict(
        rid=rid, inventorySha256=recovery.INVENTORY_SHA256, archiveSourceCommit=source["commit"],
        buildCommit=build["commit"], testCommit=test["commit"], plan=file_record(output / selection.PLAN),
        makerArtifact=pin["artifact"], catalog=file_record(output / recovery.CATALOG),
        testExecuted=False, publishable=False,
    )))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original", type=Path)
    parser.add_argument("repaired", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("rid", choices=RIDS)
    args = parser.parse_args()
    try:
        prepare(args.original, args.repaired, args.output, args.rid)
    except (ValueError, OSError, KeyError, BadZipFile, subprocess.SubprocessError) as error:
        raise SystemExit("Original Agent inputs rejected: " + str(error))
