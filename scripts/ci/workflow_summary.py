#!/usr/bin/env python3
"""Audit selected job outcomes, including unexpected skips and cancellation."""
import json
import os
import sys


def outcomes(jobs, expected):
    if set(jobs) != set(expected):
        raise ValueError("Workflow job inventory differs from its outcome audit")
    for name, required in expected.items():
        outcome = "success" if required else "skipped"
        if jobs[name]["result"] != outcome:
            raise ValueError(f"{name}: expected {outcome}, observed {jobs[name]['result']}")


def verify(jobs, mode, *, builder_diagnostics=False, operation=None):
    if mode == "component":
        if operation not in ("test", "native", "publish"):
            raise ValueError("Unknown component operation")
        outcomes(jobs, {"test": operation == "test", "native": operation == "native",
                        "compose": operation == "native", "publish": operation == "publish"})
        return "All required component jobs succeeded."
    if jobs["prepare"]["result"] != "success":
        raise ValueError("Version selection did not succeed")
    outputs = jobs["prepare"]["outputs"]
    count = int(outputs["selected_count"])
    if not 0 <= count <= 15:
        raise ValueError("Invalid package selection count")
    active = count > 0
    if mode == "validate":
        pending = json.loads(jobs["dependency-discovery"]["outputs"]["pending"]) if active else []
        if type(pending) is not list or len(set(pending)) != len(pending):
            raise ValueError("Invalid unpublished dependency selection")
        expected = {"preflight": active, "dependency-discovery": active, "tools": active,
            "bootstrap": active and bool(pending), "dependencies": active,
            "components": active and not builder_diagnostics, "verifier": active and not builder_diagnostics,
            "source": active and (outputs["full"] == "true" or builder_diagnostics)}
    elif mode == "release":
        build = active and outputs["resume"] != "true"
        production = int(jobs["production"]["outputs"]["selected_count"]) if build else 0
        if not 0 <= production <= count:
            raise ValueError("Invalid native component selection")
        consumers = json.loads(outputs["consumers"])
        owner = outputs["owner_selected"] == "true"
        if type(consumers) is not list or len(set(consumers)) != len(consumers) or len(consumers) + owner != count:
            raise ValueError("Publication leaves differ from the selected components")
        expected = {"validation": build, "production": build, "native": build and production > 0,
            "smoke": build, "fan-in": build, "stage": active,
            "publish-owner": active and owner, "publish-consumers": active and bool(consumers), "release": active}
    else:
        raise ValueError("Unknown workflow summary")
    outcomes(jobs, dict(prepare=True, **expected))
    return f"{count} selected packages: all required {mode} jobs succeeded." if active else "No newer versions: expensive jobs skipped."


if __name__ == "__main__":
    try:
        message = verify(json.loads(os.environ["JOB_RESULTS"]), sys.argv[1],
            builder_diagnostics=os.environ.get("BUILDER_DIAGNOSTICS") == "true", operation=os.environ.get("OPERATION"))
        print(message)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
                stream.write(message + "\n")
    except (ValueError, KeyError) as error:
        raise SystemExit("Workflow result rejected: " + str(error))
