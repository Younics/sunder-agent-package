#!/usr/bin/env python3
"""Inventory and explicitly remove reviewed, ignored pre-canonical Agent residue.

No builds, Git writes, automatic hooks, wildcard cleanup, or source migration.
See docs/physical-residue-cleanup.md. Requires Python 3.9+ and POSIX dir_fd APIs.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import sys


FAMILIES = tuple("Sunder.Package.Agent" + suffix for suffix in (
    "", ".Builder", ".Execution.Docker", ".Execution.Local", ".Mcp",
    ".Memory.Semantic", ".Provider.Anthropic", ".Provider.Gemini",
    ".Provider.LMStudio", ".Provider.OpenAI", ".Skills", ".Subagents",
    ".Tools.Files", ".Tools.Shell", ".Tools.Web",
))
RETIRED_ROOTS = (
    "fixtures/PackedProtocolConsumer",
    "src/Sunder.Package.Agent.Contracts",
    "src/Sunder.Package.Agent/OwnerProtocol",
    "src/Sunder.Package.Agent.Builder/Protocol",
    "src/Sunder.Package.Agent.Execution.Docker/Protocol",
    "src/Sunder.Package.Agent.Execution.Local/Protocol",
    "src/Sunder.Package.Agent.Mcp/Shared",
    "src/Sunder.Package.Agent.Memory.Semantic/Protocol",
    "src/Sunder.Package.Agent.Provider.Anthropic/Protocol",
    "src/Sunder.Package.Agent.Provider.Gemini/Protocol",
    "src/Sunder.Package.Agent.Provider.LMStudio/Protocol",
    "src/Sunder.Package.Agent.Provider.OpenAI/Shared",
    "src/Sunder.Package.Agent.Skills/Protocol",
    "src/Sunder.Package.Agent.Subagents/Protocol",
    "src/Sunder.Package.Agent.Skills/App",
)
RETAINED_APPS = tuple(f"src/{family}/App" for family in FAMILIES[:12]
                      if family != "Sunder.Package.Agent.Skills")
RID_LEAVES = tuple(f"src/{family}/bin/Debug/net10.0/osx-arm64" for family in FAMILIES)
ALLOWLIST = dict.fromkeys(RETIRED_ROOTS, "retired-root")
ALLOWLIST.update({f"{app}/{output}": "retained-app-output"
                  for app in RETAINED_APPS for output in ("bin", "obj")})
ALLOWLIST.update(dict.fromkeys(RID_LEAVES, "old-direct-rid"))
# A fixed historical boundary, never a moving "older than N days" policy.
RID_BEFORE = "2026-10-05T00:00:00+00:00"
RID_BEFORE_NS = int(datetime.fromisoformat(RID_BEFORE).timestamp()) * 1_000_000_000
EMPTY_EXTRAS = ("fixtures", ".circleci", "scripts/__pycache__")
# Reviewed source consolidation: only these now-empty App paths may be pruned.
# The seven removed source paths can still be in the index as unstaged deletions.
# Their absence is preserved; this is never permission to unlink a tracked file.
CONSOLIDATED_PROPERTIES = tuple(f"src/Sunder.Package.Agent{suffix}/App/Properties" for suffix in (
    "", ".Execution.Docker", ".Execution.Local", ".Mcp", ".Memory.Semantic", ".Provider.OpenAI",
))
CONSOLIDATED_APPS = tuple(f"src/Sunder.Package.Agent{suffix}/App" for suffix in (
    "", ".Mcp", ".Provider.OpenAI", ".Subagents",
))
CONSOLIDATED_EMPTY_PATHS = frozenset((*CONSOLIDATED_PROPERTIES, *CONSOLIDATED_APPS,
                                    "src/Sunder.Package.Agent/App/Runtime"))
CONSOLIDATED_MISSING = frozenset((*[p + "/AssemblyInfo.cs" for p in CONSOLIDATED_PROPERTIES],
                                "src/Sunder.Package.Agent.Subagents/App/RuntimeNamespaceMarker.cs"))
OUTPUT_NAMES = {"bin", "obj", "artifacts", "debug", "release", "testresults",
                "sunder-roles", "sunder-targets", "sunder-dev", "canonical-api",
                ".git", ".sunder", "__pycache__"}
FROZEN = "tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects"
LEGACY_ASSEMBLIES = {"Sunder.Package.Agent.Protocol", "Sunder.Package.Agent.OwnerProtocol",
                     "Sunder.Package.Agent.Contracts", "DockerProtocol", "LocalProtocol",
                     "SkillsProtocol", "SubagentsProtocol", "Sunder.Package.Agent.Mcp.Shared",
                     "Sunder.Package.Agent.Provider.OpenAI.Shared"}
POLICY = {"allowlist": ALLOWLIST, "ridBefore": RID_BEFORE, "emptyExtras": EMPTY_EXTRAS,
          "emptyScope": "src outside outputs, retained Apps, and frozen/history areas",
          "consolidatedEmptyPaths": sorted(CONSOLIDATED_EMPTY_PATHS),
          "consolidatedMissingSources": sorted(CONSOLIDATED_MISSING),
          "currentRidPolicy": "preserve empty intermediates and verified current non-legacy outputs"}


class Refusal(ValueError):
    pass


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=True).encode()).hexdigest()


def relative(value):
    if (not value or "\\" in value or ":" in value or "\0" in value
            or value.startswith("/") or any(p in ("", ".", "..") for p in value.split("/"))):
        raise Refusal(f"Non-canonical relative path: {value!r}")
    return value


def under(path, parent):
    return path == parent or path.startswith(parent + "/")


def kind(info):
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
        return "link-or-reparse"
    if stat.S_ISDIR(info.st_mode):
        return "directory"
    return "file" if stat.S_ISREG(info.st_mode) else "special"


def identity(info):
    return {"device": info.st_dev, "inode": info.st_ino, "mode": info.st_mode,
            "size": info.st_size, "mtimeNs": info.st_mtime_ns, "ctimeNs": info.st_ctime_ns,
            "links": info.st_nlink}


def git(root, *args, data=None, allowed=(0,)):
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    env["GIT_OPTIONAL_LOCKS"] = "0"
    result = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
                             "-C", str(root), *args], input=data, capture_output=True, env=env)
    if result.returncode not in allowed:
        raise Refusal(f"git {' '.join(args)}: {result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def paths(raw):
    return {relative(os.fsdecode(p)) for p in raw.split(b"\0") if p}


class Repository:
    """All repository traversal is descriptor-relative and refuses link ancestors."""

    def __init__(self, root):
        if (not hasattr(os, "O_NOFOLLOW") or os.open not in os.supports_dir_fd
                or os.scandir not in os.supports_fd):
            raise Refusal("Safe POSIX no-follow/dir_fd APIs are required; apply is not portable to Windows.")
        raw = Path(root)
        if ".." in raw.parts:
            raise Refusal("Repository path must not contain '..'")
        self.root = raw.absolute()
        self.fd = os.open(self.root.anchor, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            for part in self.root.parts[1:]:
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=self.fd)
                os.close(self.fd)
                self.fd = child
            if Path(os.fsdecode(git(self.root, "rev-parse", "--show-toplevel")).strip()) != self.root:
                raise Refusal("Use the Agent Git repository root, not a subdirectory")
            self.refresh_git()
        except BaseException:
            self.close()
            raise

    def close(self):
        os.close(self.fd)

    def refresh_git(self):
        self.tracked = paths(git(self.root, "ls-files", "--cached", "-z"))
        # Protect restored files even when their deletion is staged in the index.
        self.head = paths(git(self.root, "ls-tree", "-r", "--name-only", "-z", "HEAD",
                              allowed=(0, 128)))

    @contextmanager
    def directory(self, path=""):
        fd = os.dup(self.fd)
        try:
            for part in relative(path).split("/") if path else ():
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = child
            yield fd
        finally:
            os.close(fd)

    @contextmanager
    def parent(self, path):
        relative(path)
        parent, _, name = path.rpartition("/")
        with self.directory(parent) as fd:
            yield fd, name

    def info(self, path):
        try:
            with self.parent(path) as (fd, name):
                return os.stat(name, dir_fd=fd, follow_symlinks=False)
        except FileNotFoundError:
            return None

    def read(self, path):
        with self.parent(path) as (fd, name):
            with os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK,
                                   dir_fd=fd), "rb") as stream:
                before = os.fstat(stream.fileno())
                if kind(before) != "file":
                    raise Refusal(f"Not a regular file: {path}")
                value = stream.read()
                if identity(before) != identity(os.fstat(stream.fileno())):
                    raise Refusal(f"File changed while reading: {path}")
                return value

    def tree(self, path, *, hashes=False, skip=None):
        nodes = []

        def visit(fd, name, rel):
            if skip and skip(rel):
                return
            info = os.stat(name, dir_fd=fd, follow_symlinks=False)
            node = {"path": rel, "kind": kind(info), **identity(info)}
            nodes.append(node)
            if node["kind"] == "directory":
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                try:
                    if identity(info) != identity(os.fstat(child)):
                        raise Refusal(f"Directory changed while opening: {rel}")
                    for entry in sorted(os.listdir(child)):
                        visit(child, entry, rel + "/" + entry)
                    if identity(info) != identity(os.fstat(child)):
                        raise Refusal(f"Directory changed while scanning: {rel}")
                finally:
                    os.close(child)
            elif node["kind"] == "file" and hashes:
                with os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK,
                                       dir_fd=fd), "rb") as stream:
                    if identity(info) != identity(os.fstat(stream.fileno())):
                        raise Refusal(f"File changed while opening: {rel}")
                    sha = hashlib.sha256()
                    for block in iter(lambda: stream.read(1024 * 1024), b""):
                        sha.update(block)
                    if identity(info) != identity(os.fstat(stream.fileno())):
                        raise Refusal(f"File changed while hashing: {rel}")
                    node["sha256"] = sha.hexdigest()
            elif node["kind"] == "link-or-reparse" and stat.S_ISLNK(info.st_mode):
                node["linkTarget"] = os.readlink(name, dir_fd=fd)

        try:
            with self.parent(path) as (fd, name):
                visit(fd, name, path)
        except FileNotFoundError:
            if nodes:
                raise Refusal(f"Tree changed while scanning: {path}")
        return nodes

    def ignored(self, files):
        if not files:
            return set()
        raw = b"\0".join(os.fsencode(p) for p in files) + b"\0"
        return paths(git(self.root, "check-ignore", "--no-index", "--stdin", "-z",
                         data=raw, allowed=(0, 1)))


def validate_agent(repo):
    manifest = json.loads(repo.read("packages.json"))
    expected = {f"src/{f}/{f}.csproj" for f in FAMILIES}
    actual = [p["projectPath"] for p in manifest["packages"]]
    if set(actual) != expected or len(actual) != 15:
        raise Refusal("packages.json must contain the reviewed 15 canonical Agent projects")
    for project in expected:
        info = repo.info(project)
        if project not in repo.tracked or info is None or kind(info) != "file":
            raise Refusal(f"Missing tracked canonical project: {project}")
    current = json.loads(repo.read("compatibility/agent-current-inventory.json"))["packages"]
    if len(current) != 15 or {p["projectPath"] for p in current} != expected:
        raise Refusal("Current test inventory no longer matches the canonical project allowlist")


def evidence_name(path):
    parts = PurePosixPath(path).parts
    name = parts[-1].lower()
    return (any(p.lower() in {"sunder-roles", "sunder-targets", "artifacts", ".git"}
                or "precanonical" in p.lower() for p in parts)
            or name.startswith(("role-inputs.", "native-leaf", "native-test-evidence"))
            or "receipt" in name or "provenance" in name or "evidence" in name
            or name.endswith((".sunderpkg", ".nupkg", ".snupkg", ".tar.gz", ".zip")))


def qualified_sibling(repo, path, *, current=False):
    family = path.split("/")[1]
    base = path.rsplit("/", 1)[0]
    for retained in ("sunder-roles", "sunder-targets", "sunder-dev"):
        info = repo.info(f"{base}/{retained}")
        if info is None or kind(info) != "directory":
            raise Refusal(f"Missing current sibling output: {base}/{retained}")
    qualification = f"{base}/sunder-targets/osx-arm64/role-inputs.v1.json"
    raw = repo.read(qualification)
    sha = hashlib.sha256(raw).hexdigest()
    if repo.read(qualification + ".sha256").decode().strip() != sha:
        raise Refusal(f"Qualified sibling digest mismatch: {qualification}")
    inputs = json.loads(raw)["inputs"]
    # Qualification can be rooted at the repository or its src directory.
    # Accept only the two exact canonical identities, never arbitrary suffixes.
    project = f"src/{family}/{family}.csproj"
    if inputs.get("project") not in (project, project.removeprefix("src/")):
        raise Refusal(f"Qualified sibling project mismatch: {qualification}")
    for key, value in {"configuration": "Debug",
                       "targetFramework": "net10.0", "runtimeIdentifier": "osx-arm64"}.items():
        if inputs.get(key) != value:
            raise Refusal(f"Qualified sibling {key} mismatch: {qualification}")
    if not current and path in raw.decode().replace("\\\\", "/"):
        raise Refusal(f"Qualified sibling refers to the obsolete candidate: {path}")
    if current:
        package = next(p for p in json.loads(repo.read("packages.json"))["packages"] if p["projectPath"] == project)
        if not package.get("packageId") or inputs.get("packageId") != package["packageId"]:
            raise Refusal(f"Qualified sibling current package identity mismatch: {qualification}")
        if repo.info(qualification).st_mtime_ns < RID_BEFORE_NS:
            raise Refusal(f"Qualified sibling is not current: {qualification}")
    return {"preservedQualification": qualification, "qualificationSha256": sha}


def classify_rid(repo, path, nodes):
    family = path.split("/")[1]
    files = [n for n in nodes if n["kind"] == "file"]
    if not files:
        # Canonical builds may create this intermediate directory without emitting
        # direct role bytes. It is active bin layout, not retired output regeneration.
        return {"disposition": "preserved", "reason": "empty canonical RID intermediate"}
    newest = max(n["mtimeNs"] for n in files)
    classification = {"newestFileUtc": datetime.fromtimestamp(newest / 1e9, timezone.utc).isoformat(),
                      "mustPredateUtc": RID_BEFORE}
    deps = json.loads(repo.read(f"{path}/{family}.deps.json"))
    if deps.get("runtimeTarget", {}).get("name") != ".NETCoreApp,Version=v10.0/osx-arm64":
        raise Refusal("Direct RID leaf does not have the exact-RID runtime target")
    legacy = sorted(n.split("/")[0] for n in deps.get("libraries", {})
                    if n.split("/")[0] in LEGACY_ASSEMBLIES
                    or n.split("/")[0].startswith("Sunder.Package.Agent.")
                    and n.split("/")[0].endswith(".Protocol"))
    if newest >= RID_BEFORE_NS:
        if legacy:
            raise Refusal(f"Legacy direct RID leaf is newer than reviewed October 4 outputs ({classification})")
        return {**classification, **qualified_sibling(repo, path, current=True),
                "disposition": "preserved", "reason": "current non-legacy canonical RID output"}
    if not legacy:
        raise Refusal("Direct RID leaf has no retired protocol dependency; classification needs review")
    qualification = qualified_sibling(repo, path)
    if repo.info(qualification["preservedQualification"]).st_mtime_ns <= newest:
        raise Refusal("Qualified sibling is not newer than the direct RID leaf")
    return {**classification, **qualification, "legacyDependencies": legacy, "disposition": "eligible"}


def candidate(repo, path):
    relative(path)
    if path not in ALLOWLIST:
        raise Refusal(f"Outside exact residue allowlist: {path}")
    result = {"path": path, "category": ALLOWLIST[path], "state": "absent", "nodes": []}
    try:
        nodes = repo.tree(path)
        result["nodes"] = nodes
        if not nodes:
            return result
        if nodes[0]["kind"] != "directory":
            raise Refusal("Candidate root is not an ordinary directory")
        tracked = sorted(p for p in repo.tracked if under(p, path))
        if tracked:
            raise Refusal(f"Tracked path in candidate: {tracked[0]}")
        for n in nodes:
            relative(n["path"])
            if n["kind"] not in ("file", "directory"):
                raise Refusal(f"Link, reparse point, or special file: {n['path']}")
            if n["device"] != nodes[0]["device"] or n["device"] != os.fstat(repo.fd).st_dev:
                raise Refusal(f"Filesystem boundary: {n['path']}")
            if n["kind"] == "file" and n["links"] != 1:
                raise Refusal(f"Hard-linked file: {n['path']}")
            if n["path"] in repo.head:
                raise Refusal(f"HEAD-tracked path (possibly staged deletion): {n['path']}")
            if evidence_name(n["path"]):
                raise Refusal(f"Preserved output/evidence name: {n['path']}")
            if n["path"].lower().endswith(("proj", ".sln", ".slnx")):
                raise Refusal(f"Active project/solution: {n['path']}")
        files = [n["path"] for n in nodes if n["kind"] == "file"]
        not_ignored = sorted(set(files) - repo.ignored(files))
        if not_ignored:
            raise Refusal(f"Untracked nonignored file: {not_ignored[0]}")
        if result["category"] == "retired-root":
            outside = [p for p in files if p[len(path) + 1:].split("/")[0] not in ("bin", "obj")]
            if outside:
                raise Refusal(f"File outside retired immediate bin/obj: {outside[0]}")
        if result["category"] == "retained-app-output":
            app = path.rsplit("/", 1)[0]
            source = repo.tree(app, skip=lambda p: p != app and p.split("/")[-1].lower() in OUTPUT_NAMES)
            if any(n["path"].lower().endswith(("proj", ".sln", ".slnx")) for n in source):
                raise Refusal(f"Retained App now contains an active project: {app}")
            if not any(under(p, app) and not under(p, path) for p in repo.tracked):
                raise Refusal(f"Retained App source inventory changed: {app}")
        if result["category"] == "old-direct-rid":
            result["classification"] = classify_rid(repo, path, nodes)
            if result["classification"]["disposition"] == "preserved":
                result["state"] = "preserved"
                return result
        result["state"] = "eligible"
    except (Refusal, OSError, ValueError, KeyError) as error:
        result["state"] = "refused"
        result["reason"] = str(error)
    return result


def check_consolidation(repo, path):
    if path not in CONSOLIDATED_EMPTY_PATHS:
        raise Refusal(f"Outside exact source-consolidation empty allowlist: {path}")
    for tracked in repo.tracked:
        if under(tracked, path) and (tracked not in CONSOLIDATED_MISSING or repo.info(tracked) is not None):
            raise Refusal(f"Unreviewed tracked path in consolidated directory: {tracked}")
    family = path.split("/")[1]
    if family != "Sunder.Package.Agent.Subagents":
        canonical = f"src/{family}/Properties/AssemblyInfo.cs"
        info = repo.info(canonical)
        if canonical not in repo.tracked or info is None or kind(info) != "file":
            raise Refusal(f"Missing retained canonical assembly attributes: {canonical}")


def consolidation_empty_directories(repo):
    empty = {}
    refused = []
    # Main/App/Runtime is included only as part of an entirely empty main App
    # tree. No traversal of other source-bearing App subdirectories is authorized.
    for path in (*CONSOLIDATED_PROPERTIES, *CONSOLIDATED_APPS):
        try:
            nodes = repo.tree(path)
            for node in nodes:
                if node["kind"] not in ("file", "directory") or node["device"] != os.fstat(repo.fd).st_dev:
                    raise Refusal(f"Unexpected link/reparse/special entry or filesystem boundary: {node['path']}")
            if not nodes or any(n["kind"] != "directory" or n["path"] not in CONSOLIDATED_EMPTY_PATHS for n in nodes):
                continue  # Nonempty or unreviewed directories are retained, never recursively removed.
            check_consolidation(repo, path)
            for node in nodes:
                empty[node["path"]] = {k: node[k] for k in ("path", "device", "inode")}
        except (Refusal, OSError) as error:
            refused.append(str(error))
    return list(empty.values()), refused


def empty_directories(repo, candidates):
    removed = {c["path"] for c in candidates if c["state"] == "eligible"}
    empty, refused = consolidation_empty_directories(repo)

    def visit(path):
        if path in removed:
            return True
        if path in ALLOWLIST or path in RETAINED_APPS:
            return False  # Do not bypass a refused candidate with the empty-directory pass.
        name = path.split("/")[-1].lower()
        if (name in OUTPUT_NAMES or "history" in name or "precanonical" in name):
            return False
        info = repo.info(path)
        if info is None:
            return False
        if kind(info) != "directory":
            if kind(info) != "file":
                refused.append(f"Unexpected link/reparse/special entry outside outputs: {path}")
            return False
        with repo.directory(path) as fd:
            children = [visit(path + "/" + name) for name in sorted(os.listdir(fd))]
        if (all(children) and path != "src" and path not in RETAINED_APPS
                and not any(under(p, path) for p in repo.tracked)):
            empty.append({"path": path, "device": info.st_dev, "inode": info.st_ino})
            return True
        return False

    visit("src")
    for path in EMPTY_EXTRAS:
        info = repo.info(path)
        if info is None:
            continue
        if kind(info) != "directory":
            refused.append(f"Empty-only root is not an ordinary directory: {path}")
            continue
        with repo.directory(path) as fd:
            children = [path + "/" + name for name in os.listdir(fd)]
        if all(p in removed for p in children) and not any(under(p, path) for p in repo.tracked):
            empty.append({"path": path, "device": info.st_dev, "inode": info.st_ino})
    return empty, refused


def inventory(repo):
    repo.refresh_git()
    validate_agent(repo)
    candidates = [candidate(repo, p) for p in ALLOWLIST]
    empty, refused = empty_directories(repo, candidates)
    eligible = [c for c in candidates if c["state"] == "eligible"]
    nodes = [n for c in eligible for n in c["nodes"]]
    return {"candidates": candidates, "emptyDirectories": empty, "refusals": refused,
            "counts": {"eligibleRoots": len(eligible),
                       "preservedRoots": sum(c["state"] == "preserved" for c in candidates),
                       "refusedRoots": sum(c["state"] == "refused" for c in candidates),
                       "files": sum(n["kind"] == "file" for n in nodes),
                       "bytes": sum(n["size"] for n in nodes if n["kind"] == "file"),
                       "directories": sum(n["kind"] == "directory" for n in nodes) + len(empty),
                       "emptyOnlyDirectories": len(empty)}}


def stable_files(nodes):
    # Directory mtimes/link counts change when adjacent obsolete leaves are removed.
    return {n["path"]: {k: n[k] for k in ("kind", "size", "mode", "sha256", "linkTarget") if k in n}
            for n in nodes if n["kind"] != "directory"}


def file_proof(files):
    return {"files": files, "count": len(files), "sha256": digest(files)}


def preservation(repo, plan):
    source = repo.tracked | repo.head | paths(git(repo.root, "ls-files", "--others", "--exclude-standard", "-z"))
    files = {}
    for path in sorted(source):
        nodes = repo.tree(path, hashes=True)
        if nodes and nodes[0]["kind"] == "directory":
            raise Refusal(f"Source path became a directory or nested repository: {path}")
        files.update(stable_files(nodes) if nodes else {path: {"kind": "missing"}})
    index = os.fsdecode(git(repo.root, "rev-parse", "--git-path", "index")).strip()
    index_path = Path(index) if Path(index).is_absolute() else repo.root / index
    index_files = {str(index_path): hashlib.sha256(index_path.read_bytes()).hexdigest()}
    shared = os.fsdecode(git(repo.root, "rev-parse", "--shared-index-path")).strip()
    if shared:
        shared_path = Path(shared) if Path(shared).is_absolute() else repo.root / shared
        index_files[str(shared_path)] = hashlib.sha256(shared_path.read_bytes()).hexdigest()
    excluded = {c["path"] for c in plan["candidates"] if c["state"] == "eligible"}
    retained = []
    for path in ("src", "artifacts", "compatibility"):
        retained.extend(repo.tree(path, hashes=True, skip=lambda p: p in excluded))
    return {"source": file_proof(files), "index": index_files,
            "indexEntriesSha256": hashlib.sha256(git(repo.root, "ls-files", "--stage", "-z")).hexdigest(),
            "statusSha256": hashlib.sha256(git(repo.root, "status", "--porcelain=v1", "-z",
                                                "--untracked-files=all")).hexdigest(),
            "frozen": file_proof({p: v for p, v in files.items() if under(p, FROZEN)}),
            "retained": file_proof(stable_files(retained))}


def delete_candidate(repo, reviewed, removed):
    # Reconstruct eligibility from Git and disk; a report is never a deletion path list.
    repo.refresh_git()
    current = candidate(repo, reviewed["path"])
    if current != reviewed or current["state"] != "eligible":
        raise Refusal(f"Candidate changed since review: {reviewed['path']}")
    for node in sorted(current["nodes"], key=lambda n: n["path"].count("/"), reverse=True):
        with repo.parent(node["path"]) as (fd, name):
            now = os.stat(name, dir_fd=fd, follow_symlinks=False)
            if node["kind"] == "directory":
                # Children we just removed necessarily changed directory timestamps.
                if kind(now) != "directory" or (now.st_dev, now.st_ino) != (node["device"], node["inode"]):
                    raise Refusal(f"Directory replaced during apply: {node['path']}")
                os.rmdir(name, dir_fd=fd)  # Never recursively removes unreviewed additions.
                removed["directories"].append(node["path"])
            else:
                if identity(now) != {k: node[k] for k in identity(now)} or kind(now) != "file":
                    raise Refusal(f"File changed during apply: {node['path']}")
                os.unlink(name, dir_fd=fd)
                removed["files"].append(node["path"])
                removed["bytes"] += node["size"]


def apply_plan(repo, plan, removed):
    # Complete fresh preflight before deleting anything, then a per-root recheck.
    if plan["counts"]["refusedRoots"] or plan["refusals"]:
        raise Refusal("Preflight refused; nothing removed")
    if inventory(repo) != plan:
        raise Refusal("Inventory changed before apply")
    for entry in plan["candidates"]:
        if entry["state"] == "eligible":
            delete_candidate(repo, entry, removed)
    repo.refresh_git()
    for entry in sorted(plan["emptyDirectories"], key=lambda n: n["path"].count("/"), reverse=True):
        path = relative(entry["path"])
        if (not (path.startswith("src/") or path in EMPTY_EXTRAS)
                or path in RETAINED_APPS and path not in CONSOLIDATED_APPS):
            raise Refusal(f"Outside empty-only scope: {path}")
        if path in CONSOLIDATED_EMPTY_PATHS:
            check_consolidation(repo, path)
        elif any(under(p, path) for p in repo.tracked):
            raise Refusal(f"Empty directory now contains tracked paths: {path}")
        with repo.parent(path) as (fd, name):
            info = os.stat(name, dir_fd=fd, follow_symlinks=False)
            if kind(info) != "directory" or (info.st_dev, info.st_ino) != (entry["device"], entry["inode"]):
                raise Refusal(f"Empty directory replaced: {path}")
            os.rmdir(name, dir_fd=fd)
            removed["directories"].append(path)


def print_inventory(plan):
    for entry in plan["candidates"]:
        if entry["state"] != "absent":
            print(f"{entry['state']:8} {entry['category']:19} {entry['path']}")
            if "reason" in entry:
                print(f"         {entry['reason']}")
            if "classification" in entry:
                classification = entry["classification"]
                if entry["state"] == "preserved":
                    print(f"         {classification['reason']}")
                else:
                    print(f"         newest file: {classification['newestFileUtc']}; legacy dependencies verified")
    for entry in plan["emptyDirectories"]:
        print(f"empty    {entry['path']}")
    for error in plan["refusals"]:
        print(f"refused  {error}")
    print(json.dumps(plan["counts"], sort_keys=True))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).absolute().parents[2])
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true", help="default; inventory only")
    mode.add_argument("--check", action="store_true", help="read-only regeneration scan; exit 1 for residue, 2 for refusal")
    mode.add_argument("--apply", action="store_true", help="requires a matching --review dry-run and a new --report")
    parser.add_argument("--review", type=Path, help="previous dry-run JSON; never used to supply deletion paths")
    parser.add_argument("--report", type=Path, help="new external JSON file including source/index/retained-byte proof")
    args = parser.parse_args(argv)
    if args.apply and (not args.review or not args.report):
        parser.error("--apply requires --review and --report")
    if args.review and not args.apply:
        parser.error("--review is only meaningful with --apply")
    repo = None
    report_stream = None
    report = {}
    try:
        repo = Repository(args.repo)
        plan = inventory(repo)
        print_inventory(plan)
        report = {"schemaVersion": 1, "mode": "apply" if args.apply else "check" if args.check else "dry-run",
                  "repository": str(repo.root), "policySha256": digest(POLICY), "before": plan}
        if args.report:
            destination = args.report.absolute()
            if destination.resolve().is_relative_to(repo.root):
                raise Refusal("Evidence must be outside the Agent repository")
            report_stream = destination.open("x", encoding="utf-8")
            report["preservationBefore"] = preservation(repo, plan)

        def save():
            if report_stream:
                report_stream.seek(0)
                json.dump(report, report_stream, indent=2, sort_keys=True)
                report_stream.write("\n")
                report_stream.truncate()
                report_stream.flush()
                os.fsync(report_stream.fileno())

        save()
        if plan["counts"]["refusedRoots"] or plan["refusals"]:
            raise Refusal("Preflight refused; nothing removed")
        if args.apply:
            reviewed = json.loads(args.review.read_text())
            if (reviewed.get("schemaVersion") != 1 or reviewed.get("mode") != "dry-run"
                    or any(reviewed.get(k) != report[k] for k in
                           ("repository", "policySha256", "before", "preservationBefore"))):
                raise Refusal("Dry-run review no longer matches inventory/source/index/retained bytes; rerun and review")
            removed = report["removed"] = {"files": [], "directories": [], "bytes": 0}
            try:
                apply_plan(repo, plan, removed)
            finally:
                save()  # Record any partial progress; never silently retry or widen scope.
            report["after"] = inventory(repo)
            report["preservationAfter"] = preservation(repo, plan)
            report["preservationUnchanged"] = report["preservationBefore"] == report["preservationAfter"]
            save()
            if not report["preservationUnchanged"]:
                raise Refusal("Preservation proof changed during apply; inspect report (possible concurrent writer)")
            print(f"Removed {len(removed['files'])} files, {len(removed['directories'])} directories, {removed['bytes']} bytes.")
            print("Source, index, frozen captures and retained source/output hashes unchanged.")
            for key in ("source", "frozen", "retained"):
                proof = report["preservationAfter"][key]
                print(f"{key}: {proof['count']} paths; SHA-256 {proof['sha256']}")
            print("index: " + json.dumps(report["preservationAfter"]["index"], sort_keys=True))
            if (report["after"]["counts"]["eligibleRoots"] or report["after"]["emptyDirectories"]
                    or report["after"]["counts"]["refusedRoots"] or report["after"]["refusals"]):
                raise Refusal("Residue regenerated during apply; inspect the after inventory")
        return 1 if args.check and (plan["counts"]["eligibleRoots"] or plan["emptyDirectories"]) else 0
    except (Refusal, OSError, ValueError, KeyError) as error:
        print(f"REFUSED: {error}", file=sys.stderr)
        if report_stream:
            report["error"] = str(error)
            report_stream.seek(0)
            json.dump(report, report_stream, indent=2, sort_keys=True)
            report_stream.write("\n")
            report_stream.truncate()
        return 2
    finally:
        if report_stream:
            report_stream.close()
        if repo:
            repo.close()


if __name__ == "__main__":
    sys.exit(main())
