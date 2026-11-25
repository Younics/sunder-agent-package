"""Check required current-run cases in the native job that executed them."""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]


def verify(results, inventory, mode, rid, root=ROOT):
    import sys
    sys.path.insert(0, str(ROOT / "scripts/release"))
    import agent_cutover_200 as cutover

    owners = {Path(row["path"]).stem.casefold(): row["owner"] for row in inventory["projects"]}
    errors = []
    policy = json.loads((root / "scripts/test/required-platform-cases.json").read_bytes())
    if set(policy) != {"schemaVersion", "requirements"} or policy["schemaVersion"] != 1 or not policy["requirements"]:
        raise ValueError("Invalid required-platform test policy")
    known_rids = {row["rid"] for row in json.loads((root / "eng/ci/platforms.json").read_bytes())["platforms"]}
    known_owners = {row["packageId"] for row in json.loads((root / "packages.json").read_bytes())["packages"]}
    if rid not in known_rids:
        raise ValueError(f"Unknown required test platform {rid}")
    omission = inventory.get(cutover.DISPOSITION)
    if omission is not None and (
            not isinstance(omission, dict) or rid not in cutover.RIDS or mode != "source"
            or owners.get(cutover.SUITE.casefold()) != cutover.OWNER
            or not isinstance(omission.get("planSha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", omission.get("planSha256", ""))
            or omission != dict(className=cutover.CLASS, rid=rid, planSha256=omission["planSha256"],
                                caseCount=cutover.COUNT, status="not-selected")):
        raise ValueError("Invalid 2.0.0 Windows case disposition")
    omission_checked = False
    seen = set()
    for row in policy["requirements"]:
        fields = {"owner", "mode", "rids", "className", "expectedCount"}
        if (not fields <= row.keys() or set(row) - fields - {"methodName", "cases"}
                or row["owner"] not in known_owners or row["mode"] not in ("source", "package")
                or not row["rids"] or len(set(row["rids"])) != len(row["rids"]) or set(row["rids"]) - known_rids
                or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", row["className"])
                or "methodName" in row and not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", row["methodName"])
                or type(row["expectedCount"]) is not int or row["expectedCount"] < 1
                or "cases" in row and (len(set(row["cases"])) != row["expectedCount"]
                                       or len(row["cases"]) != row["expectedCount"])):
            raise ValueError("Invalid required-platform test group")
        key = (row["owner"], row["mode"], row["className"], row.get("methodName"))
        if key in seen:
            raise ValueError("Duplicate required-platform test group")
        seen.add(key)
        if row["owner"] not in owners.values() or mode != row["mode"] or rid not in row["rids"]:
            continue
        prefix = row["className"] + "."
        qualified = row.get("methodName")
        observations = [case for case in results if owners.get(case["project"]) == row["owner"]
                        and (case["fullyQualifiedName"] == prefix + qualified if qualified
                             else case["fullyQualifiedName"].rpartition(".")[0] == row["className"])]
        if omission is not None and key == (cutover.OWNER, "source", cutover.CLASS, None):
            omission_checked = True
            if observations or row["expectedCount"] != cutover.COUNT or set(row["rids"]) != cutover.RIDS:
                errors.append(f"{rid}: 2.0.0 Windows case disposition conflicts with current execution or policy")
            continue
        names = {case["test"] for case in observations}
        if not (len(observations) == len(names) == row["expectedCount"]
                and all(case["outcome"] == "passed" for case in observations)
                and ("cases" not in row or names == {prefix + name for name in row["cases"]})):
            errors.append(f"{rid}: required {prefix}{qualified or '*'} did not execute once and pass "
                          f"(expected {row['expectedCount']}, observed {len(observations)})")
    if rid.startswith("linux-") and mode == "source":
        linux = json.loads((root / "scripts/test/ci-scenarios.json").read_bytes())["requiredLinuxOwners"]
        for method, owner in linux.items():
            if owner in owners.values():
                observed = [case for case in results if owners.get(case["project"]) == owner
                            and case["fullyQualifiedName"].rpartition(".")[2] == method]
                if not observed or any(case["outcome"] != "passed" for case in observed):
                    errors.append(f"{rid}: required {method} missing or non-passing")
    if omission is not None and not omission_checked:
        errors.append(f"{rid}: 2.0.0 Windows case group missing from policy")
    return errors
