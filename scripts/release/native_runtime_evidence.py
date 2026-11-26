#!/usr/bin/env python3
"""Bind native execution to exact test identities, scope and immutable archive inputs."""
import argparse
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from family_policy import FAMILY_PACKAGE_IDS
from native_release_inputs import RIDS, compact, digest, file_record, integer, json_file, keys, parse, read_bytes, relative, require, scan_tar, sha, write_tar
from release_inputs import archive_metadata

CLASS = "Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests"
DEVELOPMENT = "CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost"
PUBLISHED = "PublishedArchives_InstallActivateAndExerciseEveryNativeWorkerThroughRuntimeHost"
MAKER = "MakerArchive_CurrentRidCreatesBuildsPacksAndInvokesWithoutAgent"
PROFILES = {"development": "unpublished-development-import", "published": "published-installed"}
OPERATIONS = ["create", "author", "build", "archive", "validate-extract", "activate-development", "invoke", "unload-development", "worker-stopped"]
RECEIPT = "native-runtime-qualification.json"
INSTALLED = "installed-native-test-evidence.json"
OWNER = "younics.ai.agent"
MAKER_ID = "younics.sunder.package.maker"


def execution_ids(package=None):
    require(package is None or package in FAMILY_PACKAGE_IDS, "Unknown package under test")
    return set(FAMILY_PACKAGE_IDS) if package is None else {OWNER, package}


def check_installed_size(package, size):
    # Independent release ceilings must still fit the existing whole-family budget.
    import json
    policy = json.loads((Path(__file__).resolve().parents[2] / "eng/ci/native-release.json").read_bytes())
    budgets = policy["installedBudgetMiB"]
    require(set(budgets) == set(FAMILY_PACKAGE_IDS) and all(type(v) is int and v > 0 for v in budgets.values())
            and sum(budgets.values()) * 1024**2 <= policy["installedProjectionGate"]["maximumBytes"],
            "Independent installed budgets exceed the family ceiling")
    require(0 < size <= budgets[package] * 1024**2, "Installed-size budget exceeded for " + package)


def validate_metrics(package, rid):
    metric = package["metrics"]
    keys(metric, "measurement thresholds")
    measure, threshold = metric["measurement"], metric["thresholds"]
    keys(measure, "archive archiveBytes capturedAtUtc currentRidInstalledBytes formatVersion profile rid startupMilliseconds workerProcessCount workerRssBytes")
    keys(threshold, "expectedWorkerProcessCount maximumArchiveBytes maximumCurrentRidInstalledBytes maximumStartupMilliseconds maximumWorkerRssBytes")
    require(type(measure["formatVersion"]) is int and measure["formatVersion"] == 1
            and measure["profile"] == "dotnetWorker" and measure["rid"] == rid
            and measure["archive"] == package["testedArchive"]["path"]
            and measure["archiveBytes"] == package["testedArchive"]["size"], "Invalid Core metric identity")
    require(isinstance(measure["capturedAtUtc"], str) and re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})", measure["capturedAtUtc"]),
        "Invalid metric capture timestamp")
    for source, name in ((measure, "workerProcessCount"), (threshold, "expectedWorkerProcessCount")):
        require(type(source[name]) is int and source[name] == 1, "Expected exactly one native Worker process")
    for field in ("archiveBytes", "currentRidInstalledBytes", "startupMilliseconds", "workerRssBytes"):
        actual = measure[field]
        maximum = threshold["maximum" + field[0].upper() + field[1:]]
        types = (int, float) if field == "startupMilliseconds" else (int,)
        require(type(actual) in types and type(maximum) in types and math.isfinite(actual)
                and math.isfinite(maximum) and 0 < actual <= maximum, "Core native metric budget failed: " + field)


def report_names(mode, rid=None, *, published_app=False, package=None):
    names = ["native-family.trx", "scope/qualification.json", "maker-inputs/inputs.json", "maker-inputs/sunder-platform-contracts.v1.json"]
    if mode == "development":
        if package in (None, MAKER_ID):
            names += ["maker/journey.json", "maker/create.json", "maker/build.json", "maker/archive.json"]
        if published_app and package in (None, OWNER):
            names.append("apps/agent-snapshot/process.txt")
    else:
        names.append("scope/published-registry.json")
        names += [package_id + "." + rid + ".json" for package_id in execution_ids(package)]
    return sorted(names)


def validate_receipt(value, mode, records, material, rid, *, package=None):
    historical = isinstance(material, str)
    field = "coreCommit" if historical else "appArtifact"
    installed_lane = bool(package) and mode == "published"
    keys(value, f"schemaVersion {field} rid profile installedAcceptance registryOrigin archives tests reports" + (" releasePackage" if package else "")
         + (" acceptanceSourceCommit" if installed_lane else ""))
    if installed_lane:
        require(isinstance(value["acceptanceSourceCommit"], str) and re.fullmatch("[0-9a-f]{40}", value["acceptanceSourceCommit"]),
                "Missing installed acceptance source")
    if package is not None:
        require(not historical and value["releasePackage"] == package
                and sorted(row["packageId"] for row in records) == sorted(execution_ids(package)),
                "Package smoke must cover exactly its subject and Agent dependency")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == (4 if installed_lane else 3 if package else 1 if historical else 2) and value[field] == material
        and value["rid"] == rid and value["profile"] == PROFILES[mode] and value["installedAcceptance"] is (mode == "published")
        and value["archives"] == records and value["tests"] == tests(mode, package), "Native execution receipt differs from its exact source, archives or phase")
    require(type(value["reports"]) is list and [row["path"] for row in value["reports"]] == report_names(mode, rid, published_app=not historical, package=package),
            "Native execution receipt lacks its exact original reports")
    for row in value["reports"]:
        keys(row, "path size sha256")
        relative(row["path"]); integer(row["size"], 32 * 1024 * 1024, 1); sha(row["sha256"])
    validate_scope(dict(schemaVersion=1, rid=rid, profile=value["profile"], installedAcceptance=value["installedAcceptance"],
        registryOrigin=value["registryOrigin"], archives=records), mode, records, rid)


def verify_reports(value, loader):
    mode = next(mode for mode, profile in PROFILES.items() if value["profile"] == profile)
    documents = {}
    for row in value["reports"]:
        data = loader(row["path"])
        require(len(data) == row["size"] and digest(data) == row["sha256"], "Native execution report bytes differ from the retained receipt")
        documents[row["path"]] = data
    package = value.get("releasePackage")
    validate_trx(documents["native-family.trx"], mode, package)
    scope = parse(documents["scope/qualification.json"])
    validate_scope(scope, mode, value["archives"], value["rid"])
    require(scope["registryOrigin"] == value["registryOrigin"], "Scope and receipt disagree on Registry origin")
    inputs = parse(documents["maker-inputs/inputs.json"])
    if value["schemaVersion"] == 1:
        require(inputs["schemaVersion"] == 2 and inputs["coreCommit"] == value["coreCommit"] and inputs["workingTreeClean"] is True,
                "Historical native execution requires its original source evidence")
    else:
        require(inputs["schemaVersion"] == 3 and inputs["app"]["rid"] == value["rid"] and inputs["app"]["artifact"] == value["appArtifact"],
                "Native execution used another published App/Host")
    catalog = documents["maker-inputs/sunder-platform-contracts.v1.json"]
    require(inputs["platformCatalog"]["sha256"] == digest(catalog) and inputs["platformCatalog"]["size"] == len(catalog),
            "Native execution used a different platform catalog")
    if mode == "development":
        if value["schemaVersion"] >= 2 and package in (None, OWNER):
            log = documents["apps/agent-snapshot/process.txt"]
            require(b"agent-family-app: materialized-and-activated" in log and b"agent-family-app: retired" in log,
                    "Agent App snapshot activation/retirement evidence is missing")
        if package in (None, MAKER_ID):
            validate_maker(parse(documents["maker/journey.json"]), inputs, value["archives"], value["rid"])
            for operation in ("create", "build", "archive"):
                require(parse(documents["maker/" + operation + ".json"])["state"] == "succeeded", "Maker job did not complete")
    else:
        proof = parse(documents["scope/published-registry.json"])
        plans = proof.get("schemaVersion") == 3
        keys(proof, "schemaVersion registryOrigin archiveDownloadAuthentication downloads policies" + (" install reinstall" if plans else ""))
        require((not package or plans) and type(proof["schemaVersion"]) is int and proof["schemaVersion"] in (2, 3) and proof["registryOrigin"] == value["registryOrigin"]
            and proof["archiveDownloadAuthentication"] == "anonymous",
            "Published native execution lacks anonymous Registry download evidence")
        require(sorted((row["packageId"], row["archiveSha256"], row["size"]) for row in proof["downloads"])
            == sorted((row["packageId"], row["sha256"], row["size"]) for row in value["archives"]),
            "Published native execution did not install the exact downloaded archives")
        require(len(proof["policies"]) == len(value["archives"])
            and {row["package"]["packageId"] for row in proof["policies"]} == {row["packageId"] for row in value["archives"]}
            and all(row["lease"]["state"] == "valid" and row["lease"]["responseNonce"] and row["lease"]["responseSha256"] for row in proof["policies"]),
            "Published native execution lacks genuine nonce-bound policy observations")
        downloads = {row["packageId"]: row for row in proof["downloads"]}
        for row in proof["policies"]:
            keys(row, "package lease")
            subject, lease = row["package"], row["lease"]
            keys(subject, "packageId version packageContentIdentity")
            sha(subject["packageContentIdentity"])
            download = downloads[subject["packageId"]]
            keys(download, "packageId version archiveSha256 size")
            require(download["version"] == subject["version"], "Published archive download and policy versions differ")
            keys(lease, "state errorCode watermark responseNonce responseSha256 observedAtUtc validUntilUtc clock")
            sha(lease["responseSha256"])
            require(isinstance(lease["responseNonce"], str) and re.fullmatch(r"[0-9a-f]{32}", lease["responseNonce"])
                    and lease["errorCode"] is None, "Invalid published policy response identity")
            watermark = lease["watermark"]
            keys(watermark, "registryOrigin packagePolicyRevision packagePolicyFingerprint")
            sha(watermark["packagePolicyFingerprint"])
            require(watermark["registryOrigin"] == value["registryOrigin"] and type(watermark["packagePolicyRevision"]) is int
                    and watermark["packagePolicyRevision"] > 0 and lease["clock"] is not None
                    and lease["observedAtUtc"] and lease["validUntilUtc"], "Invalid published policy lease authority")
        if plans:
            import hub_install_evidence
            hub_install_evidence.verify(proof, value["rid"], package or "younics.ai.agent.tools.shell")
        for archive in value["archives"]:
            metric = parse(documents[archive["packageId"] + "." + value["rid"] + ".json"])
            validate_metrics(dict(metrics=metric, testedArchive=dict(path=archive["archive"], size=archive["size"])), value["rid"])
            if package is not None:
                check_installed_size(archive["packageId"], metric["measurement"]["currentRidInstalledBytes"])


def tests(mode, package=None):
    require(mode in PROFILES, "Unknown native execution mode")
    return [CLASS + "." + method for method in (
        ([DEVELOPMENT, MAKER] if package in (None, MAKER_ID) else [DEVELOPMENT]) if mode == "development" else [PUBLISHED])]


def validate_trx(data, mode, package=None):
    require(len(data) <= 32 * 1024 * 1024, "Oversized native TRX")
    text = data.decode("utf-8-sig")
    require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(), "Unsafe native TRX")
    root = ET.fromstring(text)
    definitions = {}
    for definition in root.findall(".//{*}TestDefinitions/{*}UnitTest"):
        method = definition.find("{*}TestMethod")
        identity = method.get("className", "").split(",", 1)[0] + "." + method.get("name", "") if method is not None else ""
        require(definition.get("id") not in definitions, "Duplicate native test definition")
        definitions[definition.get("id")] = identity
    results = root.findall(".//{*}UnitTestResult")
    selected = tests(mode, package)
    require(len(results) == len(selected) and all(result.get("outcome") == "Passed" for result in results)
        and sorted(definitions.get(result.get("testId"), "") for result in results) == sorted(selected),
        "Missing, duplicate, skipped or nonpassing native test identities")
    summary = root.find("{*}ResultSummary")
    counters = summary.find("{*}Counters") if summary is not None else None
    require(summary is not None and summary.get("outcome") == "Completed" and counters is not None,
            "Native test run did not complete")
    for name in ("total", "executed", "passed"):
        require(counters.get(name) == str(len(selected)), "Native test counters differ from the exact selected scope")
    for name, count in counters.attrib.items():
        if name not in ("total", "executed", "passed"):
            require(count == "0", "Native test run contains an error, skip or incomplete result")
    require(not root.findall(".//{*}RunInfo") and not root.findall(".//{*}ErrorInfo"), "Native test run contains runner errors")


def archive_records(directory, package=None):
    records = []
    for path in sorted(Path(directory).glob("*.sunderpkg")):
        manifest, *_ = archive_metadata(path)
        records.append(dict(packageId=manifest["id"], archive=path.name, size=path.stat().st_size,
                            sha256=file_record(path)["sha256"]))
    require(sorted(record["packageId"] for record in records) == sorted(execution_ids(package)),
            "Native execution archive set differs from the required scope")
    return sorted(records, key=lambda row: row["packageId"])


def validate_scope(scope, mode, records, rid):
    keys(scope, "schemaVersion rid profile installedAcceptance registryOrigin archives")
    require(scope["schemaVersion"] == 1 and scope["rid"] == rid and scope["profile"] == PROFILES[mode]
        and scope["installedAcceptance"] is (mode == "published") and scope["archives"] == records,
        "Native execution scope or exact archive bytes differ")
    if mode == "development":
        require(scope["registryOrigin"] is None, "Development imports cannot claim Registry authority")
    else:
        from agent_release import origin
        require(origin(scope["registryOrigin"]) == scope["registryOrigin"], "Published native acceptance requires an exact HTTPS Registry origin")


def validate_maker(journey, inputs, records, rid):
    require(journey["schemaVersion"] == 1 and journey["rid"] == rid and journey["withoutAgent"] is True
        and journey["profile"] == PROFILES["development"] and journey["installedAcceptance"] is False
        and journey["callerBoundary"] == "supervised-native-app-rpc" and journey["makerViewAcceptance"] is False
        and journey["operations"] == OPERATIONS, "Maker journey is incomplete or claims the wrong authority/view scope")
    expected = next(row for row in records if row["packageId"] == "younics.sunder.package.maker")
    require(journey["makerArchiveSha256"] == expected["sha256"], "Maker journey did not exercise the selected final archive")
    require(inputs["packages"] and journey["nuget"] == {row["name"]: row["sha256"] for row in inputs["packages"]},
            "Maker jobs did not use the recorded authoring inputs")
    for field in ("archiveSha256", "sourceLockSha256"):
        sha(journey[field])


def record(metrics, archives, mode, package=None):
    metrics = Path(metrics).resolve(strict=True)
    require(mode in PROFILES, "Unknown native execution mode")
    destination = metrics / RECEIPT
    require(not destination.exists(), "Native execution receipts are immutable; use a new result directory")
    records = archive_records(archives, package)
    scope = json_file(metrics / "scope/qualification.json")
    rid = scope["rid"]
    validate_scope(scope, mode, records, rid)
    validate_trx(read_bytes(metrics / "native-family.trx", 32 * 1024 * 1024), mode, package)
    input_root = metrics / "maker-inputs"
    inputs = json_file(input_root / "inputs.json")
    from published_app import selection
    app = selection(rid, installed=mode == "published")
    require(inputs["schemaVersion"] == 3 and inputs["app"]["rid"] == rid
            and inputs["app"]["artifact"] == app, "Native test input must be the declared published App/Host")
    archive = input_root / "distribution" / app["name"]
    require(file_record(archive, maximum=384 * 1024**2) ==
            {key: app[value] for key, value in (("path", "name"), ("size", "size"), ("sha256", "sha256"))},
            "Native App distribution changed after preparation")
    reports = report_names(mode, rid, published_app=True, package=package)
    if mode == "development" and package in (None, MAKER_ID):
        journey = json_file(metrics / "maker/journey.json")
        validate_maker(journey, inputs, records, rid)
        for row in inputs["packages"]:
            path = input_root / "feed" / row["name"]
            require(path.stat().st_size == row["size"] and digest(read_bytes(path)) == row["sha256"], "Maker developer feed changed")
        for operation in ("create", "build", "archive"):
            require(json_file(metrics / "maker" / (operation + ".json"))["state"] == "succeeded", "Maker job did not complete")
    value = dict(schemaVersion=2, appArtifact=app, rid=rid, profile=PROFILES[mode],
        installedAcceptance=mode == "published", registryOrigin=scope["registryOrigin"], archives=records,
        tests=tests(mode, package), reports=[file_record(metrics / name, name) for name in sorted(reports)])
    if package is not None: value.update(schemaVersion=3, releasePackage=package)
    if package is not None and mode == "published":
        from agent_release import git
        value.update(schemaVersion=4, acceptanceSourceCommit=git(Path(__file__).resolve().parents[2], "rev-parse", "HEAD").decode().strip())
    validate_receipt(value, mode, records, app, rid, package=package)
    verify_reports(value, lambda name: read_bytes(metrics / name, 32 * 1024 * 1024))
    destination.write_bytes(compact(value))
    return value


def export_installed(metrics, destination):
    metrics, destination = Path(metrics).resolve(strict=True), Path(destination).absolute()
    value = json_file(metrics / RECEIPT)
    from published_app import selection
    validate_receipt(value, "published", value["archives"], selection(value["rid"], installed=True), value["rid"], package=value.get("releasePackage"))
    verify_reports(value, lambda name: read_bytes(metrics / name, 32 * 1024 * 1024))
    require(not destination.exists() and destination.name == "native-installed-" + value["rid"] + ".tar.gz", "Installed native handoff must be new and exact-RID")
    sidecar = destination.with_name("native-installed-" + value["rid"] + ".json")
    require(not sidecar.exists(), "Installed native receipt sidecar is immutable")
    destination.parent.mkdir(parents=True, exist_ok=True)
    write_tar(destination, {RECEIPT: metrics / RECEIPT,
        **{"reports/" + row["path"]: metrics / row["path"] for row in value["reports"]}})
    with sidecar.open("xb") as stream: stream.write(compact(value))


def read_installed_bundle(path, records, core, rid, registry, *, package=None):
    # The bounded sidecar supplies the expected membership before the strict raw
    # tar scan. Never let tarfile expand untrusted PAX/link metadata first.
    value = json_file(Path(path).with_name("native-installed-" + rid + ".json"), 1024 * 1024)
    validate_receipt(value, "published", records, core, rid, package=package)
    require(value["registryOrigin"] == registry, "Installed native Registry differs from the selected publication")
    verify_bundle(path, value)
    return value


def verify_bundle(path, value):
    expected = {RECEIPT: dict(size=len(compact(value)), sha256=digest(compact(value))),
                **{"reports/" + row["path"]: row for row in value["reports"]}}
    reports = {}
    scan_tar(path, expected, lambda name, data: reports.__setitem__(name, data), capture_names=expected)
    verify_reports(value, lambda name: reports["reports/" + name])


def installed_set(directory, records, core, registry, *, exact_directory=True, package=None):
    directory = Path(directory).resolve(strict=True)
    names = {"native-installed-" + rid + suffix for rid in RIDS for suffix in (".json", ".tar.gz")}
    observed = {path.name for path in directory.iterdir() if exact_directory or path.name.startswith("native-installed-")}
    require(observed == names, "Installed acceptance requires exactly six native RID handoffs")
    from published_app import selection
    runs = [read_installed_bundle(directory / ("native-installed-" + rid + ".tar.gz"), records,
                                   core if core is not None else selection(rid, installed=True), rid, registry, package=package) for rid in RIDS]
    if package:
        require(len({v["acceptanceSourceCommit"] for v in runs}) == 1, "Mixed installed acceptance sources")
    release = dict(schemaVersion=1, coreCommit=core) if core is not None else dict(schemaVersion=2)
    return dict(release, registryOrigin=registry, archives=records, nativeRuns=runs,
                bundles=[file_record(directory / name) for name in sorted(names)])


if __name__ == "__main__":
    import sys
    if len(sys.argv) == 4 and sys.argv[1] == "export-installed":
        export_installed(sys.argv[2], sys.argv[3])
        raise SystemExit(0)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=PROFILES)
    parser.add_argument("metrics", type=Path)
    parser.add_argument("archives", type=Path)
    parser.add_argument("--package", choices=FAMILY_PACKAGE_IDS)
    args = parser.parse_args()
    value = record(args.metrics, args.archives, args.mode, args.package)
    print(f"Qualified {len(value['archives'])} exact {value['rid']} archives in {value['profile']} scope; installedAcceptance={value['installedAcceptance']}.")
