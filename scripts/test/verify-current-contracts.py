#!/usr/bin/env python3
"""Verify CURRENT contracts against independently authored expectations.

--source-only audits current declarations/topology without build outputs.
--evidence consumes a complete current 15-family qualified-artifact input index.
--propose prints a candidate byte record for separate review; it never writes an
expectation, baseline, lock, qualification, or source file. Default evidence mode
compares the qualified proposal to the separately reviewed byte records.
"""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "release"))
import agent_release as release

INVENTORY = "compatibility/agent-current-inventory.json"
RECORDS = "compatibility/agent-current-contracts.json"
AGENT = "younics.ai.agent"
STACK = "sunder.stack.contributor"
require = release.require


def index(rows, field):
    result = {row[field]: row for row in rows}
    require(len(result) == len(rows), "Duplicate " + field)
    return result


def expected_inventory(root):
    expected = release.load(root / INVENTORY)
    require(expected["schemaVersion"] == 1, "Unsupported current inventory")
    packages, contracts = index(expected["packages"], "packageId"), index(expected["contracts"], "contractId")
    require(len(packages) == 15 and len({p["projectPath"] for p in packages.values()}) == 15
            and sum(p["hasApp"] for p in packages.values()) == 12, "Expected 15 project/package pairs and 12 App roles")
    require(len(contracts) == 31 and sum(c["exposure"] == "public" for c in contracts.values()) == 18
            and sum(c["exposure"] == "package" for c in contracts.values()) == 13, "Expected 18 public / 13 private contracts")
    require(expected["platform"] == dict(contractId=STACK, authorityKind="platform", authorityId="sunder.core", version="2.0.0"), "Platform authority differs")
    inventory = release.inventory(root)
    actual = index(inventory["packages"], "packageId")
    require(packages.keys() == actual.keys() and inventory["releaseVersion"] == expected["packageVersion"], "Current release family differs")
    require(set(inventory["capabilities"]) == packages.keys(), "Capability owners differ")
    for package_id, package in packages.items():
        require(all(package[key] == actual[package_id][key] for key in ("key", "projectPath")), "Current project/package pair differs")
        require(sorted(package["providers"]) == actual[package_id]["providerIds"], "Current provider IDs differ")
        require(set(package["providers"].values()) <= contracts.keys() | {STACK}, "Unknown provider contract")
        require(set(package["requiredRuntime"]) <= contracts.keys(), "Unknown required contract")
    for contract in contracts.values():
        require(contract["methods"] == sorted(set(contract["methods"])) and contract["methods"]
                and set(contract["streams"]) <= set(contract["methods"]), "Invalid method/stream expectation")
        require(contract["source"].startswith(str(Path(packages[contract["packageId"]]["projectPath"]).parent) + "/"), "Contract source owner differs")
        if contract["exposure"] == "public": require(contract["packageId"] == AGENT, "Public API owner differs")
    return expected, inventory


def source_files(root):
    return sorted(path for path in (root / "src").rglob("*.cs") if not {"bin", "obj"}.intersection(path.parts))


def verify_source(root, expected):
    public, providers = [], {}
    for path in source_files(root):
        text = path.read_text(encoding="utf-8-sig")
        public.extend(re.findall(r'\[SunderService\(SunderServiceExposure.Public,[^\]]*ContractId\s*=\s*"([^"]+)"', text))
        package = next((p for p in expected["packages"] if path.is_relative_to(root / Path(p["projectPath"]).parent)), None)
        if package:
            providers.setdefault(package["packageId"], []).extend(re.findall(r'\[SunderServiceProvider\([^\]]*ProviderId\s*=\s*"([^"]+)"', text))
    require(sorted(public) == sorted(c["contractId"] for c in expected["contracts"] if c["exposure"] == "public"),
            "Authored public declarations differ from the approved 18-API inventory")
    # This private service intentionally uses the ordinary-service default identity.
    profile = release.source_file(root, "src/Sunder.Package.Agent/Services/AgentProfileService.cs").read_text()
    require("[SunderService]" in profile and "class AgentProfileService" in profile, "Default private profile service differs")
    providers.setdefault(AGENT, []).append("younics.ai.agent.agent.profile.provider")
    solution = [p.attrib["Path"].replace("\\", "/") for p in ET.parse(root / "Sunder.AgentPackage.slnx").iter("Project")]
    for package in expected["packages"]:
        require(sorted(providers.get(package["packageId"], [])) == sorted(package["providers"]), "Authored providers differ: " + package["key"])
        project = ET.parse(release.source_file(root, package["projectPath"]))
        require(project.findtext(".//SunderPackageFamilyId") == package["packageId"], "Source family differs")
        require(solution.count(package["projectPath"]) == 1, "Canonical project absent/duplicated in solution")
        require(not any(project.findall(".//" + tag) for tag in ("SunderPackageRole", "SunderPackageAppProject", "SunderPackagePrimaryProject", "ProjectReference")), "Noncanonical authored role graph")
        require((project.findtext(".//SunderSdkAvaloniaReference") == "true") == package["hasApp"], "Authored App role differs")
        metadata = (root / Path(package["projectPath"]).parent / "PackageMetadata.cs").read_text()
        require(f'Id = "{package["packageId"]}"' in metadata and "SunderPackageDependency" not in metadata, "Package metadata identity/dependency differs")
    for contract in expected["contracts"]:
        source = release.source_file(root, contract["source"]).read_text()
        require("[SunderService" in source, "Missing authored service: " + contract["source"])
        if contract["contractId"] != "younics.ai.agent.agent.profile":
            require(f'ContractId = "{contract["contractId"]}"' in source and f'ServiceId = "{contract["service"]}"' in source, "Source contract/service identity differs")


def actions(contract):
    return ["discover", "invoke"] + (["subscribe"] if contract["streams"] else [])


def contract_version(contract, expected):
    return contract.get("contractVersion", expected["publicVersion"] if contract["exposure"] == "public" else expected["packageVersion"])


def uses(package, expected):
    result = {}
    for contract in expected["contracts"]:
        cid = contract["contractId"]
        if contract["packageId"] == package["packageId"] and contract["app"]:
            result[("app", cid)] = (contract["exposure"] == "package", actions(contract))
        if contract["exposure"] == "public" and (package["packageApi"] or package["packageId"] == AGENT and contract["runtime"]):
            result[("runtime", cid)] = (cid in package["requiredRuntime"], actions(contract))
    if package["key"] == "agent-tools-web": result[("runtime", "younics.ai.agent.execution.target")] = (False, ["discover"])
    return result


def contract_record(snapshot, contract, expected):
    raw = snapshot["canonicalDescriptor"].encode("utf-8")
    descriptor = release.decode(raw)
    version = contract_version(contract, expected)
    require(descriptor["descriptorVersion"] == 1 and descriptor["schemaProfile"] == "sunder.rpc.values.v1", "Descriptor profile differs")
    require(descriptor["contractId"] == snapshot["contractId"] == contract["contractId"]
            and descriptor["version"] == snapshot["contractVersion"] == version
            and snapshot["exposure"] == contract["exposure"], "Contract identity/version/exposure differs")
    require(len(raw) == snapshot["descriptorBytes"] and release.sha(raw) == snapshot["descriptorSha256"], "Descriptor bytes/hash differ")
    release.digest(snapshot["abiSha256"])
    require(set(descriptor["services"]) == {contract["service"]}, "Service inventory differs")
    methods = descriptor["services"][contract["service"]]["methods"]
    require(sorted(methods) == contract["methods"], "Method inventory differs")
    require(all(m["kind"] in ("unary", "server-stream") for m in methods.values())
            and sorted(k for k, m in methods.items() if m["kind"] == "server-stream") == contract["streams"], "Stream inventory differs")
    return dict(packageId=contract["packageId"], contractId=contract["contractId"], exposure=contract["exposure"],
                canonicalDescriptor=snapshot["canonicalDescriptor"], descriptorSha256=snapshot["descriptorSha256"], abiSha256=snapshot["abiSha256"])


def verify_manifest(manifest, package, expected, inventory):
    contracts = index(expected["contracts"], "contractId")
    policies = uses(package, expected)
    require(manifest["id"] == package["packageId"] and manifest["version"] == expected["packageVersion"], "Manifest owner/version differs")
    bundles = index(manifest["contractBundles"], "contractId")
    owned = {cid for cid, c in contracts.items() if c["packageId"] == package["packageId"]}
    require(set(bundles) == owned | set(package["providers"].values()) | {cid for _, cid in policies}, "Bundle inventory differs")
    for cid, bundle in bundles.items():
        platform = cid == STACK
        authority = "sunder.core" if platform else contracts[cid]["packageId"]
        require(bundle["authorityId"] == authority and bundle["authorityKind"] == ("platform" if platform else "package")
                and bundle["source"] == ("owned" if authority == package["packageId"] else "imported")
                and bundle["exposure"] == ("public" if platform else contracts[cid]["exposure"]), "Bundle authority/exposure differs")
        require(bundle["version"] == (expected["platform"]["version"] if platform else contract_version(contracts[cid], expected)), "Bundle version differs")
    providers = index(manifest["provides"], "providerId")
    require(providers.keys() == package["providers"].keys(), "Provider inventory differs")
    for provider_id, cid in package["providers"].items():
        provider = providers[provider_id]
        require(provider["contractId"] == cid and provider["role"] == "runtime"
                and provider["audience"] == ("public" if cid == STACK or contracts[cid]["exposure"] == "public" else "owner"), "Provider authority differs")
        require(provider["contractVersion"] == bundles[cid]["version"] and all(provider[f] == bundles[cid][f] for f in ("descriptorSha256", "abiSha256")), "Provider tuple differs")
    actual_uses = {(u["role"], u["contractId"]): (u["required"], u["actions"]) for u in manifest["usesContracts"]}
    require(len(actual_uses) == len(manifest["usesContracts"]) and actual_uses == policies, "Required/action/role policy differs")
    for use in manifest["usesContracts"]:
        bundle = bundles[use["contractId"]]
        require(use["contractVersion"] == bundle["version"] and use["abiSha256"] == bundle["abiSha256"], "Use tuple differs")
    dependencies = manifest["dependsOn"]
    if package["packageId"] == AGENT:
        require(not dependencies, "Owner must not acquire a package dependency")
    else:
        require(len(dependencies) == 1 and all(dependencies[0][key] == value for key, value in
                dict(packageId=AGENT, versionRange=">=2.0.0 <3.0.0", presence="optional" if package["key"] == "agent-builder" else "required").items()), "Package dependency policy differs")
    public_ids = {cid for cid, contract in contracts.items() if contract["exposure"] == "public"}
    api_imports = manifest.get("packageApiImports", [])
    if package["packageApi"]:
        require(len(api_imports) == 1 and api_imports[0]["packageId"] == AGENT
                and api_imports[0]["presence"] == ("optional" if package["key"] == "agent-builder" else "required"), "Package API authority differs")
        for field in ("bundleRefs", "useRefs"):
            refs = api_imports[0][field]
            require(len(refs) == len(public_ids) and {r["contractId"] for r in refs} == public_ids, "Package API subset differs")
        require(all(r["role"] == "runtime" for r in api_imports[0]["useRefs"]), "Package API grants non-Runtime use")
    else: require(not api_imports, "Unexpected package-wide API import")
    imported_providers = set(package["providers"].values()) & public_ids if package["packageId"] != AGENT else set()
    actual_imports = manifest.get("providerImports", [])
    require(len(actual_imports) == len(imported_providers) and {p["contractId"] for p in actual_imports} == imported_providers
            and all(p["packageId"] == AGENT and p.get("presence", "required") == ("optional" if package["key"] == "agent-builder" else "required")
                    for p in actual_imports), "Provider import authority differs")
    stacks = {pid for pid, cid in package["providers"].items() if cid == STACK}
    platform_imports = manifest.get("platformImports", [])
    if stacks:
        require(len(platform_imports) == 1 and all(platform_imports[0][key] == value for key, value in
                dict(contractId=STACK, authorityId="sunder.core", authorityKind="platform").items()), "Platform import authority differs")
        refs = platform_imports[0]["providerRefs"]
        require(len(refs) == len(stacks) and {r["providerId"] for r in refs} == stacks, "Platform provider closure differs")
    else: require(not platform_imports, "Unexpected platform import")
    targets = manifest["targets"]
    require(sorted(t["role"] for t in targets) == (["app", "runtime"] if package["hasApp"] else ["runtime"]), "Role topology differs")
    require(all(t["kind"] == ("worker" if t["role"] == "runtime" else "avalonia") for t in targets), "Role kind differs")
    actual_caps = {cap for t in targets for cap in t["requiredHostCapabilities"]}
    expected_caps = set(inventory["capabilities"][package["packageId"]])
    require(actual_caps == expected_caps, f"Capability inventory differs ({package['packageId']}): missing={sorted(expected_caps - actual_caps)}, extra={sorted(actual_caps - expected_caps)}")


def propose(root, evidence_path, expected, inventory):
    evidence = release.load(evidence_path)
    require(evidence["schemaVersion"] == 1 and evidence["scope"] == "local-unpublished-current-RID", "Expected current qualified-artifact index")
    require(evidence["inventorySha256"] == release.file_sha(root / INVENTORY), "Qualified input inventory differs")
    packages = index(expected["packages"], "packageId")
    rows = index(evidence["packages"], "packageId")
    require(rows.keys() == packages.keys(), "All 15 qualified family inputs are required")
    records, inputs = [], {}

    def pin(path, sha=None):
        actual = release.file_sha(path)
        require(sha is None or actual == sha, "Stale qualified input: " + str(path))
        require(path not in inputs or inputs[path] == actual, "Input changed during verification")
        inputs[path] = actual
        return path

    pin(root / INVENTORY)
    pin(root / "packages.json")
    for package_id, package in packages.items():
        row = rows[package_id]
        qualification_path = pin(release.source_file(root, row["qualification"]["path"]), row["qualification"]["sha256"])
        archive_path = pin(release.source_file(root, row["archive"]["path"]), row["archive"]["sha256"])
        qualification = release.load(qualification_path)
        frame, plan = qualification["inputs"], qualification["rolePlan"]
        require(qualification["schemaVersion"] == 1 and qualification["leaf"] == "sunder-dev", "Expected Core role qualification V1")
        require(frame["packageId"] == package_id and frame["packageVersion"] == expected["packageVersion"]
                and frame["project"] == str(Path(package["projectPath"]).relative_to("src"))
                and frame["configuration"] == evidence["configuration"] and frame["runtimeIdentifier"] == evidence["rid"]
                and frame["targetFramework"] == "net10.0", "Qualified source coordinates differ")
        require(plan["hasApp"] == package["hasApp"] and [r["role"] for r in plan["roles"]] == (["worker", "app"] if package["hasApp"] else ["worker"]), "Qualified roles differ")
        for item in frame["sources"]:
            pin(release.source_file(root / "src", item["path"]), item["sha256"])
        for field in ("ownedBaseline", "contractLock"):
            path = release.qualified_owned_baseline(root / "src", frame) if field == "ownedBaseline" else release.source_file(root / "src", frame[field])
            if path is not None: pin(path, frame[field + "Sha256"])
        tracked = index(frame["externalInputs"], "path")
        for item in frame["items"]:
            if item["kind"] in ("compile", "resource"):
                key = item["kind"] + ":" + item["identity"]
                require(key in tracked, "Evaluated source/resource lacks qualification")
                pin(release.source_file(root / "src", item["identity"]), tracked[key]["sha256"])
        source_uses = release.source_role_use_evidence(qualification)
        receipt = release.decode(plan["roles"][0]["receipt"].encode())
        emitted = index(receipt["contracts"], "contractKey")
        bundles = index(receipt["bundles"], "contractKey")
        require(emitted.keys() == bundles.keys(), "Receipt bundle/contract closure differs")
        owned = {c["contractId"]: c for key, c in emitted.items() if bundles[key]["source"] == "owned"}
        selected = [c for c in expected["contracts"] if c["packageId"] == package_id]
        require(set(owned) == {c["contractId"] for c in selected}, "Owned receipt inventory differs")
        baseline_path = root / Path(package["projectPath"]).parent / "sunder-contracts.owned.json"
        baseline = release.load(baseline_path) if baseline_path.exists() else {"contracts": {}}
        if baseline_path.exists():
            require(baseline["authorityKind"] == "package" and baseline["authorityId"] == baseline["packageId"] == package_id, "Owned authority differs")
        require({c["contractId"] for c in baseline["contracts"].values()} == {c["contractId"] for c in selected if c["exposure"] == "public"}, "Owned public inventory differs")
        for contract in selected:
            record = contract_record(owned[contract["contractId"]], contract, expected)
            bundle = bundles[owned[contract["contractId"]]["contractKey"]]
            require(bundle["authorityKind"] == "package" and bundle["authorityId"] == package_id, "Receipt authority differs")
            if contract["exposure"] == "public":
                current = next(c["current"] for c in baseline["contracts"].values() if c["contractId"] == contract["contractId"])
                require(current["state"] == "release-frozen" and current["packageRelease"] == dict(packageId=package_id, packageVersion=expected["packageVersion"]), "Public contract requires owning Accept/Freeze")
                require(all(current[f] == record[f] for f in ("canonicalDescriptor", "descriptorSha256", "abiSha256")), "Owned/receipt tuple differs")
                require(release.sha(b"sunder.rpc.abi.v1\0" + current["canonicalAbiProjection"].encode()) == record["abiSha256"], "Owned ABI digest differs")
            records.append(record)
        manifest, _, _ = release.archive_metadata(archive_path)
        verify_manifest(manifest, package, expected, inventory)
        require({t["rid"] for t in manifest["targets"]} == {evidence["rid"]}, "Qualified artifact RID differs")
        locked = release.load(root / Path(package["projectPath"]).parent / "sunder-contracts.lock.json")
        release.verify_consumer_projection(locked, manifest, role_uses=source_uses["roleUses"])
        with zipfile.ZipFile(archive_path) as archive:
            qualified_files = index(qualification["files"], "path")
            names = [i.filename for i in archive.infolist() if not i.is_dir()]
            require(len(names) == len(set(names)) and set(names) == qualified_files.keys(), "Qualified/archive file closure differs")
            for name in names:
                raw = archive.read(name)
                require(len(raw) == qualified_files[name]["bytes"] and release.sha(raw) == qualified_files[name]["sha256"], "Qualified archive bytes differ")
            by_id = index(list(emitted.values()), "contractId")
            for bundle in manifest["contractBundles"]:
                snapshot = by_id[bundle["contractId"]]
                require(archive.read("payload/shared/" + bundle["descriptorPath"]) == snapshot["canonicalDescriptor"].encode()
                        and all(bundle[f] == snapshot[f] for f in ("descriptorSha256", "abiSha256")), "Archive/receipt descriptor differs")
    require(all(release.file_sha(path) == sha for path, sha in inputs.items()), "Verification inputs changed")
    return dict(schemaVersion=1, inventorySha256=release.file_sha(root / INVENTORY),
                contracts=sorted(records, key=lambda c: c["contractId"]),
                qualificationInputs={str(path.relative_to(root)): sha for path, sha in sorted(inputs.items())})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--source-only", action="store_true")
    parser.add_argument("--evidence", type=Path)
    parser.add_argument("--propose", action="store_true")
    args = parser.parse_args()
    try:
        require(args.source_only != bool(args.evidence) and (not args.propose or args.evidence), "Select --source-only or --evidence; --propose requires evidence")
        root = args.root.resolve()
        expected, inventory = expected_inventory(root)
        verify_source(root, expected)
        if args.source_only:
            print("Current source inventory: 15 packages, 12 App / 15 Worker, 18 public / 13 private.")
            return 0
        proposed = propose(root, args.evidence, expected, inventory)
        if args.propose: print(json.dumps(proposed, ensure_ascii=False, indent=2))
        else:
            reviewed = release.load(root / RECORDS)
            require(reviewed["schemaVersion"] == 1 and reviewed["inventorySha256"] == proposed["inventorySha256"]
                    and reviewed["contracts"] == proposed["contracts"], "Current records differ; review a separate --propose result")
            print("Current qualified artifacts match independent inventory and reviewed contract records.")
        return 0
    except (ValueError, KeyError, TypeError, OSError, ET.ParseError, zipfile.BadZipFile) as error:
        print("Current contract verification failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
