#!/usr/bin/env python3
"""Cheap CI version selection and explicit test ownership; no SDK evaluation."""
import argparse
import json
import os
from pathlib import Path
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


def against_base(source, base):
    r.digest(base, 40)
    # Fetch the event's immutable base in shallow Actions checkouts. A missing
    # base is an error, never a reason to silently select all or skip tests.
    if subprocess.run(["git", "cat-file", "-e", base + "^{commit}"], cwd=source,
                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode:
        subprocess.run(["git", "fetch", "--no-tags", "--depth=1", "origin", base], cwd=source, check=True)
    paths = set(subprocess.check_output(["git", "ls-tree", "-r", "--name-only", base], cwd=source, text=True).splitlines())
    selected = []
    for row in r.inventory(source)["packages"]:
        r.source_file(source, row["projectPath"])
        if row["projectPath"] not in paths:
            selected.append(row["packageId"])
            continue
        tree = ET.fromstring(subprocess.check_output(["git", "show", base + ":" + row["projectPath"]], cwd=source))
        versions = tree.findall("./PropertyGroup/Version")
        r.require(len(versions) == 1 and not versions[0].attrib
                  and all("Condition" not in group.attrib for group in tree.findall("./PropertyGroup") if group.find("Version") is not None),
                  "Base project must author one unconditional literal Version")
        previous = r.version(versions[0].text)
        if r.version(row["version"]) > previous:
            selected.append(row["packageId"])
    return sorted(selected)


def projects(inventory, selected):
    """Family boundary checks accompany every nonempty component selection."""
    if selected is None or "younics.ai.agent" in selected:
        return inventory["projects"]
    return [row for row in inventory["projects"] if selected and row["owner"] in {*selected, "agent-family"}]


def scope_inventory(inventory, selected, mode, platform):
    rows = projects(inventory, selected)
    if selected and mode == "source" and platform == "linux":
        # This independent Linux filesystem security boundary stays mandatory
        # even when only another family member advances. Run its required cases,
        # not its unrelated component suite.
        rows += [dict(row, requiredOnly=True, sourceOnlyTests=[]) for row in inventory["projects"]
                 if row not in rows and row["owner"] == "younics.ai.agent.execution.local"]
    return {**inventory, "projects": rows}


def write_outputs(selected):
    value = dict(packages=selected, selected_count=len(selected))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write("packages=" + json.dumps(selected, separators=(",", ":")) + "\n")
            stream.write(f"selected_count={len(selected)}\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write("## Version selection\n\n" + ("\n".join("- `" + item + "`" for item in selected)
                         if selected else "No newer authored versions. Component validation and packaging are skipped.") + "\n")
    print(json.dumps(value))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT)
    choice = parser.add_mutually_exclusive_group(required=True)
    choice.add_argument("--base")
    choice.add_argument("--all", action="store_true")
    choice.add_argument("--packages", help="JSON package IDs from the release planner")
    parser.add_argument("--verify-versions", action="store_true", help="Evaluate selected versions after SDK setup")
    args = parser.parse_args()
    if args.base:
        selected = against_base(args.source, args.base)
    elif args.all:
        selected = [row["packageId"] for row in r.inventory(args.source)["packages"]]
    else:
        selected = json.loads(args.packages)
    selected = package_ids(args.source, selected)
    if args.verify_versions:
        import agent_selection
        agent_selection.evaluated_packages(args.source, selected)
    write_outputs(selected)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("CI version selection rejected: " + str(error))
