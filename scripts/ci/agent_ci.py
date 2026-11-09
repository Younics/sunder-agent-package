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


def resolve(source, core, output, packages=None, *, allow_missing=False):
    import dependency_inputs
    return dependency_inputs.resolve(source, core, output, packages=packages, allow_missing=allow_missing)


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


def selected_files(args, source, core, packages=None):
    if args.inputs:
        root = inputs.directory(args.inputs)
        if (root / inputs.MANIFEST).exists():
            value = inputs.verify(source, root, inputs.file_sha(root / inputs.MANIFEST), head(source), head(core), packages)
            paths = [root / item["asset"]["name"] for item in value["candidates"]]
            catalog = root / value["platformCatalog"]["asset"]["name"]
            return scoped_candidates(source, paths, packages), catalog
        value = inputs.load(root / "ci-inputs.json")
        inputs.closed(value, "schemaVersion coreCommit candidates platformCatalog" + (" packageIds" if value.get("schemaVersion") == 3 else ""))
        inputs.require(value["schemaVersion"] in (1, 2, 3) and value["coreCommit"] == head(core), "CI Core checkout differs from the selected input")
        declared = inputs.package_scope(source, value.get("packageIds"))
        inputs.require(value.get("packageIds", declared) == declared and set(inputs.package_scope(source, packages)) <= set(declared),
                       "CI inputs do not cover the requested package scope")
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
        inputs.verify_selected_files(source, paths[:-1], paths[-1], declared)
        return scoped_candidates(source, paths[:-1], packages), paths[-1]
    inputs.require(args.catalog, "Supply exact --candidate and --catalog files, or --inputs")
    return [inputs.regular(path).resolve() for path in args.candidate or []], inputs.regular(args.catalog).resolve()


def scoped_candidates(source, candidates, packages):
    required = {key for _, lock in inputs.lock_documents(source, packages) for key in lock["packageArtifacts"]}
    return [path for path in candidates if inputs.archive_metadata(path)[0]["id"] in required]


def test_dependency_scope(source, selected):
    import project_scope
    source = Path(source).resolve(strict=True)
    inventory = inputs.load(generate_inventory(source, selected))
    scope = set()
    for mode in ("source", "package"):
        roots = project_scope.projects(source, selected, inventory, mode)
        props = {"SunderUseSourceSdk": "true" if mode == "source" else "false", "Configuration": "Release"}
        scope.update(project_scope.packages(source, roots, props, os.environ))
    return sorted(scope)


def arguments(properties):
    return ["-p:" + key + "=" + value for key, value in properties.items()]


def output_directory(project, env):
    value = subprocess.run(["dotnet", "msbuild", str(project), "-p:Configuration=Release", "-getProperty:TargetDir"],
        cwd=Path(project).parent, env=env, check=True, text=True, stdout=subprocess.PIPE).stdout.strip()
    inputs.require(value and Path(value).is_absolute(), "Missing evaluated test/tool output directory")
    return Path(value)


def prepare(args):
    import core_tools
    import project_scope
    import version_selection
    source, core = Path(args.source).resolve(strict=True), Path(args.core).resolve(strict=True)
    inputs.require(core == source.parent / "sunder-core", "Core must be the explicit sibling source checkout")
    before = inputs.source_snapshot(source)
    output = Path(args.work).resolve()
    inputs.require(not output.is_relative_to(source) and not output.is_relative_to(core), "Keep CI state outside source checkouts")
    home, cache = output / "home", output / "home/contracts/v1/sha256"
    env = inputs.isolated_env(home, cache)
    props = {"Configuration": "Release", "SunderUseSourceSdk": "false" if args.mode == "package" else "true", "ContinuousIntegrationBuild": "true",
             "SunderContractCacheRoot": str(cache)}
    selection = getattr(args, "packages", None)
    selection = None if selection is None else version_selection.package_ids(source, json.loads(selection))
    projects = getattr(args, "project", None) or project_scope.projects(source, selection, {"projects": []}, args.mode)
    inputs.require(projects, "No component projects selected")
    for project in projects:
        inputs.source_file(source, project)
    scope = project_scope.packages(source, projects, props, env)
    candidates, catalog = selected_files(args, source, core, scope) if scope else ([], None)
    selected = inputs.verify_selected_files(source, candidates, catalog, scope) if scope else {}
    root = new_directory(args.work)
    home.mkdir()
    try:
        for project in projects:
            subprocess.run(["dotnet", "restore", str(source / project), "--locked-mode", *arguments(props)], env=env, check=True)
        tools = (core_tools.verify(core, args.tools) if getattr(args, "tools", None)
                 else core_tools.prepare(core, root / "tools"))
        package_tools = (core_tools.package_tasks(source,
            [row["projectPath"] for row, _ in inputs.lock_documents(source, scope)], props, env)
            if args.mode == "package" and scope else {})
        props.update(SunderPackageBuildTaskAssembly=str(package_tools.get("Sunder.Package.Build.dll", tools / "Sunder.Package.Build.dll")),
                     SunderPackageBuildTaskSourceIsImmutable="true")
        for package, lock in inputs.lock_documents(source, scope) if scope else []:
            command = ["dotnet", "msbuild", str(source / package["projectPath"]), "-t:RestoreSunderContracts", *arguments(props)]
            paths = [str(selected[key]) for key in sorted(lock["packageArtifacts"])]
            inputs.require(all(not any(c in path for c in "%\r\n;") for path in [str(catalog), *paths]), "Ambiguous MSBuild input path")
            if paths: command.append("-p:SunderPackageCandidatePath=" + "%3B".join(paths))
            if inputs.catalog_requirements(lock): command.append("-p:SunderPlatformCatalogPath=" + str(catalog))
            if paths or inputs.catalog_requirements(lock): subprocess.run(command, cwd=source, env=env, check=True)
        state = dict(schemaVersion=2, source=str(source), core=str(core), sourceCommit=head(source), coreCommit=head(core),
                      home=str(home), cache=str(cache), properties=props, sourceInputs=before,
                      candidates=[str(path) for path in candidates], catalog=str(catalog) if catalog else None,
                      packages=scope, selectedPackages=selection, projects=projects, tools=str(tools),
                      inputHashes={str(path): inputs.file_sha(path) for path in [*candidates, *([catalog] if catalog else []), *package_tools.values(), tools / core_tools.MANIFEST,
                                   *[path for path in sorted(tools.rglob("*")) if path.is_file()]]})
        (root / "ci-state.json").write_bytes(inputs.canonical(state) + b"\n")
        print(root / "ci-state.json")
    finally:
        inputs.require(inputs.source_snapshot(source) == before, "CI hydration changed committed contract/release inputs")


def context(path):
    import core_tools
    path = Path(path).resolve(strict=True)
    state = inputs.load(path)
    inputs.closed(state, "schemaVersion source core sourceCommit coreCommit home cache properties sourceInputs candidates catalog inputHashes"
                  + (" packages selectedPackages projects tools" if state.get("schemaVersion") == 2 else ""))
    inputs.require(state["schemaVersion"] in (1, 2) and head(state["source"]) == state["sourceCommit"]
                   and head(state["core"]) == state["coreCommit"], "CI checkout identity changed")
    inputs.require(inputs.source_snapshot(state["source"]) == state["sourceInputs"], "CI contract/release inputs changed")
    inputs.require(all(inputs.file_sha(file) == digest for file, digest in state["inputHashes"].items()), "CI input/tool bytes changed")
    if state.get("packages") == []:
        inputs.require(not state["candidates"] and state["catalog"] is None, "Dependency-free test roots cannot receive contract inputs")
    else:
        inputs.verify_selected_files(state["source"], state["candidates"], state["catalog"], state.get("packages"))
    if state["schemaVersion"] == 2:
        core_tools.verify(state["core"], state["tools"])
    props = state["properties"]
    inputs.require(props["SunderUseSourceSdk"] in ("true", "false") and props["Configuration"] == "Release"
                   and props["SunderPackageBuildTaskSourceIsImmutable"] == "true" and props["SunderContractCacheRoot"] == state["cache"],
                   "CI build context differs")
    env = inputs.isolated_env(state["home"], state["cache"])
    env.update(props)
    if state["catalog"] is not None:
        env["SUNDER_AGENT_SOURCE_PLATFORM_CATALOG"] = state["catalog"]
    if state.get("tools"):
        env["SUNDER_AGENT_CORE_TEST_DRIVER"] = str(Path(state["tools"]) / core_tools.DRIVER)
    return state, env


def execute(state_path, command, cwd=None, log=None):
    state, env = context(state_path)
    command = list(command)
    inputs.require(command, "A CI command is required")
    if command[0] == "dotnet":
        inputs.require(len(command) > 1 and command[1] in ("build", "test", "restore", "msbuild"), "Unsupported CI .NET command")
        if command[1] in ("build", "test"):
            inputs.require("--no-restore" in command, "Use the explicitly restored dependency graph")
        if state.get("projects"):
            target = Path(cwd or state["source"]) / command[2]
            prepared = {Path(state["source"]) / project for project in state["projects"]}
            inputs.require("Sunder.AgentPackage.slnx" in state["projects"] or target.resolve() in {project.resolve() for project in prepared},
                           "Command targets a project outside the prepared component scope: " + str(target))
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
    if args.mode == "package":
        inventory = inputs.load(inventory_path)
        inventory["projects"] = [row for row in inventory["projects"]
                                 if "package" in row["modes"] and "PackageCompatibility" in row.get("lanes", [])]
        inventory_path.write_bytes(inputs.canonical(inventory) + b"\n")
        if not inventory["projects"]:
            (inventory_path.parent / "compatibility-selection.json").write_bytes(inputs.canonical(dict(
                schemaVersion=1, packageIds=selected, disposition="no-owned-compatibility", projects=[])) + b"\n")
            print("No locked-SDK compatibility cases belong to this component; selection retained.")
            return
    import project_scope
    if getattr(args, "shared", False):
        inputs.require(selected is None, "Shared family validation is an explicit full-validation lane")
        inventory = inputs.load(inventory_path)
        inventory["projects"] = [row for row in inventory["projects"] if row["owner"] == "agent-family"]
        inventory_path.write_bytes(inputs.canonical(inventory) + b"\n")
        args.project = [row["path"] for row in inventory["projects"] if args.mode in row["modes"]]
    else:
        args.project = project_scope.projects(source, selected, inputs.load(inventory_path) if selected is not None else {}, args.mode)
    with tempfile.TemporaryDirectory(prefix="sunder-agent-ci-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
        root = Path(temporary)
        if not args.inputs and not args.candidate:
            # Include actual test dependencies when preparing local inputs, too.
            props = {"SunderUseSourceSdk": "false" if args.mode == "package" else "true", "Configuration": "Release"}
            scope = project_scope.packages(source, args.project, props, os.environ)
            resolve(source, args.core, root / "inputs", scope)
            args.inputs = root / "inputs"
        args.work = root / "prepared"
        prepare(args)
        state = args.work / "ci-state.json"
        if builder_diagnostics:
            test_lanes.builder_diagnostics(state, inventory_path)
            return
        for project in args.project:
            status = execute(state, ["dotnet", "build", project, "-c", "Release", "--no-restore"])
            if status: raise subprocess.CalledProcessError(status, "dotnet build " + project)
        test_lanes.validate(state, args.mode, args.stress)


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
    verifier_parser = commands.add_parser("prepare-verifier")
    verifier_parser.add_argument("--core", required=True)
    verifier_parser.add_argument("--tools", required=True)
    family_parser = commands.add_parser("test-family")
    family_parser.add_argument("--core", required=True)
    family_parser.add_argument("--directory", required=True)
    resolve_parser = commands.add_parser("resolve")
    resolve_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    resolve_parser.add_argument("--core", required=True)
    resolve_parser.add_argument("--output", required=True)
    resolve_parser.add_argument("--packages", help="JSON package IDs whose exact dependencies are required")
    resolve_parser.add_argument("--allow-missing", action="store_true", help="Prepare published inputs and schedule native unpublished owners")
    resolve_parser.add_argument("--test-dependencies", action="store_true", help="Include the evaluated owned test dependency closure")
    complete_parser = commands.add_parser("complete-inputs")
    complete_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    complete_parser.add_argument("--core", required=True)
    complete_parser.add_argument("--inputs", required=True)
    complete_parser.add_argument("--candidates", required=True)
    tools_parser = commands.add_parser("prepare-tools")
    tools_parser.add_argument("--core", required=True)
    tools_parser.add_argument("--output", required=True)
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
    prepare_parser.add_argument("--packages", help="JSON component package IDs; omitted means explicit full preparation")
    prepare_parser.add_argument("--project", action="append", help="Explicit restore/build root, relative to the Agent repository")
    prepare_parser.add_argument("--tools", help="Verified platform Core tool handoff")
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
    validate_parser.add_argument("--shared", action="store_true", help="Run shared family assertions once during full validation")
    validate_parser.add_argument("--packages", help="JSON version-selected package IDs; omitted means full validation")
    validate_parser.add_argument("--tools", help="Verified platform Core tool handoff")
    run_parser = commands.add_parser("run")
    run_parser.add_argument("--state", required=True)
    run_parser.add_argument("--cwd")
    run_parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "matrices": matrices(args.package_tests)
    elif args.action in ("test", "report", "retain-verifier", "prepare-verifier", "test-family"):
        import test_lanes
        if args.action == "test": test_lanes.tests(args.state, args.lane)
        elif args.action == "report": test_lanes.report(args.mode, args.platform)
        elif args.action == "retain-verifier": test_lanes.retain_verifier()
        elif args.action == "prepare-verifier": test_lanes.prepare_verifier(args.core, args.tools)
        else: test_lanes.family(args.core, args.directory)
    elif args.action == "resolve":
        selected = None if args.packages is None else json.loads(args.packages)
        if args.test_dependencies: selected = test_dependency_scope(args.source, selected)
        resolve(args.source, args.core, args.output, selected, allow_missing=args.allow_missing)
    elif args.action == "complete-inputs":
        import dependency_inputs
        dependency_inputs.complete(args.source, args.core, args.inputs, args.candidates)
    elif args.action == "prepare-tools":
        import core_tools
        core_tools.prepare(args.core, args.output)
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
