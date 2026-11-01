#!/usr/bin/env python3
"""Prepare or check exact local authoring selections through Core, never a Registry.

NuGet restore is a separate prerequisite. This command does not select contracts,
accept/freeze baselines, synthesize receipts, or rebuild candidate archives.
"""
import argparse
import os
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import release_inputs as inputs


def prepare(source, candidates=(), catalog=None, cache_root=None, check=False):
    source = Path(source).resolve(strict=True)
    documents = inputs.lock_documents(source)
    before = inputs.source_snapshot(source)
    selected = {} if check else inputs.verify_selected_files(source, candidates, catalog)
    properties = []
    if cache_root is not None:
        inputs.require(bool(str(cache_root).strip()), "An explicit cache root must be nonempty")
        cache_root = str(Path(cache_root).expanduser().resolve())
        inputs.require(not any(char in cache_root for char in "%\r\n;"), "Ambiguous MSBuild cache path")
        properties.append("-p:SunderContractCacheRoot=" + cache_root)
    paths = [*selected.values(), *([catalog] if catalog is not None else [])]
    inputs.require(all(not any(char in str(path) for char in "%\r\n;") for path in paths), "Ambiguous MSBuild input path")

    def run(package, target, extra=()):
        return subprocess.run(["dotnet", "msbuild", str(source / package["projectPath"]),
                               "-t:" + target, *properties, *extra], cwd=source, check=True)

    try:
        if not check:
            for package, lock in documents:
                extra = []
                paths = [str(selected[key]) for key in sorted(lock["packageArtifacts"])]
                if paths: extra.append("-p:SunderPackageCandidatePath=" + "%3B".join(paths))
                if inputs.catalog_requirements(lock): extra.append("-p:SunderPlatformCatalogPath=" + str(Path(catalog).resolve()))
                if extra: run(package, "RestoreSunderContracts", extra)
        for package, _ in documents:
            run(package, "CheckSunderContractCache")
    finally:
        inputs.require(inputs.source_snapshot(source) == before, "Contract preparation changed authored locks, baselines or release inputs")
    print(f"Verified {len(documents)} canonical projects; authored selections are unchanged.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--candidate", action="append", default=[])
    parser.add_argument("--catalog")
    parser.add_argument("--cache-root", default=os.environ.get("SunderContractCacheRoot"))
    parser.add_argument("--check", action="store_true", help="Verify cache readiness without restoring contract evidence")
    args = parser.parse_args()
    if args.check:
        if args.candidate or args.catalog:
            parser.error("--check does not accept restore inputs")
    elif not args.candidate or not args.catalog:
        parser.error("Supply exact --candidate and --catalog inputs")
    prepare(args.source, args.candidate, args.catalog, args.cache_root, args.check)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit("Local contract preparation failed: " + str(error))
