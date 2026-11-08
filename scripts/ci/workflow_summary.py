#!/usr/bin/env python3
"""Audit selected job outcomes, including unexpected skips and cancellation."""
import json
import os
import sys


def verify(jobs, mode):
    if jobs["prepare"]["result"] != "success":
        raise ValueError("Version selection did not succeed")
    outputs = jobs["prepare"]["outputs"]
    count = int(outputs["selected_count"])
    if not 0 <= count <= 15:
        raise ValueError("Invalid package selection count")
    active = count > 0
    if mode == "validate":
        expected = {"preflight": active, "dependencies": active, "source": active}
    elif mode == "release":
        build = active and outputs["resume"] != "true"
        expected = {"validation": build, "native": build, "fan-in": build, "publish": active, "release": active}
    else:
        raise ValueError("Unknown workflow summary")
    for name, required in expected.items():
        outcome = "success" if required else "skipped"
        if jobs[name]["result"] != outcome:
            raise ValueError(f"{name}: expected {outcome}, observed {jobs[name]['result']}")
    return f"{count} selected packages: all required {mode} jobs succeeded." if active else "No newer versions: expensive jobs skipped."


if __name__ == "__main__":
    try:
        message = verify(json.loads(os.environ["JOB_RESULTS"]), sys.argv[1])
        print(message)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
                stream.write(message + "\n")
    except (ValueError, KeyError) as error:
        raise SystemExit("Workflow result rejected: " + str(error))
