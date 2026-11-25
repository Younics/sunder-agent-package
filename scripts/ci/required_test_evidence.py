#!/usr/bin/env python3
"""Verify test-owned required execution inventories without feature-specific workflow code."""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import ci_platforms
import version_selection

ROOT = Path(__file__).resolve().parents[2]
POLICY = ROOT / "scripts/test/required-platform-cases.json"
require = version_selection.r.require


def requirements(source, policy, selected):
    known = set(version_selection.package_ids(source, [row["packageId"] for row in version_selection.r.inventory(source)["packages"]]))
    require(set(policy) == {"schemaVersion", "requirements"} and policy["schemaVersion"] == 1,
            "Unknown required-test policy")
    require(type(policy["requirements"]) is list and policy["requirements"], "Empty required-test policy")
    seen = set()
    for row in policy["requirements"]:
        require(set(row) <= {"owner", "mode", "rids", "className", "methodName", "expectedCount", "cases"}
                and {"owner", "mode", "rids", "className", "expectedCount"} <= set(row), "Invalid required-test fields")
        require(row["owner"] in known and row["mode"] in ("source", "package"), "Unknown required-test owner/mode")
        require(type(row["rids"]) is list and row["rids"] and len(row["rids"]) == len(set(row["rids"]))
                and set(row["rids"]) <= set(ci_platforms.RIDS), "Invalid required-test RIDs")
        require(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", row["className"]) is not None, "Invalid required-test class")
        require("methodName" not in row or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", row["methodName"]) is not None,
                "Invalid required-test method")
        require(type(row["expectedCount"]) is int and row["expectedCount"] > 0, "Invalid required-test count")
        if "cases" in row:
            require(type(row["cases"]) is list and all(type(case) is str and case for case in row["cases"])
                    and len(row["cases"]) == len(set(row["cases"])) == row["expectedCount"],
                    "Required-test exact cases/count differ")
        key = (row["owner"], row["mode"], row["className"], row.get("methodName"))
        require(key not in seen, "Duplicate required-test group")
        seen.add(key)
    return [row for row in policy["requirements"] if row["owner"] in selected]


def plan(rows):
    patterns = sorted({f"agent-component-{row['owner']}-{row['mode']}-{rid}-*" for row in rows for rid in row["rids"]})
    return dict(has_requirements=bool(rows), pattern="@(" + "|".join(patterns) + ")" if patterns else "")


def observations(document):
    definitions = {}
    for definition in document.findall(".//{*}UnitTest"):
        method = definition.find("./{*}TestMethod")
        identifier = definition.get("id")
        require(identifier and identifier not in definitions and method is not None, "Invalid required-test definition")
        definitions[identifier] = (method.get("className", "").split(",", 1)[0], method.get("name", ""))
    observed = []
    for result in document.findall(".//{*}UnitTestResult"):
        require(result.get("testId") in definitions, "Required-test result has no definition")
        observed.append((*definitions[result.get("testId")], result.get("testName"), result.get("outcome")))
    return observed


def read_scope(directory, scope):
    pattern = f"agent-component-{scope[0]}-{scope[1]}-{scope[2]}-*"
    directories = list(Path(directory).glob(pattern))
    require(len(directories) == 1, f"Missing or duplicate required-test scope: {scope}")
    return [record for path in sorted(directories[0].rglob("*.trx")) for record in observations(ET.parse(path))]


def case_results(row, observed):
    return [(name, outcome) for cls, method, name, outcome in observed
            if cls == row["className"] and ("methodName" not in row or method == row["methodName"])]


def verify_cases(row, rid, observed, *, historical=False):
    cases = case_results(row, observed)
    label = f"{row['owner']} / {rid} / {row['className']}" + ("." + row["methodName"] if "methodName" in row else "")
    require(len(cases) == len({name for name, _ in cases}) == row["expectedCount"],
            f"Missing, duplicate or unexpected required-test cases: {label}")
    if "cases" in row:
        require({name for name, _ in cases} == {row["className"] + "." + case for case in row["cases"]},
                f"Required-test exact identities differ: {label}")
    require(all(outcome == "Passed" for _, outcome in cases), f"Failed or skipped required-test cases: {label}")
    print(f"{label}: {len(cases)} passed; zero skipped" + (" (original historical)" if historical else ""))
    return dict(owner=row["owner"], mode=row["mode"], rid=rid, className=row["className"],
                methodName=row.get("methodName"), passed=len(cases))


def verify(directory, rows):
    reports, verified = {}, []
    for row in rows:
        for rid in row["rids"]:
            scope = (row["owner"], row["mode"], rid)
            if scope not in reports:
                reports[scope] = read_scope(directory, scope)
            verified.append(verify_cases(row, rid, reports[scope]))
    return verified


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, help="JSON package IDs from the ordinary selection")
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--plan", action="store_true")
    operation.add_argument("--results", type=Path)
    parser.add_argument("--history", type=Path)
    parser.add_argument("--history-sha256", default="")
    parser.add_argument("--historical-receipt", type=Path)
    args = parser.parse_args()
    selected = version_selection.package_ids(ROOT, json.loads(args.packages))
    rows = requirements(ROOT, json.loads(POLICY.read_text()), selected)
    if args.plan:
        value = plan(rows)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                for key, item in value.items():
                    stream.write(f"{key}={str(item).lower() if type(item) is bool else item}\n")
        print(json.dumps(value))
    else:
        import windows_case_history as history
        if args.history_sha256:
            require(args.history and args.historical_receipt, "Windows history needs separate originals and receipt")
            reviewed = history.checked_history(ROOT, args.history, args.history_sha256)
            rows, historical = history.audit(reviewed, rows, args.results, args.history)
        else:
            require(not args.history or not args.history.exists(), "Unreviewed historical test evidence")
            historical = None
        verified = verify(args.results, rows)
        result = dict(requiredGroups=len(verified), passed=sum(row["passed"] for row in verified))
        if historical:
            with args.historical_receipt.open("xb") as output:
                output.write(history.inputs.canonical(historical) + b"\n")
            result["historicalGroups"] = len(historical["groups"])
            result["historicalPassed"] = sum(row["passed"] for row in historical["groups"])
        print(json.dumps(result))


if __name__ == "__main__":
    main()
