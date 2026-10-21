#!/usr/bin/env python3
"""Prepare exact locked local inputs for ordinary CI, then run normal .NET commands.

Production cold/release approval remains in release_inputs.py. This entry point
uses its byte/lock validator and Core's RestoreSunderContracts, without minting
release evidence or requiring production credentials and offline runner adapters.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import release_inputs as inputs


def head(root):
    return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()


def new_directory(path):
    path = Path(path).absolute()
    inputs.require(not path.exists() and not path.is_symlink(), "CI output must be a new directory")
    path.mkdir(parents=True)
    return path.resolve()


def resolve(locator_json, output, cold=False, core_commit=None):
    selected = inputs.locator(inputs.decode(locator_json.encode()), inputs.MAX_MANIFEST)
    if cold:
        source = Path(__file__).resolve().parents[2]
        inputs.digest(core_commit, 40)
        inputs.fetch(source, output, head(source), core_commit, selected=selected)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.write("core_commit=" + core_commit + "\n")
        return
    root = new_directory(output)
    inputs.download(selected, root / "ci-inputs.json", inputs.MAX_MANIFEST)
    value = inputs.load(root / "ci-inputs.json")
    inputs.closed(value, "schemaVersion coreCommit candidates platformCatalog")
    inputs.require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1, "Unknown CI input schema")
    inputs.digest(value["coreCommit"], 40)
    inputs.require(type(value["candidates"]) is list and 0 < len(value["candidates"]) <= 128, "Invalid CI candidate set")
    names = {"ci-inputs.json"}
    for item in [*value["candidates"], value["platformCatalog"]]:
        maximum = 67108864 if item is value["platformCatalog"] else 268435456
        inputs.locator(item, maximum)
        inputs.require(item["name"].casefold() not in names, "CI asset name collision")
        names.add(item["name"].casefold())
        expected_repo = "Younics/sunder-core" if item is value["platformCatalog"] else "Younics/sunder-agent-package"
        inputs.require(item["repository"] == expected_repo, "Unexpected CI input repository")
        inputs.download(item, root / item["name"], maximum)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write("core_commit=" + value["coreCommit"] + "\n")
    print(value["coreCommit"])


def selected_files(args, source, core):
    if args.inputs:
        root = inputs.directory(args.inputs)
        if (root / inputs.MANIFEST).exists():
            value = inputs.verify(source, root, inputs.file_sha(root / inputs.MANIFEST), head(source), head(core))
            return ([root / item["asset"]["name"] for item in value["candidates"]], root / value["platformCatalog"]["asset"]["name"])
        value = inputs.load(root / "ci-inputs.json")
        inputs.closed(value, "schemaVersion coreCommit candidates platformCatalog")
        inputs.require(value["schemaVersion"] == 1 and value["coreCommit"] == head(core), "CI Core checkout differs from the selected input")
        paths = []
        for item in [*value["candidates"], value["platformCatalog"]]:
            inputs.locator(item)
            path = inputs.regular(root / item["name"])
            inputs.require(path.stat().st_size == item["size"] and inputs.file_sha(path) == item["sha256"], "CI input changed after download")
            paths.append(path.resolve())
        return paths[:-1], paths[-1]
    inputs.require(args.candidate and args.catalog, "Supply exact --candidate and --catalog files, or --inputs")
    return [inputs.regular(path).resolve() for path in args.candidate], inputs.regular(args.catalog).resolve()


def arguments(properties):
    return ["-p:" + key + "=" + value for key, value in properties.items()]


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
             "SunderAgentVersion": inputs.inventory(source)["releaseVersion"], "SunderContractCacheRoot": str(cache)}
    try:
        # Explicit full-graph dependency restore precedes package contract hydration.
        subprocess.run(["dotnet", "restore", str(source / "Sunder.AgentPackage.slnx"), "--locked-mode", *arguments(props)], env=env, check=True)
        driver = core / "languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj"
        subprocess.run(["dotnet", "restore", str(driver), "--locked-mode"], env=env, check=True)
        subprocess.run(["dotnet", "build", str(driver), "-c", "Release", "--no-restore",
                        "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false"], env=env, check=True)
        tools = root / "tools"
        shutil.copytree(driver.parent / "bin/Release/net10.0", tools)
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
    fixture = path.parent / "provider-fixture/test-environment.json"
    if fixture.exists():
        values = inputs.load(fixture)
        inputs.require(all(key.startswith(("SUNDER_AGENT_SHARED_PROVIDER_FIXTURE_", "SUNDER_AGENT_PUBLIC_FIXTURE_"))
                           and isinstance(value, str) for key, value in values.items()), "Unexpected fixture environment")
        env.update(values)
    return state, env


def fixtures(state_path):
    state, env = context(state_path)
    source, core = Path(state["source"]), Path(state["core"])
    tools = Path(state["properties"]["SunderPackageBuildTaskAssembly"]).parent
    test_output = source / "tests/Sunder.Package.Agent.Tests/bin/Release/net10.0"
    selected = inputs.verify_selected_files(source, state["candidates"], state["catalog"])
    values = {"candidate": selected["younics.ai.agent"], "driver": tools / "Sunder.Package.Build.Tests.dll",
              "sdk": test_output / "Sunder.Sdk.dll", "worker-sdk": test_output / "Sunder.Sdk.Worker.dll",
              "di-abstractions": test_output / "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "platform-catalog": Path(state["catalog"])}
    command = [sys.executable, "-B", str(source / "scripts/test/prepare-shared-provider-fixture.py")]
    for name, path in values.items(): command += ["--" + name, str(path), "--" + name + "-sha256", inputs.file_sha(path)]
    command += ["--task-sha256", inputs.file_sha(tools / "Sunder.Package.Build.dll"), "--core-root", str(core),
                "--seed-lock-sha256", inputs.file_sha(core / "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"),
                "--output", str(Path(state_path).resolve().parent / "provider-fixture")]
    subprocess.run(command, env=env, cwd=source, check=True)
    context(state_path)


def execute(state_path, command, cwd=None):
    state, env = context(state_path)
    inputs.require(command, "A CI command is required")
    if command[0] == "dotnet":
        inputs.require(len(command) > 1 and command[1] in ("build", "test", "restore", "msbuild"), "Unsupported CI .NET command")
        if command[1] in ("build", "test"):
            inputs.require("--no-restore" in command, "Use the explicitly restored dependency graph")
        command += arguments(state["properties"])
    try:
        return subprocess.run(command, cwd=cwd or state["source"], env=env).returncode
    finally:
        context(state_path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    resolve_parser = commands.add_parser("resolve")
    resolve_parser.add_argument("--locator", default=os.environ.get("SUNDER_AGENT_CI_INPUTS"), required=False)
    resolve_parser.add_argument("--output", required=True)
    resolve_parser.add_argument("--cold", action="store_true")
    resolve_parser.add_argument("--core-commit")
    prepare_parser = commands.add_parser("prepare")
    prepare_parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    prepare_parser.add_argument("--core", required=True)
    prepare_parser.add_argument("--work", required=True)
    prepare_parser.add_argument("--inputs")
    prepare_parser.add_argument("--candidate", action="append")
    prepare_parser.add_argument("--catalog")
    prepare_parser.add_argument("--mode", choices=("source", "package"), default="source")
    for name in ("fixtures", "run"):
        subparser = commands.add_parser(name)
        subparser.add_argument("--state", required=True)
        if name == "run":
            subparser.add_argument("--cwd")
            subparser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "resolve":
        inputs.require(bool(args.locator), "SUNDER_AGENT_CI_INPUTS must locate the exact retained CI inputs")
        resolve(args.locator, args.output, args.cold, args.core_commit)
    elif args.action == "prepare": prepare(args)
    elif args.action == "fixtures": fixtures(args.state)
    else:
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        return execute(args.state, command, args.cwd)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit("Agent CI preparation failed: " + str(error))
