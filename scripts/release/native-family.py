#!/usr/bin/env python3
"""Native component production, immutable composition and exact final-archive evidence."""

import os
from contextlib import contextmanager
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
import zlib
import native_runtime_evidence as runtime_evidence

from agent_release import (
    FAMILY_PACKAGE_IDS, FAMILY_PROJECTS, owned_contracts, has_public_evidence, source_role_use_evidence,
    validate_role_uses, version as semantic_version,
)
from native_release_inputs import (
    MAX_DOCUMENT, MAX_LEAF, MAX_RUN, RIDS, ROLE_INPUTS, compact, digest, file_record,
    file_set, integer, json_file, keys, parse, projection, qualification, read_bytes, regular,
    relative, require, scan_tar, sha, tree, validate_manifest, verify_archive, write_tar,
)

SCHEMA = 3
MAX_PROVENANCE = 256 * 1024 * 1024


def inventory(repository):
    rows = json_file(repository / "packages.json")["packages"]
    require(len(rows) == len(FAMILY_PROJECTS) and all(row["artifactType"] == "sunderpkg" for row in rows)
            and len({row["packageId"].lower() for row in rows}) == len(rows)
            and len({row["projectPath"].lower() for row in rows}) == len(rows), "Expected the complete unique runtime family")
    require({row["packageId"] for row in rows} == set(FAMILY_PACKAGE_IDS), "Expected the exact AI Agent runtime family")
    for row in rows:
        require(re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)*", row["packageId"]), "Invalid package ID")
        relative(row["projectPath"], 1024, 32)
    return sorted(rows, key=lambda row: row["key"])


def release_version(value):
    try:
        semantic_version(value)
    except ValueError as error:
        raise ValueError("Expected an explicit release version (full strict SemVer)") from error


def plan_coordinate(value):
    try:
        semantic_version(value)
        return False
    except ValueError:
        return str(value).endswith(".json")


def identity(version, agent_commit, *historical, repository=None, package_ids=None):
    require(re.fullmatch(r"[0-9a-f]{40}", agent_commit), "Agent commit must be a full lowercase Git SHA")
    result = {"schemaVersion": SCHEMA, "version": version, "agentCommit": agent_commit}
    if not plan_coordinate(version):
        release_version(version)
    if historical:
        core_tag, core_commit, core_version = historical
        release_version(core_version)
        require(re.fullmatch(r"[0-9a-f]{40}", core_commit) and core_tag in
                ("sdk/v" + core_version, "nuget-sunder-package-build/v" + core_version), "Historical Core coordinate differs")
        result["core"] = {"tag": core_tag, "commit": core_commit, "packageVersion": core_version}
    else:
        from agent_release import sdk_materials
        require(repository is not None, "Need the Agent source to identify declared dependencies")
        result["nuget"] = sdk_materials(repository, package_ids)
    if plan_coordinate(version):
        from agent_selection import selected, retained_role_uses
        plan = json_file(Path(version).resolve())
        require(plan["artifactSourceCommit"] == agent_commit and selected(plan), "Native selection source differs or selects no packages")
        del result["version"]
        result.update(schemaVersion=4, planSha256=digest(read_bytes(Path(version).resolve())),
            packageVersions={row["packageId"]: row["version"] for row in selected(plan)},
            familyVersions={row["packageId"]: row["version"] for row in plan["packages"]},
            retainedSourceRoleUses=retained_role_uses(plan, os.environ.get("SUNDER_AGENT_RETAINED_ARCHIVE_DIR", "")),
            retainedArtifacts=[dict(path=row["retainedArchive"]["name"], size=row["retainedArchive"]["size"],
                sha256=row["retainedArchive"]["sha256"]) for row in plan["packages"] if row["retainedArchive"] is not None])
        if plan["schemaVersion"] == 3:
            from agent_selection import validate
            result["releasePackage"] = validate(plan, repository, agent_commit)["releasePackage"]
        if not historical:
            result["nuget"] = sdk_materials(repository, result["packageVersions"])
    return result


def release_rows(repository, coordinate):
    rows = inventory(repository)
    if plan_coordinate(coordinate):
        from agent_selection import load, selected
        versions = {row["packageId"]: row["version"] for row in selected(load(coordinate, repository))}
        return [dict(row, version=versions[row["packageId"]]) for row in rows if row["packageId"] in versions]
    return [dict(row, version=coordinate) for row in rows]


def package_version(expected, package_id):
    return expected["packageVersions"][package_id] if expected["schemaVersion"] in (4, 5, 6) else expected["version"]


def identity_keys(expected):
    return " ".join(expected)


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
    return ["-p:ContinuousIntegrationBuild=true", "-p:RestoreLockedMode=true",
            "-p:SunderPackageRoleInputSourceRoot=" + str(repository)]


def frozen_baselines(repository, rows, version):
    if not plan_coordinate(version):
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
                         "rpc-authoring.private-baseline-unsupported: private-only contracts are source-derived and cannot have an independent owned baseline: " + str(path))
                require(current["state"] in ("release-frozen", "published"),
                        "Freeze every public owned baseline for this release before native Build: " + str(path))


def record_shape(record, path, maximum=MAX_LEAF):
    keys(record, "path sha256 size")
    require(record["path"] == path, "Unexpected artifact locator: " + str(record["path"]))
    relative(path)
    sha(record["sha256"])
    integer(record["size"], maximum, 1)


validate_metrics = runtime_evidence.validate_metrics


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
    keys(run, identity_keys(expected) + " rid bundle packages" + (" familyTests" if expected["schemaVersion"] in (4, 5, 6) else "")
         + (" execution" if expected["schemaVersion"] == 6 else ""))
    require(all(run[key] == value for key, value in expected.items()) and run["rid"] in RIDS,
            "Native evidence schema/identity differs from the approved selection")
    rid = run["rid"]
    record_shape(run["bundle"], "native-leaves-" + rid + ".tar.gz", MAX_RUN)
    require(isinstance(run["packages"], list) and len(run["packages"]) == len(rows)
            and sorted(p["packageId"] for p in run["packages"]) == sorted(row["packageId"] for row in rows),
             "Native evidence must contain exactly the selected canonical packages")
    projects = {row["packageId"]: row["projectPath"] for row in rows}
    for package in run["packages"]:
        keys(package, "packageId workerProject qualification leaf testedArchive metricsSha256 metricsSize metrics"
             + (" sourceRoleUses" if "sourceRoleUses" in package else ""))
        if "sourceRoleUses" in package:
            keys(package["sourceRoleUses"], "lockSha256 roleUses")
            sha(package["sourceRoleUses"]["lockSha256"])
            validate_role_uses(package["sourceRoleUses"]["roleUses"])
        package_id = package["packageId"]
        project = projects[package_id]
        require(package["workerProject"] == project, "Mixed canonical project")
        prefix = package_id + "/" + rid + "/"
        record_shape(package["qualification"], prefix + ROLE_INPUTS, MAX_DOCUMENT)
        record_shape(package["testedArchive"], package_id + "." + package_version(expected, package_id) + ".sunderpkg")
        sha(package["metricsSha256"])
        integer(package["metricsSize"], MAX_DOCUMENT, 1)
        leaf = package["leaf"]
        keys(leaf, "path hasApp files modulePath moduleSha256 runtimeConfigPath runtimeConfigSha256 dependencyManifestPath dependencyManifestSha256")
        files = file_set(leaf["files"])
        require(leaf == leaf_record(package_id, project, rid, files, leaf["hasApp"]), "Mixed native leaf/module projection")
        validate_metrics(package, rid)
    if expected["schemaVersion"] in (4, 5, 6):
        tests = run["familyTests"]
        require(type(tests) is list and len(tests) == len(expected["familyVersions"]) and {test["packageId"] for test in tests} == set(expected["familyVersions"]),
                "Selective native builds still require all fifteen Worker tests")
        for test in tests:
            keys(test, "packageId testedArchive metricsSha256 metricsSize metrics")
            record_shape(test["testedArchive"], test["packageId"] + "." + expected["familyVersions"][test["packageId"]] + ".sunderpkg")
            sha(test["metricsSha256"]); integer(test["metricsSize"], MAX_DOCUMENT, 1)
            validate_metrics(test, rid)
            if "releasePackage" in expected:
                runtime_evidence.check_installed_size(test["packageId"], test["metrics"]["measurement"]["currentRidInstalledBytes"])
            if test["packageId"] in expected["packageVersions"]:
                package = next(package for package in run["packages"] if package["packageId"] == test["packageId"])
                require(all(package[field] == test[field] for field in test), "Selected native metric differs from the full-family smoke test")
            else:
                require(test["testedArchive"] in expected["retainedArtifacts"], "Smoke test did not use the exact unchanged archive")
    if expected["schemaVersion"] == 6:
        records = [dict(packageId=test["packageId"], archive=test["testedArchive"]["path"],
                        size=test["testedArchive"]["size"], sha256=test["testedArchive"]["sha256"])
                   for test in sorted(run["familyTests"], key=lambda row: row["packageId"])]
        runtime_evidence.validate_receipt(run["execution"], "development", records, runtime_material(expected, rid), rid,
                                          package=expected.get("releasePackage"))


def transport(run, bundle, destination=None):
    require(file_record(bundle, run["bundle"]["path"], MAX_RUN) == run["bundle"], "Native bundle hash/size mismatch")
    expected = {}
    metadata = {}
    roles = {}
    manifests = {}
    execution_reports = {}
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
        if "metrics" in package:
            metric_path = "metrics/" + package["packageId"] + "." + run["rid"] + ".json"
            expected[metric_path] = {"sha256": package["metricsSha256"], "size": package["metricsSize"]}
            metadata[metric_path] = ("metrics", package)
    for test in run.get("familyTests", []):
        metric_path = "metrics/" + test["packageId"] + "." + run["rid"] + ".json"
        expected[metric_path] = {"sha256": test["metricsSha256"], "size": test["metricsSize"]}
        metadata[metric_path] = ("metrics", test)
    for report in run.get("execution", {}).get("reports", []):
        name = "execution/" + run["rid"] + "/" + report["path"]
        expected[name] = report
        metadata[name] = ("execution-report", report)

    def consume(name, data):
        kind, package = metadata[name]
        if kind == "qualification":
            q, files = qualification(data, package["packageId"], package["workerProject"], run["rid"], package_version(run, package["packageId"]))
            require(list(files.values()) == package["leaf"]["files"], "Native provenance differs from Core's qualified file closure")
            require(q["rolePlan"]["hasApp"] == package["leaf"]["hasApp"], "Native provenance differs from qualified App role")
            source_uses = source_role_use_evidence(q)
            require(package.get("sourceRoleUses") == source_uses
                    or "sourceRoleUses" not in package and not source_uses["roleUses"],
                    "Native source role uses differ from the qualified Worker receipt")
            roles[package["packageId"]] = q["rolePlan"]["hasApp"]
        elif kind == "manifest":
            manifest = validate_manifest(data, package["packageId"], package_version(run, package["packageId"]), [run["rid"]])
            manifests[package["packageId"]] = any(t["role"] == "app" for t in manifest["targets"])
        elif kind == "metrics":
            require(parse(data) == package["metrics"], "Core metric file differs from native provenance")
        elif kind == "execution-report":
            execution_reports[package["path"]] = data

    scan_tar(bundle, expected, consume, destination, metadata)
    if "execution" in run:
        runtime_evidence.verify_reports(run["execution"], execution_reports.__getitem__)
    require(roles == manifests, "Incomplete/mixed native App role closure")
    require(file_record(bundle, run["bundle"]["path"], MAX_RUN) == run["bundle"], "Native bundle changed during validation")
    return execution_reports


def native_root(repository, project, rid):
    require(rid in RIDS, "Unknown native platform")
    outputs = repository / Path(project).parent / "bin/Release"
    matches = list(outputs.glob("*/sunder-targets/" + rid + "/" + ROLE_INPUTS))
    require(len(matches) == 1, "Missing/ambiguous qualified native output: " + str(outputs))
    regular(matches[0])
    return matches[0].parent


def select_native(repository, package_id, rid, version):
    require(rid in RIDS, "Unsupported native RID")
    release_version(version)
    row = next(row for row in inventory(repository) if row["packageId"] == package_id)
    root = native_root(repository, row["projectPath"], rid)
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
    rows = release_rows(repository, version)
    expected = identity(version, agent_commit, core_tag, core_commit, core_version)
    archives, metrics = Path(archives).resolve(), Path(metrics).resolve()
    output = artifact_output(output, repository)
    packages, sources = [], {}
    with tempfile.TemporaryDirectory(prefix=".native-record-", dir=output) as work:
        work = Path(work)
        (work / "metrics").mkdir()
        for row in rows:
            package_id, project = row["packageId"], row["projectPath"]
            package_release = row["version"]
            root = native_root(repository, project, rid)
            data = read_bytes(root / ROLE_INPUTS)
            q, files = qualification(data, package_id, project, rid, package_release)
            require(read_bytes(root / (ROLE_INPUTS + ".sha256"), 65) == (digest(data) + "\n").encode(), "Qualification sidecar mismatch")
            require(tree(root / "sunder-dev") == files, "Native leaf changed after Core qualification")
            prefix = package_id + "/" + rid + "/"
            manifest = read_bytes(root / "sunder-dev/manifest/sunder-package.json")
            validate_manifest(manifest, package_id, package_release, [rid], q["rolePlan"]["hasApp"])
            leaf = leaf_record(package_id, project, rid, files, q["rolePlan"]["hasApp"])
            metric_name = package_id + "." + rid + ".json"
            metric_data = read_bytes(metrics / metric_name)
            (work / "metrics" / metric_name).write_bytes(metric_data)
            package = {"packageId": package_id, "workerProject": project,
                        "qualification": {"path": prefix + ROLE_INPUTS, "sha256": digest(data), "size": len(data)},
                        "sourceRoleUses": source_role_use_evidence(q),
                       "leaf": leaf, "testedArchive": file_record(archives / (package_id + "." + package_release + ".sunderpkg")),
                       "metricsSha256": digest(metric_data), "metricsSize": len(metric_data), "metrics": parse(metric_data)}
            validate_metrics(package, rid)
            verify_archive(archives / package["testedArchive"]["path"], [(rid, package)], package_release)
            packages.append(package)
            sources[prefix + ROLE_INPUTS] = root / ROLE_INPUTS
            sources[prefix + ROLE_INPUTS + ".sha256"] = root / (ROLE_INPUTS + ".sha256")
            sources.update({prefix + "sunder-dev/" + name: root / "sunder-dev" / name for name in files})
            sources["metrics/" + metric_name] = work / "metrics" / metric_name
        family_tests = []
        if expected["schemaVersion"] == 4:
            for package_id, package_release in sorted(expected["familyVersions"].items()):
                metric_name = package_id + "." + rid + ".json"
                metric_data = read_bytes(metrics / metric_name)
                (work / "metrics" / metric_name).write_bytes(metric_data)
                family_tests.append(dict(packageId=package_id, testedArchive=file_record(archives / (package_id + "." + package_release + ".sunderpkg")),
                    metricsSha256=digest(metric_data), metricsSize=len(metric_data), metrics=parse(metric_data)))
                sources["metrics/" + metric_name] = work / "metrics" / metric_name
        bundle = work / ("native-leaves-" + rid + ".tar.gz")
        write_tar(bundle, sources)
        run = dict(expected, rid=rid, bundle=file_record(bundle, maximum=MAX_RUN), packages=packages)
        if expected["schemaVersion"] == 4:
            run["familyTests"] = family_tests
        validate_run(run, expected, rows)
        transport(run, bundle)
        # Revalidate the producer trees and tested archives after capture, too.
        for row, package in zip(rows, packages):
            root = native_root(repository, row["projectPath"], rid)
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
    print(f"Recorded {len(rows)} qualified native leaves and full-family {rid} smoke evidence.")


def verify_document(document, artifacts, rows, expected):
    keys(document, identity_keys(expected) + " nativeRuns finalArtifacts" + (" executionBundles" if expected["schemaVersion"] == 6 else ""))
    require(all(document[key] == value for key, value in expected.items()), "Native family schema/identity differs from the approved selection")
    runs = document["nativeRuns"]
    require(isinstance(runs, list) and sorted(run["rid"] for run in runs) == sorted(RIDS), "Require exactly six unique native RID runs")
    for run in runs:
        validate_run(run, expected, rows)
    if expected["schemaVersion"] == 6:
        bundles = document["executionBundles"]
        require(type(bundles) is list and [row["path"] for row in bundles] == sorted("native-execution-" + rid + ".tar.gz" for rid in RIDS),
                "Missing/extra durable development execution report bundles")
        for record in bundles:
            record_shape(record, record["path"], MAX_RUN)
            require(file_record(artifacts / record["path"], maximum=MAX_RUN) == record, "Durable execution report bundle differs from native evidence")
            run = next(run for run in runs if record["path"] == "native-execution-" + run["rid"] + ".tar.gz")
            runtime_evidence.verify_bundle(artifacts / record["path"], run["execution"])
    records = document["finalArtifacts"]
    versions = expected.get("familyVersions", {row["packageId"]: expected.get("version") for row in rows})
    expected_names = {package_id + "." + version + ".sunderpkg" for package_id, version in versions.items()}
    require(isinstance(records, list) and len(records) == len(expected_names) and {r["path"] for r in records} == expected_names,
            "Require the complete final family archive set")
    actual_names = {p.name for p in artifacts.iterdir() if p.suffix in (".sunderpkg", ".nupkg")}
    require(actual_names == expected_names, "Missing/extra final family artifacts")
    for record in records:
        record_shape(record, record["path"])
        require(file_record(artifacts / record["path"]) == record, "Native family archive differs from evidence: " + record["path"])
        package_id = next(package_id for package_id, version in versions.items() if record["path"] == package_id + "." + version + ".sunderpkg")
        if expected["schemaVersion"] in (4, 5, 6) and package_id not in expected["packageVersions"]:
            require(record in expected["retainedArtifacts"] and all(next(test for test in run["familyTests"]
                if test["packageId"] == package_id)["testedArchive"] == record for run in runs), "Retained archive differs from native-tested bytes")
            continue
        packages = [(run["rid"], next(p for p in run["packages"] if p["packageId"] == package_id)) for run in runs]
        if expected["schemaVersion"] in (5, 6):
            require(all(package["testedArchive"] == record for _, package in packages),
                    "Native smoke must exercise the exact final archive on every RID")
        verify_archive(artifacts / record["path"], packages, versions[package_id])


def component_identity(repository, package_id, agent_commit, *historical):
    from agent_release import inventory as source_inventory
    row = next(row for row in source_inventory(repository)["packages"] if row["packageId"] == package_id)
    expected = identity(row["version"], agent_commit, *historical, repository=repository, package_ids={package_id})
    return dict(expected, schemaVersion=1, packageId=package_id, workerProject=row["projectPath"])


def runtime_material(expected, rid):
    if "core" in expected:
        return expected["core"]["commit"]
    from published_app import selection
    return selection(rid)


def selected_component(repository, package_id, expected):
    core = expected.get("core")
    history = (core["tag"], core["commit"], core["packageVersion"]) if core is not None else ()
    return component_identity(repository, package_id, expected["agentCommit"], *history)


def validate_component(run, expected):
    keys(run, identity_keys(expected) + " rid bundle packages")
    require(all(run[key] == value for key, value in expected.items()) and run["rid"] in RIDS,
            "Component leaf source, version or native platform differs")
    rid, package_id = run["rid"], expected["packageId"]
    record_shape(run["bundle"], "native-leaves-" + package_id + "-" + rid + ".tar.gz", MAX_RUN)
    require(type(run["packages"]) is list and len(run["packages"]) == 1, "A native component must contain one package")
    package = run["packages"][0]
    keys(package, "packageId workerProject qualification leaf sourceRoleUses")
    require(package["packageId"] == package_id and package["workerProject"] == expected["workerProject"], "Wrong native component project")
    record_shape(package["qualification"], package_id + "/" + rid + "/" + ROLE_INPUTS, MAX_DOCUMENT)
    keys(package["sourceRoleUses"], "lockSha256 roleUses")
    sha(package["sourceRoleUses"]["lockSha256"])
    validate_role_uses(package["sourceRoleUses"]["roleUses"])
    leaf = package["leaf"]
    require(leaf == leaf_record(package_id, expected["workerProject"], rid, file_set(leaf["files"]), leaf["hasApp"]),
            "Invalid component role projection")


def collect_component(repository, output, rid, expected):
    """Retain one Core-qualified leaf. Testing happens after six-RID composition."""
    package_id, project = expected["packageId"], expected["workerProject"]
    root = native_root(repository, project, rid)
    data = read_bytes(root / ROLE_INPUTS)
    q, files = qualification(data, package_id, project, rid, expected["version"])
    require(tree(root / "sunder-dev") == files, "Component bytes changed after Core qualification")
    prefix = package_id + "/" + rid + "/"
    package = dict(packageId=package_id, workerProject=project,
        qualification=file_record(root / ROLE_INPUTS, prefix + ROLE_INPUTS, MAX_DOCUMENT),
        sourceRoleUses=source_role_use_evidence(q), leaf=leaf_record(package_id, project, rid, files, q["rolePlan"]["hasApp"]))
    sources = {prefix + ROLE_INPUTS: root / ROLE_INPUTS, prefix + ROLE_INPUTS + ".sha256": root / (ROLE_INPUTS + ".sha256")}
    sources.update({prefix + "sunder-dev/" + name: root / "sunder-dev" / name for name in files})
    output = artifact_output(output, repository)
    bundle = output / ("native-leaves-" + package_id + "-" + rid + ".tar.gz")
    require(not bundle.exists(), "Component leaf handoff is immutable")
    write_tar(bundle, sources)
    run = dict(expected, rid=rid, bundle=file_record(bundle, maximum=MAX_RUN), packages=[package])
    validate_component(run, expected)
    transport(run, bundle)
    require(tree(root / "sunder-dev") == files, "Component bytes changed during capture")
    (output / ("native-leaf-evidence-" + package_id + "-" + rid + ".json")).write_bytes(compact(run))


def candidate_receipt(inputs, expected):
    path = inputs / (expected["packageId"] + ".candidate.json")
    value = json_file(path, MAX_PROVENANCE, 4 * 1024 * 1024)
    keys(value, identity_keys(expected) + " nativeRuns archive")
    require(all(value[key] == item for key, item in expected.items()), "Candidate source/version differs")
    runs = value["nativeRuns"]
    require(type(runs) is list and sorted(run["rid"] for run in runs) == sorted(RIDS), "Candidate requires exactly six native leaves")
    for run in runs:
        validate_component(run, expected)
        transport(run, inputs / run["bundle"]["path"])
    record = value["archive"]
    record_shape(record, expected["packageId"] + "." + expected["version"] + ".sunderpkg")
    require(file_record(inputs / record["path"]) == record, "Immutable composed candidate changed")
    verify_archive(inputs / record["path"], [(run["rid"], run["packages"][0]) for run in runs], expected["version"])
    return value


def compose_component(repository, inputs, output, expected):
    """Preflight all six leaves, then call Core's no-build prebuilt pack once."""
    package_id = expected["packageId"]
    runs = []
    for rid in RIDS:
        run = json_file(inputs / ("native-leaf-evidence-" + package_id + "-" + rid + ".json"), MAX_PROVENANCE, 4 * 1024 * 1024)
        validate_component(run, expected)
        require(run["rid"] == rid, "Mixed component native RID")
        transport(run, inputs / run["bundle"]["path"])
        runs.append(run)
    output = artifact_output(output, repository)
    require(not (output / (package_id + ".candidate.json")).exists(), "Composed candidates are immutable")
    with tempfile.TemporaryDirectory(prefix=".component-pack-", dir=output) as temporary:
        work = Path(temporary)
        for run in runs: transport(run, inputs / run["bundle"]["path"], work)
        selection = work / "selection.json"
        selection.write_bytes(compact(dict(schemaVersion=1, inputs=[dict(runtimeIdentifier=run["rid"],
            path=run["packages"][0]["qualification"]["path"], sha256=run["packages"][0]["qualification"]["sha256"]) for run in runs])))
        archive = work / (package_id + "." + expected["version"] + ".sunderpkg")
        # Keep MSBuild's list separator literal; %3B produces one escaped RID item.
        subprocess.run(["dotnet", "msbuild", str(repository / expected["workerProject"]), "-t:PackSunderPackage",
            "-p:Configuration=Release", "-p:BuildProjectReferences=false", "-p:SunderPackageUsePrebuiltLeaves=true",
            '-p:SunderPackageComposeRuntimeIdentifiers="' + ";".join(RIDS) + '"',
            "-p:SunderPackagePrebuiltRoleInputsFile=" + str(selection), "-p:SunderPackageOutputPath=" + str(archive),
            *tool_properties(repository)], cwd=repository, check=True)
        verify_archive(archive, [(run["rid"], run["packages"][0]) for run in runs], expected["version"])
        receipt = dict(expected, nativeRuns=runs, archive=file_record(archive))
        for run in runs:
            shutil.copyfile(inputs / run["bundle"]["path"], output / run["bundle"]["path"])
        shutil.copyfile(archive, output / archive.name)
        (output / (package_id + ".candidate.json")).write_bytes(compact(receipt))
    candidate_receipt(output, expected)


ARCHIVE_SET = "archive-set.json"


def component_leaf_name(package_id, rid):
    return f"native-leaf-evidence-{package_id}-{rid}.json"


def gather_candidates(repository, candidates, output, expected):
    """Copy selected immutable candidates and retained unchanged versions; no packing."""
    output = artifact_output(output, repository)
    records, leaves = [], {rid: {} for rid in RIDS}
    for row in inventory(repository):
        package_id = row["packageId"]
        if package_id not in expected["familyVersions"]:
            continue
        if package_id in expected["packageVersions"]:
            component = selected_component(repository, package_id, expected)
            require(component["version"] == expected["packageVersions"][package_id], "Candidate version differs from release selection")
            receipt = candidate_receipt(candidates, component)
            record, source = receipt["archive"], candidates
            for run in receipt["nativeRuns"]:
                data = compact(run)
                leaves[run["rid"]][package_id] = dict(path=component_leaf_name(package_id, run["rid"]),
                                                      size=len(data), sha256=digest(data))
        else:
            name = package_id + "." + expected["familyVersions"][package_id] + ".sunderpkg"
            record = next(record for record in expected["retainedArtifacts"] if record["path"] == name)
            source = Path(os.environ["SUNDER_AGENT_RETAINED_ARCHIVE_DIR"])
        require(file_record(source / record["path"]) == record, "Family input archive changed")
        target = output / record["path"]
        if target.exists(): require(file_record(target) == record, "Cannot replace another composed candidate")
        else: shutil.copyfile(source / record["path"], target)
        records.append(record)
    require({path.name for path in output.glob("*.sunderpkg")} == {record["path"] for record in records}, "Unexpected family candidate archive")
    records.sort(key=lambda item: item["path"])
    index = compact(dict(planSha256=expected["planSha256"], archives=records, leaves=leaves))
    with (output / ARCHIVE_SET).open("xb") as stream:
        stream.write(index)
    return records


def verify_archive_set(archives, expected_sha):
    path = archives / ARCHIVE_SET
    data = read_bytes(path, MAX_DOCUMENT)
    sha(expected_sha)
    require(digest(data) == expected_sha, "Inspected archive index changed")
    value = parse(data)
    keys(value, "planSha256 archives leaves")
    records = value["archives"]
    actual = [file_record(path) for path in sorted(archives.glob("*.sunderpkg"))]
    require(records and records == actual, "Native runner did not receive the exact inspected family")
    return value


def record_final(repository, leaves, archives, metrics, output, rid, expected, archive_set_sha):
    require(rid in RIDS, "Unknown native smoke platform")
    require({path.name for path in archives.glob("*.sunderpkg")} ==
            {key + "." + version + ".sunderpkg" for key, version in expected["familyVersions"].items()},
            "Record only a complete, already-smoked final family")
    index = verify_archive_set(archives, archive_set_sha)
    require(index["planSha256"] == expected["planSha256"] and set(index["leaves"]) == set(RIDS)
            and set(index["leaves"][rid]) == set(expected["packageVersions"]),
            "Inspected archive index has a different plan or incomplete native leaves")
    records = index["archives"]
    output = artifact_output(output, repository)
    evidence_path = output / ("native-leaf-evidence-" + rid + ".json")
    bundle_path = output / ("native-leaves-" + rid + ".tar.gz")
    require(not evidence_path.exists() and not bundle_path.exists(), "Final native handoffs are immutable; retain failed attempts separately")
    with tempfile.TemporaryDirectory(prefix=".native-final-", dir=output) as temporary:
        work, packages, tests = Path(temporary) / "payload", [], []
        work.mkdir()
        for record in records:
            package_id = next(key for key, version in expected["familyVersions"].items() if record["path"] == key + "." + version + ".sunderpkg")
            metric_name = package_id + "." + rid + ".json"
            metric_data = read_bytes(metrics / metric_name)
            test = dict(packageId=package_id, testedArchive=record, metricsSha256=digest(metric_data),
                metricsSize=len(metric_data), metrics=parse(metric_data))
            validate_metrics(test, rid)
            tests.append(test)
            if package_id in expected["packageVersions"]:
                component = selected_component(repository, package_id, expected)
                evidence = leaves / component_leaf_name(package_id, rid)
                require(file_record(evidence, maximum=MAX_PROVENANCE) == index["leaves"][rid][package_id],
                        "Native leaf evidence differs from the inspected composition")
                leaf = json_file(evidence, MAX_PROVENANCE, 4 * 1024 * 1024)
                validate_component(leaf, component)
                require(leaf["rid"] == rid, "Mixed inspected native RID")
                transport(leaf, leaves / leaf["bundle"]["path"], work)
                packages.append(dict(leaf["packages"][0], **{key: value for key, value in test.items() if key != "packageId"}))
            (work / "metrics").mkdir(exist_ok=True)
            (work / "metrics" / metric_name).write_bytes(metric_data)
        execution = None
        if expected["schemaVersion"] == 6:
            execution = json_file(metrics / runtime_evidence.RECEIPT)
            records = [dict(packageId=test["packageId"], archive=test["testedArchive"]["path"], size=test["testedArchive"]["size"],
                            sha256=test["testedArchive"]["sha256"]) for test in sorted(tests, key=lambda test: test["packageId"])]
            runtime_evidence.validate_receipt(execution, "development", records, runtime_material(expected, rid), rid,
                                              package=expected.get("releasePackage"))
            runtime_evidence.verify_reports(execution, lambda name: read_bytes(metrics / name, 32 * 1024 * 1024))
            for report in execution["reports"]:
                destination = work / "execution" / rid / report["path"]
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(metrics / report["path"], destination)
        bundle = Path(temporary) / bundle_path.name
        write_tar(bundle, {path.relative_to(work).as_posix(): path for path in work.rglob("*") if path.is_file()})
        run = dict(expected, rid=rid, bundle=file_record(bundle, maximum=MAX_RUN), packages=packages, familyTests=tests)
        if execution is not None: run["execution"] = execution
        rows = [row for row in inventory(repository) if row["packageId"] in expected["packageVersions"]]
        validate_run(run, expected, rows)
        transport(run, bundle)
        os.replace(bundle, bundle_path)
        with evidence_path.open("xb") as stream: stream.write(compact(run))


def finalize_candidates(repository, native_input, archives, expected):
    require({path.name for path in native_input.iterdir()} == {"native-" + kind + "-" + rid + suffix
        for rid in RIDS for kind, suffix in (("leaf-evidence", ".json"), ("leaves", ".tar.gz"))}, "Missing/extra final native smoke handoffs")
    rows = [row for row in inventory(repository) if row["packageId"] in expected["packageVersions"]]
    runs, executions = [], []
    for rid in RIDS:
        run = json_file(native_input / ("native-leaf-evidence-" + rid + ".json"), MAX_PROVENANCE, 4 * 1024 * 1024)
        validate_run(run, expected, rows)
        require(run["rid"] == rid, "Mixed final native evidence")
        reports = transport(run, native_input / run["bundle"]["path"])
        if expected["schemaVersion"] == 6:
            # Keep the original bounded reports durable on the coordinator, not
            # only inside expiring Actions artifacts or as unverifiable hashes.
            with tempfile.TemporaryDirectory(prefix=".native-execution-", dir=archives) as temporary:
                work = Path(temporary)
                (work / runtime_evidence.RECEIPT).write_bytes(compact(run["execution"]))
                for name, data in reports.items():
                    path = work / "reports" / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                bundle_name = "native-execution-" + rid + ".tar.gz"
                bundle = work / bundle_name
                write_tar(bundle, {path.relative_to(work).as_posix(): path for path in work.rglob("*") if path.is_file()})
                destination = archives / bundle_name
                if destination.exists():
                    require(file_record(destination) == file_record(bundle), "Durable execution reports cannot be replaced")
                else:
                    with destination.open("xb") as output, bundle.open("rb") as source: shutil.copyfileobj(source, output)
                executions.append(file_record(destination, maximum=MAX_RUN))
        runs.append(run)
    document = dict(expected, nativeRuns=runs, finalArtifacts=[file_record(path) for path in sorted(archives.glob("*.sunderpkg"))])
    if expected["schemaVersion"] == 6: document["executionBundles"] = sorted(executions, key=lambda row: row["path"])
    verify_document(document, archives, rows, expected)
    evidence = archives / "native-test-evidence.json"
    if evidence.exists(): require(evidence.read_bytes() == compact(document), "Final native evidence cannot be replaced")
    else:
        with evidence.open("xb") as output: output.write(compact(document))


def assemble(args):
    native_input, output, version, agent_commit, core_tag, core_commit, core_version, repository = args
    repository, native_input = Path(repository).resolve(), Path(native_input).resolve()
    rows = release_rows(repository, version)
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
        expected_metrics = {p["packageId"] + "." + run["rid"] + ".json": p for run in runs for p in run.get("familyTests", run["packages"])}
        require({path.name for path in metric_directory.iterdir()} == set(expected_metrics), "Missing/extra native metric copies")
        for name, package in expected_metrics.items():
            require(file_record(metric_directory / name, maximum=MAX_DOCUMENT) ==
                    {"path": name, "sha256": package["metricsSha256"], "size": package["metricsSize"]}, "Native metric copy differs from provenance")
    with tempfile.TemporaryDirectory(prefix=".native-fanin-", dir=output) as work:
        work = Path(work)
        inputs, archives = work / "inputs", work / "archives"
        inputs.mkdir()
        archives.mkdir()
        if expected["schemaVersion"] == 4:
            retained = Path(os.environ.get("SUNDER_AGENT_RETAINED_ARCHIVE_DIR", ""))
            for record in expected["retainedArtifacts"]:
                path = retained / record["path"]
                require(file_record(path) == record, "Retained archive differs from the release plan")
                shutil.copyfile(path, archives / record["path"])
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
            archive = archives / (package_id + "." + row["version"] + ".sunderpkg")
            # All restore/tool retention happens explicitly BEFORE entering fan-in.
            # Core's public prebuilt dispatch reevaluates/validates each RID and
            # performs transactional Pack; it never bootstraps mutable source tools.
            subprocess.run(["dotnet", "msbuild", str(repository / row["projectPath"]), "-t:PackSunderPackage",
                            "-p:Configuration=Release", "-p:BuildProjectReferences=false", "-p:SunderPackageUsePrebuiltLeaves=true",
                             '-p:SunderPackageComposeRuntimeIdentifiers="' + ";".join(RIDS) + '"',
                             "-p:SunderPackagePrebuiltRoleInputsFile=" + str(selection),
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
    print(f"Composed {len(rows)} selected archives without Build/Compile/Restore; reused the remaining family bytes.")


def main():
    command, *args = sys.argv[1:]
    if command == "version" and len(args) == 2:
        from agent_release import inventory as source_inventory
        print(next(row["version"] for row in source_inventory(Path(args[0]))["packages"] if row["packageId"] == args[1]))
    elif command in ("collect-component", "compose-component") and len(args) == 5:
        repository, source, destination, package_id, *versions = args
        repository = Path(repository).resolve()
        expected = component_identity(repository, package_id, *versions)
        if command == "collect-component": collect_component(repository, source, destination, expected)
        else: compose_component(repository, Path(source).resolve(), destination, expected)
    elif command in ("gather", "finalize") and len(args) == 5:
        source, destination, version, *versions, repository = args
        repository = Path(repository).resolve()
        expected = identity(version, *versions, repository=repository)
        require(expected["schemaVersion"] == 4, "Final candidates require an explicit package selection")
        expected["schemaVersion"] = 6
        if command == "gather":
            print(compact(gather_candidates(repository, Path(source).resolve(), destination, expected)).decode("utf-8"))
        else:
            finalize_candidates(repository, Path(source).resolve(), Path(destination).resolve(), expected)
    elif command == "verify-archive-set" and len(args) == 2:
        verify_archive_set(Path(args[0]).resolve(), args[1])
    elif command == "record-final" and len(args) == 9:
        rid, version, agent_commit, leaves, archives, metrics, output, repository, archive_set_sha = args
        expected = identity(version, agent_commit, repository=Path(repository).resolve())
        require(expected["schemaVersion"] == 4, "Final native smoke requires an explicit package selection")
        expected["schemaVersion"] = 6
        record_final(Path(repository).resolve(), Path(leaves).resolve(), Path(archives).resolve(), Path(metrics).resolve(),
                     output, rid, expected, archive_set_sha)
    elif command == "record" and len(args) == 10:
        capture(args)
    elif command == "assemble" and len(args) == 8:
        assemble(args)
    elif command == "verify" and len(args) in (5, 8):
        evidence, artifacts, inventory_path, *versions = args
        document = json_file(Path(evidence).resolve(), MAX_PROVENANCE, 24 * 1024 * 1024)
        expected = identity(*versions, repository=Path(inventory_path).resolve().parent)
        if document.get("schemaVersion") in (5, 6):
            require(expected["schemaVersion"] == 4, "Final archive evidence requires a package selection")
            expected["schemaVersion"] = document["schemaVersion"]
        verify_document(document, Path(artifacts).resolve(), release_rows(Path(inventory_path).resolve().parent, versions[0]), expected)
        print(f"Verified schema-version-{document['schemaVersion']} native evidence, Core qualifications and {len(document['finalArtifacts'])} exact family archives.")
    elif command == "select-native" and len(args) == 4:
        print(select_native(Path(args[0]).resolve(), *args[1:]))
    elif command == "native-root" and len(args) == 3:
        repository = Path(args[0]).resolve()
        row = next(row for row in inventory(repository) if row["packageId"] == args[1])
        print(native_root(repository, row["projectPath"], args[2]))
    elif command == "producer-preflight" and len(args) in (2, 3):
        repository = Path(args[0]).resolve()
        tool_properties(repository)
        rows = release_rows(repository, args[1])
        if len(args) == 3:
            rows = [row for row in rows if row["packageId"] == args[2]]
            require(len(rows) == 1, "Native producer is outside the selected component scope")
        frozen_baselines(repository, rows, args[1])
    else:
        raise ValueError("Invalid native-family command/arguments")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, StopIteration, RecursionError, subprocess.CalledProcessError,
            tarfile.TarError, zipfile.BadZipFile, zlib.error) as error:
        print("Native release validation failed: " + str(error), file=sys.stderr)
        sys.exit(1)
