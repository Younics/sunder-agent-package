#!/usr/bin/env python3
"""Local/Actions entry points for one package's build, inspection and native handoffs."""
import argparse
from pathlib import Path
import os
import shutil
import subprocess
import sys

import agent_ci as ci
import agent_release as release
import agent_selection as selection
import release_inputs as inputs

ROOT = Path(__file__).resolve().parents[2]


def output(name, value):
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(f"{name}={value}\n")


def main(args):
    context = args.context.resolve()
    plan_path = context / selection.PLAN
    commit = ci.head(ROOT)
    inputs.require(inputs.file_sha(plan_path) == args.plan_sha256, "Package selection digest differs")
    plan = selection.load(plan_path, ROOT)
    if commit != plan["artifactSourceCommit"]:
        import agent_pipeline
        inputs.require(args.action == "installed", "Earlier drafts permit installed testing only; never build a tagged version")
        # Recovery verifies original qualification with the producer. Installed
        # acceptance below is a new observation made by this controller.
        agent_pipeline.source_guard(None, plan)
    package = plan["releasePackage"]
    inputs.require(package == args.package, "Package selection belongs to another lane")
    packages = release.canonical([package]).decode()
    deps = context / "inputs"
    family = ROOT / "artifacts/release"
    os.environ.update(SUNDER_AGENT_RELEASE_PLAN_PATH=str(plan_path), SUNDER_AGENT_RETAINED_ARCHIVE_DIR=str(context / "retained"))
    rid = args.rid
    helper = [sys.executable, "-B", "scripts/ci/agent_ci.py"]
    native = [sys.executable, "-B", "scripts/release/native-family.py"]
    def run(command, **kwargs):
        subprocess.run(list(map(str, command)), cwd=kwargs.pop("cwd", ROOT), check=True, **kwargs)

    if args.action == "inputs":
        selection.evaluated_packages(ROOT, [package])
        scope = ci.test_dependency_scope(ROOT, [package])
        inputs.fetch(ROOT, deps, commit, packages=scope)
        output("inputs_sha256", inputs.file_sha(deps / inputs.MANIFEST))
        # Retained dependencies have an independently published archive and
        # qualification. Transfer them once; never ask a native job to publish.
        selection.retain(plan, context / "retained", archives=False)
        for row in plan["packages"]:
            if row["retainedArchive"]:
                locator = row["retainedArchive"]
                inputs.require(inputs.file_sha(deps / locator["name"]) == locator["sha256"], "Build and smoke dependencies differ")
        return
    if args.action in ("leaf", "compose"):
        inputs.require(inputs.file_sha(deps / inputs.MANIFEST) == args.inputs_sha256, "Locked input digest differs")
    if args.action == "leaf":
        options = ["--packages", packages, "--rid", rid, "--inputs", deps, "--release-leaf", "artifacts/leaf"]
        if package == "younics.ai.agent.execution.local" and rid.startswith("win-") and selection.selected(plan)[0]["version"] == "2.0.0":
            import agent_pipeline
            inputs.require(plan["cutoverPlan"] is not None, "Missing original 2.0.0 Windows Local scope; stop for review")
            agent_pipeline.source_guard(None, plan)
            options += ["--cutover-200-plan", plan_path]
        run([*helper, "validate", *options])
        return
    if args.action == "compose":
        temporary = Path(os.environ["RUNNER_TEMP"]) / "compose"
        candidate = ROOT / "artifacts/candidate"
        run([*helper, "prepare", "--packages", packages, "--inputs", deps, "--work", temporary])
        run([*helper, "run", "--state", temporary / "ci-state.json", "--", *native, "compose-component",
             ROOT, "artifacts/leaves", candidate, package, commit])
        for row in plan["packages"]:
            if row["retainedArchive"]:
                name = row["retainedArchive"]["name"]
                # gather verifies the published locator again. The single verified
                # dependency copy in the context is also the runtime smoke input.
                os.link(deps / name, context / "retained" / name)
        run([*native, "gather", candidate, family, plan_path, commit, ROOT])
        run([*helper, "prepare-verifier"])
        env = dict(os.environ, SUNDER_REQUIRE_TEST_PREREQUISITES="1", SUNDER_AGENT_REPOSITORY_ROOT=str(ROOT),
                   SUNDER_AGENT_PACKAGE_ARCHIVE_DIR=str(family))
        test_filter = "(FullyQualifiedName~PackageFamilyArchiveContractTests|FullyQualifiedName~AppNativeAssetContractTests)&FullyQualifiedName!~MeetInstalledProjectionBudget"
        run(["dotnet", "vstest", "artifacts/package-test-binaries/Sunder.Package.Agent.Archive.Tests.dll",
             "--TestCaseFilter:" + test_filter,
             "--Logger:trx", "--ResultsDirectory:artifacts/archive-results"], env=env)
        run([sys.executable, "-B", "scripts/test/generate-test-duration-report.py", "artifacts/archive-results", "--expected-skips", "0"])
        output("index_sha256", release.file_sha(family / "archive-set.json"))
    elif args.action == "verify-owner":
        inputs.require(package == selection.OWNER, "Only the owner has an independent lock-authoring reference")
        row, = selection.selected(plan)
        if any(entry.get("selectedPackageVersion") == row["version"] for _, lock in inputs.lock_documents(ROOT)
               for key, entry in lock["packageArtifacts"].items() if key == package):
            import owner_authoring
            archive = family / selection.archive_name(row)
            reference = owner_authoring.authored_archive(package, archive, os.environ.get("GITHUB_RUN_ID", ""))
            (ROOT / "artifacts/owner-reproduction.json").write_bytes(release.canonical(dict(
                packageId=package, source=commit, sha256=release.file_sha(archive), authoring=reference)) + b"\n")
    elif args.action == "development":
        run([*native, "verify-archive-set", family, args.index_sha256])
        metrics = ROOT / "artifacts/native-metrics" / rid
        import bash_runtime
        run(bash_runtime.command(ROOT / "scripts/release/run-native-runtime-smoke.sh", family, metrics, "development", package))
        run([*native, "record-final", rid, plan_path, commit, "artifacts/leaves", family, metrics, "artifacts/native", ROOT, args.index_sha256])
    elif args.action == "assemble":
        run([*native, "verify-archive-set", family, args.index_sha256])
        run([*native, "finalize", "artifacts/native", family, plan_path, commit, ROOT])
        shutil.copyfile(plan_path, family / selection.PLAN)
        import bash_runtime
        run(bash_runtime.audit_command(ROOT / "scripts/release/generate-package-output-audit.sh", family,
                                family / "native-test-evidence.json", ROOT, family / "package-output-audit.json"))
        release.verify_family(ROOT, family, plan["registryOrigin"])
        run(["dotnet", "vstest", "artifacts/package-test-binaries/Sunder.Package.Agent.Archive.Tests.dll",
             "--TestCaseFilter:FullyQualifiedName~MeetInstalledProjectionBudget", "--Logger:trx",
             "--ResultsDirectory:artifacts/budget-results"], env=dict(os.environ, SUNDER_REQUIRE_TEST_PREREQUISITES="1",
                 SUNDER_AGENT_REPOSITORY_ROOT=str(ROOT), SUNDER_AGENT_PACKAGE_ARCHIVE_DIR=str(family),
                 SUNDER_AGENT_PACKAGE_OUTPUT_AUDIT_REQUIRED="1"))
        run([sys.executable, "-B", "scripts/test/generate-test-duration-report.py", "artifacts/budget-results", "--expected-skips", "0"])
    elif args.action == "installed":
        import native_runtime_evidence as evidence
        destination = ROOT / "artifacts/installed-native"
        destination.mkdir()
        if (family / evidence.INSTALLED).exists():
            # `recover` already authenticated these durable originals.
            for suffix in (".json", ".tar.gz"):
                name = f"native-installed-{rid}{suffix}"
                shutil.copyfile(family / name, destination / name)
        else:
            import bash_runtime
            metrics = ROOT / "artifacts/installed-metrics" / rid
            run(bash_runtime.command(ROOT / "scripts/release/run-native-runtime-smoke.sh", family, metrics, "published", package),
                env=dict(os.environ, SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN=plan["registryOrigin"]))
            evidence.export_installed(metrics, destination / f"native-installed-{rid}.tar.gz")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("inputs", "leaf", "compose", "verify-owner", "development", "assemble", "installed"))
    parser.add_argument("--context", type=Path, default=Path(os.environ.get("RUNNER_TEMP", ".")) / "context")
    parser.add_argument("--plan-sha256", default=os.environ.get("PLAN_SHA"))
    parser.add_argument("--package", default=os.environ.get("PACKAGE"))
    parser.add_argument("--inputs-sha256", default=os.environ.get("INPUTS_SHA"))
    parser.add_argument("--index-sha256", default=os.environ.get("INDEX_SHA"))
    parser.add_argument("--rid", default=os.environ.get("RID"))
    main(parser.parse_args())
