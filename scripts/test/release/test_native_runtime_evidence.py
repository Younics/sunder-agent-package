"""Selection/scope validation only; these fixtures are not native execution evidence."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import native_runtime_evidence as evidence


def trx(mode):
    root = ET.Element("TestRun")
    definitions, results = ET.SubElement(root, "TestDefinitions"), ET.SubElement(root, "Results")
    for index, name in enumerate(evidence.tests(mode)):
        definition = ET.SubElement(definitions, "UnitTest", id=str(index))
        ET.SubElement(definition, "TestMethod", className=name.rsplit(".", 1)[0], name=name.rsplit(".", 1)[1])
        ET.SubElement(results, "UnitTestResult", testId=str(index), testName="untrusted display name", outcome="Passed")
    summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
    ET.SubElement(summary, "Counters", total=str(len(evidence.tests(mode))), executed=str(len(evidence.tests(mode))),
                  passed=str(len(evidence.tests(mode))), failed="0", error="0", notExecuted="0", aborted="0")
    return root


def native_documents(records, rid, core, mode="development", registry="https://registry.example"):
    """Synthetic transport documents only, never an execution/preparation claim."""
    from native_release_inputs import digest
    encode = lambda value: json.dumps(value, separators=(",", ":")).encode()
    catalog = b"synthetic catalog transport bytes"
    inputs = dict(schemaVersion=2, coreCommit=core, workingTreeClean=True,
                  packages=[dict(name=f"package-{index}.nupkg", sha256=str(index) * 64) for index in range(6)],
                  platformCatalog=dict(path="sunder-platform-contracts.v1.json", size=len(catalog), sha256=digest(catalog)))
    scope = dict(schemaVersion=1, rid=rid, profile=evidence.PROFILES[mode], installedAcceptance=mode == "published",
                 registryOrigin=None if mode == "development" else registry, archives=records)
    documents = {"native-family.trx": ET.tostring(trx(mode)), "scope/qualification.json": encode(scope),
                 "maker-inputs/inputs.json": encode(inputs), "maker-inputs/sunder-platform-contracts.v1.json": catalog}
    if mode == "development":
        owner = next(row for row in records if row["packageId"] == "younics.sunder.package.maker")
        journey = dict(schemaVersion=1, rid=rid, withoutAgent=True, profile=evidence.PROFILES[mode], installedAcceptance=False,
                       callerBoundary="supervised-native-app-rpc", makerViewAcceptance=False, operations=evidence.OPERATIONS,
                       makerArchiveSha256=owner["sha256"], archiveSha256="b" * 64, sourceLockSha256="c" * 64,
                       nuget={row["name"]: row["sha256"] for row in inputs["packages"]})
        documents["maker/journey.json"] = encode(journey)
        documents.update({"maker/" + operation + ".json": encode(dict(state="succeeded")) for operation in ("create", "build", "archive")})
    else:
        documents["scope/published-registry.json"] = encode(dict(schemaVersion=2, registryOrigin=scope["registryOrigin"],
            archiveDownloadAuthentication="anonymous",
            downloads=[dict(packageId=row["packageId"], version="2.0.0", archiveSha256=row["sha256"], size=row["size"]) for row in records],
            policies=[dict(package=dict(packageId=row["packageId"], version="2.0.0", packageContentIdentity="c" * 64),
                lease=dict(state="valid", errorCode=None, responseNonce="a" * 32, responseSha256="b" * 64,
                    watermark=dict(registryOrigin=registry, packagePolicyRevision=1, packagePolicyFingerprint="d" * 64),
                    observedAtUtc="2026-11-14T00:00:00Z", validUntilUtc="2026-11-15T00:00:00Z", clock={})) for row in records]))
        documents.update({package_id + "." + rid + ".json": encode(dict(syntheticTransportFixture=True)) for package_id in evidence.FAMILY_PACKAGE_IDS})
        for row in records:
            documents[row["packageId"] + "." + rid + ".json"] = encode(dict(measurement=dict(formatVersion=1, profile="dotnetWorker", rid=rid,
                archive=row["archive"], archiveBytes=row["size"], capturedAtUtc="2026-11-14T00:00:00Z", currentRidInstalledBytes=1,
                startupMilliseconds=1.5, workerProcessCount=1, workerRssBytes=1), thresholds=dict(expectedWorkerProcessCount=1,
                maximumArchiveBytes=row["size"], maximumCurrentRidInstalledBytes=1, maximumStartupMilliseconds=2.0, maximumWorkerRssBytes=1)))
    receipt = dict(schemaVersion=1, coreCommit=core, rid=rid, profile=scope["profile"], installedAcceptance=scope["installedAcceptance"],
                   registryOrigin=scope["registryOrigin"], archives=records, tests=evidence.tests(mode),
                   reports=[dict(path=name, size=len(data), sha256=digest(data)) for name, data in sorted(documents.items())])
    return receipt, documents


class NativeRuntimeEvidenceTests(unittest.TestCase):
    def test_published_proof_rejects_rehashed_wrong_nonce_authority_versions_and_metrics(self):
        from native_release_inputs import digest
        records = [dict(packageId="younics.sunder.package.maker", archive="maker.sunderpkg", size=1, sha256="a" * 64)]
        for kind in ("schema", "download-authentication", "legacy-authentication", "nonce", "response-hash", "origin",
                     "revision", "version", "archive-hash", "archive-size", "missing-download", "metric-identity", "metric-budget"):
            value, documents = native_documents(records, "win-x64", "c" * 40, "published")
            evidence.verify_reports(value, documents.__getitem__)
            name = "scope/published-registry.json" if not kind.startswith("metric-") else records[0]["packageId"] + ".win-x64.json"
            changed = json.loads(documents[name])
            if kind == "schema": changed["schemaVersion"] = 1
            elif kind == "download-authentication": changed["archiveDownloadAuthentication"] = "api-key"
            elif kind == "legacy-authentication":
                changed.pop("archiveDownloadAuthentication")
                changed.update(schemaVersion=1, authenticatedIdentityRead=True, authenticatedArchiveDownloads=True)
            elif kind == "nonce": changed["policies"][0]["lease"]["responseNonce"] = True
            elif kind == "response-hash": changed["policies"][0]["lease"]["responseSha256"] = "not-a-response-hash"
            elif kind == "origin": changed["policies"][0]["lease"]["watermark"]["registryOrigin"] = "https://other.example"
            elif kind == "revision": changed["policies"][0]["lease"]["watermark"]["packagePolicyRevision"] = 0
            elif kind == "version": changed["downloads"][0]["version"] = "3.0.0"
            elif kind == "archive-hash": changed["downloads"][0]["archiveSha256"] = "f" * 64
            elif kind == "archive-size": changed["downloads"][0]["size"] = 2
            elif kind == "missing-download": changed["downloads"].clear()
            elif kind == "metric-identity": changed["measurement"]["rid"] = "linux-x64"
            else: changed["measurement"]["startupMilliseconds"] = 1000
            documents[name] = json.dumps(changed).encode()
            row = next(row for row in value["reports"] if row["path"] == name)
            row.update(size=len(documents[name]), sha256=digest(documents[name]))
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                evidence.verify_reports(value, documents.__getitem__)

    def test_installed_handoffs_require_all_six_exact_rids_origin_archives_and_original_reports(self):
        from native_release_inputs import compact
        records = [dict(packageId="younics.sunder.package.maker", archive="maker.sunderpkg", size=1, sha256="a" * 64)]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            handoff = root / "handoff"
            for rid in evidence.RIDS:
                metrics = root / rid
                receipt, documents = native_documents(records, rid, "c" * 40, "published")
                for name, data in documents.items():
                    path = metrics / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                (metrics / evidence.RECEIPT).write_bytes(compact(receipt))
                evidence.export_installed(metrics, handoff / ("native-installed-" + rid + ".tar.gz"))
            result = evidence.installed_set(handoff, records, "c" * 40, "https://registry.example")
            self.assertEqual(6, len(result["nativeRuns"]))
            for records_input, core, registry in (([], "c" * 40, "https://registry.example"),
                (records, "b" * 40, "https://registry.example"), (records, "c" * 40, "https://other.example")):
                with self.assertRaises(ValueError): evidence.installed_set(handoff, records_input, core, registry)
            path = handoff / ("native-installed-" + evidence.RIDS[0] + ".json")
            original = path.read_bytes()
            path.unlink()
            with self.assertRaises(ValueError): evidence.installed_set(handoff, records, "c" * 40, "https://registry.example")
            path.write_bytes(original)
            value = json.loads(original)
            value["profile"] = evidence.PROFILES["development"]
            path.write_bytes(compact(value))
            with self.assertRaises(ValueError): evidence.installed_set(handoff, records, "c" * 40, "https://registry.example")

    def test_receipts_bind_original_reports_and_cannot_relabel_prior_evidence(self):
        records = [dict(packageId="younics.sunder.package.maker", archive="maker.sunderpkg", size=1, sha256="a" * 64)]
        receipt, documents = native_documents(records, "linux-x64", "c" * 40)
        evidence.validate_receipt(receipt, "development", records, "c" * 40, "linux-x64")
        evidence.verify_reports(receipt, documents.__getitem__)
        with self.assertRaises(ValueError):
            evidence.validate_receipt(receipt, "published", records, "c" * 40, "linux-x64")
        for name in documents:
            changed = {**documents, name: documents[name] + b"changed"}
            with self.subTest(name=name), self.assertRaises(ValueError):
                evidence.verify_reports(receipt, changed.__getitem__)

    def test_exact_method_definitions_not_display_names_establish_each_phase(self):
        for mode in evidence.PROFILES:
            evidence.validate_trx(ET.tostring(trx(mode)), mode)
        with self.assertRaises(ValueError):
            evidence.validate_trx(ET.tostring(trx("development")), "published")

    def test_skips_duplicates_incomplete_runs_and_runner_errors_are_not_acceptance(self):
        def mutations(root):
            return [
                (root.find("Results/UnitTestResult"), "outcome", "NotExecuted"),
                (root.find("ResultSummary"), "outcome", "Aborted"),
                (root.find("ResultSummary/Counters"), "error", "1"),
                (root.find("TestDefinitions/UnitTest/TestMethod"), "className", "Wrong.Owner"),
                (root.find("TestDefinitions/UnitTest/TestMethod"), "name", evidence.PUBLISHED),
            ]
        for index in range(5):
            root = trx("development")
            element, key, value = mutations(root)[index]
            element.set(key, value)
            with self.subTest(index=index), self.assertRaises(ValueError):
                evidence.validate_trx(ET.tostring(root), "development")
        for kind in ("duplicate", "missing", "runner"):
            root = trx("development")
            if kind == "duplicate": root.find("Results").append(copy.deepcopy(root.find("Results/UnitTestResult")))
            elif kind == "missing": root.find("Results").remove(root.find("Results/UnitTestResult"))
            else: ET.SubElement(root.find("ResultSummary"), "RunInfo", outcome="Error")
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                evidence.validate_trx(ET.tostring(root), "development")

    def test_development_scope_cannot_be_relabeled_as_installed_or_change_archive_bytes(self):
        records = [dict(packageId="fixture", archive="fixture.1.0.0.sunderpkg", size=1, sha256="a" * 64)]
        scope = dict(schemaVersion=1, rid="win-x64", profile=evidence.PROFILES["development"],
                     installedAcceptance=False, registryOrigin=None, archives=records)
        evidence.validate_scope(scope, "development", records, "win-x64")
        for key, value in (("installedAcceptance", True), ("registryOrigin", "https://registry.example"),
                           ("profile", evidence.PROFILES["published"]), ("rid", "linux-arm64"), ("archives", [])):
            changed = {**scope, key: value}
            with self.subTest(key=key), self.assertRaises(ValueError):
                evidence.validate_scope(changed, "development", records, "win-x64")

    def test_published_scope_requires_an_explicit_https_origin(self):
        scope = dict(schemaVersion=1, rid="linux-arm64", profile=evidence.PROFILES["published"],
                     installedAcceptance=True, registryOrigin="https://registry.example", archives=[])
        evidence.validate_scope(scope, "published", [], "linux-arm64")
        for value in (None, "http://registry.example", "https://token@registry.example", "https://registry.example/path"):
            with self.subTest(value=value), self.assertRaises((ValueError, TypeError)):
                evidence.validate_scope({**scope, "registryOrigin": value}, "published", [], "linux-arm64")

    def test_maker_receipt_retains_exact_feed_owner_archive_and_non_ui_scope(self):
        inputs = dict(packages=[dict(name=f"package-{index}.nupkg", sha256=str(index) * 64) for index in range(6)])
        records = [dict(packageId="younics.sunder.package.maker", sha256="a" * 64)]
        journey = dict(schemaVersion=1, rid="osx-arm64", withoutAgent=True, profile=evidence.PROFILES["development"],
                       installedAcceptance=False, callerBoundary="supervised-native-app-rpc", makerViewAcceptance=False,
                       operations=evidence.OPERATIONS, makerArchiveSha256="a" * 64, archiveSha256="b" * 64,
                       sourceLockSha256="c" * 64, nuget={row["name"]: row["sha256"] for row in inputs["packages"]})
        evidence.validate_maker(journey, inputs, records, "osx-arm64")
        for key, value in (("callerBoundary", "worker-owner-rpc"), ("makerViewAcceptance", True), ("withoutAgent", False),
                           ("operations", evidence.OPERATIONS[:-1]), ("makerArchiveSha256", "b" * 64), ("nuget", {})):
            with self.subTest(key=key), self.assertRaises(ValueError):
                evidence.validate_maker({**journey, key: value}, inputs, records, "osx-arm64")


if __name__ == "__main__":
    unittest.main()
