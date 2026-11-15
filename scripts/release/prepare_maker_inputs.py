#!/usr/bin/env python3
"""Pack the six exact Core developer packages for native Maker acceptance, without publication."""
import argparse
import hashlib
import json
from pathlib import Path
import runpy
import re
import subprocess
import sys

from release_inputs import isolated_env


def prepare(core, output, expected_commit, *, host_only=False):
    core = Path(core).resolve(strict=True)
    if not re.fullmatch(r"[0-9a-f]{40}", expected_commit):
        raise ValueError("Maker input source requires an exact lowercase Core commit")
    commit = subprocess.check_output(["git", "-C", str(core), "rev-parse", "HEAD"], text=True).strip()
    if commit != expected_commit:
        raise ValueError("Maker input source differs from the selected Core commit")
    initially_clean = not subprocess.check_output(["git", "-C", str(core), "status", "--porcelain"], text=True).strip()
    output = Path(output).absolute()
    if output.exists() or output.is_symlink():
        raise ValueError("Maker input output must be new; retained candidates are never overwritten")
    output.mkdir(parents=True)
    environment = isolated_env(output / "home", output / "home/contracts/v1/sha256")
    environment.pop("SUNDER_AGENT_NATIVE_REGISTRY_TOKEN", None)
    environment.pop("SUNDER_REGISTRY_NATIVE_READ_TOKEN", None)
    feed = output / "feed"
    feed.mkdir()
    verifier = runpy.run_path(str(core / "scripts/release/verify-nuget-package-set.py"))
    rows = json.loads((core / "eng/test-oracles/core-public-packages.json").read_text())["nuget"]
    if {row["packageId"] for row in rows} != set(verifier["PACKAGE_IDS"]) or len(rows) != 6:
        raise ValueError("Maker acceptance requires the complete unique public Core NuGet surface")
    packages = []
    for row in ([] if host_only else rows):
        project = core / row["project"]
        properties = ["-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false",
                      "-p:ArtifactsPath=" + str(output / "nuget-artifacts")]
        with (output / (row["packageId"] + ".pack.log")).open("wb") as log:
            subprocess.run(["dotnet", "restore", str(project), "--locked-mode", *properties], cwd=core,
                           env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
            subprocess.run(["dotnet", "pack", str(project), "-c", "Release", "--no-restore", *properties, "-o", str(feed)],
                           cwd=core, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
        value = json.loads(subprocess.check_output(["dotnet", "msbuild", str(project), "-p:Configuration=Release",
            *properties, "-getProperty:PackageId,PackageVersion"], cwd=core, env=environment, text=True))["Properties"]
        if value["PackageId"] != row["packageId"]:
            raise ValueError("Core's package inventory differs from its evaluated NuGet identity")
        archive = feed / (value["PackageId"] + "." + value["PackageVersion"] + ".nupkg")
        verifier["verify_archive"](archive, value["PackageId"], value["PackageVersion"], commit)
        packages.append(dict(packageId=value["PackageId"], version=value["PackageVersion"], name=archive.name,
                             size=archive.stat().st_size, sha256=hashlib.sha256(archive.read_bytes()).hexdigest()))
    if {path.name for path in feed.iterdir()} != {row["name"] for row in packages}:
        raise ValueError("Maker feed contains missing or extra archives")
    catalog = prepare_catalog(core, output, environment)
    app = prepare_app(core, output, environment)
    if subprocess.check_output(["git", "-C", str(core), "rev-parse", "HEAD"], text=True).strip() != commit:
        raise ValueError("Core source commit changed during Maker input preparation; outputs were retained")
    clean = initially_clean and not subprocess.check_output(["git", "-C", str(core), "status", "--porcelain"], text=True).strip()
    (output / "inputs.json").write_text(json.dumps(dict(schemaVersion=2, coreCommit=commit, workingTreeClean=clean,
        packages=packages, platformCatalog=file_record(catalog, output), app=app), indent=2) + "\n", encoding="utf-8")
    return feed


def file_record(path, root):
    return dict(path=path.relative_to(root).as_posix(), size=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest())


def build_host_project(core, output, environment, relative, log_name):
    project = core / "languages/csharp/dotnet" / relative
    properties = ["-p:ArtifactsPath=" + str(output / "host-artifacts"), "-p:GeneratePackageOnBuild=false"]
    with (output / log_name).open("wb") as log:
        subprocess.run(["dotnet", "restore", str(project), "--locked-mode", *properties], cwd=core, env=environment,
                       stdout=log, stderr=subprocess.STDOUT, check=True)
        subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--no-restore", *properties], cwd=core, env=environment,
                       stdout=log, stderr=subprocess.STDOUT, check=True)
    path = Path(subprocess.check_output(["dotnet", "msbuild", str(project), "-p:Configuration=Release", *properties,
        "-getProperty:TargetPath"], cwd=core, env=environment, text=True).strip()).resolve(strict=True)
    path.relative_to(output)
    return path


def prepare_catalog(core, output, environment):
    baseline = json.loads((core / "eng/platform-contracts/sunder-contracts.owned.json").read_text())
    releases = {row["current"]["catalogRelease"]["releaseVersion"] for row in baseline["contracts"].values()}
    if len(releases) != 1:
        raise ValueError("Ambiguous source Core catalog release")
    tool = build_host_project(core, output, environment, Path("tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj"), "catalog.log")
    catalog = output / "sunder-platform-contracts.v1.json"
    with (output / "catalog.log").open("ab") as log:
        for arguments in (("check", "--core-root", core), ("export", "--core-root", core, "--catalog-release", releases.pop(), "--output", catalog)):
            subprocess.run(["dotnet", str(tool), "platform-contracts", *map(str, arguments)], cwd=core, env=environment,
                           stdout=log, stderr=subprocess.STDOUT, check=True)
    return catalog


def prepare_app(core, output, environment):
    target = build_host_project(core, output, environment, Path("host/Sunder.App/Sunder.App.csproj"), "app.log")
    paths = sorted(path for path in target.parent.rglob("*") if path.is_file())
    if any(path.is_symlink() for path in paths):
        raise ValueError("Native App inputs must be regular build outputs")
    return dict(targetPath=target.relative_to(output).as_posix(), files=[file_record(path, output) for path in paths])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("core", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("expected_commit")
    parser.add_argument("--host-only", action="store_true", help="Prepare the exact App/catalog without repacking Maker's six authoring NuGets.")
    args = parser.parse_args()
    print(prepare(args.core, args.output, args.expected_commit, host_only=args.host_only))
