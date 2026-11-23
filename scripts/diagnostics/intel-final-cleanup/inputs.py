"""Read-only collection of the exact protected inputs for the Intel diagnostic."""
from concurrent.futures import ThreadPoolExecutor
from hashlib import file_digest, sha256
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parent
PINS = json.loads((ROOT / "inputs.json").read_bytes())
BASE = "repos/Younics/sunder-agent-package/actions"
VERSION = "2.0.0"
CORE = {"tag": "nuget-sunder-package-build/v1.1.0", "commit": PINS["core"], "packageVersion": "1.1.0"}
CATALOG = "sunder-platform-contracts.v1.json"


def digest(path):
    with path.open("rb") as stream:
        return file_digest(stream, "sha256").hexdigest()


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", path], timeout=45))


def verify_run():
    run = api(f"{BASE}/runs/{PINS['run']}/attempts/{PINS['attempt']}")
    assert (run["id"], run["run_attempt"], run["head_sha"], run["workflow_id"], run["status"], run["conclusion"]) == (
        PINS["run"], PINS["attempt"], PINS["head"], 275269183, "completed", "failure")


def artifact(artifact_id, name, expected_hash, temp, cache=None):
    metadata = api(f"{BASE}/artifacts/{artifact_id}")
    assert (metadata["id"], metadata["name"], metadata["expired"], metadata["digest"]) == (
        artifact_id, name, False, "sha256:" + expected_hash)
    assert (metadata["workflow_run"]["id"], metadata["workflow_run"]["head_sha"]) == (PINS["run"], PINS["head"])
    assert 0 < metadata["size_in_bytes"] <= 256 * 1024**2
    path = temp / f"{artifact_id}-verified.zip"
    if cache:
        path = cache / path.name
    else:
        with path.open("xb") as stream:
            subprocess.run(["gh", "api", f"{BASE}/artifacts/{artifact_id}/zip"],
                           stdout=stream, timeout=240, check=True)
    assert path.stat().st_size == metadata["size_in_bytes"] and digest(path) == expected_hash
    return path


def copy_member(zipped, name, target, ceiling, size=None):
    info = zipped.getinfo(name)
    assert info.file_size > 0 and info.file_size <= ceiling and (size is None or info.file_size == size)
    assert not info.is_dir() and (info.external_attr >> 16) & 0o170000 in (0, 0o100000)
    with zipped.open(info) as stream, target.open("xb") as output:
        shutil.copyfileobj(stream, output, 1024**2)


def extract_candidate(row, downloads, output, cache):
    package, artifact_id, outer_sha, size, archive_sha = row
    assert re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)*", package)
    path = artifact(artifact_id, "agent-candidate-" + package, outer_sha, downloads, cache)
    receipt_name = package + ".candidate.json"
    archive = f"{package}.{VERSION}.sunderpkg"
    target = output / archive
    with ZipFile(path) as zipped:
        assert len(zipped.namelist()) == len(set(zipped.namelist()))
        with zipped.open(receipt_name) as stream:
            receipt = json.loads(stream.read(4 * 1024**2))
        assert (receipt["schemaVersion"], receipt["version"], receipt["agentCommit"], receipt["core"], receipt["packageId"]) == (
            1, VERSION, PINS["head"], CORE, package)
        assert receipt["archive"] == dict(path=archive, size=size, sha256=archive_sha)
        copy_member(zipped, archive, target, 256 * 1024**2, size)
    assert target.stat().st_size == size and digest(target) == archive_sha
    print(f"{package}: {archive_sha}", flush=True)
    return dict(packageId=package, archive=archive, size=size, sha256=archive_sha)


def main(output, cache=None):
    verify_run()
    rows = PINS["archives"]
    assert len(rows) == len({row[0] for row in rows}) == len({row[1] for row in rows}) == 15
    archives = output / "archives"
    archives.mkdir(parents=True, exist_ok=False)
    with TemporaryDirectory(dir=output) as directory:
        downloads = Path(directory)
        selection = PINS["catalog"]
        zip_path = artifact(selection["artifact"], "agent-dependency-inputs", selection["artifactSha256"], downloads, cache)
        with ZipFile(zip_path) as zipped:
            assert len(zipped.namelist()) == len(set(zipped.namelist()))
            copy_member(zipped, CATALOG, output / CATALOG, 16 * 1024**2)
        assert digest(output / CATALOG) == selection["sha256"]
        with ThreadPoolExecutor(max_workers=5) as pool:
            records = sorted(pool.map(lambda row: extract_candidate(row, downloads, archives, cache), rows),
                             key=lambda row: row["packageId"])
    assert {path.name for path in archives.iterdir()} == {item["archive"] for item in records}
    scope = dict(schemaVersion=1, rid="osx-x64", profile="unpublished-development-import",
                 installedAcceptance=False, registryOrigin=None, archives=records)
    scope_bytes = json.dumps(scope, separators=(",", ":")).encode("utf-8")
    assert sha256(scope_bytes).hexdigest() == PINS["archiveScopeSha256"]
    (output / "expected-scope.json").write_bytes(scope_bytes)
    print("Verified all 15 bytesets and the exact catalog; no package was rebuilt or uploaded.")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]) if len(sys.argv) == 3 else None)
