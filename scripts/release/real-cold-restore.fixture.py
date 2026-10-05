#!/usr/bin/env python3
"""Isolated owning integration: actual Core Restore task + retained Agent inputs.

Usage: <agent-root> <existing-Sunder.Package.Build.dll> <retained-core-catalog.json>
The task assembly is used read-only; only temporary project/lock copies and a new
Sunder home are written. No Core or Agent project is built or modified.
"""

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

import agent_release as release
import release_inputs as inputs


def main(source, tasks, catalog):
    source, tasks, catalog = Path(source).resolve(), release.regular(tasks).resolve(), release.regular(catalog).resolve()
    before = inputs.source_snapshot(source)
    with tempfile.TemporaryDirectory(prefix="agent-real-cold-") as temporary:
        work = Path(temporary).resolve()
        home = work / "sunder-home"
        home.mkdir()
        cache = home / "contracts/v1/sha256"
        env = inputs.isolated_env(home, cache)
        prefix = inputs.offline_prefix()
        inputs.verify_network_denial(prefix, env, source)
        core_task_name = "Sunder.Package.Build.Tasks.RestoreSunderContractsTask"
        projects = []
        for package, lock in inputs.lock_documents(source):
            directory = work / package["packageId"]
            directory.mkdir()
            original = source / Path(package["projectPath"]).parent / "sunder-contracts.lock.json"
            shutil.copyfile(original, directory / "sunder-contracts.lock.json")
            project = ET.Element("Project")
            ET.SubElement(project, "UsingTask", TaskName=core_task_name, AssemblyFile=str(tasks))
            group = ET.SubElement(project, "ItemGroup")
            ET.SubElement(group, "Candidate", Include="$(SunderPackageCandidatePath)", Condition="'$(SunderPackageCandidatePath)' != ''")
            target = ET.SubElement(project, "Target", Name="RestoreSunderContracts")
            ET.SubElement(target, core_task_name, ProjectDirectory=str(directory), ContractLockPath=str(directory / "sunder-contracts.lock.json"),
                          ContractCacheRoot=str(cache), CandidateArchives="@(Candidate)", PlatformCatalogPath="$(SunderPlatformCatalogPath)")
            path = directory / "Restore.proj"
            ET.ElementTree(project).write(path, encoding="utf-8", xml_declaration=True)
            projects.append((path, lock, original))
        owner = next(p for p in release.inventory(source)["packages"] if p["packageId"] == "sunder.package.agent")
        candidate_project = Path(owner["projectPath"])
        candidate = source / candidate_project.parent / "bin/Debug/net10.0" / f"{candidate_project.stem}.2.0.0.sunderpkg"
        probe = next(path for path, lock, _ in projects if lock["packageArtifacts"])
        common = ["-t:RestoreSunderContracts", "-nr:false", "-p:SunderPlatformCatalogPath=" + str(catalog)]
        missing = subprocess.run([*prefix, "dotnet", "msbuild", str(probe), *common], env=env, text=True, capture_output=True)
        release.require(missing.returncode != 0 and "contract-cache.entry-missing" in missing.stdout + missing.stderr and not cache.exists(),
                        "Actual Core cold-cache missing diagnostic did not hold: " + missing.stdout + missing.stderr)
        for path, lock, original in projects:
            args = [*prefix, "dotnet", "msbuild", str(path), *common]
            if lock["packageArtifacts"]:
                args.append("-p:SunderPackageCandidatePath=" + str(candidate))
            completed = subprocess.run(args, env=env, text=True, capture_output=True)
            release.require(completed.returncode == 0, "Core exact hydration failed: " + completed.stdout + completed.stderr)
            release.require((path.parent / "sunder-contracts.lock.json").read_bytes() == original.read_bytes(), "Core restore changed copied lock bytes")
        state = {p.relative_to(cache).as_posix(): (release.file_sha(p), p.stat().st_mtime_ns) for p in cache.rglob("*") if p.is_file()}
        # Exact offline replay must preserve all Core-owned body/receipt bytes and mtimes.
        repeated = subprocess.run([*prefix, "dotnet", "msbuild", str(probe), *common, "-p:SunderPackageCandidatePath=" + str(candidate)], env=env, text=True, capture_output=True)
        release.require(repeated.returncode == 0, repeated.stdout + repeated.stderr)
        release.require(state == {p.relative_to(cache).as_posix(): (release.file_sha(p), p.stat().st_mtime_ns) for p in cache.rglob("*") if p.is_file()}, "Warm exact replay changed cache evidence")
        release.require(inputs.source_snapshot(source) == before, "Owning cold integration changed real source inputs")
        print("Actual Core task passed: isolated missing-cache diagnostic, 15 exact offline hydrations, unchanged locks/baselines, byte/mtime-preserving replay.")


if __name__ == "__main__":
    main(*sys.argv[1:])
