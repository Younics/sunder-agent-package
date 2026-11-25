"""Read GitHub Actions results as data, never as sources or executable inputs."""
from concurrent.futures import ThreadPoolExecutor
import subprocess
from threading import Timer

import release_inputs as inputs

REPOSITORY = "Younics/sunder-agent-package"
API = f"repos/{REPOSITORY}/actions"


def api_json(route):
    return inputs.decode(subprocess.check_output(["gh", "api", "-H", "Accept: application/vnd.github+json",
                                                  f"{API}/{route}"], timeout=45))


def entries(route, key, *, limit=100):
    separator = "&" if "?" in route else "?"
    page_size = 20  # Full jobs can contain large step inventories on six-RID runs.
    for page in range(1, limit + 1):
        values = api_json(f"{route}{separator}per_page={page_size}&page={page}")[key]
        inputs.require(type(values) is list and len(values) <= page_size, "Invalid Actions inventory")
        yield from values
        if len(values) < page_size:
            return
    raise ValueError("Actions inventory exceeded its bound")


def attempt_jobs(run, attempt, *, reverse=False, stop_after_first=None):
    """Read one completed attempt. Reverse traversal is only an existence-query optimization."""
    route = f"runs/{inputs.positive(run)}/attempts/{inputs.positive(attempt)}/jobs"
    size = 20
    first = api_json(f"{route}?per_page={size}&page=1")
    total = inputs.positive(first["total_count"])
    inputs.require(total <= 2000, "Completed Actions job inventory exceeded its bound")
    count = (total + size - 1) // size

    def page(index):
        result = first if index == 1 else api_json(f"{route}?per_page={size}&page={index}")
        jobs = result["jobs"]
        inputs.require(result["total_count"] == total and type(jobs) is list
                       and len(jobs) == min(size, total - (index - 1) * size),
                       "Completed Actions job inventory changed")
        return jobs

    if stop_after_first is not None:
        pages = range(count, 0, -1) if reverse else range(1, count + 1)
        for index in pages:
            if any(stop_after_first(job) for job in page(index)):
                return True
        return False
    inputs.require(not reverse, "Full attempt inventory must preserve its original order")
    with ThreadPoolExecutor(max_workers=4) as executor:
        values = [job for jobs in executor.map(page, range(1, count + 1)) for job in jobs]
    inputs.require(len({inputs.positive(job["id"]) for job in values}) == total, "Duplicate Actions job in completed attempt")
    return values


def run_origin(run, path, event):
    return (run["path"] == ".github/workflows/" + path
            and run["repository"]["full_name"] == REPOSITORY
            and run["head_repository"]["id"] == run["repository"]["id"]
            and run["head_repository"]["full_name"] == REPOSITORY
            and run["event"] == event and run["status"] == "completed")


def artifact_receipt(run, artifact, name, maximum):
    repository = inputs.positive(run["repository"]["id"])
    origin = dict(id=inputs.positive(run["id"]), repository_id=repository, head_repository_id=repository,
                  head_branch=run["head_branch"], head_sha=inputs.digest(run["head_sha"], 40))
    inputs.require(artifact["name"] == name and all(artifact["workflow_run"][k] == v for k, v in origin.items()),
                   "Actions artifact came from another run or repository")
    inputs.require(artifact["expired"] is False and artifact["digest"].startswith("sha256:"),
                   "Original Actions artifact is unavailable or has no digest")
    size = inputs.positive(artifact["size_in_bytes"])
    inputs.require(size <= maximum, "Actions artifact exceeds its byte bound")
    return dict(artifactId=inputs.positive(artifact["id"]), size=size, zipSha256=inputs.digest(artifact["digest"][7:]))


def download(record, path):
    route = f"{API}/artifacts/{inputs.positive(record['artifactId'])}/zip"
    with path.open("xb") as output, subprocess.Popen(
        ["gh", "api", "--allow-escape-sequences", route], stdout=subprocess.PIPE
    ) as process:
        deadline = Timer(120, process.kill)
        deadline.start()
        try:
            size = 0
            while data := process.stdout.read(65536):
                size += len(data)
                inputs.require(size <= record["size"], "Actions artifact exceeded its declared size")
                output.write(data)
            inputs.require(process.wait() == 0, "Actions artifact download failed")
        finally:
            deadline.cancel()
            if process.poll() is None:
                process.kill()
    verify_zip(record, path)


def verify_zip(record, path):
    path = inputs.regular(path)
    inputs.require(path.stat().st_size == record["size"] and inputs.file_sha(path) == record["zipSha256"],
                   "Original Actions artifact size or ZIP digest differs")
