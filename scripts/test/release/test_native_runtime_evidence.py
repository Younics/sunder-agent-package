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
import published_app


def trx(mode, package=None):
    root = ET.Element("TestRun")
    definitions, results = ET.SubElement(root, "TestDefinitions"), ET.SubElement(root, "Results")
    methods = evidence.tests(mode, package)
    for index, name in enumerate(methods):
        definition = ET.SubElement(definitions, "UnitTest", id=str(index))
        ET.SubElement(definition, "TestMethod", className=name.rsplit(".", 1)[0], name=name.rsplit(".", 1)[1])
        ET.SubElement(results, "UnitTestResult", testId=str(index), testName="untrusted display name", outcome="Passed")
    summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
    ET.SubElement(summary, "Counters", total=str(len(methods)), executed=str(len(methods)),
                  passed=str(len(methods)), failed="0", error="0", notExecuted="0", aborted="0")
    return root


def native_documents(records, rid, core, mode="development", registry="https://registry.example", package=None, proof_version=4):
    """Synthetic transport documents only, never an execution/preparation claim."""
    from native_release_inputs import digest
    encode = lambda value: json.dumps(value, separators=(",", ":")).encode()
    catalog = b"synthetic catalog transport bytes"
    app = published_app.selection(rid, installed=mode == "published") if core is None else None
    material = dict(schemaVersion=3, app=dict(rid=rid, artifact=app)) if app else dict(schemaVersion=2, coreCommit=core, workingTreeClean=True)
    inputs = dict(material, packages=[dict(name=f"package-{index}.nupkg", sha256=str(index) * 64) for index in range(6)],
                  platformCatalog=dict(path="sunder-platform-contracts.v1.json", size=len(catalog), sha256=digest(catalog)))
    scope = dict(schemaVersion=1, rid=rid, profile=evidence.PROFILES[mode], installedAcceptance=mode == "published",
                 registryOrigin=None if mode == "development" else registry, archives=records)
    documents = {"native-family.trx": ET.tostring(trx(mode, package)), "scope/qualification.json": encode(scope),
                 "maker-inputs/inputs.json": encode(inputs), "maker-inputs/sunder-platform-contracts.v1.json": catalog}
    if mode == "development":
        if app and package in (None, evidence.OWNER):
            documents["apps/agent-snapshot/process.txt"] = b"agent-family-app: materialized-and-activated\nagent-family-app: retired\n"
    if mode == "development" and package in (None, evidence.MAKER_ID):
        owner = next(row for row in records if row["packageId"] == "younics.sunder.package.maker")
        journey = dict(schemaVersion=1, rid=rid, withoutAgent=True, profile=evidence.PROFILES[mode], installedAcceptance=False,
                       callerBoundary="supervised-native-app-rpc", makerViewAcceptance=False, operations=evidence.OPERATIONS,
                       makerArchiveSha256=owner["sha256"], archiveSha256="b" * 64, sourceLockSha256="c" * 64,
                       nuget={row["name"]: row["sha256"] for row in inputs["packages"]})
        documents["maker/journey.json"] = encode(journey)
        documents.update({"maker/" + operation + ".json": encode(dict(state="succeeded")) for operation in ("create", "build", "archive")})
    elif mode == "published":
        if package == evidence.OWNER:
            documents["apps/agent-uninstall/process.txt"] = (
                b"agent-family-app: materialized-and-activated\nagent-family-app: uninstall-applied\nagent-family-app: retired\n")
        proof = dict(schemaVersion=2, registryOrigin=scope["registryOrigin"],
            archiveDownloadAuthentication="anonymous",
            downloads=[dict(packageId=row["packageId"], version="2.0.0", archiveSha256=row["sha256"], size=row["size"]) for row in records],
            policies=[dict(package=dict(packageId=row["packageId"], version="2.0.0", packageContentIdentity="c" * 64),
                lease=dict(state="valid", errorCode=None, responseNonce="a" * 32, responseSha256="b" * 64,
                    watermark=dict(registryOrigin=registry, packagePolicyRevision=1, packagePolicyFingerprint="d" * 64),
                    observedAtUtc="2026-11-14T00:00:00Z", validUntilUtc="2026-11-15T00:00:00Z", clock={})) for row in records])
        if package:
            proof["schemaVersion"] = proof_version
            for phase, number, action in (("install", 1, "package-install"), ("reinstall", 3, "package-update")):
                rows = [dict(packageId=key, version="2.0.0", sourcePackageContentIdentity="c" * 64, actionKind=action,
                        provenance=dict(source="Registry", policy="ExplicitVersion", registryOrigin=registry + "/", sourcePackageId=key,
                                        requestedVersion="2.0.0", sourceIdentity="c" * 64),
                        targets=[dict(role="runtime", rid=rid)],
                        projections=[dict(kind=kind, rid=rid if kind == "runtime" else None, sha256="b"*64, size=2,
                             manifestSha256="e"*64, projectionContentIdentity="a"*64, sourceArchiveSha256=row["sha256"])
                             for kind in ("shared", "runtime")])
                         for row in records for key in [row["packageId"]]
                         if phase == "install" or key == package or (proof_version == 4 and key == evidence.OWNER)]
                proof[phase] = dict(resolutionFingerprint="d"*64, expectedResolutionFingerprint="d"*64, sourceFingerprint="e"*64,
                        rid=rid, runtimeSessionApplied=True, committedStamp=dict(runtimeInstanceId="f"*32, sessionGeneration=number),
                        consents=[], packages=rows)
                if proof_version == 4:
                    requested = sorted(row["packageId"] for row in records) if phase == "install" else [package]
                    proof[phase]["requestedPackages"] = [dict(packageId=key, version="2.0.0") for key in requested]
        documents["scope/published-registry.json"] = encode(proof)
        documents.update({package_id + "." + rid + ".json": encode(dict(syntheticTransportFixture=True)) for package_id in evidence.execution_ids(package)})
        for row in records:
            documents[row["packageId"] + "." + rid + ".json"] = encode(dict(measurement=dict(formatVersion=1, profile="dotnetWorker", rid=rid,
                archive=row["archive"], archiveBytes=row["size"], capturedAtUtc="2026-11-14T00:00:00Z", currentRidInstalledBytes=1,
                startupMilliseconds=1.5, workerProcessCount=1, workerRssBytes=1), thresholds=dict(expectedWorkerProcessCount=1,
                maximumArchiveBytes=row["size"], maximumCurrentRidInstalledBytes=1, maximumStartupMilliseconds=2.0, maximumWorkerRssBytes=1)))
    identity = dict(schemaVersion=2, appArtifact=app) if app else dict(schemaVersion=1, coreCommit=core)
    if package is not None: identity.update(schemaVersion=3, releasePackage=package)
    if package is not None and mode == "published": identity.update(schemaVersion=4, acceptanceSourceCommit="b"*40)
    receipt = dict(identity, rid=rid, profile=scope["profile"], installedAcceptance=scope["installedAcceptance"],
                   registryOrigin=scope["registryOrigin"], archives=records, tests=evidence.tests(mode, package),
                   reports=[dict(path=name, size=len(data), sha256=digest(data)) for name, data in sorted(documents.items())])
    return receipt, documents


class NativeRuntimeEvidenceTests(unittest.TestCase):
    def test_scoped_installed_receipts_cannot_be_reused_for_a_sibling_or_a_full_family(self):
        package = "younics.ai.agent.mcp"
        records = [dict(packageId=key, archive=key + ".2.0.0.sunderpkg", size=1, sha256="a" * 64)
                   for key in sorted(evidence.execution_ids(package))]
        rid = "linux-arm64"
        value, documents = native_documents(records, rid, None, "published", package=package)
        app = published_app.selection(rid, installed=True)
        evidence.validate_receipt(value, "published", records, app, rid, package=package)
        evidence.verify_reports(value, documents.__getitem__)
        for subject, inputs in ((None, records), ("younics.ai.agent.skills", records), (package, records[:1])):
            with self.subTest(subject=subject), self.assertRaises(ValueError):
                evidence.validate_receipt(value, "published", inputs, app, rid, package=subject)
        self.assertIn(evidence.CLASS + "." + evidence.MAKER, evidence.tests("development", evidence.MAKER_ID))
        self.assertNotIn(evidence.CLASS + "." + evidence.MAKER, evidence.tests("development", evidence.OWNER))
        with self.assertRaisesRegex(ValueError, "Installed-size budget"):
            evidence.check_installed_size(evidence.OWNER, 360003313)

    def test_scoped_install_rejects_download_only_and_unfenced_or_substituted_reinstall(self):
        from native_release_inputs import digest
        key = evidence.OWNER
        records = [dict(packageId=key, archive=key + ".2.0.0.sunderpkg", size=7, sha256="b"*64)]
        value, docs = native_documents(records, "osx-arm64", None, "published", package=key)
        evidence.verify_reports(value, docs.__getitem__)
        log = "apps/agent-uninstall/process.txt"
        stopped = docs[log].replace(b"agent-family-app: uninstall-applied\n", b"")
        incomplete = copy.deepcopy(value)
        next(row for row in incomplete["reports"] if row["path"] == log).update(size=len(stopped), sha256=digest(stopped))
        with self.assertRaisesRegex(ValueError, "uninstall evidence"):
            evidence.verify_reports(incomplete, {**docs, log: stopped}.__getitem__)
        name = "scope/published-registry.json"
        for change in (
            lambda p: (p.pop("reinstall"), p.pop("install"), p.update(schemaVersion=2)),
            lambda p: p["reinstall"].update(expectedResolutionFingerprint="b"*64),
            lambda p: p["reinstall"]["committedStamp"].update(sessionGeneration=1),
            lambda p: p["install"]["packages"][0]["provenance"].update(source="LocalArchive"),
            lambda p: p["reinstall"]["packages"][0]["projections"][0].update(sha256="e"*64),
            lambda p: p["install"]["packages"][0]["projections"].pop(),
        ):
            proof = json.loads(docs[name]); change(proof)
            data = json.dumps(proof).encode()
            edited = copy.deepcopy(value)
            next(row for row in edited["reports"] if row["path"] == name).update(size=len(data), sha256=digest(data))
            with self.assertRaises(ValueError):
                evidence.verify_reports(edited, {**docs, name: data}.__getitem__)

    def test_extension_reinstall_names_one_root_and_reapplies_only_its_tested_closure(self):
        import hub_install_evidence
        extension, rid = "younics.ai.agent.subagents", "linux-arm64"
        records = [dict(packageId=key, archive=key + ".2.0.0.sunderpkg", size=7, sha256=digit * 64)
                   for key, digit in ((evidence.OWNER, "a"), (extension, "b"))]
        def report(version):
            receipt, docs = native_documents(records, rid, None, "published", package=extension, proof_version=version)
            evidence.verify_reports(receipt, docs.__getitem__)
            return json.loads(docs["scope/published-registry.json"])
        legacy, current = report(3), report(4)
        self.assertEqual([extension], [row["packageId"] for row in legacy["reinstall"]["packages"]])
        for change in (
            lambda op: op["packages"].pop(0),
            lambda op: op["packages"].append(op["packages"][0]),
            lambda op: op["packages"][0].update(packageId="younics.ai.agent.tools.shell"),
            lambda op: op["packages"][0]["provenance"].update(policy="TransitiveDependency"),
            lambda op: op["packages"][0]["projections"][0].update(sha256="c"*64),
            lambda op: op["requestedPackages"].append(dict(packageId=evidence.OWNER, version="2.0.0")),
            lambda op: op["requestedPackages"][0].update(version="2.0.1"),
        ):
            with self.subTest(change=change):
                edited = copy.deepcopy(current)
                change(edited["reinstall"])
                with self.assertRaises(ValueError):
                    hub_install_evidence.verify(edited, rid, extension, evidence.OWNER)

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
                receipt, documents = native_documents(records, rid, None, "published")
                for name, data in documents.items():
                    path = metrics / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                (metrics / evidence.RECEIPT).write_bytes(compact(receipt))
                evidence.export_installed(metrics, handoff / ("native-installed-" + rid + ".tar.gz"))
            result = evidence.installed_set(handoff, records, None, "https://registry.example")
            self.assertEqual(set(evidence.RIDS), {row["rid"] for row in result["nativeRuns"]})
            for records_input, core, registry in (([], None, "https://registry.example"),
                (records, "b" * 40, "https://registry.example"), (records, None, "https://other.example")):
                with self.assertRaises(ValueError): evidence.installed_set(handoff, records_input, core, registry)
            path = handoff / ("native-installed-" + evidence.RIDS[0] + ".json")
            original = path.read_bytes()
            path.unlink()
            with self.assertRaises(ValueError): evidence.installed_set(handoff, records, None, "https://registry.example")
            path.write_bytes(original)
            value = json.loads(original)
            value["profile"] = evidence.PROFILES["development"]
            path.write_bytes(compact(value))
            with self.assertRaises(ValueError): evidence.installed_set(handoff, records, None, "https://registry.example")

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
