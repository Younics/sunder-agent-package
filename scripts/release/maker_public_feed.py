"""Use NuGet restore to seed Maker's test-local feed with published authoring inputs."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

import release_inputs as inputs

ROOT = Path(__file__).resolve().parents[2]
LOCK = "eng/ci/maker-nuget.lock.json"
PACKAGE_IDS = frozenset(("Sunder.Package.Build", "Sunder.Package.Templates", "Sunder.Sdk",
                         "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker"))


def selection(source):
    lock = inputs.load(inputs.source_file(source, LOCK))
    inputs.closed(lock, "schemaVersion packages")
    records = lock["packages"]
    for item in records:
        inputs.closed(item, "packageId version")
        inputs.version(item["version"])
        inputs.require("+" not in item["version"], "Expected a NuGet version without build metadata")
    inputs.require(type(lock["schemaVersion"]) is int and lock["schemaVersion"] == 1
                   and len(records) == len(PACKAGE_IDS)
                   and {row["packageId"] for row in records} == PACKAGE_IDS,
                   "Maker requires one exact publication for each authoring package")
    return records


def prepare(root, source=ROOT):
    records = selection(source)
    root = inputs.directory(root)
    feed = root / "feed"
    feed.mkdir()
    cache = root / "nuget"
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    props = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(props, "TargetFramework").text = "net10.0"
    packages = ET.SubElement(project, "ItemGroup")
    for row in records:
        ET.SubElement(packages, "PackageDownload", Include=row["packageId"], Version=f"[{row['version']}]")
    restore = root / "MakerInputs.csproj"
    ET.ElementTree(project).write(restore, encoding="utf-8")
    env = inputs.isolated_env(root / "home", root / "home/contracts")
    env = {key: value for key, value in env.items() if not key.lower().startswith(("restore", "nuget_"))}
    subprocess.run(["dotnet", "restore", str(restore), "--configfile", str(source / "NuGet.Config"),
                    "--packages", str(cache), "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false",
                    "-p:ImportDirectoryPackagesProps=false"], cwd=source, env=env, check=True, timeout=180)
    result = []
    for row in records:
        package_id, version = row["packageId"], row["version"]
        name = f"{package_id}.{version}.nupkg"
        path = cache / package_id.lower() / version.lower() / name.lower()
        shutil.copyfile(inputs.regular(path), feed / name)
        result.append(dict(row, name=name, size=path.stat().st_size, sha256=inputs.file_sha(path)))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Existing scratch directory with no feed or receipt yet")
    args = parser.parse_args()
    rows = prepare(args.directory)
    with (args.directory / "maker-feed.json").open("x", encoding="utf-8") as stream:
        json.dump(dict(schemaVersion=1, packages=rows), stream, indent=2)
        stream.write("\n")
