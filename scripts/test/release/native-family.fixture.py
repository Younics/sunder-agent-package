#!/usr/bin/env python3
"""Transport/orchestration regressions; synthetic bytes and mocked MSBuild, no native attestation.

Fixtures use the published Core V1 input/selection shapes. Opaque receipt contents
are carried unchanged; Core's own prebuilt suite owns receipt/compiler validation.
"""

import copy
import gzip
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import native_release_inputs as inputs

SCRIPT = Path(__file__).resolve().parents[2] / "release"
SPEC = importlib.util.spec_from_file_location("native_family", SCRIPT / "native-family.py")
family = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(family)
VERSION = "2.0.0"
VERSIONS = [VERSION, "a" * 40, "sdk/v1.1.0", "c" * 40, "1.1.0"]


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else inputs.compact(data))


def zip_bytes(path, files):
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(files.items()):
            archive.writestr(zipfile.ZipInfo(name), data)


def file_list(files):
    return [{"path": name, "bytes": len(data), "sha256": inputs.digest(data)} for name, data in sorted(files.items())]


def create_producer(root):
    repository = root / "native checkout"
    rows = [{"key": str(i), "packageId": package_id, "artifactType": "sunderpkg",
             "projectPath": "src/P" + str(i) + "/P" + str(i) + ".csproj"}
            for i, package_id in enumerate(family.FAMILY_PACKAGE_IDS)]
    write(repository / "packages.json", {"packages": rows})
    for row in rows:
        directory = repository / Path(row["projectPath"]).parent
        write(directory / "Source.cs", b"// reviewed source\n")
        write(repository / row["projectPath"], b"<Project />")
        write(directory / "sunder-contracts.owned.json", {"schemaVersion": 1, "authorityKind": "package",
              "authorityId": row["packageId"], "packageId": row["packageId"], "contracts": {}})
        write(directory / "sunder-contracts.lock.json", {})
    for rid in inputs.RIDS:
        archives = root / "smoke archives" / rid
        metrics = root / "metrics" / rid
        archives.mkdir(parents=True)
        for row in rows:
            project = row["projectPath"]
            name, package_id = Path(project).stem, row["packageId"]
            directory = repository / Path(project).parent
            has_app = name == "P0"
            target = {"role": "runtime", "rid": rid, "kind": "worker", "entryPoint": "lib/" + name + ".dll",
                      "targetFramework": "net10.0", "executionRuntime": {"id": "sunder.runtime.dotnet",
                      "versionRange": ">=10.0.0 <11.0.0", "moduleAbi": "sunder.dotnet-worker-module.v1"}}
            manifest = {"id": package_id, "version": VERSION, "targets": [target]}
            base = "payload/runtime/" + rid + "/lib/" + name
            config_base = ("payload/runtime/shared/lib/" if has_app else "payload/shared/lib/") + name
            common = "payload/runtime/shared/lib/Common.dll" if has_app else "payload/shared/lib/Common.dll"
            files = {base + ".dll": b"retained managed Worker bytes " + rid.encode(),
                     config_base + ".runtimeconfig.json": b'{"runtimeOptions":{"tfm":"net10.0"}}',
                     config_base + ".deps.json": inputs.compact({"rid": rid}), common: b"shared exact bytes"}
            # Force the bounded POSIX path-extension round trip, not just short tar names.
            files["payload/runtime/" + rid + "/" + "/".join(["long-segment" * 3] * 5) + "/asset.bin"] = b"long path bytes"
            if has_app:
                manifest["targets"].append({"role": "app", "rid": rid, "kind": "dotnet", "targetFramework": "net10.0"})
                files["payload/app/shared/lib/P0.App.dll"] = b"retained App bytes " + rid.encode()
            files["manifest/sunder-package.json"] = inputs.compact(manifest)
            files["manifest/content-index.json"] = inputs.compact({"schemaVersion": 1, "files": [
                {"path": f["path"], "sha256": f["sha256"], "size": f["bytes"]} for f in file_list(files)]})
            bundle = directory / "bin/Release/net10.0/sunder-targets" / rid
            for filename, data in files.items():
                write(bundle / "sunder-dev" / filename, data)
            baseline = str(Path(project).parent / "sunder-contracts.owned.json")
            lock = str(Path(project).parent / "sunder-contracts.lock.json")
            fingerprint = inputs.digest(b"fixture compilation frame")
            receipt = {"schemaVersion": 1, "producer": {"name": "Sunder.Package.Build", "version": "1.1.0", "language": "csharp"},
                       "inputFingerprint": fingerprint, "lockSha256": inputs.digest((repository / lock).read_bytes()),
                       "packageId": package_id, "packageVersion": VERSION, "contracts": [], "packageArtifacts": [], "bundles": [],
                       "packageArtifactImports": [], "packageApiImports": [], "providerImports": [], "platformImports": [], "providers": [], "uses": []}
            roles = []
            for role in (["worker", "app"] if has_app else ["worker"]):
                retained_receipt = dict(receipt, role="app") if role == "app" else receipt
                roles.append({"role": role, "libraries": [], "generatedInputs": [{"path": "generated.cs", "bytes": 0, "sha256": inputs.digest(b"")}],
                              "references": [], "runtimeReferences": [], "receipt": inputs.compact(retained_receipt).decode()})
            frame = {"packageId": package_id, "packageVersion": VERSION, "project": project, "projectIdentity": name,
                     "configuration": "Release", "targetFramework": "net10.0", "runtimeIdentifier": rid, "assemblyName": name,
                     "ownedBaseline": baseline, "ownedBaselineSha256": inputs.digest((repository / baseline).read_bytes()),
                     "contractLock": lock, "contractLockSha256": inputs.digest((repository / lock).read_bytes()), "properties": {}, "items": [],
                     "sources": [{"path": str(Path(project).parent / "Source.cs"), "bytes": len(b"// reviewed source\n"), "sha256": inputs.digest(b"// reviewed source\n")}],
                     "toolset": [], "externalInputs": []}
            evidence = inputs.compact({"schemaVersion": 1, "inputs": frame,
                                      "rolePlan": {"inputFingerprint": fingerprint, "nativePlanSha256": inputs.digest(b"native plan"),
                                                   "hasApp": has_app, "roles": roles}, "leaf": "sunder-dev", "files": file_list(files)})
            write(bundle / inputs.ROLE_INPUTS, evidence)
            write(bundle / (inputs.ROLE_INPUTS + ".sha256"), (inputs.digest(evidence) + "\n").encode())
            archive = archives / (package_id + "." + VERSION + ".sunderpkg")
            zip_bytes(archive, files)
            metric = {"measurement": {"formatVersion": 1, "profile": "dotnetWorker", "rid": rid, "archive": archive.name,
                                      "archiveBytes": archive.stat().st_size, "capturedAtUtc": "2026-10-05T01:02:03Z",
                                      "currentRidInstalledBytes": 10000, "startupMilliseconds": 1.5, "workerProcessCount": 1, "workerRssBytes": 100000},
                      "thresholds": {"expectedWorkerProcessCount": 1, "maximumArchiveBytes": 1000000, "maximumCurrentRidInstalledBytes": 20000,
                                     "maximumStartupMilliseconds": 20, "maximumWorkerRssBytes": 200000}}
            write(metrics / (package_id + "." + rid + ".json"), metric)
    return repository


def mock_dotnet(args):
    with open(os.environ["NATIVE_FIXTURE_LOG"], "a", encoding="utf-8") as log:
        log.write(json.dumps(args) + "\n")
    assert args[0] == "msbuild" and "-t:PackSunderPackage" in args
    assert "-p:SunderPackageUsePrebuiltLeaves=true" in args and "-p:BuildProjectReferences=false" in args
    assert not any(arg.lower() in ("build", "compile", "restore", "-restore", "-t:build", "-t:getsunderpackageroleinputs") for arg in args)
    props = dict(arg[3:].split("=", 1) for arg in args if arg.startswith("-p:"))
    assert props["Configuration"] == "Release" and props["ContinuousIntegrationBuild"] == "true"
    assert props["SunderPackageComposeRuntimeIdentifiers"].split("%3B") == list(inputs.RIDS)
    if props["SunderUseSourceSdk"] == "true":
        assert props["SunderPackageBuildTaskSourceIsImmutable"] == "true" and props["SunderPackageBuildTaskAssembly"]
    if os.environ.get("NATIVE_FIXTURE_CORE_FAILURE"):
        sys.exit(31)  # No retry/fallback after Core rejection.
    selection_path = Path(props["SunderPackagePrebuiltRoleInputsFile"])
    selection_bytes = selection_path.read_bytes()
    selection = json.loads(selection_bytes)
    assert inputs.compact(selection) == selection_bytes  # no BOM/LF/reordered members
    assert list(selection) == ["schemaVersion", "inputs"] and selection["schemaVersion"] == 1
    assert len(selection["inputs"]) == 6
    source = Path(props["SunderPackageRoleInputSourceRoot"])
    files, targets = {}, []
    package_id = None
    for selected in selection["inputs"]:
        assert list(selected) == ["runtimeIdentifier", "path", "sha256"]
        evidence_path = selection_path.parent / selected["path"]
        data = evidence_path.read_bytes()
        assert inputs.digest(data) == selected["sha256"]
        evidence = json.loads(data)
        package_id = evidence["inputs"]["packageId"]
        # An explicit mock of Core's current-source refusal, not a production bypass.
        for record in evidence["inputs"]["sources"]:
            if inputs.digest((source / record["path"]).read_bytes()) != record["sha256"]:
                sys.exit(32)
        leaf = evidence_path.parent / "sunder-dev"
        targets.extend(json.loads((leaf / "manifest/sunder-package.json").read_bytes())["targets"])
        for record in evidence["files"]:
            if record["path"] in inputs.METADATA:
                continue
            name = record["path"]
            # Exercise Core's common-file storage projection on final verification.
            if name.endswith("/Common.dll"):
                name = "payload/runtime/shared/lib/Common.dll"
            elif name.endswith(".deps.json"):
                name = "payload/runtime/" + selected["runtimeIdentifier"] + "/lib/" + Path(name).name
            elif name.endswith("/P0.App.dll"):
                name = "payload/app/" + selected["runtimeIdentifier"] + "/lib/P0.App.dll"
            data = (leaf / record["path"]).read_bytes()
            assert name not in files or files[name] == data
            files[name] = data
    files["manifest/sunder-package.json"] = inputs.compact({"id": package_id, "version": VERSION, "targets": targets})
    files["manifest/content-index.json"] = inputs.compact({"schemaVersion": 1, "files": [
        {"path": f["path"], "sha256": f["sha256"], "size": f["bytes"]} for f in file_list(files)]})
    zip_bytes(Path(props["SunderPackageOutputPath"]), files)


class NativeFamilyFixture(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scope = tempfile.TemporaryDirectory(prefix="sunder-native-inputs-")
        cls.root = Path(cls.scope.name).resolve()
        cls.template = cls.root / "template"
        repository = create_producer(cls.template)
        for rid in inputs.RIDS:
            subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "record", rid, *VERSIONS,
                            str(cls.template / "smoke archives" / rid), str(cls.template / "metrics" / rid),
                            str(cls.template / "handoff"), str(repository)], check=True, capture_output=True, text=True)

    @classmethod
    def tearDownClass(cls):
        cls.scope.cleanup()

    def setUp(self):
        self.work = self.root / self._testMethodName
        shutil.copytree(self.template, self.work)
        # Relocate the checkout and remove every native producer output.
        self.repository = self.work / "relocated checkout"
        (self.work / "native checkout").rename(self.repository)
        for directory in (self.repository / "src").iterdir():
            shutil.rmtree(directory / "bin")
        self.handoff = self.work / "handoff"
        self.output = self.work / "family"
        self.log = self.work / "dotnet-calls.jsonl"
        mock_bin = self.work / "mock bin"
        mock_bin.mkdir()
        shim = mock_bin / "dotnet"
        shim.write_text("#!/bin/sh\nexec " + shlex.quote(sys.executable) + " -B " + shlex.quote(str(Path(__file__).resolve())) + " --mock-dotnet \"$@\"\n")
        shim.chmod(0o755)
        self.env = dict(os.environ, PATH=str(mock_bin) + os.pathsep + os.environ["PATH"], NATIVE_FIXTURE_LOG=str(self.log),
                        SUNDER_NATIVE_USE_SOURCE_SDK="false", SUNDER_NATIVE_TASK_ASSEMBLY="")

    def assemble(self, success=True):
        result = subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "assemble", str(self.handoff), str(self.output),
                                 *VERSIONS, str(self.repository)], env=self.env, capture_output=True, text=True)
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, "Accepted invalid input")
        return result

    def verify(self, success=True):
        result = subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "verify", str(self.output / "native-test-evidence.json"),
                                 str(self.output), str(self.repository / "packages.json"), *VERSIONS], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)

    def mutate_tar(self, change):
        rid = inputs.RIDS[0]
        bundle = self.handoff / ("native-leaves-" + rid + ".tar.gz")
        with tarfile.open(bundle, "r:gz") as archive:
            entries = [(copy.copy(entry), archive.extractfile(entry).read()) for entry in archive]
        entries = change(entries)
        with tarfile.open(bundle, "w:gz", format=tarfile.PAX_FORMAT) as archive:
            for entry, data in entries:
                archive.addfile(entry, io.BytesIO(data) if entry.isreg() else None)
        path = self.handoff / ("native-leaf-evidence-" + rid + ".json")
        run = json.loads(path.read_bytes())
        run["bundle"] = inputs.file_record(bundle, maximum=inputs.MAX_RUN)
        write(path, run)  # Even a recomputed outer bundle hash cannot bypass closure checks.

    def test_relocated_six_rid_fanin_preserves_worker_app_shared_bytes_and_provenance(self):
        before = {path.relative_to(self.handoff): inputs.digest(path.read_bytes()) for path in self.handoff.rglob("*") if path.is_file()}
        self.assemble()
        self.verify()
        self.assertEqual(len(self.log.read_text().splitlines()), 15)
        document = json.loads((self.output / "native-test-evidence.json").read_bytes())
        self.assertEqual(document["schemaVersion"], 3)
        self.assertEqual(len(document["finalArtifacts"]), 15)
        for run in document["nativeRuns"]:
            for package in run["packages"]:
                lock = self.repository / Path(package["workerProject"]).parent / "sunder-contracts.lock.json"
                self.assertEqual({"lockSha256": inputs.digest(lock.read_bytes()), "roleUses": []}, package["sourceRoleUses"])
        after = {path.relative_to(self.handoff): inputs.digest(path.read_bytes()) for path in self.handoff.rglob("*") if path.is_file()}
        self.assertEqual(before, after)
        self.assertFalse(list(self.repository.rglob("bin")))

    def test_source_use_projection_cannot_be_changed_apart_from_qualified_receipt(self):
        path = self.handoff / ("native-leaf-evidence-" + inputs.RIDS[0] + ".json")
        run = json.loads(path.read_bytes())
        run["packages"][0]["sourceRoleUses"]["lockSha256"] = "a" * 64
        write(path, run)
        result = self.assemble(success=False)
        self.assertIn("Native source role uses differ from the qualified Worker receipt", result.stderr)
        self.assertFalse(self.log.exists(), "Pack ran before source-use evidence was authenticated")

    def test_bundle_rejects_traversal_links_duplicates_extras_missing_and_corruption_before_pack(self):
        original = self.handoff / ("native-leaves-" + inputs.RIDS[0] + ".tar.gz")
        original_bytes = original.read_bytes()
        evidence = self.handoff / ("native-leaf-evidence-" + inputs.RIDS[0] + ".json")
        evidence_bytes = evidence.read_bytes()
        for mutation in ("traversal", "rooted", "drive", "backslash", "device", "symlink", "hardlink", "directory", "duplicate", "case", "extra", "missing", "corrupt"):
            with self.subTest(mutation=mutation):
                original.write_bytes(original_bytes)
                evidence.write_bytes(evidence_bytes)

                def change(entries):
                    header, data = entries[0]
                    if mutation == "missing":
                        return entries[1:]
                    if mutation == "duplicate":
                        return entries + [entries[0]]
                    if mutation == "extra":
                        extra = tarfile.TarInfo("unreviewed.bin")
                        extra.size = 1
                        return entries + [(extra, b"x")]
                    if mutation in ("symlink", "hardlink", "directory"):
                        header.type = {"symlink": tarfile.SYMTYPE, "hardlink": tarfile.LNKTYPE, "directory": tarfile.DIRTYPE}[mutation]
                        header.linkname = "../../outside" if mutation != "directory" else ""
                        header.size = 0
                        entries[0] = (header, b"")
                    elif mutation == "corrupt":
                        entries[0] = (header, bytes([data[0] ^ 1]) + data[1:])
                    else:
                        header.name = {"traversal": "../outside", "rooted": "/outside", "drive": "C:/outside", "backslash": "a\\b",
                                       "device": "NUL.txt", "case": header.name.upper()}[mutation]
                        header.pax_headers = {}
                    return entries

                self.mutate_tar(change)
                self.assemble(success=False)
                self.assertFalse(self.log.exists(), "MSBuild ran before complete transport validation")
                self.assertFalse(list(self.output.glob(".native-fanin-*")), "Rejected transport was extracted")
        self.assertFalse((self.work / "outside").exists())

    def test_qualification_sidecar_identity_and_file_closure_are_bound_before_extraction(self):
        original = self.handoff / ("native-leaves-" + inputs.RIDS[0] + ".tar.gz")
        original_bytes = original.read_bytes()
        evidence_path = self.handoff / ("native-leaf-evidence-" + inputs.RIDS[0] + ".json")
        original_run = json.loads(evidence_path.read_bytes())
        for mutation in ("sidecar", "qualification-hash", "rid", "version", "project", "role", "file"):
            with self.subTest(mutation=mutation):
                original.write_bytes(original_bytes)
                run = copy.deepcopy(original_run)
                package = run["packages"][0]
                name = package["qualification"]["path"]

                def change(entries):
                    for index, (header, data) in enumerate(entries):
                        if mutation == "sidecar" and header.name == name + ".sha256":
                            entries[index] = (header, b"f" * 64 + b"\n")
                        elif header.name == name and mutation != "sidecar":
                            doc = json.loads(data)
                            if mutation == "qualification-hash":
                                data += b" "
                            else:
                                if mutation in ("rid", "version", "project"):
                                    doc["inputs"][{"rid": "runtimeIdentifier", "version": "packageVersion", "project": "project"}[mutation]] = "wrong"
                                elif mutation == "role":
                                    doc["rolePlan"]["roles"] = []
                                else:
                                    doc["files"][0]["sha256"] = "f" * 64
                                data = inputs.compact(doc)
                                package["qualification"].update(sha256=inputs.digest(data), size=len(data))
                            header.size = len(data)
                            entries[index] = (header, data)
                    # Re-sign the outer declarations in the negative fixture; semantic mismatch must still fail.
                    if mutation not in ("sidecar", "qualification-hash"):
                        for index, (header, _) in enumerate(entries):
                            if header.name == name + ".sha256":
                                entries[index] = (header, (package["qualification"]["sha256"] + "\n").encode())
                    write(evidence_path, run)
                    return entries

                write(evidence_path, run)
                self.mutate_tar(change)
                self.assemble(success=False)
                self.assertFalse(self.log.exists())

    def test_missing_mixed_rid_and_failed_metrics_refuse_all_pack_calls(self):
        path = self.handoff / ("native-leaf-evidence-" + inputs.RIDS[-1] + ".json")
        original = path.read_bytes()
        for mutation in ("missing", "mixed", "metric", "schema2", "duplicate-json"):
            with self.subTest(mutation=mutation):
                path.write_bytes(original)
                doc = json.loads(original)
                if mutation == "missing":
                    path.unlink()
                elif mutation == "duplicate-json":
                    path.write_bytes(original.replace(b'"schemaVersion":3', b'"schemaVersion":3,"SchemaVersion":3', 1))
                else:
                    if mutation == "mixed":
                        doc["rid"] = inputs.RIDS[0]
                    elif mutation == "metric":
                        doc["packages"][0]["metrics"]["measurement"]["workerProcessCount"] = 2
                    else:
                        doc["schemaVersion"] = 2
                    write(path, doc)
                self.assemble(success=False)
                self.assertFalse(self.log.exists())

    def test_extra_fanin_inputs_and_tampered_metric_copies_refuse_pack(self):
        unexpected = self.handoff / "native-leaves-linux-musl-x64.tar.gz"
        write(unexpected, b"unselected input")
        self.assemble(success=False)
        self.assertFalse(self.log.exists())
        unexpected.unlink()
        metric = next((self.handoff / "metrics").iterdir())
        metric.write_bytes(b"changed metric")
        self.assemble(success=False)
        self.assertFalse(self.log.exists())

    def test_native_selection_and_preflight_do_not_freeze_or_build(self):
        native = self.work / "native selection checkout"
        shutil.copytree(self.template / "native checkout", native)
        row = family.inventory(native)[0]
        baseline = native / Path(row["projectPath"]).parent / "sunder-contracts.owned.json"
        before = baseline.read_bytes()
        result = subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "producer-preflight", str(native), VERSION],
                                env=self.env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        result = subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "select-native", str(native), row["packageId"], inputs.RIDS[0], VERSION],
                                env=self.env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        selection = Path(result.stdout.strip())
        data = selection.read_bytes()
        document = json.loads(data)
        self.assertEqual(inputs.compact(document), data)
        self.assertEqual(document, {"schemaVersion": 1, "inputs": [{"runtimeIdentifier": inputs.RIDS[0], "path": inputs.ROLE_INPUTS,
                          "sha256": inputs.digest((selection.parent / inputs.ROLE_INPUTS).read_bytes())}]})
        self.assertFalse(self.log.exists())
        self.assertEqual(baseline.read_bytes(), before)
        document = json.loads(before)
        document["contracts"] = {"api": {"contractId": "sunder.fixture.api", "current": {"state": "draft", "exposure": "public"}, "history": []}}
        write(baseline, document)
        result = subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "producer-preflight", str(native), VERSION],
                                env=self.env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Freeze every public owned baseline", result.stderr)
        self.assertFalse(self.log.exists())
        document["contracts"]["api"]["current"]["exposure"] = "package"
        write(baseline, document)
        with self.assertRaisesRegex(ValueError, "private-baseline-migration-required"):
            family.frozen_baselines(native, [row], VERSION)
        document["retiredPrivateContracts"] = document["contracts"]
        document["contracts"] = {}
        write(baseline, document)
        retired = baseline.read_bytes()
        family.frozen_baselines(native, [row], VERSION)
        self.assertEqual(retired, baseline.read_bytes())
        baseline.unlink()
        family.frozen_baselines(native, [row], VERSION)
        self.assertFalse(baseline.exists())
        result = subprocess.run([sys.executable, "-B", str(SCRIPT / "native-family.py"), "producer-preflight", str(native), "../invalid"],
                                env=self.env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("explicit release version", result.stderr)

    def test_qualified_absent_baseline_marker_preserves_exact_package_version(self):
        row = family.inventory(self.template / "native checkout")[0]
        rid = inputs.RIDS[0]
        evidence = self.template / "native checkout" / Path(row["projectPath"]).parent / "bin/Release/net10.0/sunder-targets" / rid / inputs.ROLE_INPUTS
        document = json.loads(evidence.read_bytes())
        frame = document["inputs"]
        frame["ownedBaseline"] = "baseline-absent:" + frame["ownedBaseline"]
        frame["ownedBaselineSha256"] = inputs.digest(b"")
        version = "2.0.0-beta.1+Build.7"
        self.assertEqual(version, family.identity(version, VERSIONS[1], VERSIONS[2], VERSIONS[3], VERSIONS[4])["version"])
        for invalid in ("2.0.0-01", "2.0.0+", "2.0.0+" + "x" * 251):
            with self.assertRaisesRegex(ValueError, "explicit release version"):
                family.release_version(invalid)
        frame["packageVersion"] = version
        data = inputs.compact(document)
        actual, _ = inputs.qualification(data, row["packageId"], row["projectPath"], rid, version)
        self.assertEqual(document, actual)
        with self.assertRaisesRegex(ValueError, "Mixed/stale"):
            inputs.qualification(data, row["packageId"], row["projectPath"], rid, version.lower())
        for marker, digest in (("baseline-absent:../escape.json", inputs.digest(b"")),
                               (frame["ownedBaseline"], "f" * 64), ("baseline-absent:", inputs.digest(b""))):
            frame.update(ownedBaseline=marker, ownedBaselineSha256=digest)
            with self.assertRaises(ValueError):
                inputs.qualification(inputs.compact(document), row["packageId"], row["projectPath"], rid, version)

    def test_core_failure_and_stale_source_preserve_prior_family_and_resume_without_rebuild(self):
        self.assemble()
        before = {p.name: p.read_bytes() for p in self.output.iterdir()}
        source = self.repository / "src/P0/Source.cs"
        original = source.read_bytes()
        source.write_bytes(b"// stale source\n")
        self.assemble(success=False)
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.output.iterdir()})
        source.write_bytes(original)
        self.env["NATIVE_FIXTURE_CORE_FAILURE"] = "1"
        count = len(self.log.read_text().splitlines())
        self.assemble(success=False)
        self.assertEqual(len(self.log.read_text().splitlines()), count + 1, "Core failure was retried/fell back")
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.output.iterdir()})
        del self.env["NATIVE_FIXTURE_CORE_FAILURE"]
        self.assemble()
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.output.iterdir()})

    def test_final_verifier_rejects_rehashed_tampered_native_bytes_and_extra_archives(self):
        self.assemble()
        archive = next(self.output.glob("*.sunderpkg"))
        with zipfile.ZipFile(archive) as packed:
            files = {name: packed.read(name) for name in packed.namelist()}
        original = archive.read_bytes()
        module = next(name for name in files if name.endswith(".dll"))
        files[module] += b"changed after native test"
        zip_bytes(archive, files)
        document_path = self.output / "native-test-evidence.json"
        document = json.loads(document_path.read_bytes())
        old_document = document_path.read_bytes()
        document["finalArtifacts"] = [inputs.file_record(archive) if r["path"] == archive.name else r for r in document["finalArtifacts"]]
        write(document_path, document)
        self.verify(success=False)
        archive.write_bytes(original)
        document_path.write_bytes(old_document)
        write(self.output / "obsolete.nupkg", b"extra")
        self.verify(success=False)

    def test_final_verifier_rejects_ambiguous_shared_layers_and_changed_app_role(self):
        self.assemble()
        archive = self.output / ("younics.ai.agent." + VERSION + ".sunderpkg")
        original = archive.read_bytes()
        document_path = self.output / "native-test-evidence.json"
        document_bytes = document_path.read_bytes()
        for mutation in ("ambiguous", "app"):
            with self.subTest(mutation=mutation):
                with zipfile.ZipFile(io.BytesIO(original)) as packed:
                    files = {name: packed.read(name) for name in packed.namelist()}
                if mutation == "ambiguous":
                    files["payload/shared/lib/Common.dll"] = files["payload/runtime/shared/lib/Common.dll"]
                else:
                    manifest = json.loads(files["manifest/sunder-package.json"])
                    manifest["targets"] = [target for target in manifest["targets"] if target["role"] != "app"]
                    files["manifest/sunder-package.json"] = inputs.compact(manifest)
                zip_bytes(archive, files)
                document = json.loads(document_bytes)
                document["finalArtifacts"] = [inputs.file_record(archive) if r["path"] == archive.name else r for r in document["finalArtifacts"]]
                write(document_path, document)
                self.verify(success=False)

    def test_workspace_mode_requires_and_passes_complete_retained_immutable_toolset(self):
        self.env["SUNDER_NATIVE_USE_SOURCE_SDK"] = "true"
        self.assemble(success=False)
        self.assertFalse(self.log.exists())
        tools = self.work / "retained tasks"
        self.env["SUNDER_NATIVE_TASK_ASSEMBLY"] = str(tools / "Sunder.Package.Build.dll")
        for name in ("Sunder.Package.Build.dll", "Sunder.Package.Build.Editor.dll", "Sunder.Package.Format.dll", "Sunder.Sdk.dll",
                     "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll", "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
                     "Microsoft.Extensions.Logging.Abstractions.dll"):
            write(tools / name, b"mock immutable tool bytes")
        if sys.platform == "darwin":
            self.env["SUNDER_NATIVE_TASK_ASSEMBLY"] = self.env["SUNDER_NATIVE_TASK_ASSEMBLY"].replace("/private/var/", "/var/", 1)
        self.assemble()

    def test_json_tar_and_filesystem_bounds(self):
        for data in (b"[" * 17 + b"0" + b"]" * 17, b'{"A":1,"a":2}', b"[NaN]", b"\xef\xbb\xbf{}"):
            with self.subTest(data=data), self.assertRaises(ValueError):
                inputs.parse(data)
        with self.assertRaises(ValueError):
            inputs.parse(b"[0,0,0,0]", budget=3)
        path = self.work / "oversized-pax.tar.gz"
        header = tarfile.TarInfo("pax")
        header.type, header.size = tarfile.XHDTYPE, 2 ** 30
        with gzip.open(path, "wb") as stream:
            stream.write(header.tobuf())
        with self.assertRaisesRegex(ValueError, "PAX"):
            inputs.scan_tar(path, {}, lambda *_: None)
        leaf = self.work / "linked leaf"
        leaf.mkdir()
        (leaf / "link").symlink_to(self.repository / "packages.json")
        with self.assertRaisesRegex(ValueError, "Linked"):
            inputs.tree(leaf)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--mock-dotnet":
        mock_dotnet(sys.argv[2:])
    else:
        unittest.main(verbosity=2)
