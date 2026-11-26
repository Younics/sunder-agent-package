"""Authenticate a complete staged package when only its protected release controller changed."""
import hashlib
import os
from pathlib import Path
import sys
import tempfile
from zipfile import ZipFile

import agent_publication as publication
import agent_release as r
import agent_selection as selection
import native_runtime_evidence as native
import release_inputs as inputs

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
import hosted_evidence as hosted

# A controller repair cannot change *any* product, App/Host pin, lock, native
# test, acceptance policy, or build input relative to the staged producer.
CONTROL_FILES = frozenset({
    ".github/workflows/sunder-package-release.yml", ".github/workflows/sunder-package-ship.yml",
    "scripts/ci/package_release.py", "scripts/release/agent_selection.py",
    "scripts/release/agent_pipeline.py", "scripts/release/agent_staged_source.py",
    "scripts/release/publish-registry-package.sh", "scripts/test/release/test_agent_staged_source.py",
    "docs/RELEASES.md",
})


def primary(plan):
    row, = selection.selected(plan)
    return {selection.archive_name(row), selection.PLAN, "native-test-evidence.json", "package-output-audit.json",
            "release-provenance.json", "toolchain.txt", "SHA256SUMS", "release-sbom.spdx.json",
            *("native-execution-" + rid + ".tar.gz" for rid in native.RIDS)}


def fingerprint(info, plan, controller):
    row, = selection.selected(plan)
    r.require(plan["schemaVersion"] == 3 and row["packageId"] == plan["releasePackage"]
              and info["target_commitish"] == plan["artifactSourceCommit"] and info["tag_name"] == row["tag"],
              "Staged package/source differs")
    names = primary(plan)
    assets = sorted((selection.asset_locator(info, name) for name in names), key=lambda a: a["name"])
    return r.sha(r.canonical(dict(controller=r.digest(controller, 40), source=plan["artifactSourceCommit"],
                                  tag=row["tag"], releaseId=r.positive(info["id"]), assets=assets)))


def compare_qualified(archive, info, plan):
    """Compare; do not extract or fill a missing draft asset from Actions."""
    by_name = {a["name"]: a for a in info["assets"]}
    expected = primary(plan) - {"release-provenance.json", "toolchain.txt", "SHA256SUMS", "release-sbom.spdx.json"}
    retained = {row["retainedArchive"]["name"]: row["retainedArchive"] for row in plan["packages"] if not row["selected"]}
    with ZipFile(archive) as z:
        members = z.infolist()
        r.require(len(members) == len(expected) + len(retained) and {i.filename for i in members} == expected | retained.keys(),
                  "Original qualified artifact inventory differs")
        for member in members:
            name = member.filename
            size, sha = ((retained[name]["size"], retained[name]["sha256"]) if name in retained
                         else (by_name[name]["size"], by_name[name]["digest"][7:]))
            r.require(0 < member.file_size == size <= 384 * 1024**2, "Original qualified file size differs")
            with z.open(member) as stream:
                r.require(hashlib.file_digest(stream, "sha256").hexdigest() == sha, "Draft differs from the qualified bytes: " + name)


def authenticate(controller, info, plan):
    """Read-only planner; authenticate the original main push, job, artifact and draft before routing recovery."""
    row, = selection.selected(plan)
    producer = r.digest(plan["artifactSourceCommit"], 40)
    r.require(producer != controller and info["draft"], "Expected a complete earlier draft")
    store = publication.GitHubRelease(selection.REPOSITORY, row["tag"])
    info = store.get(info["id"])
    r.require(info["draft"], "Staged draft was announced during selection")
    publication.remote_tag(store, producer)
    bound = fingerprint(info, plan, controller)
    ancestry = store.api(f"compare/{producer}...{r.digest(controller, 40)}")
    r.require(ancestry["status"] == "ahead" and ancestry["merge_base_commit"]["sha"] == producer,
              "Staged producer is not an ancestor of the controller")
    with tempfile.TemporaryDirectory(prefix="sunder-staged-source-") as temp:
        path = Path(temp) / "release-provenance.json"
        locator = selection.asset_locator(info, path.name)
        r.require(locator["size"] <= 4 * 1024**2, "Oversized staged provenance")
        inputs.download(locator, path)
        value = r.load(path)
        inv = value["invocation"]
        r.require(value["schemaVersion"] == 4 and value["artifactSourceCommit"] == inv["workflowSha"] == producer
                  and value["planSha256"] == r.sha(r.canonical(plan) + b"\n") and value["lockSetSha256"] == plan["lockSetSha256"]
                  and value["publication"] == {"registryOrigin": plan["registryOrigin"]}
                  and inv["releaseId"] == info["id"] and inv["event"] == "push" and inv["repository"] == selection.REPOSITORY
                  and inv["workflowPath"] == publication.WORKFLOW
                  and inv["workflowRef"] == f"{selection.REPOSITORY}/{publication.WORKFLOW}@refs/heads/main",
                  "Staged provenance does not identify the original main push")
        run_id, attempt = r.positive(int(inv["runId"])), r.positive(int(inv["runAttempt"]))
        run = hosted.api_json(f"runs/{run_id}/attempts/{attempt}")
        r.require(hosted.run_origin(run, Path(publication.WORKFLOW).name, "push") and run["head_branch"] == "main"
                  and run["id"] == run_id and run["run_attempt"] == attempt and run["head_sha"] == producer
                  and run["repository"]["id"] == inv["repositoryId"] == int(os.environ["GITHUB_REPOSITORY_ID"]),
                  "Wrong original protected-main producer")
        prefix = ("Main Agent" if row["packageId"] == selection.OWNER else row["packageId"]) + " / "
        jobs = {j["name"]: j for j in hosted.attempt_jobs(run_id, attempt)
                if j["run_id"] == run_id and j["run_attempt"] == attempt and j["head_sha"] == producer}
        qualified = jobs[prefix + "Verify six native receipts"]
        staging = jobs[prefix + "Stage release and upload to Hub"]
        r.require(qualified["conclusion"] == "success" and all(
            jobs[prefix + phase + rid]["conclusion"] == "success" for phase in ("Owned tests · ", "Exercise composed archive · ")
            for rid in native.RIDS) and any(s["name"] == "Stage or recover the original tested release" and s["conclusion"] == "success"
                                           for s in staging["steps"]), "Original package qualification/staging did not finish")
        name = f"qualified-{row['packageId']}-{attempt}"
        item, = hosted.entries(f"runs/{run_id}/artifacts?name={name}", "artifacts")
        receipt = hosted.artifact_receipt(run, item, name, 896 * 1024**2)
        r.require(qualified["started_at"] <= item["created_at"] <= qualified["completed_at"], "Wrong qualified artifact producer job")
        archive = Path(temp) / "qualified.zip"
        hosted.download(receipt, archive)
        compare_qualified(archive, info, plan)
    return bound


def source_tree(root):
    return {path: obj for line in r.git(root, "ls-tree", "-rz", "HEAD").split(b"\0") if line
            for obj, path in [line.split(b"\t", 1)] if os.fsdecode(path) not in CONTROL_FILES}


def checkout(control_root, plan):
    """Always use the real original checkout; never override GitHub's run/ref/SHA variables."""
    producer, controller = plan["artifactSourceCommit"], os.environ["GITHUB_SHA"]
    if producer == controller:
        return control_root
    r.digest(os.environ.get("SUNDER_STAGED_INPUTS_SHA256", ""), 64)
    original = control_root.parent / "sunder-agent-producer"
    r.require(plan["schemaVersion"] == 3 and original.resolve(strict=True) == original and original.is_dir()
              and r.git(original, "rev-parse", "HEAD").decode().strip() == producer
              and not r.git(original, "status", "--porcelain", "--untracked-files=all").strip()
              and source_tree(original) == source_tree(control_root), "Wrong or changed original staged source")
    return original
