#!/usr/bin/env python3
"""Small GitHub asset transport and public installation checks owned by Agent."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
import time
from urllib.parse import quote

import agent_cohort as cohort
import agent_release as r

WORKFLOW = ".github/workflows/sunder-package-release.yml"
REPOSITORY = "Younics/sunder-agent-package"
REQUEST = "agent-family-cohort-request.json"
MAX_ASSET = 2 * 1024 ** 3


def asset_name(value):
    r.require(type(value) is str and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,159}", value), "Unsafe release asset name")
    return value


def asset_identity(asset):
    r.positive(asset["id"])
    asset_name(asset["name"])
    r.require(asset["state"] == "uploaded" and r.positive(asset["size"]) <= MAX_ASSET
              and type(asset.get("digest")) is str and asset["digest"].startswith("sha256:"), "Incomplete release asset")
    r.digest(asset["digest"][7:])
    return asset["size"], asset["digest"]


def file_identity(path):
    path = r.regular(path)
    size = path.stat().st_size
    r.require(0 < size <= MAX_ASSET, "Release file exceeds its byte bound")
    digest = r.file_sha(path)
    r.require(path.stat().st_size == size, "Release file changed during hashing")
    return size, "sha256:" + digest


class GitHubRelease:
    def __init__(self, repository, tag):
        match = re.fullmatch(r"agent(?:-[a-z0-9]+)*/v(.+)", tag)
        r.require(repository == REPOSITORY and match, "Unexpected package release")
        r.version(match[1])
        self.repository, self.tag = repository, tag

    @property
    def prerelease(self):
        return not r.version(self.tag.split("/v", 1)[1])[1]

    def api(self, suffix, method="GET", value=None):
        command = ["gh", "api", "--hostname", "github.com", "--method", method,
            "-H", "Accept: application/vnd.github+json", f"repos/{self.repository}/{suffix}"]
        if value is not None: command += ["--input", "-"]
        result = subprocess.run(command, input=r.canonical(value) if value is not None else None, capture_output=True, timeout=300)
        r.require(result.returncode == 0, f"GitHub {method} failed: {suffix}")
        return r.decode(result.stdout) if result.stdout.strip() else None

    def resolve(self):
        from agent_selection import releases
        matches = [value for value in releases() if value["tag_name"] == self.tag]
        r.require(len(matches) <= 1, "Ambiguous package release")
        return matches[0] if matches else None

    def get(self, release_id):
        value = self.api("releases/" + str(r.positive(int(release_id))))
        r.require(type(value["id"]) is int and value["id"] == int(release_id) and value["tag_name"] == self.tag
                  and value["prerelease"] is self.prerelease and type(value["draft"]) is bool, "Release identity/version differs")
        r.require(type(value["assets"]) is list and len(value["assets"]) <= 256, "Oversized release inventory")
        names, identifiers = set(), set()
        for asset in value["assets"]:
            asset_identity(asset)
            r.require(asset["name"].casefold() not in names and asset["id"] not in identifiers, "Unsafe/ambiguous release asset")
            names.add(asset["name"].casefold()); identifiers.add(asset["id"])
        return value

    def create(self, source, title):
        r.digest(source, 40)
        value = self.resolve()
        if value is None:
            try:
                self.api("releases", "POST", dict(tag_name=self.tag, target_commitish=source, name=title,
                    draft=True, prerelease=not r.version(self.tag.split("/v", 1)[1])[1], make_latest="false"))
            except (ValueError, subprocess.TimeoutExpired): pass
            for _ in range(4):
                value = self.resolve()
                if value is not None: break
                time.sleep(1)
        r.require(value and value["target_commitish"] == source and value["prerelease"] is self.prerelease, "Release source/version differs")
        current = self.get(value["id"])
        r.require(current["target_commitish"] == source, "Release source changed")
        return current

    def download(self, asset, destination):
        destination = Path(destination)
        expected = asset_identity(asset)
        r.require(destination.parent.is_dir() and not destination.is_symlink(), "Unsafe download destination")
        with tempfile.NamedTemporaryFile(dir=destination.parent, prefix=".release-", delete=False) as output:
            path = Path(output.name)
        try:
            with path.open("wb") as output, tempfile.TemporaryFile() as errors:
                process = subprocess.Popen(["gh", "api", "--hostname", "github.com", "-H", "Accept: application/octet-stream",
                    f"repos/{self.repository}/releases/assets/{asset['id']}"], stdout=subprocess.PIPE, stderr=errors)
                timer = threading.Timer(300, process.kill)
                timer.start()
                try:
                    size = 0
                    while data := process.stdout.read(1024 * 1024):
                        size += len(data)
                        r.require(size <= asset["size"], "Downloaded asset exceeds its declared size")
                        output.write(data)
                    r.require(process.wait(timeout=10) == 0, "Release download failed")
                finally:
                    timer.cancel()
                    if process.poll() is None: process.kill()
                    process.wait()
                    process.stdout.close()
            r.require(file_identity(path) == expected, "Downloaded asset bytes differ")
            if destination.exists():
                r.require(file_identity(destination) == expected, "Download would overwrite different bytes")
            else:
                os.link(path, destination)  # Atomic no-replace publication.
        finally:
            path.unlink(missing_ok=True)

    def upload(self, release_id, paths):
        paths = list(paths)
        names = [asset_name(Path(path).name).casefold() for path in paths]
        r.require(len(names) == len(set(names)), "Duplicate upload names")
        for path in paths:
            path = Path(path)
            expected = file_identity(path)
            for attempt in range(4):
                value = self.get(release_id)
                r.require(value["draft"], "Cannot change an announced release")
                r.require(not any(asset["name"].casefold() == path.name.casefold() and asset["name"] != path.name
                                  for asset in value["assets"]), "Case-colliding release asset")
                existing = [asset for asset in value["assets"] if asset["name"] == path.name]
                if existing:
                    item = existing[0]
                    r.require(asset_identity(item) == expected, "Immutable asset conflict")
                    with tempfile.TemporaryDirectory() as temporary: self.download(item, Path(temporary) / path.name)
                    r.require(file_identity(path) == expected, "Upload input changed")
                    break
                r.require(attempt < 3, "Release upload did not become observable")
                try:
                    subprocess.run(["gh", "api", "--hostname", "github.com", "--method", "POST", "-H", "Content-Type: application/octet-stream",
                        "--input", str(path), f"https://uploads.github.com/repos/{self.repository}/releases/{release_id}/assets?name={quote(path.name, safe='')}"],
                        capture_output=True, timeout=300)
                except subprocess.TimeoutExpired: pass
                r.require(file_identity(path) == expected, "Upload input changed")
                time.sleep(1)

    def verify(self, release_id, files, draft=None):
        value = self.get(release_id)
        r.require((draft is None or value["draft"] is draft) and {asset["name"] for asset in value["assets"]} == set(files), "Release inventory/state differs")
        with tempfile.TemporaryDirectory() as temporary:
            for asset in value["assets"]:
                r.require(file_identity(files[asset["name"]]) == asset_identity(asset), "Local/remote bytes differ")
                self.download(asset, Path(temporary) / asset["name"])
        return value


def remote_tag(store, source):
    target = store.api("git/ref/tags/" + quote(store.tag, safe=""))["object"]
    for _ in range(9):
        r.digest(target["sha"], 40)
        if target["type"] == "commit":
            r.require(target["sha"] == source, "Agent tag moved away from its source")
            return
        r.require(target["type"] == "tag", "Invalid tag target")
        target = store.api("git/tags/" + target["sha"])["object"]
    raise r.Invalid("Excessive tag indirection")


def install_plans(directory, provenance):
    """Exercise every exact family package and role on all six supported RIDs."""
    manifests = {item["packageId"]: r.archive_metadata(Path(directory) / item["path"])[0] for item in provenance["artifacts"]}
    identities = {(item["packageId"], item["version"], item["packageContentIdentity"]) for item in provenance["artifacts"]}
    # This postcheck asks the Registry for the tested artifacts' requirements.
    # Keep each role/framework profile complete; App features cannot grant Worker support.
    requirements = {}
    for manifest in manifests.values():
        for target in manifest["targets"]:
            managed_app = target["role"] == "app" and target["kind"] == "avalonia"
            key = (target["role"], target["kind"], target["targetFramework"] if managed_app else "",
                   target["avaloniaVersion"] if managed_app else "")
            requirements.setdefault(key, set()).update(target["requiredHostCapabilities"])
    profiles = [dict(role=role, kind=kind, manifestVersions=[1], sdkMajors=[1],
                     hostCapabilities=sorted(capabilities), targetFrameworks=[framework] if framework else [],
                     avaloniaVersion=avalonia or None)
                for (role, kind, framework, avalonia), capabilities in sorted(requirements.items())]
    from ci_platforms import RIDS
    r.require(set(manifests) == set(r.FAMILY_PACKAGE_IDS) and len(identities) == len(manifests), "Incomplete/duplicate family install-plan input")
    for rid in RIDS:
        targets = {key: [dict(role=t["role"], rid=rid) for t in manifest["targets"] if t["rid"] == rid] for key, manifest in manifests.items()}
        payload = dict(packages=[dict(packageId=key, version=manifest["version"], desiredTargets=targets[key]) for key, manifest in sorted(manifests.items())],
            installedPackages=[], includePrerelease=any(not r.version(manifest["version"])[1] for manifest in manifests.values()),
            compatibility=dict(profiles=profiles, rid=rid))
        status, data, failed = cohort.http(provenance["publication"]["registryOrigin"], "/api/v1/packages/resolve-package-changes", r.encoded(payload))
        r.require(not failed and status == 200, "Public install-plan resolution failed for " + rid)
        value = r.decode(data)
        r.require(value["success"] is True and not value["errors"] and not value["conflicts"] and len(value["items"]) == len(manifests)
            and {(item["packageId"], item["version"], item["packageContentIdentity"]) for item in value["items"]} == identities, "Install plan does not select the exact Listed family")
        for item in value["items"]:
            expected = {(target["role"], rid) for target in targets[item["packageId"]]}
            r.require({(target["role"], target["rid"]) for target in item["targets"]} == expected
                and {(artifact["kind"], artifact["rid"]) for artifact in item["artifacts"]} == expected | {("shared", None)}, "Install plan omitted a role or selected another RID")
