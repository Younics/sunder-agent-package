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


def report_names(mode, rid=None):
    names = ["native-family.trx", "scope/qualification.json", "maker-inputs/inputs.json", "maker-inputs/sunder-platform-contracts.v1.json"]
    if mode == "development":
        names += ["maker/journey.json", "maker/create.json", "maker/build.json", "maker/archive.json"]
    else:
        names.append("scope/published-registry.json")
        names += [package_id + "." + rid + ".json" for package_id in FAMILY_PACKAGE_IDS]
    return sorted(names)


def validate_receipt(value, mode, records, core, rid):
    keys(value, "schemaVersion coreCommit rid profile installedAcceptance registryOrigin archives tests reports")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1 and value["coreCommit"] == core
        and value["rid"] == rid and value["profile"] == PROFILES[mode] and value["installedAcceptance"] is (mode == "published")
        and value["archives"] == records and value["tests"] == tests(mode), "Native execution receipt differs from its exact source, archives or phase")
    require(type(value["reports"]) is list and [row["path"] for row in value["reports"]] == report_names(mode, rid),
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
    validate_trx(documents["native-family.trx"], mode)
    validate_scope(parse(documents["scope/qualification.json"]), mode, value["archives"], value["rid"])
    inputs = parse(documents["maker-inputs/inputs.json"])
    require(inputs["schemaVersion"] == 2 and inputs["coreCommit"] == value["coreCommit"] and inputs["workingTreeClean"] is True,
            "Native execution requires exact clean Core preparation evidence")
    catalog = documents["maker-inputs/sunder-platform-contracts.v1.json"]
    require(inputs["platformCatalog"]["sha256"] == digest(catalog) and inputs["platformCatalog"]["size"] == len(catalog),
            "Native execution used a different platform catalog")
    if mode == "development":
        validate_maker(parse(documents["maker/journey.json"]), inputs, value["archives"], value["rid"])
        for operation in ("create", "build", "archive"):
            require(parse(documents["maker/" + operation + ".json"])["state"] == "succeeded", "Maker job did not complete")
    else:
        proof = parse(documents["scope/published-registry.json"])
        keys(proof, "schemaVersion registryOrigin archiveDownloadAuthentication downloads policies")
        require(type(proof["schemaVersion"]) is int and proof["schemaVersion"] == 2 and proof["registryOrigin"] == value["registryOrigin"]
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
        for archive in value["archives"]:
            metric = parse(documents[archive["packageId"] + "." + value["rid"] + ".json"])
            validate_metrics(dict(metrics=metric, testedArchive=dict(path=archive["archive"], size=archive["size"])), value["rid"])


def tests(mode):
    require(mode in PROFILES, "Unknown native execution mode")
    return [CLASS + "." + method for method in ([DEVELOPMENT, MAKER] if mode == "development" else [PUBLISHED])]


def validate_trx(data, mode):
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
    selected = tests(mode)
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


def archive_records(directory):
    records = []
    for path in sorted(Path(directory).glob("*.sunderpkg")):
        manifest, *_ = archive_metadata(path)
        records.append(dict(packageId=manifest["id"], archive=path.name, size=path.stat().st_size,
                            sha256=file_record(path)["sha256"]))
    require(len(records) == len(FAMILY_PACKAGE_IDS) and {record["packageId"] for record in records} == set(FAMILY_PACKAGE_IDS),
            "Native execution requires exactly the complete family")
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
    require(len(inputs["packages"]) == 6 and journey["nuget"] == {row["name"]: row["sha256"] for row in inputs["packages"]},
            "Maker jobs did not use the exact six source developer packages")
    for field in ("archiveSha256", "sourceLockSha256"):
        sha(journey[field])


def record(metrics, archives, expected_core, mode):
    metrics = Path(metrics).resolve(strict=True)
    require(mode in PROFILES, "Unknown native execution mode")
    destination = metrics / RECEIPT
    require(not destination.exists(), "Native execution receipts are immutable; use a new result directory")
    records = archive_records(archives)
    scope = json_file(metrics / "scope/qualification.json")
    rid = scope["rid"]
    validate_scope(scope, mode, records, rid)
    validate_trx(read_bytes(metrics / "native-family.trx", 32 * 1024 * 1024), mode)
    input_root = metrics / "maker-inputs"
    inputs = json_file(input_root / "inputs.json")
    require(inputs["schemaVersion"] == 2 and inputs["coreCommit"] == expected_core and inputs["workingTreeClean"] is True,
            "Native release qualification requires exact clean Core source, not a dirty development checkpoint")
    for row in inputs["app"]["files"] + [inputs["platformCatalog"]]:
        keys(row, "path size sha256"); relative(row["path"])
        require(file_record(input_root / row["path"], row["path"]) == row, "Native App/catalog changed after preparation")
    reports = report_names(mode, rid)
    if mode == "development":
        journey = json_file(metrics / "maker/journey.json")
        validate_maker(journey, inputs, records, rid)
        for row in inputs["packages"]:
            path = input_root / "feed" / row["name"]
            require(path.stat().st_size == row["size"] and digest(read_bytes(path)) == row["sha256"], "Maker developer feed changed")
        for operation in ("create", "build", "archive"):
            require(json_file(metrics / "maker" / (operation + ".json"))["state"] == "succeeded", "Maker job did not complete")
    value = dict(schemaVersion=1, coreCommit=expected_core, rid=rid, profile=PROFILES[mode],
        installedAcceptance=mode == "published", registryOrigin=scope["registryOrigin"], archives=records,
        tests=tests(mode), reports=[file_record(metrics / name, name) for name in sorted(reports)])
    validate_receipt(value, mode, records, expected_core, rid)
    verify_reports(value, lambda name: read_bytes(metrics / name, 32 * 1024 * 1024))
    destination.write_bytes(compact(value))
    return value


def export_installed(metrics, destination):
    metrics, destination = Path(metrics).resolve(strict=True), Path(destination).absolute()
    value = json_file(metrics / RECEIPT)
    validate_receipt(value, "published", value["archives"], value["coreCommit"], value["rid"])
    verify_reports(value, lambda name: read_bytes(metrics / name, 32 * 1024 * 1024))
    require(not destination.exists() and destination.name == "native-installed-" + value["rid"] + ".tar.gz", "Installed native handoff must be new and exact-RID")
    sidecar = destination.with_name("native-installed-" + value["rid"] + ".json")
    require(not sidecar.exists(), "Installed native receipt sidecar is immutable")
    destination.parent.mkdir(parents=True, exist_ok=True)
    write_tar(destination, {RECEIPT: metrics / RECEIPT,
        **{"reports/" + row["path"]: metrics / row["path"] for row in value["reports"]}})
    with sidecar.open("xb") as stream: stream.write(compact(value))


def read_installed_bundle(path, records, core, rid, registry):
    # The bounded sidecar supplies the expected membership before the strict raw
    # tar scan. Never let tarfile expand untrusted PAX/link metadata first.
    value = json_file(Path(path).with_name("native-installed-" + rid + ".json"), 1024 * 1024)
    validate_receipt(value, "published", records, core, rid)
    require(value["registryOrigin"] == registry, "Installed native Registry differs from the selected publication")
    verify_bundle(path, value)
    return value


def verify_bundle(path, value):
    expected = {RECEIPT: dict(size=len(compact(value)), sha256=digest(compact(value))),
                **{"reports/" + row["path"]: row for row in value["reports"]}}
    reports = {}
    scan_tar(path, expected, lambda name, data: reports.__setitem__(name, data), capture_names=expected)
    verify_reports(value, lambda name: reports["reports/" + name])


def installed_set(directory, records, core, registry, *, exact_directory=True):
    directory = Path(directory).resolve(strict=True)
    names = {"native-installed-" + rid + suffix for rid in RIDS for suffix in (".json", ".tar.gz")}
    observed = {path.name for path in directory.iterdir() if exact_directory or path.name.startswith("native-installed-")}
    require(observed == names, "Installed acceptance requires exactly six native RID handoffs")
    runs = [read_installed_bundle(directory / ("native-installed-" + rid + ".tar.gz"), records, core, rid, registry) for rid in RIDS]
    return dict(schemaVersion=1, coreCommit=core, registryOrigin=registry, archives=records, nativeRuns=runs,
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
    parser.add_argument("core_commit")
    args = parser.parse_args()
    value = record(args.metrics, args.archives, args.core_commit, args.mode)
    print(f"Qualified {len(value['archives'])} exact {value['rid']} archives in {value['profile']} scope; installedAcceptance={value['installedAcceptance']}.")
