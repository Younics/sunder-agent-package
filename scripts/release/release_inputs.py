#!/usr/bin/env python3
"""Authored dependencies -> exact cold Core restore -> network-denied acceptance.

This is release orchestration, not another Core cache/qualification writer. Only
RestoreSunderContracts creates cache evidence. No selection or baseline command is
used, and every command is fenced by the original source-input byte inventory.
"""

import atexit
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import socket
import subprocess
import sys
import tempfile

from agent_release import (Invalid, archive_metadata, authoring_origin, canonical, closed,
                           decode, digest, file_sha, inventory, load, locks, positive,
                           package_imports, regular, require, safe_relative, sha, source_file, version)

from ci_platforms import RIDS
MANIFEST = "cold-inputs.json"
MAX_MANIFEST = 262144
TOOL_FILES = ("Sunder.Package.Build.dll", "Sunder.Package.Build.Editor.dll", "Sunder.Package.Format.dll", "Sunder.Sdk.dll",
              "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
              "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "Microsoft.Extensions.Logging.Abstractions.dll")


def python_version():
    require(sys.version_info >= (3, 9), "Agent release automation requires Python 3.9 or newer")


def directory(path):
    path = Path(path).absolute()
    require(not path.is_symlink(), "Release input directory cannot be a link")
    # Resolve trusted OS aliases such as macOS /var before checking owned descendants.
    path = path.parent.resolve() / path.name
    require(path.is_dir(), "Release input directory is missing")
    return path


def locator(value, maximum=268435456):
    closed(value, "repository releaseId assetId name size sha256")
    require(value["repository"] in ("Younics/sunder-core", "Younics/sunder-agent-package"), "Unreviewed input repository")
    positive(value["releaseId"])
    positive(value["assetId"])
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,159}", value["name"]), "Unsafe durable asset name")
    require(positive(value["size"]) <= maximum, "Durable input exceeds its byte bound")
    digest(value["sha256"])
    return value


def gh_json(route):
    data = subprocess.check_output(["gh", "api", "-H", "Accept: application/vnd.github+json", route])
    return decode(data)


def download(selected, output, maximum=268435456):
    selected = locator(selected, maximum)
    repository = selected["repository"]
    release = gh_json(f"repos/{repository}/releases/{selected['releaseId']}")
    require(release["id"] == selected["releaseId"], "Durable input release was replaced")
    matches = [a for a in release["assets"] if a["id"] == selected["assetId"] or a["name"] == selected["name"]]
    require(len(matches) == 1, "Durable input asset is missing, renamed, or ambiguous")
    asset = matches[0]
    route = f"repos/{repository}/releases/assets/{selected['assetId']}"
    require(asset["id"] == selected["assetId"] and asset["name"] == selected["name"]
            and asset["size"] == selected["size"] and asset["digest"] == "sha256:" + selected["sha256"]
            and asset["state"] == "uploaded" and asset["url"] == "https://api.github.com/" + route,
            "Durable input metadata differs from the reviewed locator")
    require(not output.exists() and not output.is_symlink(), "Input destination already exists")
    process = subprocess.Popen(["gh", "api", "-H", "Accept: application/octet-stream", route], stdout=subprocess.PIPE)
    try:
        count = 0
        with output.open("xb") as stream:
            while True:
                chunk = process.stdout.read(65536)
                if not chunk:
                    break
                count += len(chunk)
                require(count <= selected["size"], "Durable input exceeded its approved length")
                stream.write(chunk)
        require(process.wait() == 0 and count == selected["size"] and file_sha(output) == selected["sha256"],
                "Durable input bytes differ from their approved hash/size")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        process.stdout.close()


def package_scope(source, packages=None):
    known = {row["packageId"] for row in inventory(source)["packages"]}
    if packages is None:
        return sorted(known)
    require(type(packages) is list and packages and all(type(item) is str for item in packages)
            and len(packages) == len(set(packages)) and set(packages) <= known, "Invalid dependency package scope")
    return sorted(packages)


def lock_documents(source, packages=None):
    selected = set(package_scope(source, packages))
    documents = [(p, load(source_file(source, str(PurePosixPath(p["projectPath"]).parent / "sunder-contracts.lock.json"))))
                 for p in inventory(source)["packages"] if p["packageId"] in selected]
    for _, lock in documents:
        package_imports(lock)
    return documents


def catalog_requirements(lock):
    values = list(lock["platformImports"].values())
    for selected in lock["packageArtifacts"].values():
        values += [dict(record, contractId=key) for key, record in selected["contracts"].items() if record["authorityKind"] == "platform"]
    return values


def validate_manifest(source, value, source_sha, core_sha, packages=None):
    closed(value, "schemaVersion artifactSourceCommit lockSetSha256 core candidates platformCatalog"
           + (" packageIds" if value.get("schemaVersion") == 3 else ""))
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] in (1, 2, 3), "Unknown cold-input manifest schema")
    declared = package_scope(source, value.get("packageIds"))
    require(value.get("packageIds", declared) == declared and set(package_scope(source, packages)) <= set(declared),
            "Dependency manifest does not cover the requested package scope")
    require(value["artifactSourceCommit"] == digest(source_sha, 40)
            and value["lockSetSha256"] == locks(source), "Cold inputs do not bind the exact source/production locks")
    version_field = "sdkVersion" if value["schemaVersion"] == 1 else "buildToolVersion"
    closed(value["core"], "artifactSourceCommit " + version_field)
    require(value["core"]["artifactSourceCommit"] == digest(core_sha, 40), "Cold Core source differs")
    version(value["core"][version_field])
    require(type(value["candidates"]) is list and len(value["candidates"]) <= 128, "Invalid candidate inventory")
    selected = {}
    platforms = set()
    for _, lock in lock_documents(source, declared):
        for package_id, item in lock["packageArtifacts"].items():
            if "registryOrigin" in item:
                authoring_origin(item["registryOrigin"])
            key = (package_id, item["selectedPackageVersion"], item["selectedPackageContentIdentity"], item["publicInventorySha256"])
            selected[key] = item
        for item in catalog_requirements(lock):
            if "registryOrigin" in item:
                authoring_origin(item["registryOrigin"])
            platforms.add((item["authorityId"], item["selectedAuthorityRelease"], item["selectedAuthorityContentIdentity"]))
    actual = []
    for item in value["candidates"]:
        closed(item, "packageId version packageContentIdentity publicInventorySha256 asset")
        require(re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)*", item["packageId"]), "Invalid candidate package ID")
        version(item["version"])
        digest(item["packageContentIdentity"])
        digest(item["publicInventorySha256"])
        input_asset(item["asset"], value["schemaVersion"])
        require((value["schemaVersion"] != 1 or item["asset"]["repository"] == "Younics/sunder-agent-package")
                and item["asset"]["name"].endswith(".sunderpkg"), "Candidate must be an Agent archive")
        actual.append(tuple(item[k] for k in ("packageId", "version", "packageContentIdentity", "publicInventorySha256")))
    require(actual == sorted(selected), "Missing/extra/duplicate or differently selected cold candidates")
    require(len({key[0] for key in actual}) == len(actual), "Initial family profile requires one exact candidate per package ID")
    catalog = value["platformCatalog"]
    closed(catalog, "authorityId releaseVersion catalogSha256 asset")
    require(catalog["authorityId"] == "sunder.core" and platforms == {(catalog["authorityId"], catalog["releaseVersion"], catalog["catalogSha256"])},
            "Cold catalog must cover every direct and inventory-only platform constraint")
    digest(catalog["catalogSha256"])
    version(catalog["releaseVersion"])
    input_asset(catalog["asset"], value["schemaVersion"], 67108864)
    require((value["schemaVersion"] != 1 or catalog["asset"]["repository"] == "Younics/sunder-core") and catalog["asset"]["sha256"] == catalog["catalogSha256"],
            "Core catalog asset/content identity differs")
    names = [i["asset"]["name"] for i in [*value["candidates"], catalog]]
    require(len(names) == len({n.casefold() for n in names}) and MANIFEST not in names, "Cold input filename collision")
    return value


def input_asset(value, schema, maximum=268435456):
    if schema == 1:
        return locator(value, maximum)
    closed(value, "name size sha256")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,159}", value["name"]), "Unsafe dependency filename")
    require(positive(value["size"]) <= maximum, "Dependency exceeds its byte bound")
    digest(value["sha256"])
    return value


def verify(source, bundle, approved_sha, source_sha, core_sha, packages=None):
    bundle = directory(bundle)
    require(file_sha(bundle / MANIFEST) == digest(approved_sha), "Cold manifest differs from its approved hash")
    value = validate_manifest(source, load(bundle / MANIFEST), source_sha, core_sha, packages)
    expected = {MANIFEST, *[i["asset"]["name"] for i in [*value["candidates"], value["platformCatalog"]]]}
    require({p.name for p in bundle.iterdir()} == expected, "Cold bundle has missing/extra members")
    for item in [*value["candidates"], value["platformCatalog"]]:
        path = regular(bundle / item["asset"]["name"])
        require(path.stat().st_size == item["asset"]["size"] and file_sha(path) == item["asset"]["sha256"], "Cold input bytes changed")
    verify_selected_files(source, [bundle / item["asset"]["name"] for item in value["candidates"]],
                          bundle / value["platformCatalog"]["asset"]["name"], value.get("packageIds"))
    return value


def verify_candidate_file(source, path, packages=None):
    """Admit one exact dependency before any downstream build may consume it."""
    path = regular(path).resolve()
    require(path.stat().st_size <= 268435456, "Candidate exceeds the archive bound")
    manifest, public, public_sha = archive_metadata(path)
    key = (manifest["id"], manifest["version"], public["packageContentIdentity"], public_sha)
    entries = [entry for _, lock in lock_documents(source, packages) for package_id, entry in lock["packageArtifacts"].items() if package_id == manifest["id"]]
    expected_selections = sorted({(item["selectedPackageVersion"], item["selectedPackageContentIdentity"], item["publicInventorySha256"]) for item in entries})
    require(expected_selections == [key[1:]],
            "Candidates do not match every exact committed package selection: " + manifest["id"]
            + "; expected (version, content identity, public inventory)=" + canonical(expected_selections).decode()
            + "; actual=" + canonical(key[1:]).decode())
    expected = {c["contractId"]: {
        **{k: c[k] for k in ("artifactSource", "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity")},
        "resolvedContractVersion": c["contractVersion"], "descriptorBytes": c["descriptorBytes"],
        "descriptorSha256": c["descriptorSha256"], "abiSha256": c["abiSha256"], "etag": '"' + c["descriptorSha256"] + '"'
    } for c in public["contracts"]}
    require(len(expected) == public["publicContractCount"] and all(item["publicContractCount"] == len(expected)
            and canonical(item["contracts"]) == canonical(expected) for item in entries), "Candidate does not reproduce the complete committed lock entry")
    return key, path


def verify_selected_files(source, candidates, catalog_path, packages=None):
    """Shared local/CI/release admission against complete committed lock selections.

    This validates bytes, not production approval. Release callers additionally
    bind their source, lock set and durable asset coordinates in validate_manifest.
    """
    documents = lock_documents(source, packages)
    expected = {(package_id, item["selectedPackageVersion"], item["selectedPackageContentIdentity"], item["publicInventorySha256"])
                for _, lock in documents for package_id, item in lock["packageArtifacts"].items()}
    paths, actual = {}, set()
    for path in candidates:
        key, path = verify_candidate_file(source, path, packages)
        require(key[0] not in paths, "Duplicate selected candidate")
        actual.add(key)
        paths[key[0]] = path
    require(actual == expected, "Candidates do not match every exact committed package selection")
    catalog_path = regular(catalog_path).resolve()
    catalog = load(catalog_path)
    require(catalog["schemaVersion"] == 1 and catalog["authorityKind"] == "platform" and catalog["authorityId"] == "sunder.core"
            and catalog["contractCount"] == len(catalog["contracts"]), "Catalog release/inventory differs")
    expected_catalogs = {(item["authorityId"], item["selectedAuthorityRelease"], item["selectedAuthorityContentIdentity"])
                         for _, lock in documents for item in catalog_requirements(lock)}
    require(expected_catalogs == {(catalog["authorityId"], catalog["releaseVersion"], file_sha(catalog_path))},
            "Catalog bytes do not match all committed platform selections")
    catalog_records = {c["contractId"]: c for c in catalog["contracts"]}
    require(len(catalog_records) == len(catalog["contracts"]), "Duplicate catalog contract")
    for _, lock in documents:
        for item in catalog_requirements(lock):
            contract = catalog_records[item["contractId"]]
            require(all(contract[k] == item[k] for k in ("descriptorBytes", "descriptorSha256", "abiSha256"))
                    and contract["contractVersion"] == item["resolvedContractVersion"]
                    and sha(contract["canonicalDescriptor"].encode()) == item["descriptorSha256"], "Catalog does not reproduce the exact locked RPC tuple")
    return paths


def fetch(source, destination, source_sha, core_sha, selected=None, *, packages=None, prepared=False):
    if selected is None:
        # Dependencies and hashes are discovered/generated, not operator approvals.
        import dependency_inputs
        source, destination = Path(source).resolve(), Path(destination).resolve()
        core = source.parent / "sunder-core"
        require(subprocess.check_output(["git", "-C", str(core), "rev-parse", "HEAD"], text=True).strip() == core_sha, "Core checkout differs")
        resolved = load(destination / "ci-inputs.json") if prepared else dependency_inputs.resolve(source, core, destination, packages=packages)
        if prepared:
            closed(resolved, "schemaVersion coreCommit candidates platformCatalog" + (" packageIds" if resolved.get("schemaVersion") == 3 else ""))
            require(resolved["schemaVersion"] in (2, 3) and resolved["coreCommit"] == core_sha
                and resolved.get("packageIds", package_scope(source)) == package_scope(source, packages), "Prepared dependency scope or Core differs")
            for asset in [*resolved["candidates"], resolved["platformCatalog"]]:
                input_asset(asset, resolved["schemaVersion"])
                require(dependency_inputs.record(destination / asset["name"]) == asset, "Prepared dependency bytes changed")
            verify_selected_files(source, [destination / item["name"] for item in resolved["candidates"]],
                destination / resolved["platformCatalog"]["name"], packages)
        versions = {release for name, release in dependency_inputs.core_packages(source, packages) if name == "Sunder.Package.Build"}
        require(len(versions) == 1, "Expected one declared Build tool version")
        candidates = []
        for asset in resolved["candidates"]:
            manifest, public, public_sha = archive_metadata(destination / asset["name"])
            candidates.append(dict(packageId=manifest["id"], version=manifest["version"], packageContentIdentity=public["packageContentIdentity"],
                publicInventorySha256=public_sha, asset=asset))
        catalog = load(destination / resolved["platformCatalog"]["name"])
        value = dict(schemaVersion=2, artifactSourceCommit=source_sha, lockSetSha256=locks(source),
            core=dict(artifactSourceCommit=core_sha, buildToolVersion=next(iter(versions))), candidates=sorted(candidates, key=lambda item: item["packageId"]),
            platformCatalog=dict(authorityId=catalog["authorityId"], releaseVersion=catalog["releaseVersion"],
                catalogSha256=resolved["platformCatalog"]["sha256"], asset=resolved["platformCatalog"]))
        if packages is not None:
            value.update(schemaVersion=3, packageIds=package_scope(source, packages))
        (destination / "ci-inputs.json").unlink()  # Replace our transient CI index, not a source file.
        (destination / MANIFEST).write_bytes(canonical(value) + b"\n")
        verify(source, destination, file_sha(destination / MANIFEST), source_sha, core_sha, packages)
        print("Resolved authored dependencies and generated the input inventory automatically.")
        return value
    selected = locator(selected, MAX_MANIFEST)
    destination = Path(destination).absolute()
    require(not destination.exists() and not destination.is_symlink(), "Cold input destination must be new")
    destination.mkdir()
    download(selected, destination / MANIFEST, MAX_MANIFEST)
    value = validate_manifest(source, load(destination / MANIFEST), source_sha, core_sha, packages)
    for item in [*value["candidates"], value["platformCatalog"]]:
        download(item["asset"], destination / item["asset"]["name"], 67108864 if item is value["platformCatalog"] else 268435456)
    verify(source, destination, selected["sha256"], source_sha, core_sha, packages)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write("locator=" + canonical(selected).decode() + "\n")
    print("Verified reviewed numeric assets, selected candidates/catalog, source and exact locks.")


def source_snapshot(source):
    names = {"packages.json"}
    names.update(name for name in ("Directory.Build.props", "Directory.Build.targets") if (Path(source) / name).exists())
    for package in inventory(source)["packages"]:
        names.add(package["projectPath"])
        parent = PurePosixPath(package["projectPath"]).parent
        names.update(str(parent / name) for name in ("sunder-contracts.lock.json", "sunder-contracts.owned.json", "packages.release.lock.json"))
    result = {}
    for name in sorted(names):
        path = source_file(source, name, optional=name.endswith("/sunder-contracts.owned.json"))
        # Missing private-only baselines are inputs too: adding one must trip the
        # same cold-source fence as editing/removing a retained public baseline.
        result[name if path else "baseline-absent:" + name] = file_sha(path) if path else sha(b"")
    return result


def clean_source(source, source_sha):
    require(subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip() == source_sha,
            "Cold source checkout is not the approved artifact source")
    require(subprocess.run(["git", "-C", str(source), "diff", "--quiet", "HEAD", "--", "packages.json", "src",
                            "Directory.Build.props", "Directory.Build.targets"], check=False).returncode == 0,
            "Cold source/lock/baseline checkout has local changes")


def properties(source, mode):
    require(mode in ("source", "package"), "Unknown source/dependency mode")
    result = ["-p:Configuration=Release",
              "-p:ContinuousIntegrationBuild=true", "-p:SunderUseSourceSdk=" + ("true" if mode == "source" else "false"),
              "-p:UseSharedCompilation=false", "-p:NuGetAudit=false"]
    if mode == "package":
        result.append("-p:RestoreLockedMode=true")
    return result


def run(command, env, cwd, capture=False):
    return subprocess.run(command, cwd=cwd, env=env, stdout=subprocess.PIPE if capture else None,
                          stderr=subprocess.STDOUT if capture else None, text=True, check=False)


def offline_prefix():
    # An externally installed adapter is required where the OS launcher cannot
    # deny the complete process tree. It is reviewed independently of asset data.
    if os.environ.get("SUNDER_AGENT_OFFLINE_RUNNER"):
        require(Path(os.environ["SUNDER_AGENT_OFFLINE_RUNNER"]).is_absolute(), "Offline runner path must be absolute")
        path = regular(os.environ["SUNDER_AGENT_OFFLINE_RUNNER"]).resolve()
        require(file_sha(path) == digest(os.environ.get("SUNDER_AGENT_OFFLINE_RUNNER_SHA256", "")), "Offline runner digest differs")
        require(os.access(path, os.X_OK), "Offline runner is not executable")
        root = Path(tempfile.mkdtemp(prefix="sunder-offline-runner-")).resolve()
        snapshot = root / path.name
        shutil.copyfile(path, snapshot)
        require(file_sha(snapshot) == os.environ["SUNDER_AGENT_OFFLINE_RUNNER_SHA256"], "Offline runner changed during snapshot")
        snapshot.chmod(0o555)
        root.chmod(0o555)
        def cleanup():
            root.chmod(0o755)
            snapshot.chmod(0o755)
            shutil.rmtree(root)
        atexit.register(cleanup)
        return [str(snapshot), "--"]
    if sys.platform == "darwin":
        require(Path("/usr/bin/sandbox-exec").is_file(), "macOS network-denial launcher is unavailable")
        return ["/usr/bin/sandbox-exec", "-p", "(version 1)(allow default)(deny network*)", "--"]
    if sys.platform == "linux":
        require(shutil.which("unshare"), "Linux network namespaces are unavailable; provision a reviewed offline runner")
        return ["unshare", "--user", "--map-root-user", "--net", "--"]
    raise Invalid("Provision SUNDER_AGENT_OFFLINE_RUNNER and its reviewed SHA-256 for process-tree network denial on this OS")


def isolated_env(home, cache):
    env = {k: v for k, v in os.environ.items() if k not in ("GH_TOKEN", "GITHUB_TOKEN", "SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_PUBLISH_TOKEN")}
    env.update(SUNDER_HOME=str(home), SUNDER_CONTRACT_CACHE=str(cache), SunderContractCacheRoot=str(cache),
               MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="true",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", PYTHONDONTWRITEBYTECODE="1")
    return env


def verify_network_denial(prefix, env, source):
    # This uses only an in-process loopback listener, never a live remote service.
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        command = [*prefix, sys.executable, "-B", str(Path(__file__).resolve()), "probe-network", str(listener.getsockname()[1])]
        answer = run(command, env, source, capture=True)
        require(answer.returncode == 0 and answer.stdout.strip() == "sunder-network-denied-v1", "Offline launcher did not execute a network-denied child; no fallback is permitted")


def prepare(source, bundle, work, mode, source_sha, core_sha, approved_sha, solution=False):
    source, bundle = directory(source), directory(bundle)
    clean_source(source, source_sha)
    value = verify(source, bundle, approved_sha, source_sha, core_sha)
    before = source_snapshot(source)
    work = Path(work).absolute()
    require(not work.exists() and not work.is_symlink() and source not in work.parents, "Cold work/home must be new and outside reviewed source")
    work.mkdir()
    home = work / "sunder-home"
    home.mkdir()
    cache = home / "contracts/v1/sha256"
    env = isolated_env(home, cache)
    common = properties(source, mode)
    rows = inventory(source)["packages"]
    prefix = offline_prefix()
    verify_network_denial(prefix, env, source)
    try:
        # Never narrow Restore with -r: retain all six declared RID graphs.
        restore_projects = [source / "Sunder.AgentPackage.slnx"] if solution else [source / p["projectPath"] for p in rows]
        for project in restore_projects:
            args = ["dotnet", "restore", str(project), *common]
            if mode == "package":
                args.append("--locked-mode")
            if os.environ.get("NUGET_SOURCE"):
                args += ["--source", os.environ["NUGET_SOURCE"]]
            require(run(args, env, source).returncode == 0, "Explicit dependency restore failed")
        tool = os.environ.get("SUNDER_NATIVE_TASK_ASSEMBLY", "")
        if mode == "source" and not tool:
            # Explicit online tooling preparation; no Agent package Build has begun.
            project = source.parent / "sunder-core/languages/csharp/dotnet/packages/Sunder.Package.Build/Sunder.Package.Build.csproj"
            require(run(["dotnet", "build", str(project), "-c", "Release", "--no-restore", "-p:ContinuousIntegrationBuild=true", "-p:UseSharedCompilation=false"], env, source).returncode == 0,
                    "Core tool preparation failed")
            evaluated = run(["dotnet", "msbuild", str(project), "-p:Configuration=Release", "-p:ContinuousIntegrationBuild=true", "-getProperty:TargetDir"], env, source, capture=True)
            require(evaluated.returncode == 0, "Could not resolve prepared Core tool output")
            shutil.copytree(evaluated.stdout.strip(), work / "tools")
            tool = str(work / "tools/Sunder.Package.Build.dll")
        if tool:
            for name in TOOL_FILES:
                regular(Path(tool).parent / name)
            common += ["-p:SunderPackageBuildTaskAssembly=" + str(Path(tool).resolve()), "-p:SunderPackageBuildTaskSourceIsImmutable=true"]
        for package in rows:
            evaluated = run(["dotnet", "msbuild", str(source / package["projectPath"]), *common,
                             "-getProperty:Configuration,Version,ContinuousIntegrationBuild,RuntimeIdentifiers,SunderPackageRuntimeIdentifiers,ProjectAssetsFile"], env, source, True)
            require(evaluated.returncode == 0, "Could not verify restored project context")
            props = decode(evaluated.stdout.encode())["Properties"]
            require(props["Configuration"] == "Release" and props["Version"] == package["version"]
                    and props["ContinuousIntegrationBuild"].lower() == "true"
                    and set(props["RuntimeIdentifiers"].split(";")) == set(RIDS)
                    and (not props["SunderPackageRuntimeIdentifiers"] or set(props["SunderPackageRuntimeIdentifiers"].split(";")) == set(RIDS)),
                    "Restore context differs from producer's full declared RID graph")
            assets = load(props["ProjectAssetsFile"])
            require({key.rsplit("/", 1)[1] for key in assets["targets"] if "/" in key} == set(RIDS), "Restored assets omit/add a declared RID")
        common += ["-p:SunderContractCacheRoot=" + str(cache)]
        catalog_path = str(bundle / value["platformCatalog"]["asset"]["name"])
        candidates = {i["packageId"]: str(bundle / i["asset"]["name"]) for i in value["candidates"]}
        documents = lock_documents(source)
        probe = next((p for p, lock in documents if lock["packageArtifacts"] and catalog_requirements(lock)), None)
        require(probe is not None, "Initial Agent release needs a package/catalog cold-cache probe")
        # Supplying only the catalog explicitly forces Core's offline branch. No
        # Registry-origin override or selection mutation is involved, even for pinned locks.
        cold = run([*prefix, "dotnet", "msbuild", str(source / probe["projectPath"]), "-t:RestoreSunderContracts", *common,
                    "-p:SunderPlatformCatalogPath=" + catalog_path], env, source, True)
        (work / "cold-missing.log").write_text(cold.stdout, encoding="utf-8")
        require(cold.returncode != 0 and "contract-cache.entry-missing" in cold.stdout and not cache.exists(),
                "Cold pre-hydration did not fail with Core's missing-cache diagnostic without publishing evidence")
        for package, lock in documents:
            args = [*prefix, "dotnet", "msbuild", str(source / package["projectPath"]), "-t:RestoreSunderContracts", *common]
            selected = [candidates[key] for key in sorted(lock["packageArtifacts"])]
            require(all(not any(c in path for c in "%;\r\n") for path in [catalog_path, *selected]), "Cold input path is ambiguous to MSBuild")
            if selected:
                args.append("-p:SunderPackageCandidatePath=" + "%3B".join(selected))
            if catalog_requirements(lock):
                args.append("-p:SunderPlatformCatalogPath=" + catalog_path)
            if selected or catalog_requirements(lock):
                require(run(args, env, source).returncode == 0, "Exact cold hydration failed; no online retry is permitted")
            require(source_snapshot(source) == before, "Hydration changed reviewed source/lock/baseline bytes")
        verify(source, bundle, approved_sha, source_sha, core_sha)
        state = {"schemaVersion": 1, "source": str(source), "bundle": str(bundle), "manifestSha256": approved_sha,
                 "sourceSha": source_sha, "coreSha": core_sha, "mode": mode, "home": str(home), "cache": str(cache),
                 "properties": common, "sourceInputs": before}
        (work / "cold-state.json").write_bytes(canonical(state) + b"\n")
        print(str(work / "cold-state.json"))
    finally:
        require(source_snapshot(source) == before, "Release preparation changed reviewed source/lock/baseline bytes")
        clean_source(source, source_sha)


def execute(state_path, command):
    state = load(state_path)
    closed(state, "schemaVersion source bundle manifestSha256 sourceSha coreSha mode home cache properties sourceInputs")
    require(state["schemaVersion"] == 1 and source_snapshot(state["source"]) == state["sourceInputs"], "Cold build source/locks changed")
    clean_source(state["source"], state["sourceSha"])
    verify(state["source"], state["bundle"], state["manifestSha256"], state["sourceSha"], state["coreSha"])
    require(Path(state["cache"]).is_dir(), "Cold hydration evidence is missing")
    env = isolated_env(state["home"], state["cache"])
    # Fan-in invokes the frozen native helper, which launches its own MSBuild
    # processes. Supply the identical evaluated properties there as environment
    # properties too, rather than editing its qualified Pack implementation.
    for prop in state["properties"]:
        key, value = prop[3:].split("=", 1)
        env[key] = value
    prefix = offline_prefix()
    verify_network_denial(prefix, env, state["source"])
    if command[0] == "dotnet":
        require(command[1] in ("build", "msbuild", "test"), "Offline command is not an approved build/check operation")
        if command[1] in ("build", "test"):
            require("--no-restore" in command, "Offline build/test must use the explicitly restored dependency graph")
        command += state["properties"]
    try:
        return run([*prefix, *command], env, state["source"]).returncode
    finally:
        require(source_snapshot(state["source"]) == state["sourceInputs"], "Offline command changed reviewed lock/baseline bytes")
        verify(state["source"], state["bundle"], state["manifestSha256"], state["sourceSha"], state["coreSha"])
        clean_source(state["source"], state["sourceSha"])


def main():
    python_version()
    command, *args = sys.argv[1:]
    if command == "check-python":
        print("Python 3.9+ release prerequisite satisfied")
    elif command == "check-offline":
        verify_network_denial(offline_prefix(), isolated_env(args[0], str(Path(args[0]) / "contracts/v1/sha256")), Path.cwd())
        print("Network-denied child process verified")
    elif command == "fetch":
        packages = None
        prepared = "--prepared" in args
        if prepared: args.remove("--prepared")
        if "--packages" in args:
            index = args.index("--packages")
            packages = decode(args[index + 1].encode())
            del args[index:index + 2]
        fetch(*args, packages=packages, prepared=prepared)
    elif command == "verify":
        verify(*args)
    elif command == "prepare":
        solution = args[-1:] == ["--solution"]
        prepare(*(args[:-1] if solution else args), solution=solution)
    elif command == "exec":
        require(len(args) >= 3 and args[1] == "--", "Expected state -- command")
        sys.exit(execute(args[0], args[2:]))
    elif command == "probe-network":
        try:
            with socket.create_connection(("127.0.0.1", int(args[0])), timeout=2):
                raise Invalid("Network is available inside the offline child")
        except OSError:
            print("sunder-network-denied-v1")
    else:
        raise Invalid("Unknown cold release-input command")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit("Agent cold inputs rejected: " + str(error))
