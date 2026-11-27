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

# Controller-only inputs. The installed App pin is distinct from the producer
# App, which was and remains an input to original development qualification.
CONTROL_FILES = frozenset({
    ".github/workflows/sunder-package-release.yml", ".github/workflows/sunder-package-ship.yml",
    "scripts/ci/package_release.py", "scripts/release/agent_selection.py",
    "scripts/release/agent_pipeline.py", "scripts/release/agent_staged_source.py",
    "scripts/release/publish-registry-package.sh", "scripts/test/release/test_agent_staged_source.py",
    "scripts/test/release/publish-registry-package.fixture.sh", "docs/RELEASES.md",
    "scripts/release/agent_channel.py", "scripts/release/agent_cohort.py",
    "scripts/release/import-agent-publication-evidence.sh", "scripts/test/release/test_selective_pipeline.py",
    "scripts/test/release/test_agent_channel.py", "scripts/test/release/agent-release.fixture.py", "scripts/README.md",
    "eng/ci/installed-app-release.json", "scripts/ci/hub_install_regression.py", "docs/native-release-inputs.md",
    "scripts/test/release/test_native_runtime_evidence.py", "scripts/test/release/test_prepare_maker_inputs.py",
    "scripts/test/release/test_version_selection.py", "scripts/test/release/test_agent_ci.py",
})

# The first entry is the original blob (or absent). Only the listed exact
# revisions of a shared harness file may be normalized to that original.
REVIEWED_BLOBS = {
    "scripts/release/agent_publication.py": ("b8b9bf3535a6e7599881c1f4ee72c207161459f6", "e6c246b648885e9ddcd8582c54f1f883fc3cca0a"),
    "scripts/test/release/test_agent_pipeline.py": ("deac657a1afefc708599e060a0725e0e8806a6f9", "25a1f5d4e3e60d6a71beeae38eed7f80cdfb9832"),
    "scripts/release/agent_release.py": ("5bc5b159ceee802c99d87f2802798d586318bab5", "85852c635789a28e383bb6425343eeb6efce6224"),
    "scripts/ci/bash_runtime.py": ("d029f6a53ce254e4ccf4ab61197cc3f9f6c93009", "7a97eb0160f3e524ca7dbda44d4acdbff7f35e92"),
    ".github/workflows/sunder-package-smoke.yml": ("5a672a6223c7207270f49924e07d1a77694b80eb", "ed1367add50cb59e74431121ddb62740598f05e1"),
    "scripts/ci/workflow_summary.py": ("6e3aec1b8af1177dfccd0824adbe586caae30cae", "27e42a226254abfe14a2629e1ce996eb81e526c8"),
    "scripts/release/Sunder.Agent.NativeReleaseTests.targets": ("e67e53d54c4862e990a8889f858df8d8cf81ee9d", "cbd262b82ee23167afeabeb132c04ab7ef9130c6"),
    "scripts/release/native_runtime_evidence.py": ("2bfb601ff398f14dcf73ca4057cf9757eca831e0", "21fdd0b581701f1953fa10698efe70c5c7c9ec93", "558a5e75e0afab7b4b4cfaaf9a42ebd269a166ed"),
    "scripts/release/prepare_maker_inputs.py": ("9e62de05eed793a1ccdb4191fb7c9439c559bbe8", "9e2a365c2dee4cc14e45b445c114ffcd378325d8"),
    "scripts/release/published_app.py": ("76f020db857e8b18b272b0025680e477814222da", "6f4da11f27b3c4419a24f89af3a3d196d1ae77f3"),
    "scripts/release/run-native-runtime-smoke.sh": ("fbf8d805a202d12159d91fd0393dc4258f42a5c6", "aee580a51abfae77c962d6a8a003f8481c543ff4"),
    "tests/fixtures/AgentNativeReleaseSmokeTests.cs": ("3f25b9dc65e738b44ab7e92f7b69a184b53433ae", "f9a2109e7d2b59e847c6867ec1e500fa1651d66d", "0ebb7129543797414db67a1b33ee9944e2ba552b"),
    "tests/fixtures/AgentNativeReleaseSmokeTests.Development.cs": ("9a13ce2eded4ff84ee4d8993b81eec9fda3ebf57", "cdd4c321d98a192d4db019f630e6a499fbc5b9c9"),
    "tests/fixtures/NativeAppSnapshotProbe.cs": ("9ead5daab3f8efade26c1a1f4ab2575540f3596e", "e203a3c0264ae254e6a0e1c0325ece89612a9b16"),
    "tests/fixtures/AgentNativeReleaseSmokeTests.Hub.cs": (None, "78259be19599a8c39810d1d38be06b9d413be08b", "d19224f03daed983ec9548e211faca93a982aa95"),
    "scripts/release/hub_install_evidence.py": (None, "5e806a61eba6cd978046fff6862c674a0ac2be0d", "ada9c99e0d361af8e06dc293332c840920db61a2"),
}


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
        staging, = [jobs[prefix + name] for name in ("Store the tested archive in its GitHub release", "Stage release and upload to Hub")
                    if prefix + name in jobs]
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
    tree = {os.fsdecode(path): obj for line in r.git(root, "ls-tree", "-rz", "HEAD").split(b"\0") if line
            for obj, path in [line.split(b"\t", 1)] if os.fsdecode(path) not in CONTROL_FILES}
    for path, (old, *reviewed) in REVIEWED_BLOBS.items():
        if tree.get(path) in {b"100644 blob " + blob.encode("ascii") for blob in reviewed}:
            if old is None:
                del tree[path]
            else:
                tree[path] = b"100644 blob " + old.encode("ascii")
    return tree


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
