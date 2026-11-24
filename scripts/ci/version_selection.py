#!/usr/bin/env python3
"""Plan validation from Git changes or explicit package IDs, without SDK evaluation."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import posixpath
import subprocess
import sys
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import agent_release as r

ROOT = Path(__file__).resolve().parents[2]


def package_ids(source, value):
    rows = r.inventory(source)["packages"]
    known = {row["packageId"] for row in rows}
    r.require(type(value) is list and all(type(item) is str for item in value)
              and len(value) == len(set(value)) and set(value) <= known, "Invalid CI package selection")
    return sorted(value)


def changed_paths(source, base):
    r.digest(base, 40)
    # Fetch the event's immutable base in shallow Actions checkouts. A missing
    # base is an error, never a reason to silently select all or skip tests.
    if subprocess.run(["git", "cat-file", "-e", base + "^{commit}"], cwd=source,
                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode:
        subprocess.run(["git", "fetch", "--no-tags", "--depth=1", "origin", base], cwd=source, check=True)
    # A rename affects both ends, including a deletion from an owning package.
    raw = subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "-z", base, "HEAD", "--"], cwd=source)
    return [os.fsdecode(path) for path in raw.split(b"\0") if path]


def documentation_only(path):
    return path in ("README.md", "CHANGELOG.md") or (path.startswith("docs/") and path.endswith(".md"))


def affected_by_changes(source, base):
    paths = [path for path in changed_paths(source, base) if not documentation_only(path)]
    if not paths:
        return []
    known = {row["projectPath"]: row["packageId"] for row in r.inventory(source)["packages"]}
    all_ids = set(known.values())
    if any(not path.startswith(("src/", "tests/")) for path in paths):
        return sorted(all_ids)

    solution = ET.parse(r.source_file(source, "Sunder.AgentPackage.slnx"))
    authored = {path: ET.parse(r.source_file(source, path)) for path in known}
    owners = dict(known)
    for node in solution.iter("Project"):
        path = node.attrib["Path"]
        if not path.startswith("tests/"):
            continue
        tree = ET.parse(r.source_file(source, path))
        owner = tree.findall("./PropertyGroup/SunderTestOwner")
        if len(owner) == 1 and owner[0].text in all_ids:
            owners[path] = owner[0].text
            authored[path] = tree
    roots = {}
    for project, owner in owners.items():
        roots.setdefault(str(PurePosixPath(project).parent), set()).add(owner)

    selected = set()
    for path in paths:
        candidates = [ids for folder, ids in roots.items() if path.startswith(folder + "/")]
        if len(candidates) != 1 or len(candidates[0]) != 1:
            # Shared source or fixtures, retired folders, and unknown ownership
            # must never turn an executable change into an empty validation.
            return sorted(all_ids)
        selected.update(candidates[0])

    consumers = {package_id: set() for package_id in all_ids}
    for project, package_id in known.items():
        lock_path = str(PurePosixPath(project).with_name("sunder-contracts.lock.json"))
        lock = r.load(r.source_file(source, lock_path))
        for dependency in lock["packageArtifacts"]:
            if dependency in consumers:
                consumers[dependency].add(package_id)
    for project, tree in authored.items():
        for reference in tree.findall("./ItemGroup/ProjectReference"):
            path = posixpath.normpath(posixpath.join(posixpath.dirname(project),
                reference.get("Include", "").replace("\\", "/")))
            if path in known:
                consumers[known[path]].add(owners[project])
    pending = list(selected)
    while pending:
        dependents = consumers[pending.pop()] - selected
        selected.update(dependents)
        pending.extend(dependents)
    return sorted(selected)


def projects(inventory, selected):
    """Component ownership is exact; family checks run once in their shared lane."""
    if selected is None:
        return inventory["projects"]
    return [row for row in inventory["projects"] if row["owner"] in selected]


def scope_inventory(inventory, selected, mode, platform):
    return {**inventory, "projects": projects(inventory, selected)}


def write_outputs(selected, *, changed_scope=False):
    value = dict(packages=selected, selected_count=len(selected), full=len(selected) == len(r.FAMILY_PACKAGE_IDS))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write("packages=" + json.dumps(selected, separators=(",", ":")) + "\n")
            stream.write(f"selected_count={len(selected)}\n")
            stream.write("full=" + str(value["full"]).lower() + "\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            empty = "No executable changes against the event base." if changed_scope else "No packages were requested."
            stream.write("## Validation scope\n\n" + ("\n".join("- `" + item + "`" for item in selected)
                         if selected else empty) + "\n")
    print(json.dumps(value))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT)
    choice = parser.add_mutually_exclusive_group(required=True)
    choice.add_argument("--changed-since", metavar="BASE_SHA")
    choice.add_argument("--all", action="store_true")
    choice.add_argument("--packages", help="JSON package IDs from the release planner")
    choice.add_argument("--manual", help="Comma-separated package IDs, or all")
    parser.add_argument("--verify-versions", action="store_true", help="Evaluate selected versions after SDK setup")
    args = parser.parse_args()
    if args.changed_since is not None:
        selected = affected_by_changes(args.source, args.changed_since)
    elif args.all or args.manual == "all":
        selected = [row["packageId"] for row in r.inventory(args.source)["packages"]]
    elif args.manual is not None:
        selected = [value.strip() for value in args.manual.split(",")]
    else:
        selected = json.loads(args.packages)
    selected = package_ids(args.source, selected)
    if args.verify_versions:
        import agent_selection
        agent_selection.evaluated_packages(args.source, selected)
    write_outputs(selected, changed_scope=args.changed_since is not None)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("CI version selection rejected: " + str(error))
