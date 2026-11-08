#!/usr/bin/env python3
"""Prepare authored dependencies automatically, then run normal .NET commands."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

import bash_runtime

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import release_inputs as inputs


def configure_console():
    # Redirected Windows streams otherwise use the ANSI code page, even when
    # the .NET child and retained logs are UTF-8.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")


def console_write(text, stream=None):
    stream = sys.stdout if stream is None else stream
    encoding = getattr(stream, "encoding", None)
    if encoding:
        text = text.encode(encoding, errors="backslashreplace").decode(encoding)
    stream.write(text)
    stream.flush()


def describe_error(error):
    return f"{type(error).__name__}: {error}" + "".join("\n" + note for note in getattr(error, "__notes__", []))


def note_error(error, message):
    # Retain CLI diagnostics on Python versions without BaseException.add_note.
    error.__notes__ = [*getattr(error, "__notes__", []), message]


def head(root):
    return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()


def new_directory(path):
    path = Path(path).absolute()
    inputs.require(not path.exists() and not path.is_symlink(), "CI output must be a new directory")
    path.mkdir(parents=True)
    return path.resolve()


def resolve(source, core, output):
    import dependency_inputs
    return dependency_inputs.resolve(source, core, output)


def coordinates(source):
    import dependency_inputs
    value = dependency_inputs.coordinates(source)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.writelines(f"{key}={item}\n" for key, item in value.items())
    print(inputs.canonical(value).decode())


def matrices(package_tests):
    import ci_platforms
    value = ci_platforms.matrices(package_tests, json.loads(os.environ.get("RUNNER_VARIABLES", "{}")))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.writelines(f"{key}={json.dumps(item, separators=(',', ':'))}\n" for key, item in value.items())
    print(json.dumps(value))


def selected_files(args, source, core):
    if args.inputs:
        root = inputs.directory(args.inputs)
        if (root / inputs.MANIFEST).exists():
            value = inputs.verify(source, root, inputs.file_sha(root / inputs.MANIFEST), head(source), head(core))
            return ([root / item["asset"]["name"] for item in value["candidates"]], root / value["platformCatalog"]["asset"]["name"])
        value = inputs.load(root / "ci-inputs.json")
        inputs.closed(value, "schemaVersion coreCommit candidates platformCatalog")
        inputs.require(value["schemaVersion"] in (1, 2) and value["coreCommit"] == head(core), "CI Core checkout differs from the selected input")
        paths = []
        for item in [*value["candidates"], value["platformCatalog"]]:
            if value["schemaVersion"] == 1:
                inputs.locator(item)
            else:
                inputs.closed(item, "name size sha256")
                inputs.require(Path(item["name"]).name == item["name"] and item["name"] not in (".", ".."), "Unsafe input filename")
                inputs.positive(item["size"]); inputs.digest(item["sha256"])
            path = inputs.regular(root / item["name"])
            inputs.require(path.stat().st_size == item["size"] and inputs.file_sha(path) == item["sha256"], "CI input changed after download")
            paths.append(path.resolve())
        return paths[:-1], paths[-1]
    inputs.require(args.candidate and args.catalog, "Supply exact --candidate and --catalog files, or --inputs")
    return [inputs.regular(path).resolve() for path in args.candidate], inputs.regular(args.catalog).resolve()


def arguments(properties):
    return ["-p:" + key + "=" + value for key, value in properties.items()]


def output_directory(project, env):
    value = subprocess.run(["dotnet", "msbuild", str(project), "-p:Configuration=Release", "-getProperty:TargetDir"],
        cwd=Path(project).parent, env=env, check=True, text=True, stdout=subprocess.PIPE).stdout.strip()
    inputs.require(value and Path(value).is_absolute(), "Missing evaluated test/tool output directory")
    return Path(value)


def prepare(args):
    source, core = Path(args.source).resolve(strict=True), Path(args.core).resolve(strict=True)
    inputs.require(core == source.parent / "sunder-core", "Core must be the explicit sibling source checkout")
    candidates, catalog = selected_files(args, source, core)
    selected = inputs.verify_selected_files(source, candidates, catalog)
    before = inputs.source_snapshot(source)
    output = Path(args.work).resolve()
    inputs.require(not output.is_relative_to(source) and not output.is_relative_to(core), "Keep CI state outside source checkouts")
    root = new_directory(args.work)
    home, cache = root / "home", root / "home/contracts/v1/sha256"
    home.mkdir()
    env = inputs.isolated_env(home, cache)
    props = {"Configuration": "Release", "SunderUseSourceSdk": "false" if args.mode == "package" else "true", "ContinuousIntegrationBuild": "true",
             "SunderContractCacheRoot": str(cache)}
    try:
        # Explicit full-graph dependency restore precedes package contract hydration.
        subprocess.run(["dotnet", "restore", str(source / "Sunder.AgentPackage.slnx"), "--locked-mode", *arguments(props)], env=env, check=True)
        driver = core / "languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj"
        subprocess.run(["dotnet", "restore", str(driver), "--locked-mode"], env=env, check=True)
        subprocess.run(["dotnet", "build", str(driver), "-c", "Release", "--no-restore",
                        "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false"], env=env, check=True)
        tools = root / "tools"
        shutil.copytree(output_directory(driver, env), tools)
        for name in inputs.TOOL_FILES: inputs.regular(tools / name)
        props.update(SunderPackageBuildTaskAssembly=str(tools / "Sunder.Package.Build.dll"), SunderPackageBuildTaskSourceIsImmutable="true")
        for package, lock in inputs.lock_documents(source):
            command = ["dotnet", "msbuild", str(source / package["projectPath"]), "-t:RestoreSunderContracts", *arguments(props)]
            paths = [str(selected[key]) for key in sorted(lock["packageArtifacts"])]
            inputs.require(all(not any(c in path for c in "%\r\n;") for path in [str(catalog), *paths]), "Ambiguous MSBuild input path")
            if paths: command.append("-p:SunderPackageCandidatePath=" + "%3B".join(paths))
            if inputs.catalog_requirements(lock): command.append("-p:SunderPlatformCatalogPath=" + str(catalog))
            if paths or inputs.catalog_requirements(lock): subprocess.run(command, cwd=source, env=env, check=True)
        state = dict(schemaVersion=1, source=str(source), core=str(core), sourceCommit=head(source), coreCommit=head(core),
                     home=str(home), cache=str(cache), properties=props, sourceInputs=before,
                     candidates=[str(path) for path in candidates], catalog=str(catalog),
                     inputHashes={str(path): inputs.file_sha(path) for path in [*candidates, catalog, *sorted(tools.glob("*.dll"))]})
        (root / "ci-state.json").write_bytes(inputs.canonical(state) + b"\n")
        print(root / "ci-state.json")
    finally:
        inputs.require(inputs.source_snapshot(source) == before, "CI hydration changed committed contract/release inputs")


def context(path):
    path = Path(path).resolve(strict=True)
    state = inputs.load(path)
    inputs.closed(state, "schemaVersion source core sourceCommit coreCommit home cache properties sourceInputs candidates catalog inputHashes")
    inputs.require(state["schemaVersion"] == 1 and head(state["source"]) == state["sourceCommit"]
                   and head(state["core"]) == state["coreCommit"], "CI checkout identity changed")
    inputs.require(inputs.source_snapshot(state["source"]) == state["sourceInputs"], "CI contract/release inputs changed")
    inputs.require(all(inputs.file_sha(file) == digest for file, digest in state["inputHashes"].items()), "CI input/tool bytes changed")
    inputs.verify_selected_files(state["source"], state["candidates"], state["catalog"])
    props = state["properties"]
    inputs.require(props["SunderUseSourceSdk"] in ("true", "false") and props["Configuration"] == "Release"
                   and props["SunderPackageBuildTaskSourceIsImmutable"] == "true" and props["SunderContractCacheRoot"] == state["cache"],
                   "CI build context differs")
    env = inputs.isolated_env(state["home"], state["cache"])
    env.update(props)
    env["SUNDER_AGENT_SOURCE_PLATFORM_CATALOG"] = state["catalog"]
    return state, env


def execute(state_path, command, cwd=None, log=None):
    state, env = context(state_path)
    command = list(command)
    inputs.require(command, "A CI command is required")
    if command[0] == "dotnet":
        inputs.require(len(command) > 1 and command[1] in ("build", "test", "restore", "msbuild"), "Unsupported CI .NET command")
        if command[1] in ("build", "test"):
            inputs.require("--no-restore" in command, "Use the explicitly restored dependency graph")
        command += arguments(state["properties"])
    started, status, process, failure = time.monotonic(), None, None, None
    errors = {}
    if log is not None:
        log = Path(log)
        log.parent.mkdir(parents=True, exist_ok=True)
        env.update(DOTNET_CLI_UI_LANGUAGE="en", VSLANG="1033")
    try:
        if log is None:
            status = subprocess.run(command, cwd=cwd or state["source"], env=env).returncode
        else:
            with log.open("w", encoding="utf-8") as output:
                with subprocess.Popen(command, cwd=cwd or state["source"], env=env, stdout=subprocess.PIPE,
                                      stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace") as process:
                    for line in process.stdout:
                        # A failed mirror must not close the pipe early, lose the
                        # child's status, or leave it blocked on a full pipe.
                        if "log" not in errors:
                            try:
                                output.write(line)
                                output.flush()
                            except (OSError, UnicodeError, ValueError) as error:
                                errors["log"] = describe_error(error)
                        if "console" not in errors:
                            try:
                                console_write(line)
                            except (OSError, UnicodeError, ValueError) as error:
                                errors["console"] = describe_error(error)
                    status = process.wait()
            if errors:
                raise OSError("CI output capture failed: " + json.dumps(errors))
        return status
    except BaseException as error:
        failure = error
        errors["execution"] = describe_error(error)
        raise
    finally:
        if process is not None:
            status = process.returncode
        try:
            context(state_path)
        except Exception as error:
            errors["verification"] = describe_error(error)
            if failure is None:
                failure = error
                raise
            note_error(failure, "Post-execution input verification also failed: " + describe_error(error))
        finally:
            if log is not None:
                try:
                    log.with_suffix(".command.json").write_text(json.dumps({
                        "command": command, "cwd": str(cwd or state["source"]), "state": str(Path(state_path).resolve()),
                        "exitCode": status, "elapsedMilliseconds": round((time.monotonic() - started) * 1000),
                        "output": log.name, "errors": errors,
                    }, indent=2) + "\n", encoding="utf-8")
                except Exception as error:
                    if failure is None: raise
                    note_error(failure, "Command evidence could not be written: " + describe_error(error))


def generate_inventory(source, selected=None, mode="source"):
    import version_selection
    reports = source / "artifacts/reports"
    reports.mkdir(parents=True, exist_ok=True)
    destination = reports / "test-inventory.json"
    destination.unlink(missing_ok=True)
    command, result, failure, temporary = [], None, None, None
    started = time.monotonic()
    try:
        command = bash_runtime.command("scripts/test/generate-test-inventory.sh")
        result = subprocess.run(command, cwd=source, capture_output=True, text=True, encoding="utf-8", errors="replace")
        result.check_returncode()
        inventory = json.loads(result.stdout)
        inputs.require(isinstance(inventory, dict) and inventory.get("schemaVersion") == 2
                       and inventory.get("solution") == "Sunder.AgentPackage.slnx"
                       and isinstance(inventory.get("projects"), list) and inventory["projects"], "Invalid test inventory header/projects")
        for row in inventory["projects"]:
            inputs.require(isinstance(row, dict) and isinstance(row.get("path"), str) and row["path"].endswith(".csproj")
                           and isinstance(row.get("owner"), str) and row["owner"]
                           and isinstance(row.get("modes"), list) and row["modes"]
                           and all(mode in ("source", "package") for mode in row["modes"]), "Invalid test inventory project")
        inputs.require(len({row["path"] for row in inventory["projects"]}) == len(inventory["projects"]), "Duplicate test inventory project")
        if selected is not None:
            inventory = version_selection.scope_inventory(inventory, selected, mode, sys.platform)
        with tempfile.NamedTemporaryFile(dir=reports, prefix=".test-inventory-", suffix=".json", delete=False) as output:
            temporary = Path(output.name)
            output.write(inputs.canonical(inventory) + b"\n")
        temporary.replace(destination)
        return destination
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        failure = str(error)
        print(f"Test inventory failed: {failure}\nCommand: {command}\nDiagnostics: {reports}", file=sys.stderr)
        if result is not None:
            print(f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}", file=sys.stderr)
        raise
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
        for stream in ("stdout", "stderr"):
            (reports / f"test-inventory.{stream}.log").write_text(getattr(result, stream, ""), encoding="utf-8")
        (reports / "test-inventory.command.json").write_text(json.dumps(dict(
            command=command, cwd=str(source), exitCode=result.returncode if result is not None else None,
            error=failure, elapsedMilliseconds=round((time.monotonic() - started) * 1000)), indent=2) + "\n", encoding="utf-8")


def preflight(args):
    import version_selection
    source = Path(args.source).resolve()
    if args.packages is not None and version_selection.package_ids(source, json.loads(args.packages)) == []:
        print("No newer package versions; preflight skipped.")
        return
    generate_inventory(source)
    for group in (["source", "release"] if sys.platform == "linux" else ["source"]):
        subprocess.run([sys.executable, "-B", "scripts/test/run-ci-checks.py", group], cwd=source, check=True)


def validate(args):
    import test_lanes
    import version_selection
    source = Path(args.source).resolve()
    packages = getattr(args, "packages", None)
    selected = None if packages is None else version_selection.package_ids(source, json.loads(packages))
    if selected == []:
        print("No newer package versions; validation skipped.")
        return
    builder_diagnostics = getattr(args, "builder_diagnostics", False)
    inputs.require(not builder_diagnostics or args.mode == "source", "Builder diagnostics require source mode")
    inventory_path = generate_inventory(source, selected, args.mode)
    with tempfile.TemporaryDirectory(prefix="sunder-agent-ci-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
        root = Path(temporary)
        if not args.inputs and not args.candidate:
            resolve(source, args.core, root / "inputs")
            args.inputs = root / "inputs"
        args.work = root / "prepared"
        prepare(args)
        state = args.work / "ci-state.json"
        if builder_diagnostics:
            test_lanes.builder_diagnostics(state, inventory_path)
            return
        projects = ([row["path"] for row in inputs.load(inventory_path)["projects"] if args.mode in row["modes"]]
                    if selected is not None else ["Sunder.AgentPackage.slnx"])
        for project in projects:
            status = execute(state, ["dotnet", "build", project, "-c", "Release", "--no-restore"])
            if status: raise subprocess.CalledProcessError(status, "dotnet build " + project)
        test_lanes.validate(state, args.mode, args.stress)
        if args.mode == "package" and sys.platform == "linux": test_lanes.retain_verifier()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    matrix_parser = commands.add_parser("matrices")
    matrix_parser.add_argument("--package-tests", action="store_true")
    test_parser = commands.add_parser("test")
    test_parser.add_argument("--state", required=True)
    test_parser.add_argument("--lane", required=True, choices=("source", "artifact", "package", "required-linux", "stress"))
    report_parser = commands.add_parser("report")
    report_parser.add_argument("--mode", required=True, choices=("source", "package"))
    report_parser.add_argument("--platform", required=True, choices=("linux", "windows", "macos"))
    commands.add_parser("retain-verifier")
    family_parser = commands.add_parser("test-family")
    family_parser.add_argument("--core", required=True)
    family_parser.add_argument("--directory", required=True)
    resolve_parser = commands.add_parser("resolve")
    resolve_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    resolve_parser.add_argument("--core", required=True)
    resolve_parser.add_argument("--output", required=True)
    coordinate_parser = commands.add_parser("coordinates")
    coordinate_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    prepare_parser = commands.add_parser("prepare")
    prepare_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    prepare_parser.add_argument("--core", required=True)
    prepare_parser.add_argument("--work", required=True)
    prepare_parser.add_argument("--inputs")
    prepare_parser.add_argument("--candidate", action="append")
    prepare_parser.add_argument("--catalog")
    prepare_parser.add_argument("--mode", choices=("source", "package"), default="source")
    preflight_parser = commands.add_parser("preflight")
    preflight_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    preflight_parser.add_argument("--packages", help="JSON version-selected package IDs; omitted means full validation")
    validate_parser = commands.add_parser("validate")
    validate_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    validate_parser.add_argument("--core", required=True)
    validate_parser.add_argument("--inputs")
    validate_parser.add_argument("--candidate", action="append")
    validate_parser.add_argument("--catalog")
    validate_parser.add_argument("--mode", choices=("source", "package"), default="source")
    validate_parser.add_argument("--stress", action="store_true")
    validate_parser.add_argument("--builder-diagnostics", action="store_true", help="Run focused then full source-mode Builder tests")
    validate_parser.add_argument("--packages", help="JSON version-selected package IDs; omitted means full validation")
    run_parser = commands.add_parser("run")
    run_parser.add_argument("--state", required=True)
    run_parser.add_argument("--cwd")
    run_parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "matrices": matrices(args.package_tests)
    elif args.action in ("test", "report", "retain-verifier", "test-family"):
        import test_lanes
        if args.action == "test": test_lanes.tests(args.state, args.lane)
        elif args.action == "report": test_lanes.report(args.mode, args.platform)
        elif args.action == "retain-verifier": test_lanes.retain_verifier()
        else: test_lanes.family(args.core, args.directory)
    elif args.action == "resolve": resolve(args.source, args.core, args.output)
    elif args.action == "coordinates": coordinates(args.source)
    elif args.action == "prepare": prepare(args)
    elif args.action == "preflight": preflight(args)
    elif args.action == "validate": validate(args)
    else:
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        return execute(args.state, command, args.cwd)
    return 0


if __name__ == "__main__":
    configure_console()
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as error:
        console_write("Agent CI failed: " + describe_error(error) + "\n", sys.stderr)
        raise SystemExit(error.returncode)
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("Agent CI failed: " + describe_error(error))
