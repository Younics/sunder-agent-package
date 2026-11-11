#!/usr/bin/env python3

import argparse
import functools
import getpass
import json
import os
import re
import sys
import tempfile
import xml.etree.ElementTree as ElementTree
from collections import Counter, defaultdict
from datetime import datetime
from decimal import Decimal
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


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
    "UnprivilegedUnixTheory",
    "WindowsFact",
    "WindowsTheory",
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
        return (name[:-4] if name.casefold().endswith(".dll") else name).casefold()
    return trx_path.stem.casefold()


def timestamp(value: str | None) -> datetime | None:
    return datetime.fromisoformat(value) if value else None


def elapsed_milliseconds(start: datetime | None, finish: datetime | None) -> float | None:
    if start is None or finish is None:
        return None
    return round(max(0.0, (finish - start).total_seconds() * 1000), 3)


def read_trx(root: Path, trx_path: Path) -> tuple[list[dict[str, object]], dict[str, object]]:
    document = ElementTree.parse(trx_path)
    violations = []
    summary = document.find("./{*}ResultSummary")
    if summary is None or summary.get("outcome") not in {"Completed", "Passed", "Failed"}:
        violations.append("TRX has no completed run summary")
    definitions: dict[str, tuple[str, str]] = {}
    for unit_test in document.findall(".//{*}UnitTest"):
        test_id = unit_test.get("id")
        method = unit_test.find(".//{*}TestMethod")
        if not test_id or method is None:
            violations.append("TRX contains an incomplete test definition")
            continue
        if test_id in definitions:
            violations.append(f"TRX repeats test definition '{test_id}'")
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
    observed_ids, executions = set(), set()
    for result in document.findall(".//{*}UnitTestResult"):
        test_id = result.get("testId", "")
        fallback_name = result.get("testName", "<unknown>")
        if test_id not in definitions:
            violations.append(f"TRX result '{fallback_name}' has no definition")
        observed_ids.add(test_id)
        execution = result.get("executionId")
        if not execution or execution in executions:
            violations.append(f"TRX result '{fallback_name}' has a missing or repeated execution id")
        executions.add(execution)
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
                "message": reason if outcome == "failed" else None,
                "stackTrace": result.findtext("./{*}Output/{*}ErrorInfo/{*}StackTrace") if outcome == "failed" else None,
                "durationMilliseconds": duration_milliseconds(result.get("duration")),
                "trx": relative_trx,
            }
        )
        if (test_start := timestamp(result.get("startTime"))) is not None:
            test_starts.append(test_start)
        if (test_finish := timestamp(result.get("endTime"))) is not None:
            test_finishes.append(test_finish)

    if not results:
        violations.append("TRX contains zero results")
    if missing := set(definitions) - observed_ids:
        violations.append(f"TRX omitted results for {len(missing)} discovered definitions")
    if summary is not None:
        counters = summary.find("./{*}Counters")
        expected = {"total": len(results), "executed": sum(row["outcome"] != "skipped" for row in results),
                    "passed": sum(row["outcome"] == "passed" for row in results),
                    "failed": sum(row["outcome"] == "failed" for row in results)}
        expected.update(dict.fromkeys(("error", "timeout", "aborted", "passedButRunAborted", "inProgress", "pending", "disconnected"), 0))
        if counters is None:
            violations.append("TRX has no result counters")
        else:
            for name, count in expected.items():
                value = counters.get(name)
                if (value is None and name in {"total", "executed", "passed", "failed"}) or (value is not None and value != str(count)):
                    violations.append(f"TRX counter {name}={value}, observed {count}")
        # xUnit's adapter also emits one Error notification for each failed case.
        # Accept only exact, multiplicity-bound failed-result notifications;
        # an unrelated adapter/host error must still invalidate a complete TRX.
        failed_notifications = Counter(row["test"] for row in results if row["outcome"] == "failed")
        run_errors = False
        for info in summary.findall(".//{*}RunInfo"):
            if info.get("outcome") != "Error": continue
            notification = re.fullmatch(r"\[xUnit\.net \d+:\d{2}:\d{2}\.\d+\]\s+(.+) \[FAIL\]", info.findtext("./{*}Text", ""))
            if notification and failed_notifications[notification[1]]:
                failed_notifications[notification[1]] -= 1
            else:
                run_errors = True
        if run_errors:
            violations.append("TRX contains test-platform errors")
        if summary.get("outcome") == "Failed" and not expected["failed"]:
            violations.append("TRX run failed or aborted despite its completed cases having no failures")

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
        "violations": violations,
        "elapsedMilliseconds": elapsed_milliseconds(run_start, run_finish),
        "setupMilliseconds": elapsed_milliseconds(run_start, first_test_start),
        "testWindowMilliseconds": elapsed_milliseconds(first_test_start, last_test_finish),
        "teardownMilliseconds": elapsed_milliseconds(last_test_finish, run_finish),
    }
    return results, run


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Summarize adapter discovery, TRX completeness, failures and skip policy."
    )
    parser.add_argument("trx_directory", type=Path)
    parser.add_argument("--inventory", type=Path)
    parser.add_argument("--mode", choices=("source", "package"))
    parser.add_argument("--platform", choices=("linux", "macos", "windows"))
    parser.add_argument("--lane")
    parser.add_argument("--exclude-category")
    parser.add_argument("--verify", action="store_true", help="Require fresh adapter discovery and current source skip policy.")
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--expected-skips", type=int)
    return parser.parse_args()


def read_records(root: Path, suffix: str, violations: list[str]):
    for path in sorted(root.rglob(suffix)):
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(record, dict):
                raise ValueError("Expected a JSON object")
            yield path, record
        except (OSError, ValueError) as error:
            violations.append(f"{path.relative_to(root)}: invalid evidence: {error}")


def discovery_violations(root: Path, results: list[dict[str, object]], discoveries) -> list[str]:
    """Each selection is checked against its own adapter discovery on this runner.

    Deferred theories are discovered as a method; the adapter expands their rows
    during execution. Pre-enumerated rows must each have their exact result.
    """
    violations, covered = [], set()
    for path, discovery in discoveries:
        project = discovery["project"].casefold()
        report = (path.parent / discovery["report"]).relative_to(root).as_posix()
        expected = discovery["tests"]
        observed = [row for row in results if row["trx"] == report and row["project"] == project]
        covered.add((project, report))
        names = Counter(row["test"] for row in observed)
        selected = Counter(expected)
        methods = {row["fullyQualifiedName"] for row in observed}
        for name, count in selected.items():
            if names[name] < count and name not in methods:
                violations.append(f"{report}: discovery omitted result '{name}'")
        for row in observed:
            if row["test"] not in expected and row["fullyQualifiedName"] not in expected:
                violations.append(f"{report}: result was not discovered: '{row['test']}'")
            elif names[row["test"]] > selected[row["test"]] and row["fullyQualifiedName"] not in expected:
                violations.append(f"{report}: result repeated beyond discovery: '{row['test']}'")
        if expected and not observed:
            violations.append(f"{report}: selected project produced no results")
    for row in results:
        if (row["project"], row["trx"]) not in covered:
            violations.append(f"{row['trx']}: missing adapter discovery for {row['project']}")
    return sorted(set(violations))


def project_owners(inventory: dict[str, object]) -> dict[str, str]:
    return {
        Path(str(project["path"])).stem.casefold(): str(project["owner"])
        for project in inventory["projects"]
    }


def decode_csharp_string(value: str) -> str:
    return json.loads(value)


def csharp_tokens(source: str) -> list[str]:
    """Keep literals atomic so fixture text cannot introduce declaration scopes."""
    def comment_end(start):
        if source.startswith("//", start) or source[start] == "#":
            end = source.find("\n", start)
            return len(source) if end < 0 else end
        if source.startswith("/*", start):
            end = source.find("*/", start + 2)
            return len(source) if end < 0 else end + 2

    def interpolation_end(start, width=1):
        depth, index = 1, start + 1
        while index < len(source):
            end = comment_end(index) or literal_end(index)
            if end is not None:
                index = end
                continue
            if source[index] == "{": depth += 1
            if source[index] == "}":
                depth -= 1
                if depth == 0: return index + width
            index += 1
        return len(source)

    def literal_end(start):
        quote = start
        while quote < len(source) and source[quote] in "$@": quote += 1
        prefix = source[start:quote]
        if quote == len(source) or (source[quote] != '"' and (prefix or source[quote] != "'")):
            return None
        delimiter, verbatim, interpolated = source[quote], "@" in prefix, "$" in prefix
        if delimiter == '"' and not verbatim and source.startswith('"""', quote):
            width = 3
            while quote + width < len(source) and source[quote + width] == '"': width += 1
            index = quote + width
            while index < len(source):
                if source.startswith('"' * width, index): return index + width
                if interpolated and source.startswith("{" * len(prefix), index):
                    while source[index + 1:index + 2] == "{": index += 1
                    index = interpolation_end(index, len(prefix))
                else: index += 1
            return len(source)
        index = quote + 1
        while index < len(source):
            if not verbatim and source[index] == "\\": index += 2; continue
            if source[index] == delimiter:
                if verbatim and source[index + 1:index + 2] == '"': index += 2; continue
                return index + 1
            if interpolated and source[index] == "{":
                index = index + 2 if source[index + 1:index + 2] == "{" else interpolation_end(index)
            else: index += 1
        return len(source)

    tokens, index = [], 0
    identifier = re.compile(r"@?[^\W\d]\w*")
    while index < len(source):
        if source[index].isspace(): index += 1; continue
        end = comment_end(index)
        if end is not None: index = end; continue
        match = identifier.match(source, index)
        end = literal_end(index) or (match.end() if match else index + 1)
        tokens.append(source[index:end])
        index = end
    return tokens


def method_attribute_blocks(path: Path) -> list[tuple[str, str, list[str]]]:
    tokens = csharp_tokens(path.read_text(encoding="utf-8"))
    scopes, results = [], []
    file_namespace, index = "", 0

    def balanced_end(start, opening, closing):
        depth = 0
        for end in range(start, len(tokens)):
            if tokens[end] == opening: depth += 1
            if tokens[end] == closing:
                depth -= 1
                if depth == 0: return end + 1
        return len(tokens)

    while index < len(tokens):
        token = tokens[index]
        parent = scopes[-1][0] if scopes else None
        if token == "}":
            if scopes: scopes.pop()
        elif token == "{": scopes.append(("block", ""))
        elif token == "namespace" and parent in (None, "namespace"):
            end = index + 1
            while end < len(tokens) and tokens[end] not in (";", "{"): end += 1
            if end < len(tokens):
                name = "".join(tokens[index + 1:end])
                if tokens[end] == ";": file_namespace = name
                else: scopes.append(("namespace", name))
            index = end
        elif token in ("class", "struct", "interface", "record") and parent in (None, "namespace", "type"):
            name_index = index + 1
            if token == "record" and tokens[name_index] in ("class", "struct"): name_index += 1
            name, end = tokens[name_index].lstrip("@"), name_index + 1
            while end < len(tokens) and tokens[end] not in ("{", ";"):
                if tokens[end] in ("(", "["): end = balanced_end(end, tokens[end], ")" if tokens[end] == "(" else "]")
                else: end += 1
            if end < len(tokens) and tokens[end] == "{": scopes.append(("type", name))
            index = end
        elif token == "[" and parent == "type":
            attributes, end = [], index
            while end < len(tokens) and tokens[end] == "[":
                close = balanced_end(end, "[", "]") - 1
                start = end = end + 1
                while end <= close:
                    if tokens[end] == "(": end = balanced_end(end, "(", ")")
                    elif tokens[end] == "[": end = balanced_end(end, "[", "]")
                    elif end == close or tokens[end] == ",":
                        attributes.append("[" + "".join(tokens[start:end]) + "]")
                        start = end = end + 1
                    else: end += 1
            method = end
            while method < len(tokens) and tokens[method] not in ("(", "{", ";", "=", "}"): method += 1
            header = tokens[end:method]
            if (method < len(tokens) and tokens[method] == "(" and "public" in header
                    and not any(kind in header for kind in ("class", "struct", "interface", "record"))):
                namespace = ".".join(filter(None, [file_namespace, *(name for kind, name in scopes if kind == "namespace")]))
                class_name = "+".join(name for kind, name in scopes if kind == "type")
                results.append((".".join(filter(None, [namespace, class_name])), header[-1].lstrip("@"), attributes))
            index = end - 1
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
    repository_root: Path,
    inventory: dict[str, object],
    mode: str,
    lane: str | None,
    exclude_category: str | None,
) -> list[dict[str, object]]:
    policies: list[dict[str, object]] = []
    for project in inventory["projects"]:
        if mode not in project["modes"]:
            continue
        if lane is not None and lane not in project.get("lanes", []):
            continue
        project_path = repository_root / str(project["path"])
        project_name_value = project_path.stem.casefold()
        for source_path in sorted(repository_root / source for source in project["sources"]):
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
            elif capability == "CaseSensitiveDirectories":
                if sys.platform == "win32":
                    import ctypes
                    from ctypes import wintypes
                    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
                    create = kernel.CreateFileW
                    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                       wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
                    create.restype = wintypes.HANDLE
                    set_information = kernel.SetFileInformationByHandle
                    set_information.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
                    set_information.restype = wintypes.BOOL
                    close = kernel.CloseHandle
                    close.argtypes = [wintypes.HANDLE]
                    close.restype = wintypes.BOOL
                    handle = create(str(root), 0x100, 7, None, 3, 0x02000000, None)
                    if handle == ctypes.c_void_p(-1).value:
                        raise ctypes.WinError(ctypes.get_last_error())
                    try:
                        flags = wintypes.DWORD(1)
                        if not set_information(handle, 23, ctypes.byref(flags), ctypes.sizeof(flags)):
                            raise ctypes.WinError(ctypes.get_last_error())
                    finally:
                        close(handle)
                target = root / "case-sensitive-probe"
                target.write_text("probe", encoding="utf-8")
                return not (root / "CASE-SENSITIVE-PROBE").exists()
            else:
                return False
        return True
    except (NotImplementedError, OSError):
        return False


def expected_skip(
    policy: dict[str, object], platform: str | None = None, environment=None
) -> dict[str, object] | None:
    platform = platform or current_platform()
    environment = os.environ if environment is None else environment
    attribute = str(policy["attribute"])
    target = policy["platform"]
    reason = str(policy["reason"])
    should_skip = False
    if attribute in {"UnixFact", "UnixTheory"}:
        should_skip = platform == "windows"
    elif attribute in {"WindowsFact", "WindowsTheory"}:
        should_skip = platform != "windows"
    elif attribute == "UnprivilegedUnixTheory":
        if platform == "windows":
            should_skip = True
            reason = "This permission-denied fixture requires Unix."
        elif os.geteuid() == 0 and environment.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1":
            should_skip = True
            reason = "A real permission-denied fixture requires a non-root Unix user."
    elif attribute == "DockerIntegrationFact":
        if platform == "windows":
            should_skip = True
            reason = "The Docker bind-mount integration test requires a Unix host."
        else:
            should_skip = (
                environment.get("SUNDER_DOCKER_INTEGRATION") != "1"
                and environment.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
            )
    elif attribute == "PackageArtifactsFact":
        should_skip = (
            not environment.get("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR", "").strip()
            and environment.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
        )
    elif attribute == "NativeReleaseFact":
        should_skip = (
            not environment.get("SUNDER_AGENT_NATIVE_ARCHIVE_DIR", "").strip()
            and environment.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
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
        and environment.get("SUNDER_REQUIRE_TEST_PREREQUISITES") != "1"
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
    if arguments.verify and (arguments.inventory is None or arguments.mode is None):
        print("--verify requires --inventory and --mode.", file=sys.stderr)
        return 2

    root = arguments.trx_directory.resolve()
    trx_paths = sorted(root.rglob("*.trx"))
    results: list[dict[str, object]] = []
    runs: list[dict[str, object]] = []
    evidence_violations = [] if trx_paths else [f"No TRX files were found under {root}."]
    for path in trx_paths:
        try:
            trx_results, run = read_trx(root, path)
        except (ElementTree.ParseError, ValueError) as error:
            evidence_violations.append(f"{path.relative_to(root)}: {error}")
            continue
        results.extend(trx_results)
        runs.append(run)
        evidence_violations.extend(f"{run['trx']}: {error}" for error in run["violations"])
    commands = []
    for path, record in read_records(root, "*.command.json", evidence_violations):
        record["record"] = path.relative_to(root).as_posix()
        commands.append(record)
        if record.get("exitCode") != 0:
            evidence_violations.append(f"{path.relative_to(root)}: command did not succeed (exitCode={record.get('exitCode')})")
        if record.get("errors"):
            evidence_violations.append(f"{path.relative_to(root)}: command diagnostics failed: {record['errors']}")
    discoveries = []
    if arguments.verify:
        for path, record in read_records(root, "*.discovery.json", evidence_violations):
            try:
                if (record["schemaVersion"] != 1 or not isinstance(record["project"], str) or not record["project"]
                        or not isinstance(record["tests"], list) or not all(isinstance(name, str) and name for name in record["tests"])
                        or not isinstance(record["report"], str) or not record["report"]
                        or not isinstance(record.get("prerequisites", {}), dict)):
                    raise ValueError("Invalid discovery selection")
                (path.parent / record["report"]).resolve().relative_to(root)
                discoveries.append((path, record))
            except (KeyError, ValueError) as error:
                evidence_violations.append(f"{path.relative_to(root)}: invalid discovery: {error}")
        evidence_violations.extend(discovery_violations(root, results, discoveries))
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
        if len({row["trx"] for row in observations}) < 2:
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

    # Source, required-input and artifact lanes are disjoint parts of one suite.
    # Stress repetitions live in their own report directory.
    primary_results = results
    primary_outcome_counts: dict[str, int] = defaultdict(int)
    for result in primary_results:
        primary_outcome_counts[str(result["outcome"])] += 1
    observed_projects = sorted({result["project"] for result in primary_results})
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
    if arguments.verify:
        policy_evidence = skip_policy(arguments.repository_root, inventory, arguments.mode,
                                      arguments.lane, arguments.exclude_category)
        for _, discovery in discoveries:
            for policy in policy_evidence:
                if policy["project"] != discovery["project"].casefold():
                    continue
                identity = policy["fullyQualifiedName"]
                if not any(name == identity or name.startswith(identity + "(") for name in discovery["tests"]):
                    continue
                if (expected := expected_skip(policy, platform, discovery.get("prerequisites", {}))) is not None:
                    expected_skips.append(expected)
        expected_skips.sort(key=lambda result: tuple(str(result[key]) for key in (
            "project", "owner", "fullyQualifiedName", "test", "reason")))
        if expected_skips != skipped_tests:
            evidence_violations.append(
                f"{arguments.mode} skips differ by exact identity, reason, or owner: "
                + f"expected {expected_skips}, observed {skipped_tests}"
            )
        if repeated_tests:
            evidence_violations.append("Primary suite contains repeated test identities across its selections")
    if primary_outcome_counts["failed"] != 0:
        evidence_violations.append("Evidence must have zero primary-run failures")
    unexpected_outcomes = sum(count for outcome, count in primary_outcome_counts.items()
                              if outcome not in {"passed", "failed", "skipped"})
    if unexpected_outcomes:
        evidence_violations.append(f"Evidence has {unexpected_outcomes} unexpected primary-run outcomes")

    if arguments.expected_skips is not None and len(skipped_tests) != arguments.expected_skips:
        evidence_violations.append(
            f"expected exactly {arguments.expected_skips} primary-run skips, observed {len(skipped_tests)}"
        )

    slowest = sorted(
        results,
        key=lambda result: (
            -float(result["durationMilliseconds"]),
            str(result["project"]),
            str(result["test"]),
        ),
    )[:25]
    report = {
        "schemaVersion": 5,
        "selection": {
            "mode": arguments.mode,
            "lane": arguments.lane,
            "platform": platform,
        },
        "trxFiles": [path.relative_to(root).as_posix() for path in trx_paths],
        "projects": sorted({str(result["project"]) for result in results}),
        "runs": runs,
        "commands": commands,
        "failures": [row for row in results if row["outcome"] == "failed"],
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
        "projectSuiteEvidence": observed_projects,
        "discoveryVerified": arguments.verify,
        "skipPolicyEvidence": policy_evidence,
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
