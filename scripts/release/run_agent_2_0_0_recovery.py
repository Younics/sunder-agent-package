"""Run the original Agent native fixture against the pinned repaired Core test targets."""
import argparse
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

import collect_agent_2_0_0 as collect
import native_runtime_evidence as native
import prepare_maker_inputs as maker
import recovery_inputs_2_0_0 as recovery
from native_release_inputs import RIDS, compact, file_record, json_file, parse, read_bytes, regular, require
from release_inputs import isolated_env


APP = "Sunder.App"
TEST = "Sunder.Runtime.Host.Tests"
PROBE = "RuntimeTestProbe"
OWNER = "younics.ai.agent"
MAKER = "younics.sunder.package.maker"
APP_PROCESSES = {"development": {OWNER, MAKER, "native.maker.smoke"}, "published": {OWNER}}
REQUIRED_TEST_ASSEMBLIES = {TEST, "Sunder.Runtime.Host", "Sunder.Runtime.Contracts",
                           "Sunder.Runtime.Client", "Sunder.Runtime.LocalState", "Sunder.Package.Format"}


def require_platform(rid, *, intel_proof):
    system, arch = rid.split("-")
    require(platform.system() == {"osx": "Darwin", "linux": "Linux", "win": "Windows"}[system],
            "Recovery runner is not the selected OS")
    if intel_proof:
        require(rid == "osx-x64" and platform.mac_ver()[0].split(".")[0] == "15" and platform.machine() == "x86_64",
                "The first proof must use macOS 15 on Intel")
        result = subprocess.run(["sysctl", "-in", "sysctl.proc_translated"], capture_output=True, text=True, check=True)
        require(result.stdout.strip() in ("", "0"), "Rosetta cannot qualify the native Intel lane")
    return {"x64": "X64", "arm64": "Arm64"}[arch]


def proof_invocation():
    expected_workflow = f"{collect.selection.REPOSITORY}/{collect.INTEL_WORKFLOW}@{collect.INTEL_REF}"
    sha = collect.release.digest(os.environ["GITHUB_SHA"], 40)
    require(os.environ["GITHUB_REPOSITORY"] == collect.selection.REPOSITORY
            and os.environ["GITHUB_REPOSITORY_ID"] == str(collect.AGENT_REPO_ID)
            and os.environ["GITHUB_REF"] == collect.INTEL_REF and os.environ["GITHUB_WORKFLOW_REF"] == expected_workflow
            and os.environ["GITHUB_EVENT_NAME"] == "workflow_dispatch" and os.environ["GITHUB_WORKFLOW_SHA"] == sha
            and os.environ["GITHUB_JOB"] == "recovery-intel", "Unexpected Intel proof invocation")
    tree = recovery.git(collect.ROOT, "rev-parse", "HEAD^{tree}")
    recovery.checkout(collect.ROOT, sha, tree)
    return dict(commit=sha, tree=tree, workflowRef=expected_workflow, job="recovery-intel",
                runId=collect.release.positive(int(os.environ["GITHUB_RUN_ID"])),
                runAttempt=collect.release.positive(int(os.environ["GITHUB_RUN_ATTEMPT"])))


def test_inputs(prepared, results, core, pins, rid, mode, env):
    path = results / "maker-inputs"
    path.mkdir()
    feed = prepared / "feed"
    rows = pins["buildInputs"]["makerFeeds"][rid]["feed"] if mode == "development" else []
    if rows:
        regular(feed, directory=True)
        require({f.name for f in feed.iterdir()} == {row["name"] for row in rows}, "Wrong original Maker feed")
    (path / "feed").mkdir()
    for row in rows:
        file = recovery.checked_file(feed / row["name"], row["size"], row["sha256"])
        shutil.copyfile(file, path / "feed" / file.name)
    catalog = prepared / recovery.CATALOG
    require(file_record(catalog)["sha256"] == pins["buildInputs"]["platformCatalog"]["sha256"],
            "Wrong original platform catalog")
    shutil.copyfile(catalog, path / recovery.CATALOG)
    app = maker.prepare_app(core, path, env)
    value = dict(schemaVersion=1, buildCommit=pins["buildInputs"]["commit"], testCommit=pins["testInputs"]["commit"],
                 makerArtifact=pins["buildInputs"]["makerFeeds"][rid]["artifact"] if rows else None,
                 packages=rows, platformCatalog=file_record(path / recovery.CATALOG), app=app)
    (path / "split-inputs.json").write_bytes(compact(value))
    return path, value


def discover(original, core, output, app_target, env, tests):
    fixture = original / "tests/fixtures/AgentNativeReleaseSmokeTests.cs"
    project = core / f"languages/csharp/dotnet/tests/{TEST}/{TEST}.csproj"
    props = ["-p:CustomAfterMicrosoftCommonTargets=" + str(original / "scripts/release/Sunder.Agent.NativeReleaseTests.targets"),
             "-p:SunderAgentNativeReleaseTestSource=" + str(fixture), "-p:SunderAgentNativeAppTargetPath=" + str(app_target),
             "-p:GeneratePackageOnBuild=false"]
    test = ["dotnet", "test", str(project), "-c", "Release", "--no-restore", *props,
            "--filter", "|".join("FullyQualifiedName=" + name for name in tests)]
    # Core's test project builds nested role fixtures at its ordinary obj/bin
    # paths. The repaired Core checkout is fresh and private to this job.
    with maker.command_log(output / "test-build.log") as log:
        subprocess.run(["dotnet", "restore", str(project), "--locked-mode", *props],
                       cwd=core, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        result = subprocess.run([*test, "--list-tests"], cwd=core, env=env,
                                stdout=subprocess.PIPE, stderr=log, text=True)
        if result.stdout:
            log.write(result.stdout.encode("utf-8", errors="replace"))
        result.check_returncode()
    require(all("    " + name in result.stdout.splitlines() for name in tests), "Original native test discovery differs")
    target = subprocess.check_output(["dotnet", "msbuild", str(project), "-p:Configuration=Release", *props,
                                      "-getProperty:TargetPath"], cwd=core, env=env, text=True).strip()
    target = Path(target).resolve(strict=True)
    require(project.parent / "bin" in target.parents and target.name == TEST + ".dll",
            "Runtime test target escaped its exact Core test project")
    return test, target


def loaded_runtime(report, arch, rid, built):
    require(report["runtimeIdentifier"] == rid and report["processArchitecture"] == report["osArchitecture"] == arch
            and type(report["pid"]) is int and report["pid"] > 0, "VSTest did not execute natively on the selected RID")
    loaded = {}
    for item in report["assemblies"]:
        name, path = item["name"], Path(item["path"])
        require(name not in loaded and name in built and path == built[name][0] and file_record(path) == built[name][1],
                "A loaded Runtime assembly differs from the exact test output: " + name)
        loaded[name] = dict(file=built[name][1], mvid=item["mvid"])
    require(REQUIRED_TEST_ASSEMBLIES <= loaded.keys(), "Runtime load evidence is incomplete")
    return loaded


def app_processes(results, executable, mode):
    """Join the original CapturedProcess log and the in-App startup hook's ready marker."""
    root = results / "apps"
    regular(root, directory=True)
    require({p.name for p in root.iterdir()} == APP_PROCESSES[mode], "Original App invocation coverage differs")
    records = []
    pids = set()
    for package in sorted(APP_PROCESSES[mode]):
        [child] = list((root / package).iterdir())
        regular(child, directory=True)
        [commands] = list(child.glob("*.command.jsonl"))
        process = child / "process.txt"
        events = [parse(line) for line in read_bytes(commands, 1024**2).splitlines()]
        pid = events[0]["pid"]
        require(type(pid) is int and pid > 0 and pid not in pids and
                [row["phase"] for row in events] == ["start", "wait", "exit", "cleanup"] and all(
                row["pid"] == pid and Path(row["command"]) == executable and not row["arguments"] for row in events)
                and events[1]["timeoutMilliseconds"] == events[2]["timeoutMilliseconds"] == 15000
                and events[2]["exitCode"] == events[3]["exitCode"] == "0",
                "App did not run and exit through the original process fixture")
        status = read_bytes(process, 1024**2).decode("utf-8").splitlines()
        require(status[:3] == [f"pid={pid}; exited=True; exitCode=0", "stdout:", "SUNDER_NATIVE_APP_RPC:ready"],
                "No startup-hook readiness from the verified App process")
        pids.add(pid)
        records.append(dict(packageId=package, pid=pid, commands=file_record(commands, commands.relative_to(results).as_posix()),
                            process=file_record(process, process.relative_to(results).as_posix())))
    return records


def run(original, core, prepared, output, rid, mode, *, intel_proof):
    pins = recovery.read_inventory(collect.INVENTORY)
    original, core = collect.checkouts(original, core, pins)
    source, test = pins["archiveSource"], pins["testInputs"]
    prepared = prepared.resolve(strict=True)
    plan = collect.selection.load(prepared / collect.selection.PLAN, original, source["commit"])
    require(file_record(prepared / collect.selection.PLAN)["sha256"] == source["selectionSha256"],
            "Original recovery plan changed since collection")
    preparation = json_file(prepared / "recovery-preparation.json")
    require(preparation["rid"] == rid and preparation["inventorySha256"] == recovery.INVENTORY_SHA256
            and preparation["makerArtifact"] == pins["buildInputs"]["makerFeeds"][rid]["artifact"],
            "Prepared inputs belong to another recovery lane")
    archives = prepared / "family"
    recovery.archive_set(archives, pins["archives"], plan)
    arch = require_platform(rid, intel_proof=intel_proof)
    require(not intel_proof or mode == "development", "Intel proof is credential-free development evidence only")
    invocation = proof_invocation() if intel_proof else None
    output = output.parent.resolve(strict=True) / output.name
    require(not any(path == output or path in output.parents for path in (collect.ROOT, original, core, prepared)),
            "Test results must stay outside the source and immutable input directories")
    output.mkdir(mode=0o700)
    results = output / "reports"
    results.mkdir()
    env = isolated_env(output / "sunder-home", output / "sunder-home/contracts/v1/sha256")
    for key in ("SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN", "DOTNET_STARTUP_HOOKS", "SUNDER_TEST_NATIVE_APP_RPC_CONFIG"):
        env.pop(key, None)
    env.update(SUNDER_OFFICIAL_RUNTIME_ARCHIVE_CACHE=str(output / "official-runtime-cache"),
               SUNDER_REQUIRE_TEST_PREREQUISITES="1")
    sdk = subprocess.check_output(["dotnet", "--version"], cwd=core, env=env, text=True).strip()
    require(sdk == pins["buildInputs"]["dotnetSdk"], "Wrong exact .NET SDK")
    dotnet = Path(shutil.which("dotnet", path=env["PATH"])).resolve(strict=True)
    maker_path, inputs = test_inputs(prepared, results, core, pins, rid, mode, env)
    app_target = maker_path / inputs["app"]["targetPath"]
    executable = app_target.parent / (APP + (".exe" if rid.startswith("win-") else ""))
    tests = native.tests(mode)
    command, target = discover(original, core, output, app_target, env, tests)
    built = {path.stem: (path, file_record(path)) for path in target.parent.glob("*.dll")
             if path.name.startswith("Sunder.Runtime.") or path.name == "Sunder.Package.Format.dll"}
    marker = target.parent / "sunder-agent-native-app-target-path.txt"
    hook = target.parent / "agent-native-app-hook" / f"{APP}.Tests.dll"
    require(Path(read_bytes(marker, 4096).decode("utf-8-sig").strip()) == app_target, "Injected App target differs")
    hook_record = file_record(hook, "agent-native-app-hook/" + hook.name)
    app_files = {row["path"]: row for row in inputs["app"]["files"]}
    require(all(path.relative_to(maker_path).as_posix() in app_files for path in (app_target, executable)),
            "App preparation omitted its launch image or managed target")
    probe_build = output / "probe-build"
    probe_build.mkdir()
    # This is an SDK-only observer, not an Agent package project. Build it
    # outside either checkout, without inheriting Agent package restore policy.
    for extension in (".csproj", ".cs"):
        shutil.copyfile(collect.ROOT / f"scripts/release/recovery/{PROBE}{extension}", probe_build / (PROBE + extension))
    probe = probe_build / (PROBE + ".csproj")
    with maker.command_log(output / "probe-build.log") as log:
        subprocess.run(["dotnet", "restore", str(probe), "--locked-mode"],
                       cwd=core, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        subprocess.run(["dotnet", "build", str(probe), "-c", "Release", "--no-restore", "-o", str(probe_build / "hook")],
                       cwd=core, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    observer = probe_build / "hook" / (PROBE + ".dll")
    observer_record = file_record(observer)
    env.update(SUNDER_AGENT_NATIVE_ARCHIVE_DIR=str(archives), SUNDER_AGENT_NATIVE_METRICS_DIR=str(results),
               SUNDER_AGENT_NATIVE_PLATFORM_CATALOG=str(maker_path / recovery.CATALOG),
               SUNDER_AGENT_BUILDER_SDK_VERSION=sdk, SUNDER_AGENT_BUILDER_DOTNET_PATH=str(dotnet),
               SUNDER_AGENT_BUILDER_NUGET_FEED=str(maker_path / "feed"))
    origin = pins["registryOrigin"] if mode == "published" else None
    if origin:
        env["SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN"] = origin
    (results / "recovery-preparation.json").write_bytes(compact(preparation))
    subprocess.run([*command, "--no-build", "--logger", "trx;LogFileName=native-family.trx",
                    "--results-directory", str(results), "--environment", "DOTNET_STARTUP_HOOKS=" + str(observer),
                    "--environment", "SUNDER_RECOVERY_LOAD_REPORT=" + str(results / "loaded-runtime.json")],
                   cwd=core, env=env, check=True)

    collect.checkouts(original, core, pins)
    require(not intel_proof or invocation == proof_invocation(), "Intel proof controller changed during execution")
    recovery.archive_set(archives, pins["archives"], plan)
    records = native.archive_records(archives)
    native.validate_trx(read_bytes(results / "native-family.trx", 32 * 1024**2), mode)
    scope = json_file(results / "scope/qualification.json")
    native.validate_scope(scope, mode, records, rid)
    require(scope["registryOrigin"] == origin, "Unexpected Registry authority in native execution")
    require({p.name for p in results.glob(f"*.{rid}.json")} == {item["packageId"] + f".{rid}.json" for item in records},
            "Missing/extra native metrics")
    for item in records:
        metric = json_file(results / (item["packageId"] + f".{rid}.json"))
        native.validate_metrics(dict(metrics=metric, testedArchive=dict(path=item["archive"], size=item["size"])), rid)
    for row in inputs["app"]["files"] + [inputs["platformCatalog"]]:
        require(file_record(maker_path / row["path"], row["path"]) == row, "App or catalog changed during the test")
    require(file_record(hook, hook_record["path"]) == hook_record and file_record(observer) == observer_record
            and Path(read_bytes(marker, 4096).decode("utf-8-sig").strip()) == app_target,
            "Startup hook or injected App marker changed")
    if mode == "development":
        native.validate_maker(json_file(results / "maker/journey.json"), inputs, records, rid)
        for row in inputs["packages"]:
            recovery.checked_file(maker_path / "feed" / row["name"], row["size"], row["sha256"])
        for operation in ("create", "build", "archive"):
            require(json_file(results / f"maker/{operation}.json")["state"] == "succeeded", "Maker job did not complete")
    observation = json_file(results / "loaded-runtime.json")
    loaded = loaded_runtime(observation, arch, rid, built)
    apps = app_processes(results, executable, mode)
    (results / "recovery-test-identity.json").write_bytes(compact(dict(
        schemaVersion=1, rid=rid, sdkVersion=sdk, archiveSourceCommit=source["commit"], archiveSourceTree=source["tree"],
        buildCommit=pins["buildInputs"]["commit"], testCommit=test["commit"], testTree=test["tree"],
        invocation=invocation, runnerOSVersion=platform.mac_ver()[0] if rid.startswith("osx-") else platform.release(),
        targetKind="source-built", publishedCorePayloadExecuted=False,
        runtimeTestHostPid=observation["pid"], runtimeAssemblies=loaded, appStartupHook=hook_record,
        tests=tests, appProcesses=apps, runtimeObservationAssembly=observer_record,
        appAssembly=app_files[app_target.relative_to(maker_path).as_posix()],
        appExecutable=app_files[executable.relative_to(maker_path).as_posix()],
        inventorySha256=recovery.INVENTORY_SHA256, testExecuted=True, publishable=False)))
    print(f"{rid}: {len(tests)} exact tests, 15 Worker metrics, native Runtime and App process identities verified.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("original", "core", "prepared", "output"):
        parser.add_argument(argument, type=Path)
    parser.add_argument("rid", choices=RIDS)
    parser.add_argument("--intel-proof", action="store_true")
    args = parser.parse_args()
    try:
        run(args.original, args.core, args.prepared, args.output, args.rid, "development", intel_proof=args.intel_proof)
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as error:
        raise SystemExit("Agent recovery test rejected: " + str(error)) from None
