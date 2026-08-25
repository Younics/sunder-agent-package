#!/usr/bin/env python3

import argparse
import functools
import getpass
import hashlib
import json
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ElementTree
from collections import defaultdict
from datetime import datetime
from decimal import Decimal
from pathlib import Path


SKIP_ATTRIBUTE_NAMES = {
    "DockerIntegrationFact",
    "FileSystemFact",
    "FileSystemTheory",
    "NativeReleaseFact",
    "PackageArtifactsFact",
    "PlatformFact",
    "PlatformTheory",
    "UnixFact",
    "UnixTheory",
}


def duration_milliseconds(value: str | None) -> float:
    if not value:
        return 0.0

    hours_value, minutes_value, seconds_value = value.split(":", 2)
    if "." in hours_value:
        days_value, hours_value = hours_value.split(".", 1)
    else:
        days_value = "0"
    seconds = (
        Decimal(days_value) * 86400
        + Decimal(hours_value) * 3600
        + Decimal(minutes_value) * 60
        + Decimal(seconds_value)
    )
    return float((seconds * 1000).quantize(Decimal("0.001")))


def normalized_outcome(value: str) -> str:
    outcomes = {
        "passed": "passed",
        "failed": "failed",
        "notexecuted": "skipped",
        "skipped": "skipped",
    }
    return outcomes.get(value.casefold(), value.casefold() or "unknown")


def project_name(storage: str | None, trx_path: Path) -> str:
    if storage:
        name = Path(storage.replace("\\", "/")).name
        return name[:-4] if name.casefold().endswith(".dll") else name
    return trx_path.stem


def timestamp(value: str | None) -> datetime | None:
    return datetime.fromisoformat(value) if value else None


def elapsed_milliseconds(start: datetime | None, finish: datetime | None) -> float | None:
    if start is None or finish is None:
        return None
    return round(max(0.0, (finish - start).total_seconds() * 1000), 3)


def read_trx(root: Path, trx_path: Path) -> tuple[list[dict[str, object]], dict[str, object]]:
    document = ElementTree.parse(trx_path)
    definitions: dict[str, tuple[str, str]] = {}
    for unit_test in document.findall(".//{*}UnitTest"):
        test_id = unit_test.get("id")
        method = unit_test.find(".//{*}TestMethod")
        if not test_id or method is None:
            continue
        class_name = method.get("className", "")
        method_name = method.get("name", unit_test.get("name", ""))
        qualified_name = ".".join(part for part in (class_name, method_name) if part)
        definitions[test_id] = (
            qualified_name or unit_test.get("name", "<unknown>"),
            project_name(unit_test.get("storage"), trx_path),
        )

    relative_trx = trx_path.relative_to(root).as_posix()
    results: list[dict[str, object]] = []
    test_starts: list[datetime] = []
    test_finishes: list[datetime] = []
    for result in document.findall(".//{*}UnitTestResult"):
        test_id = result.get("testId", "")
        fallback_name = result.get("testName", "<unknown>")
        qualified_name, project = definitions.get(
            test_id,
            (fallback_name, project_name(None, trx_path)),
        )
        outcome = normalized_outcome(result.get("outcome", "unknown"))
        reason_node = result.find("./{*}Output/{*}ErrorInfo/{*}Message")
        reason = reason_node.text if reason_node is not None else None
        results.append(
            {
                "project": project,
                "test": fallback_name,
                "fullyQualifiedName": qualified_name,
                "outcome": outcome,
                "skipReason": reason if outcome == "skipped" else None,
                "durationMilliseconds": duration_milliseconds(result.get("duration")),
                "trx": relative_trx,
            }
        )
        if (test_start := timestamp(result.get("startTime"))) is not None:
            test_starts.append(test_start)
        if (test_finish := timestamp(result.get("endTime"))) is not None:
            test_finishes.append(test_finish)

    times = document.find("./{*}Times")
    run_start = timestamp(times.get("start")) if times is not None else None
    run_finish = timestamp(times.get("finish")) if times is not None else None
    first_test_start = min(test_starts) if test_starts else None
    last_test_finish = max(test_finishes) if test_finishes else None
    projects = sorted({str(result["project"]) for result in results})
    run = {
        "trx": relative_trx,
        "name": document.getroot().get("name", trx_path.stem),
        "project": projects[0] if len(projects) == 1 else None,
        "resultCount": len(results),
        "elapsedMilliseconds": elapsed_milliseconds(run_start, run_finish),
        "setupMilliseconds": elapsed_milliseconds(run_start, first_test_start),
        "testWindowMilliseconds": elapsed_milliseconds(first_test_start, last_test_finish),
        "teardownMilliseconds": elapsed_milliseconds(last_test_finish, run_finish),
    }
    return results, run


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate and optionally verify exact Sunder Agent TRX evidence."
    )
    parser.add_argument("trx_directory", type=Path)
    parser.add_argument("--inventory", type=Path)
    parser.add_argument("--mode", choices=("source", "package"))
    parser.add_argument("--platform", choices=("linux", "macos", "windows"))
    parser.add_argument("--lane")
    parser.add_argument("--exclude-category")
    parser.add_argument("--expectations", type=Path)
    parser.add_argument("--expected-skips", type=int)
    parser.add_argument("--write-expectations", type=Path)
    return parser.parse_args()


def identity_evidence(results: list[dict[str, object]]) -> list[dict[str, object]]:
    identities: dict[str, set[tuple[str, str]]] = defaultdict(set)
    for result in results:
        identities[str(result["project"])].add(
            (str(result["fullyQualifiedName"]), str(result["test"]))
        )

    evidence = []
    for project, project_identities in sorted(identities.items()):
        serialized = "".join(
            f"{fully_qualified_name}\0{test}\n"
            for fully_qualified_name, test in sorted(project_identities)
        ).encode("utf-8")
        evidence.append(
            {
                "project": project,
                "identityCount": len(project_identities),
                "identitySha256": hashlib.sha256(serialized).hexdigest(),
            }
        )
    return evidence


def project_suite(evidence: list[dict[str, object]]) -> list[str]:
    return [str(project["project"]) for project in evidence]


def aggregate_identity_evidence(results: list[dict[str, object]]) -> dict[str, object]:
    identities = sorted(
        {
            (
                str(result["project"]),
                str(result["fullyQualifiedName"]),
                str(result["test"]),
            )
            for result in results
        }
    )
    serialized = "".join(
        f"{project}\0{fully_qualified_name}\0{test}\n"
        for project, fully_qualified_name, test in identities
    ).encode("utf-8")
    return {
        "identityCount": len(identities),
        "identitySha256": hashlib.sha256(serialized).hexdigest(),
    }


def evidence_hash(evidence: list[dict[str, object]]) -> dict[str, object]:
    serialized = "".join(
        json.dumps(item, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"
        for item in evidence
    ).encode("utf-8")
    return {
        "identityCount": len(evidence),
        "identitySha256": hashlib.sha256(serialized).hexdigest(),
    }


def project_owners(inventory: dict[str, object]) -> dict[str, str]:
    return {
        Path(str(project["path"])).stem.casefold(): str(project["owner"])
        for project in inventory["projects"]
    }


def decode_csharp_string(value: str) -> str:
    return json.loads(value)


def method_attribute_blocks(path: Path) -> list[tuple[str, str, list[str]]]:
    source = path.read_text(encoding="utf-8")
    namespace_match = re.search(r"\bnamespace\s+([A-Za-z_][\w.]*)\s*[;{]", source)
    namespace = namespace_match.group(1) if namespace_match else ""
    lines = source.splitlines()
    attributes: list[str] = []
    results: list[tuple[str, str, list[str]]] = []
    current_class = ""
    index = 0
    while index < len(lines):
        line = lines[index]
        stripped = line.strip()
        class_match = re.search(
            r"\b(?:public|internal|private|protected)\s+(?:(?:sealed|static|partial|abstract)\s+)*class\s+([A-Za-z_]\w*)",
            stripped,
        )
        if class_match is not None:
            current_class = class_match.group(1)
        if stripped.startswith("["):
            block = [stripped]
            balance = stripped.count("[") - stripped.count("]")
            index += 1
            while balance > 0 and index < len(lines):
                continuation = lines[index].strip()
                block.append(continuation)
                balance += continuation.count("[") - continuation.count("]")
                index += 1
            attributes.append(" ".join(block))
            continue
        if not attributes:
            index += 1
            continue
        if not stripped or stripped.startswith("//"):
            index += 1
            continue
        declaration = stripped
        cursor = index + 1
        while "(" not in declaration and cursor < len(lines):
            declaration += " " + lines[cursor].strip()
            cursor += 1
        method_match = re.search(
            r"\b(?:public|internal)\s+(?:(?:static|async|virtual|sealed|override)\s+)*"
            r"[A-Za-z_][\w.<>,?\[\]\s]*\s+([A-Za-z_]\w*)\s*\(",
            declaration,
        )
        if method_match is not None and current_class:
            qualified_class = f"{namespace}.{current_class}" if namespace else current_class
            results.append((qualified_class, method_match.group(1), attributes.copy()))
        attributes.clear()
        index += 1
    return results


def attribute_name(attribute: str) -> str:
    match = re.match(r"\[\s*(?:[A-Za-z_]\w*\.)*([A-Za-z_]\w*)", attribute)
    return match.group(1).removesuffix("Attribute") if match else ""


def attribute_strings(attribute: str) -> list[str]:
    return [
        decode_csharp_string(match.group(0))
        for match in re.finditer(r'"(?:\\.|[^"\\])*"', attribute)
    ]


def trait_value(attributes: list[str], name: str) -> str | None:
    for attribute in attributes:
        if attribute_name(attribute) != "Trait":
            continue
        values = attribute_strings(attribute)
        if len(values) == 2 and values[0] == name:
            return values[1]
    return None


def skip_policy(
    expectations_path: Path,
    inventory: dict[str, object],
    mode: str,
    lane: str | None,
    exclude_category: str | None,
) -> list[dict[str, object]]:
    repository_root = expectations_path.resolve().parent.parent
    policies: list[dict[str, object]] = []
    for project in inventory["projects"]:
        if mode not in project["modes"]:
            continue
        if lane is not None and lane not in project.get("lanes", []):
            continue
        project_path = repository_root / str(project["path"])
        project_name_value = project_path.stem.casefold()
        for source_path in sorted(project_path.parent.rglob("*.cs")):
            if any(part in {"bin", "obj"} for part in source_path.parts):
                continue
            for class_name, method_name, attributes in method_attribute_blocks(source_path):
                if lane is not None and trait_value(attributes, "Lane") != lane:
                    continue
                if exclude_category is not None and trait_value(attributes, "Category") == exclude_category:
                    continue
                for attribute in attributes:
                    name = attribute_name(attribute)
                    if name not in SKIP_ATTRIBUTE_NAMES:
                        continue
                    strings = attribute_strings(attribute)
                    reason = strings[-1] if strings else ""
                    platform_match = re.search(r"TestPlatform\.([A-Za-z_]\w*)", attribute)
                    capability_match = re.search(r"TestFileSystemCapability\.([A-Za-z_]\w*)", attribute)
                    if name == "DockerIntegrationFact":
                        reason = "Set SUNDER_DOCKER_INTEGRATION=1 and provide a reachable Docker daemon."
                    elif name == "PackageArtifactsFact":
                        reason = "Set SUNDER_AGENT_PACKAGE_ARCHIVE_DIR to the complete package archive directory."
                    elif name == "NativeReleaseFact":
                        reason = "Set SUNDER_AGENT_NATIVE_ARCHIVE_DIR to run the native release smoke test."
                    policies.append(
                        {
                            "project": project_name_value,
                            "owner": str(project["owner"]),
                            "fullyQualifiedName": f"{class_name}.{method_name}",
                            "attribute": name,
                            "platform": platform_match.group(1) if platform_match else None,
                            "capability": capability_match.group(1) if capability_match else None,
                            "reason": reason,
                        }
                    )
    policies.sort(key=lambda item: tuple(str(item[key]) for key in (
        "project", "fullyQualifiedName", "attribute", "platform", "capability", "reason"
    )))
    return policies


def current_platform() -> str:
    if sys.platform == "win32":
        return "windows"
    if sys.platform == "darwin":
        return "macos"
    return "linux"


@functools.cache
def temp_volume_is_case_insensitive() -> bool:
    with tempfile.TemporaryDirectory(prefix="sunder-case-probe-") as root:
        Path(root, "case-probe").mkdir()
        return Path(root, "CASE-PROBE").exists()


@functools.cache
def filesystem_capability_available(capability: str) -> bool:
    if capability == "UnixDirectorySymbolicLinks" and sys.platform == "win32":
        return True
    try:
        with tempfile.TemporaryDirectory(prefix="sunder-capability-probe-") as root_value:
            root = Path(root_value)
            if capability == "FileSymbolicLinks":
                target = root / "target.txt"
                target.write_text("target", encoding="utf-8")
                (root / "link.txt").symlink_to(target)
            elif capability in {"DirectorySymbolicLinks", "UnixDirectorySymbolicLinks"}:
                target = root / "target"
                target.mkdir()
                (root / "link").symlink_to(target, target_is_directory=True)
            elif capability == "HardLinks":
                target = root / "target.txt"
                target.write_text("target", encoding="utf-8")
                os.link(target, root / "link.txt")
            else:
                return False
        return True
    except (NotImplementedError, OSError):
        return False


def expected_skip(
    policy: dict[str, object], platform: str | None = None
) -> dict[str, object] | None:
    platform = platform or current_platform()
    attribute = str(policy["attribute"])
    target = policy["platform"]
    reason = str(policy["reason"])
    should_skip = False
    if attribute in {"UnixFact", "UnixTheory"}:
        should_skip = platform == "windows"
    elif attribute == "DockerIntegrationFact":
        if platform == "windows":
            should_skip = True
            reason = "The Docker bind-mount integration test requires a Unix host."
        else:
            should_skip = (
                os.environ.get("SUNDER_DOCKER_INTEGRATION") != "1"
                and os.environ.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
            )
    elif attribute == "PackageArtifactsFact":
        should_skip = (
            not os.environ.get("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR", "").strip()
            and os.environ.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
        )
    elif attribute == "NativeReleaseFact":
        should_skip = (
            not os.environ.get("SUNDER_AGENT_NATIVE_ARCHIVE_DIR", "").strip()
            and os.environ.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
        )
    elif attribute in {"PlatformFact", "PlatformTheory"}:
        should_skip = {
            "Windows": platform != "windows",
            "Linux": platform != "linux",
            "MacOS": platform != "macos",
            "Unix": platform == "windows",
            "MacCaseInsensitive": platform != "macos",
            "NonRootUnix": platform == "windows",
            "UnixWithDistinctDevice": platform == "windows",
        }.get(str(target), False)
        if target == "NonRootUnix" and platform != "windows" and getpass.getuser() == "root":
            should_skip = True
            reason = "POSIX permission-boundary behavior cannot be observed while running as root."
        if target == "MacCaseInsensitive" and platform == "macos" and not temp_volume_is_case_insensitive():
            should_skip = True
            reason = "The temporary volume is case-sensitive."
        if target == "UnixWithDistinctDevice" and platform != "windows":
            try:
                distinct_device = os.stat("/").st_dev != os.stat("/dev").st_dev
            except OSError:
                distinct_device = False
            if not distinct_device:
                should_skip = True
                reason = "/dev does not expose a distinct device on this host."
    capability = policy["capability"]
    if (
        not should_skip
        and capability is not None
        and not filesystem_capability_available(str(capability))
        and os.environ.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
    ):
        should_skip = True
        reason = f"{reason} Required filesystem capability '{capability}' is unavailable."
    if not should_skip:
        return None
    return {
        "project": policy["project"],
        "owner": policy["owner"],
        "fullyQualifiedName": policy["fullyQualifiedName"],
        "test": policy["fullyQualifiedName"],
        "reason": reason,
    }


def write_expectations(
    path: Path,
    mode: str,
    platform: str,
    evidence: list[dict[str, object]],
    policy_evidence: list[dict[str, object]],
) -> None:
    document = (
        json.loads(path.read_text(encoding="utf-8"))
        if path.exists()
        else {"schemaVersion": 3, "modes": {}}
    )
    document["schemaVersion"] = 3
    document["modes"][mode] = {
        "projectSuite": project_suite(evidence),
        "canonicalIdentity": {
            "platform": platform,
            "projects": evidence,
        },
        "skipPolicy": evidence_hash(policy_evidence),
    }
    path.write_text(
        json.dumps(document, indent=2, ensure_ascii=True) + "\n", encoding="utf-8"
    )


def main() -> int:
    arguments = parse_arguments()
    platform = arguments.platform or current_platform()
    if arguments.inventory is not None and arguments.mode is None:
        print("--inventory requires --mode.", file=sys.stderr)
        return 2
    if arguments.lane is not None and arguments.mode is None:
        print("--lane requires --mode.", file=sys.stderr)
        return 2
    if arguments.exclude_category is not None and arguments.mode is None:
        print("--exclude-category requires --mode.", file=sys.stderr)
        return 2
    if arguments.expectations is not None and arguments.mode is None:
        print("--expectations requires --mode.", file=sys.stderr)
        return 2
    if arguments.write_expectations is not None and arguments.mode is None:
        print("--write-expectations requires --mode.", file=sys.stderr)
        return 2
    if arguments.write_expectations is not None and arguments.platform is None:
        print("--write-expectations requires an explicit --platform owner.", file=sys.stderr)
        return 2

    root = arguments.trx_directory.resolve()
    trx_paths = sorted(root.rglob("*.trx"))
    if not trx_paths:
        print(f"No TRX files were found under {root}.", file=sys.stderr)
        return 1

    results: list[dict[str, object]] = []
    runs: list[dict[str, object]] = []
    for path in trx_paths:
        trx_results, run = read_trx(root, path)
        results.extend(trx_results)
        runs.append(run)
    results.sort(
        key=lambda result: (
            str(result["project"]),
            str(result["fullyQualifiedName"]),
            str(result["test"]),
            str(result["trx"]),
        )
    )

    repetitions: dict[tuple[str, str], list[dict[str, object]]] = defaultdict(list)
    for result in results:
        repetitions[(str(result["project"]), str(result["test"]))].append(result)

    repeated_tests = []
    flake_candidates = []
    for (project, test), observations in sorted(repetitions.items()):
        if len(observations) < 2:
            continue
        outcomes = sorted({str(observation["outcome"]) for observation in observations})
        repeated = {
            "project": project,
            "test": test,
            "runs": len(observations),
            "outcomes": outcomes,
        }
        repeated_tests.append(repeated)
        if len(outcomes) > 1:
            flake_candidates.append(repeated)

    outcome_counts: dict[str, int] = defaultdict(int)
    for result in results:
        outcome_counts[str(result["outcome"])] += 1

    full_results = [
        result for result in results if str(result["trx"]).startswith("full/")
    ]
    primary_results = full_results or results
    primary_outcome_counts: dict[str, int] = defaultdict(int)
    for result in primary_results:
        primary_outcome_counts[str(result["outcome"])] += 1
    identities = identity_evidence(primary_results)
    evidence_violations: list[str] = []
    inventory: dict[str, object] | None = None
    owners: dict[str, str] = {}
    if arguments.inventory is not None:
        inventory = json.loads(arguments.inventory.read_text(encoding="utf-8"))
        owners = project_owners(inventory)
        expected_projects = sorted(
            Path(project["path"]).stem.casefold()
            for project in inventory["projects"]
            if arguments.mode in project["modes"]
            and (
                arguments.lane is None
                or arguments.lane in project.get("lanes", [])
            )
        )
        observed_projects = sorted(item["project"] for item in identities)
        if observed_projects != expected_projects:
            evidence_violations.append(
                "project identities differ: "
                + f"expected {expected_projects}, observed {observed_projects}"
            )

        source_only_tests = sorted(
            identity
            for project in inventory["projects"]
            for identity in project.get("sourceOnlyTests", [])
        )
        observed_fully_qualified_names = {
            str(result["fullyQualifiedName"]) for result in primary_results
        }
        for identity in source_only_tests:
            observed = identity in observed_fully_qualified_names
            expected = arguments.mode == "source"
            if observed != expected:
                evidence_violations.append(
                    f"source-only test '{identity}' observed={observed}, expected={expected}"
                )

    skipped_tests = []
    for result in primary_results:
        if result["outcome"] != "skipped":
            continue
        project = str(result["project"])
        reason = result["skipReason"]
        skipped_tests.append(
            {
                "project": project,
                "owner": owners.get(project),
                "fullyQualifiedName": result["fullyQualifiedName"],
                "test": result["test"],
                "reason": reason,
            }
        )
        if reason is None or not str(reason).strip():
            evidence_violations.append(
                f"skipped test '{result['fullyQualifiedName']}' has no TRX reason"
            )
        if inventory is not None and project not in owners:
            evidence_violations.append(
                f"skipped test '{result['fullyQualifiedName']}' has no inventory owner"
            )
    skipped_tests.sort(
        key=lambda result: tuple(str(result[key]) for key in (
            "project", "owner", "fullyQualifiedName", "test", "reason"
        ))
    )

    policy_evidence: list[dict[str, object]] = []
    expected_skips: list[dict[str, object]] = []
    policy_path = arguments.expectations or arguments.write_expectations
    if policy_path is not None:
        if inventory is None:
            evidence_violations.append("skip-policy verification requires --inventory")
        else:
            policy_evidence = skip_policy(
                policy_path,
                inventory,
                arguments.mode,
                arguments.lane,
                arguments.exclude_category,
            )
            expected_skips = [
                expected
                for policy in policy_evidence
                if (expected := expected_skip(policy, platform)) is not None
            ]
            expected_skips.sort(
                key=lambda result: tuple(str(result[key]) for key in (
                    "project", "owner", "fullyQualifiedName", "test", "reason"
                ))
            )

    canonical_platform: str | None = None
    canonical_identity_enforced = False
    if arguments.expectations is not None:
        expectations = json.loads(arguments.expectations.read_text(encoding="utf-8"))
        if expectations.get("schemaVersion") != 3:
            evidence_violations.append(
                f"test evidence expectations must use schemaVersion 3: {arguments.expectations}"
            )
        mode_expectations = expectations.get("modes", {}).get(arguments.mode, {})
        observed_project_suite = project_suite(identities)
        if mode_expectations.get("projectSuite") != observed_project_suite:
            evidence_violations.append(
                f"{arguments.mode} {platform} project suite differs from {arguments.expectations}: "
                + f"expected {mode_expectations.get('projectSuite')}, observed {observed_project_suite}"
            )
        canonical_identity = mode_expectations.get("canonicalIdentity", {})
        canonical_platform = canonical_identity.get("platform")
        canonical_identity_enforced = platform == canonical_platform
        if canonical_identity_enforced and canonical_identity.get("projects") != identities:
            evidence_violations.append(
                f"{arguments.mode} canonical {platform} test identity hashes differ from {arguments.expectations}"
            )
        if mode_expectations.get("skipPolicy") != evidence_hash(policy_evidence):
            evidence_violations.append(
                f"{arguments.mode} skip-policy identity differs from {arguments.expectations}"
            )
        if expected_skips != skipped_tests:
            evidence_violations.append(
                f"{arguments.mode} skips differ by exact identity, reason, or owner: "
                + f"expected {expected_skips}, observed {skipped_tests}"
            )
        if primary_outcome_counts["failed"] != 0:
            evidence_violations.append(
                f"{arguments.mode} {platform} evidence must have zero primary-run failures"
            )
        unexpected_outcomes = sum(
            count
            for outcome, count in primary_outcome_counts.items()
            if outcome not in {"passed", "failed", "skipped"}
        )
        if unexpected_outcomes != 0:
            evidence_violations.append(
                f"{arguments.mode} {platform} evidence has {unexpected_outcomes} unexpected primary-run outcomes"
            )

    if arguments.expected_skips is not None and len(skipped_tests) != arguments.expected_skips:
        evidence_violations.append(
            f"expected exactly {arguments.expected_skips} primary-run skips, observed {len(skipped_tests)}"
        )

    if arguments.write_expectations is not None:
        write_expectations(
            arguments.write_expectations,
            arguments.mode,
            platform,
            identities,
            policy_evidence,
        )
        canonical_platform = platform
        canonical_identity_enforced = True

    slowest = sorted(
        results,
        key=lambda result: (
            -float(result["durationMilliseconds"]),
            str(result["project"]),
            str(result["test"]),
        ),
    )[:25]
    report = {
        "schemaVersion": 4,
        "selection": {
            "mode": arguments.mode,
            "lane": arguments.lane,
            "platform": platform,
        },
        "trxFiles": [path.relative_to(root).as_posix() for path in trx_paths],
        "projects": sorted({str(result["project"]) for result in results}),
        "runs": runs,
        "summary": {
            "total": len(results),
            "passed": outcome_counts["passed"],
            "failed": outcome_counts["failed"],
            "skipped": outcome_counts["skipped"],
            "other": sum(
                count
                for outcome, count in outcome_counts.items()
                if outcome not in {"passed", "failed", "skipped"}
            ),
            "totalDurationMilliseconds": round(
                sum(float(result["durationMilliseconds"]) for result in results),
                3,
            ),
        },
        "primarySummary": {
            "total": len(primary_results),
            "passed": primary_outcome_counts["passed"],
            "failed": primary_outcome_counts["failed"],
            "skipped": primary_outcome_counts["skipped"],
            "other": sum(
                count
                for outcome, count in primary_outcome_counts.items()
                if outcome not in {"passed", "failed", "skipped"}
            ),
        },
        "identityEvidence": identities,
        "aggregateIdentityEvidence": aggregate_identity_evidence(primary_results),
        "projectSuiteEvidence": project_suite(identities),
        "canonicalIdentityExpectation": {
            "platform": canonical_platform,
            "enforced": canonical_identity_enforced,
        },
        "skipPolicyEvidence": evidence_hash(policy_evidence),
        "expectedSkipEvidence": expected_skips,
        "skippedTests": skipped_tests,
        "evidenceViolations": evidence_violations,
        "slowest": slowest,
        "repeatedTests": repeated_tests,
        "flakeCandidates": flake_candidates,
    }
    json.dump(report, sys.stdout, indent=2, ensure_ascii=True)
    sys.stdout.write("\n")
    return 1 if evidence_violations else 0


if __name__ == "__main__":
    raise SystemExit(main())
