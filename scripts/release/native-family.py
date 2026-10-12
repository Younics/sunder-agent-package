#!/usr/bin/env python3
"""Agent schema-3 native provenance and Core V1 prebuilt Pack orchestration."""

import math
import os
from contextlib import contextmanager
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile
import zipfile
import zlib

from agent_release import owned_contracts, has_public_evidence, version as semantic_version
from native_release_inputs import (
    MAX_DOCUMENT, MAX_LEAF, MAX_RUN, RIDS, ROLE_INPUTS, compact, digest, file_record,
    file_set, integer, json_file, keys, parse, projection, qualification, read_bytes, regular,
    relative, require, scan_tar, sha, tree, validate_manifest, verify_archive, write_tar,
)

SCHEMA = 3
MAX_PROVENANCE = 256 * 1024 * 1024


def inventory(repository):
    rows = json_file(repository / "packages.json")["packages"]
    require(len(rows) == 15 and all(row["artifactType"] == "sunderpkg" for row in rows)
            and len({row["packageId"].lower() for row in rows}) == 15
            and len({row["projectPath"].lower() for row in rows}) == 15,
            "Expected exactly 15 unique runtime packages")
    for row in rows:
        require(re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)*", row["packageId"]), "Invalid package ID")
        relative(row["projectPath"], 1024, 32)
    return sorted(rows, key=lambda row: row["key"])


def release_version(value):
    try:
        semantic_version(value)
    except ValueError as error:
        raise ValueError("Expected an explicit release version (full strict SemVer)") from error


def identity(version, agent_commit, core_tag, core_commit, core_version):
    for value in (version, core_version):
        release_version(value)
    require(all(re.fullmatch(r"[0-9a-f]{40}", commit) for commit in (agent_commit, core_commit)),
            "Agent and Core commits must be full lowercase Git SHAs")
    require(core_tag == "sdk/v" + core_version, "Core tag and package version do not agree")
    return {"schemaVersion": SCHEMA, "version": version, "agentCommit": agent_commit,
            "core": {"tag": core_tag, "commit": core_commit, "packageVersion": core_version}}


def artifact_output(path, repository):
    output = Path(path).resolve()
    require(output != repository, "Artifact output cannot replace the source root")
    if repository in output.parents:
        require(output.relative_to(repository).parts[0] == "artifacts",
                "Keep native handoffs under artifacts/ or outside the reviewed source tree")
    output.mkdir(parents=True, exist_ok=True)
    regular(output, directory=True)
    return output


def tool_properties(repository):
    mode = os.environ.get("SUNDER_NATIVE_USE_SOURCE_SDK", "false")
    require(mode in ("true", "false"), "SUNDER_NATIVE_USE_SOURCE_SDK must be true or false")
    assembly = os.environ.get("SUNDER_NATIVE_TASK_ASSEMBLY", "")
    require(mode != "true" or assembly, "Workspace native operations require SUNDER_NATIVE_TASK_ASSEMBLY: a retained complete immutable Core toolset")
    result = ["-p:SunderUseSourceSdk=" + mode, "-p:ContinuousIntegrationBuild=true",
              "-p:SunderPackageRoleInputSourceRoot=" + str(repository)]
    if mode == "false":
        result.append("-p:RestoreLockedMode=true")
    if assembly:
        assembly = Path(os.path.abspath(assembly))
        # Normalize the explicitly selected root (e.g. macOS /var -> /private/var),
        # while still rejecting links/reparse points for every tool file beneath it.
        assembly = assembly.parent.resolve() / assembly.name
        require(assembly.name == "Sunder.Package.Build.dll", "Select the retained Sunder.Package.Build.dll")
        for name in ("Sunder.Package.Build.dll", "Sunder.Package.Build.Editor.dll", "Sunder.Package.Format.dll", "Sunder.Sdk.dll",
                     "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
                     "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "Microsoft.Extensions.Logging.Abstractions.dll"):
            regular(assembly.parent / name)
        result += ["-p:SunderPackageBuildTaskAssembly=" + str(assembly), "-p:SunderPackageBuildTaskSourceIsImmutable=true"]
    return result


def frozen_baselines(repository, rows, version):
    release_version(version)
    for row in rows:
        path = repository / Path(row["projectPath"]).parent / "sunder-contracts.owned.json"
        regular(path.parent, directory=True)
        require(not path.is_symlink(), "Owned baseline cannot be a link")
        if not path.exists():
            # Core's source/qualified role checks reject lost public evidence.
            # Private-only authoring has no independent Accept/Freeze file.
            continue
        baseline = json_file(path)
        for contract in owned_contracts(baseline, row["packageId"]).values():
            current = contract.get("current")
            if current is not None:
                require(has_public_evidence(contract),
                        "rpc-authoring.private-baseline-migration-required: run MigratePrivateSunderContracts before native Build: " + str(path))
                require(current["state"] in ("release-frozen", "published"),
                        "Freeze every public owned baseline for this release before native Build: " + str(path))


def record_shape(record, path, maximum=MAX_LEAF):
    keys(record, "path sha256 size")
    require(record["path"] == path, "Unexpected artifact locator: " + str(record["path"]))
    relative(path)
    sha(record["sha256"])
    integer(record["size"], maximum, 1)


def validate_metrics(package, rid):
    metric = package["metrics"]
    keys(metric, "measurement thresholds")
    measure = metric["measurement"]
    threshold = metric["thresholds"]
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


def leaf_record(package_id, project, rid, files, has_app):
    require(type(has_app) is bool, "Missing qualified App role flag")
    module = "lib/" + Path(project).stem
    runtime_files = projection(files, "runtime", rid)
    result = {"path": package_id + "/" + rid + "/sunder-dev", "hasApp": has_app, "files": list(files.values())}
    for field, extension in (("module", ".dll"), ("runtimeConfig", ".runtimeconfig.json"), ("dependencyManifest", ".deps.json")):
        require(module + extension in runtime_files, "Incomplete framework-dependent native module evidence")
        record = runtime_files[module + extension]
        result[field + "Path"] = record["path"]
        result[field + "Sha256"] = record["sha256"]
    require(result["modulePath"] == "payload/runtime/" + rid + "/" + module + ".dll", "Worker module must remain exact-RID")
    require(module not in runtime_files and module + ".exe" not in runtime_files, "Obsolete native Worker apphost")
    return result


def validate_run(run, expected, rows):
    keys(run, "schemaVersion rid version agentCommit core bundle packages")
    require(all(run[key] == value for key, value in expected.items()) and run["rid"] in RIDS,
            "Native evidence schema/identity mismatch (schema-version-3 required)")
    rid = run["rid"]
    record_shape(run["bundle"], "native-leaves-" + rid + ".tar.gz", MAX_RUN)
    require(isinstance(run["packages"], list) and len(run["packages"]) == 15
            and sorted(p["packageId"] for p in run["packages"]) == sorted(row["packageId"] for row in rows),
            "Native evidence requires exactly 15 unique canonical packages")
    projects = {row["packageId"]: row["projectPath"] for row in rows}
    for package in run["packages"]:
        keys(package, "packageId workerProject qualification leaf testedArchive metricsSha256 metricsSize metrics")
        package_id = package["packageId"]
        project = projects[package_id]
        require(package["workerProject"] == project, "Mixed canonical project")
        prefix = package_id + "/" + rid + "/"
        record_shape(package["qualification"], prefix + ROLE_INPUTS, MAX_DOCUMENT)
        record_shape(package["testedArchive"], package_id + "." + expected["version"] + ".sunderpkg")
        sha(package["metricsSha256"])
        integer(package["metricsSize"], MAX_DOCUMENT, 1)
        leaf = package["leaf"]
        keys(leaf, "path hasApp files modulePath moduleSha256 runtimeConfigPath runtimeConfigSha256 dependencyManifestPath dependencyManifestSha256")
        files = file_set(leaf["files"])
        require(leaf == leaf_record(package_id, project, rid, files, leaf["hasApp"]), "Mixed native leaf/module projection")
        validate_metrics(package, rid)


def transport(run, bundle, destination=None):
    require(file_record(bundle, run["bundle"]["path"], MAX_RUN) == run["bundle"], "Native bundle hash/size mismatch")
    expected = {}
    metadata = {}
    roles = {}
    manifests = {}
    for package in run["packages"]:
        q = package["qualification"]
        expected[q["path"]] = q
        sidecar = (q["sha256"] + "\n").encode("ascii")
        expected[q["path"] + ".sha256"] = {"size": len(sidecar), "sha256": digest(sidecar)}
        metadata[q["path"]] = ("qualification", package)
        metadata[q["path"] + ".sha256"] = ("sidecar", package)
        for record in package["leaf"]["files"]:
            expected[package["leaf"]["path"] + "/" + record["path"]] = record
        metadata[package["leaf"]["path"] + "/manifest/sunder-package.json"] = ("manifest", package)
        metric_path = "metrics/" + package["packageId"] + "." + run["rid"] + ".json"
        expected[metric_path] = {"sha256": package["metricsSha256"], "size": package["metricsSize"]}
        metadata[metric_path] = ("metrics", package)

    def consume(name, data):
        kind, package = metadata[name]
        if kind == "qualification":
            q, files = qualification(data, package["packageId"], package["workerProject"], run["rid"], run["version"])
            require(list(files.values()) == package["leaf"]["files"], "Native provenance differs from Core's qualified file closure")
            require(q["rolePlan"]["hasApp"] == package["leaf"]["hasApp"], "Native provenance differs from qualified App role")
            roles[package["packageId"]] = q["rolePlan"]["hasApp"]
        elif kind == "manifest":
            manifest = validate_manifest(data, package["packageId"], run["version"], [run["rid"]])
            manifests[package["packageId"]] = any(t["role"] == "app" for t in manifest["targets"])
        elif kind == "metrics":
            require(parse(data) == package["metrics"], "Core metric file differs from native provenance")

    scan_tar(bundle, expected, consume, destination, metadata)
    require(roles == manifests, "Incomplete/mixed native App role closure")
    require(file_record(bundle, run["bundle"]["path"], MAX_RUN) == run["bundle"], "Native bundle changed during validation")


def select_native(repository, package_id, rid, version):
    require(rid in RIDS, "Unsupported native RID")
    release_version(version)
    row = next(row for row in inventory(repository) if row["packageId"] == package_id)
    root = repository / Path(row["projectPath"]).parent / "bin/Release/net10.0/sunder-targets" / rid
    data = read_bytes(root / ROLE_INPUTS)
    q, files = qualification(data, package_id, row["projectPath"], rid, version)
    require(tree(root / "sunder-dev") == files, "Native leaf changed after Core qualification")
    validate_manifest(read_bytes(root / "sunder-dev/manifest/sunder-package.json"), package_id, version, [rid], q["rolePlan"]["hasApp"])
    leaf_record(package_id, row["projectPath"], rid, files, q["rolePlan"]["hasApp"])
    require(read_bytes(root / (ROLE_INPUTS + ".sha256"), 65) == (digest(data) + "\n").encode(), "Qualification sidecar mismatch")
    selection = {"schemaVersion": 1, "inputs": [{"runtimeIdentifier": rid, "path": ROLE_INPUTS, "sha256": digest(data)}]}
    path = root / "native-selection.json"
    path.write_bytes(compact(selection))
    return path


@contextmanager
def publication_lock(output):
    lock = output / ".native-family-publication.lock"
    try:
        lock.mkdir()
    except FileExistsError:
        raise ValueError("Native family publication is locked: " + str(lock)) from None
    try:
        yield
    finally:
        lock.rmdir()


def capture(args):
    rid, version, agent_commit, core_tag, core_commit, core_version, archives, metrics, output, repository = args
    require(rid in RIDS, "Unsupported native RID")
    repository = Path(repository).resolve()
    rows = inventory(repository)
    expected = identity(version, agent_commit, core_tag, core_commit, core_version)
    archives, metrics = Path(archives).resolve(), Path(metrics).resolve()
    output = artifact_output(output, repository)
    packages, sources = [], {}
    with tempfile.TemporaryDirectory(prefix=".native-record-", dir=output) as work:
        work = Path(work)
        (work / "metrics").mkdir()
        for row in rows:
            package_id, project = row["packageId"], row["projectPath"]
            root = repository / Path(project).parent / "bin/Release/net10.0/sunder-targets" / rid
            data = read_bytes(root / ROLE_INPUTS)
            q, files = qualification(data, package_id, project, rid, version)
            require(read_bytes(root / (ROLE_INPUTS + ".sha256"), 65) == (digest(data) + "\n").encode(), "Qualification sidecar mismatch")
            require(tree(root / "sunder-dev") == files, "Native leaf changed after Core qualification")
            prefix = package_id + "/" + rid + "/"
            manifest = read_bytes(root / "sunder-dev/manifest/sunder-package.json")
            validate_manifest(manifest, package_id, version, [rid], q["rolePlan"]["hasApp"])
            leaf = leaf_record(package_id, project, rid, files, q["rolePlan"]["hasApp"])
            metric_name = package_id + "." + rid + ".json"
            metric_data = read_bytes(metrics / metric_name)
            (work / "metrics" / metric_name).write_bytes(metric_data)
            package = {"packageId": package_id, "workerProject": project,
                       "qualification": {"path": prefix + ROLE_INPUTS, "sha256": digest(data), "size": len(data)},
                       "leaf": leaf, "testedArchive": file_record(archives / (package_id + "." + version + ".sunderpkg")),
                       "metricsSha256": digest(metric_data), "metricsSize": len(metric_data), "metrics": parse(metric_data)}
            validate_metrics(package, rid)
            verify_archive(archives / package["testedArchive"]["path"], [(rid, package)], version)
            packages.append(package)
            sources[prefix + ROLE_INPUTS] = root / ROLE_INPUTS
            sources[prefix + ROLE_INPUTS + ".sha256"] = root / (ROLE_INPUTS + ".sha256")
            sources.update({prefix + "sunder-dev/" + name: root / "sunder-dev" / name for name in files})
            sources["metrics/" + metric_name] = work / "metrics" / metric_name
        bundle = work / ("native-leaves-" + rid + ".tar.gz")
        write_tar(bundle, sources)
        run = dict(expected, rid=rid, bundle=file_record(bundle, maximum=MAX_RUN), packages=packages)
        validate_run(run, expected, rows)
        transport(run, bundle)
        # Revalidate the producer trees and tested archives after capture, too.
        for row, package in zip(rows, packages):
            root = repository / Path(row["projectPath"]).parent / "bin/Release/net10.0/sunder-targets" / rid
            require(tree(root / "sunder-dev") == file_set(package["leaf"]["files"]), "Native leaf changed while recording")
            require(file_record(archives / package["testedArchive"]["path"]) == package["testedArchive"], "Tested archive changed while recording")
        (output / "metrics").mkdir(exist_ok=True)
        regular(output / "metrics", directory=True)
        for metric in (work / "metrics").iterdir():
            os.replace(metric, output / "metrics" / metric.name)
        os.replace(bundle, output / bundle.name)
        evidence = work / ("native-leaf-evidence-" + rid + ".json")
        evidence.write_bytes(compact(run))
        os.replace(evidence, output / evidence.name)
    print("Recorded Core V1 qualification digests, tested bytes and metric evidence for 15 " + rid + " Workers (native schema 3).")


def verify_document(document, artifacts, rows, expected):
    keys(document, "schemaVersion version agentCommit core nativeRuns finalArtifacts")
    require(all(document[key] == value for key, value in expected.items()), "Native family schema/identity mismatch (schema-version-3 required)")
    runs = document["nativeRuns"]
    require(isinstance(runs, list) and sorted(run["rid"] for run in runs) == sorted(RIDS), "Require exactly six unique native RID runs")
    for run in runs:
        validate_run(run, expected, rows)
    records = document["finalArtifacts"]
    expected_names = {row["packageId"] + "." + expected["version"] + ".sunderpkg" for row in rows}
    require(isinstance(records, list) and len(records) == 15 and {r["path"] for r in records} == expected_names,
            "Require exactly 15 final package archives")
    actual_names = {p.name for p in artifacts.iterdir() if p.suffix in (".sunderpkg", ".nupkg")}
    require(actual_names == expected_names, "Missing/extra final family artifacts")
    for record in records:
        record_shape(record, record["path"])
        require(file_record(artifacts / record["path"]) == record, "Native family archive differs from evidence: " + record["path"])
        package_id = record["path"][:-len("." + expected["version"] + ".sunderpkg")]
        packages = [(run["rid"], next(p for p in run["packages"] if p["packageId"] == package_id)) for run in runs]
        verify_archive(artifacts / record["path"], packages, expected["version"])


def assemble(args):
    native_input, output, version, agent_commit, core_tag, core_commit, core_version, repository = args
    repository, native_input = Path(repository).resolve(), Path(native_input).resolve()
    rows = inventory(repository)
    expected = identity(version, agent_commit, core_tag, core_commit, core_version)
    properties = tool_properties(repository)
    output = artifact_output(output, repository)
    expected_inputs = {"native-" + kind + "-" + rid + suffix for rid in RIDS
                       for kind, suffix in (("leaf-evidence", ".json"), ("leaves", ".tar.gz"))}
    actual_inputs = {path.name for path in native_input.iterdir()}
    require(actual_inputs - {"metrics"} == expected_inputs, "Missing/extra native fan-in inputs")
    runs = []
    # Every RID is preflighted before any extraction, MSBuild invocation or archive replacement.
    for rid in RIDS:
        run = json_file(native_input / ("native-leaf-evidence-" + rid + ".json"), MAX_PROVENANCE, 4 * 1024 * 1024)
        validate_run(run, expected, rows)
        require(run["rid"] == rid, "Mixed native RID evidence")
        transport(run, native_input / run["bundle"]["path"])
        runs.append(run)
    # Uploads may also retain convenient standalone copies of the metric files.
    # If present, that directory must be complete and match the bound tar bytes.
    if "metrics" in actual_inputs:
        metric_directory = native_input / "metrics"
        regular(metric_directory, directory=True)
        expected_metrics = {p["packageId"] + "." + run["rid"] + ".json": p for run in runs for p in run["packages"]}
        require({path.name for path in metric_directory.iterdir()} == set(expected_metrics), "Missing/extra native metric copies")
        for name, package in expected_metrics.items():
            require(file_record(metric_directory / name, maximum=MAX_DOCUMENT) ==
                    {"path": name, "sha256": package["metricsSha256"], "size": package["metricsSize"]}, "Native metric copy differs from provenance")
    with tempfile.TemporaryDirectory(prefix=".native-fanin-", dir=output) as work:
        work = Path(work)
        inputs, archives = work / "inputs", work / "archives"
        inputs.mkdir()
        archives.mkdir()
        for run in runs:
            transport(run, native_input / run["bundle"]["path"], inputs)
        for row in rows:
            package_id = row["packageId"]
            entries = []
            for run in runs:
                package = next(p for p in run["packages"] if p["packageId"] == package_id)
                entries.append({"runtimeIdentifier": run["rid"], "path": package["qualification"]["path"], "sha256": package["qualification"]["sha256"]})
            selection = inputs / (package_id + ".selection.json")
            selection.write_bytes(compact({"schemaVersion": 1, "inputs": entries}))
            archive = archives / (package_id + "." + version + ".sunderpkg")
            # All restore/tool retention happens explicitly BEFORE entering fan-in.
            # Core's public prebuilt dispatch reevaluates/validates each RID and
            # performs transactional Pack; it never bootstraps mutable source tools.
            subprocess.run(["dotnet", "msbuild", str(repository / row["projectPath"]), "-t:PackSunderPackage", "-m:1",
                            "-p:Configuration=Release", "-p:BuildProjectReferences=false", "-p:SunderPackageUsePrebuiltLeaves=true",
                            "-p:SunderPackageComposeRuntimeIdentifiers=" + "%3B".join(RIDS),
                            "-p:SunderPackagePrebuiltRoleInputsFile=" + str(selection), "-p:SunderAgentVersion=" + version,
                            "-p:SunderPackageOutputPath=" + str(archive)] + properties, cwd=repository, check=True)
        records = [file_record(path) for path in sorted(archives.glob("*.sunderpkg"))]
        document = dict(expected, nativeRuns=runs, finalArtifacts=records)
        verify_document(document, archives, rows, expected)
        evidence = archives / "native-test-evidence.json"
        evidence.write_bytes(compact(document))
        # A failed Pack/verification leaves the previous family untouched. Evidence
        # is published last; interrupted publication is detectable and rerunnable.
        with publication_lock(output):
            existing = {p.name for p in output.iterdir() if p.suffix in (".sunderpkg", ".nupkg")}
            require(existing <= {r["path"] for r in records}, "Output contains unrelated family artifacts")
            for record in records:
                os.replace(archives / record["path"], output / record["path"])
            os.replace(evidence, output / evidence.name)
    print("Composed 15 immutable archives from six Core-qualified native bundles without Build/Compile/Restore.")


def main():
    command, *args = sys.argv[1:]
    if command == "record" and len(args) == 10:
        capture(args)
    elif command == "assemble" and len(args) == 8:
        assemble(args)
    elif command == "verify" and len(args) == 8:
        evidence, artifacts, inventory_path, *versions = args
        document = json_file(Path(evidence).resolve(), MAX_PROVENANCE, 24 * 1024 * 1024)
        verify_document(document, Path(artifacts).resolve(), inventory(Path(inventory_path).resolve().parent), identity(*versions))
        print("Verified complete schema-version-3 native evidence, Core qualification digests and 15 exact final archives.")
    elif command == "select-native" and len(args) == 4:
        print(select_native(Path(args[0]).resolve(), *args[1:]))
    elif command == "producer-preflight" and len(args) == 2:
        repository = Path(args[0]).resolve()
        tool_properties(repository)
        frozen_baselines(repository, inventory(repository), args[1])
    else:
        raise ValueError("Invalid native-family command/arguments")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, StopIteration, RecursionError, subprocess.CalledProcessError,
            tarfile.TarError, zipfile.BadZipFile, zlib.error) as error:
        print("Native release validation failed: " + str(error), file=sys.stderr)
        sys.exit(1)
