"""Route successful uploads and six-platform installs across a release run's write barriers."""
import argparse
import os
from pathlib import Path
import re
import sys
import tempfile
from zipfile import ZipFile

import agent_release as r
import agent_selection as selection
from ci_platforms import RIDS

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/ci"))
import hosted_evidence as hosted

APPROVAL = "reviewed-release-plan"


def previous_approval(scope, attempt):
    """A full rerun may finish a subset of the original approval, never expand it."""
    run = scope["runId"]
    artifacts = list(hosted.entries(f"runs/{run}/artifacts?name={APPROVAL}", "artifacts"))
    if not artifacts:
        return False
    r.require(len(artifacts) == 1, "Ambiguous approval receipt")
    artifact = artifacts[0]
    origin = dict(id=run, head_sha=scope["source"], head_branch="main", repository=dict(id=int(os.environ["GITHUB_REPOSITORY_ID"])))
    record = hosted.artifact_receipt(origin, artifact, APPROVAL, 64 * 1024)
    with tempfile.TemporaryDirectory() as temporary:
        path = Path(temporary) / "review.zip"
        hosted.download(record, path)
        with ZipFile(path) as archive:
            member, = archive.infolist()
            r.require(member.filename == "review.json" and member.file_size <= 32 * 1024, "Invalid approval receipt")
            review = r.decode(archive.read(member))
    r.closed(review, "attempt plan")
    original = review["plan"]
    earlier = r.positive(review["attempt"])
    r.require(earlier < attempt, "Approval must come from an earlier attempt of this run")
    name = "Approve release plan · " + r.sha(r.canonical(original))
    jobs = [j for j in hosted.attempt_jobs(run, earlier) if j["name"] == name]
    r.require(len(jobs) == 1, "Missing or ambiguous original approval job")
    job = jobs[0]
    r.require((job["run_id"], job["run_attempt"], job["head_sha"]) == (run, earlier, scope["source"])
              and job["status"] == "completed" and job["conclusion"] == "success"
              and job["started_at"] <= artifact["created_at"] <= job["completed_at"], "Missing original successful approval")
    r.require(all(original[key] == scope[key] for key in ("repository", "runId", "source", "registryOrigin"))
              and all(original["packages"].get(p) == entry for p, entry in scope["packages"].items()),
              "Release plan changed outside the original approval; start a new run for human review")
    return True


def successful_artifact(jobs, artifacts, job_name, prefix, attempt):
    jobs = [j for j in jobs if j["name"] == job_name]
    r.require(len(jobs) == 1 and jobs[0]["status"] == "completed" and jobs[0]["conclusion"] == "success",
              "Missing, ambiguous or unsuccessful job: " + job_name)
    job = jobs[0]
    # Rerun-failed carries successful jobs into the new attempt with their old
    # timestamps. Accept only the artifact uploaded by that execution, never
    # the newest name or an earlier success for a job that just failed.
    pattern = re.compile(re.escape(prefix) + r"-([1-9][0-9]*)")
    matches = []
    for artifact in artifacts:
        suffix = pattern.fullmatch(artifact["name"])
        if suffix and int(suffix[1]) <= attempt and not artifact["expired"]:
            if job["started_at"] <= artifact["created_at"] <= job["completed_at"]:
                matches.append(artifact)
    r.require(len(matches) == 1, "Missing or ambiguous successful job artifact: " + job_name)
    return r.positive(matches[0]["id"])


def eligible_packages(phase, wave, packages, jobs, artifacts, attempt):
    ready, errors = [], []
    for package, handoff in packages.items():
        try:
            if phase == "uploads":
                prefix = "Main Agent" if wave == "owner" else package
                handoff = successful_artifact(jobs, artifacts, prefix + " / Upload the GitHub release archive to Hub",
                                              "hub-ready-" + package, attempt)
                row = dict(package=package, handoff=handoff)
            else:
                prefix = "Accept main Agent" if wave == "owner" else "Accept extensions"
                receipts = [successful_artifact(jobs, artifacts, f"{prefix} / Install from Hub · {package} · {rid}",
                                                f"installed-{package}-{rid}", attempt) for rid in RIDS]
                row = dict(package=package, handoff=handoff, receipts=receipts)
            ready.append(row)
        except r.Invalid as error:
            errors.append(f"{package}: {error}")
    return ready, errors


def route(phase):
    scope = selection.approved_scope(ROOT)
    wave = os.environ["WAVE"]
    r.require(wave in ("owner", "extensions"), "Unknown release wave")
    packages = {p: None for p in scope["packages"] if (p == selection.OWNER) == (wave == "owner")}
    if phase == "installs":
        published = r.decode(os.environ["PUBLISHED"].encode())
        r.require(type(published) is list and all(row["package"] in packages for row in published)
                  and len({row["package"] for row in published}) == len(published), "Invalid published package set")
        packages = {row["package"]: r.positive(row["handoff"]) for row in published}
    run = scope["runId"]
    attempt = r.positive(int(os.environ["GITHUB_RUN_ATTEMPT"]))
    jobs = [j for j in hosted.attempt_jobs(run, attempt)
            if (j["run_id"], j["run_attempt"], j["head_sha"]) == (run, attempt, scope["source"])]
    artifacts = [a for a in hosted.entries(f"runs/{run}/artifacts", "artifacts")
                 if (a["workflow_run"]["id"], a["workflow_run"]["head_sha"]) == (run, scope["source"])]
    ready, errors = eligible_packages(phase, wave, packages, jobs, artifacts, attempt)
    for error in errors:
        print("::error::" + error)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write("packages=" + r.canonical(ready).decode() + "\n")
        output.write("incomplete=" + str(bool(errors)).lower() + "\n")


def bind_handoff(context):
    path = context / selection.PLAN
    plan = selection.load(path, ROOT)
    selection.authorize_package(ROOT, plan)
    producer = plan["artifactSourceCommit"]
    staged = r.regular(context / "staged.sha256").read_text().strip()
    if producer == os.environ["GITHUB_SHA"]:
        r.require(not staged, "Unexpected staged producer binding")
    else:
        r.digest(staged)
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as env:
        env.write(f"PLAN_SHA={r.file_sha(path)}\nSUNDER_STAGED_INPUTS_SHA256={staged}\n")
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write("original=" + (producer if staged else "") + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("previous-approval", "uploads", "installs", "bind"))
    phase = parser.parse_args().phase
    if phase == "previous-approval":
        reused = previous_approval(r.decode(os.environ["PLAN"].encode()), r.positive(int(os.environ["GITHUB_RUN_ATTEMPT"])))
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write("reused=" + str(reused).lower() + "\n")
    elif phase == "bind":
        bind_handoff(Path(os.environ["RUNNER_TEMP"]) / "context")
    else:
        route(phase)
