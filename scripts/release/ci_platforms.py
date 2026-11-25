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


def matrices(variables=None):
    variables = variables or {}
    native, behavior = [], []
    for row in platforms():
        key = "SUNDER_AGENT_RUNNER_" + row["rid"].upper().replace("-", "_")
        labels = json.loads(variables[key]) if variables.get(key) else row["runner"]
        if not isinstance(labels, list) or not labels or any(not isinstance(label, str) or not re.fullmatch(r"[A-Za-z0-9._-]+", label) for label in labels):
            raise ValueError("Invalid native runner labels: " + key)
        native.append(dict(rid=row["rid"], runner=labels))
        if row.get("behavior"):
            behavior.append(native[-1])
    if {row["rid"] for row in behavior} != set(RIDS):
        raise ValueError("Agent behavior requires all native platforms")
    return dict(native=dict(include=native), behavior=dict(include=behavior))


def validation_targets(selected, full, variables=None):
    platforms = matrices(variables)
    scopes = [(key, "component", key) for key in selected]
    if full:
        scopes.append(("family integration", "family", ""))
    return {"include": [dict(platform, label=label, lane=lane, package=package)
                        for label, lane, package in scopes for platform in platforms["behavior"]["include"]]}
