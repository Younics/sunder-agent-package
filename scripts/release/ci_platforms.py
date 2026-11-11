"""One native platform policy for scheduling, qualification and release fan-in."""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]


def platforms(root=ROOT):
    value = json.loads((Path(root) / "eng/ci/platforms.json").read_text())
    rows = value["platforms"]
    if value["schemaVersion"] != 1 or not rows or len({row["rid"] for row in rows}) != len(rows):
        raise ValueError("Invalid CI platform policy")
    if any(not re.fullmatch(r"(?:win|linux|osx)-(?:x64|arm64)", row["rid"]) for row in rows):
        raise ValueError("Unsupported native platform")
    return rows


RIDS = tuple(row["rid"] for row in platforms())


def matrices(package_tests=False, variables=None, *, windows_filesystem_native=False):
    variables = variables or {}
    native, behavior, preflight = [], [], []
    for row in platforms():
        key = "SUNDER_AGENT_RUNNER_" + row["rid"].upper().replace("-", "_")
        labels = json.loads(variables[key]) if variables.get(key) else row["runner"]
        if not isinstance(labels, list) or not labels or any(not isinstance(label, str) or not re.fullmatch(r"[A-Za-z0-9._-]+", label) for label in labels):
            raise ValueError("Invalid native runner labels: " + key)
        native.append(dict(rid=row["rid"], runner=labels))
        extra_windows = windows_filesystem_native and row["rid"] == "win-arm64"
        if row.get("behavior") or extra_windows:
            platform = {"win": "windows", "linux": "linux", "osx": "macos"}[row["rid"].split("-")[0]]
            runner = labels if extra_windows else row["runner"]
            preflight.append(dict(rid=row["rid"], runner=runner, platform=platform))
            for mode in ("source", "package") if package_tests else ("source",):
                behavior.append(dict(rid=row["rid"], runner=runner, mode=mode,
                    platform=platform))
    return dict(native=dict(include=native), behavior=dict(include=behavior), preflight=dict(include=preflight))
