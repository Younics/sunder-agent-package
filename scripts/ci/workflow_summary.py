#!/usr/bin/env python3
"""Audit selected job outcomes, including unexpected skips and cancellation."""
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
from family_policy import FAMILY_PACKAGE_IDS

def outcomes(jobs, expected):
    if set(jobs) != set(expected):
        raise ValueError("Workflow job inventory differs from its outcome audit")
    for name, required in expected.items():
        outcome = "success" if required else "skipped"
        if jobs[name]["result"] != outcome:
            raise ValueError(f"{name}: expected {outcome}, observed {jobs[name]['result']}")


def verify(jobs, mode):
    incomplete = [f"{name}: {job['result']}" for name, job in jobs.items() if job["result"] not in ("success", "skipped")]
    if incomplete:
        raise ValueError("; ".join(incomplete))
    if jobs["prepare"]["result"] != "success":
        raise ValueError("Package selection did not succeed")
    outputs = jobs["prepare"]["outputs"]
    count = int(outputs["selected_count"])
    if not 0 <= count <= len(FAMILY_PACKAGE_IDS):
        raise ValueError("Invalid package selection count")
    active = count > 0
    if mode == "validate":
        pending = json.loads(jobs["dependencies"]["outputs"]["pending"]) if active else []
        expected = {"checks": active, "dependencies": active, "bootstrap": active and bool(pending),
                    "admit-owner": active and bool(pending), "behavior": active}
    elif mode == "release":
        build = active and outputs["resume"] != "true"
        production = json.loads(jobs["validation"]["outputs"]["production_packages"]) if build else []
        expected = {"validation": build, "native": build and bool(production),
             "archive": build, "smoke": build, "fan-in": build, "stage": active,
             "publish": active, "installed-native": active, "release": active}
    else:
        raise ValueError("Unknown workflow summary")
    outcomes(jobs, dict(prepare=True, **expected))
    return f"{count} selected packages: all required {mode} jobs succeeded." if active else "No packages selected."


if __name__ == "__main__":
    try:
        message = verify(json.loads(os.environ["JOB_RESULTS"]), sys.argv[1])
        print(message)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
                stream.write(message + "\n")
    except (ValueError, KeyError) as error:
        raise SystemExit("Workflow result rejected: " + str(error))
