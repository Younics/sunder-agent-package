#!/usr/bin/env python3
"""Closed Agent release metadata. Does not build, select dependencies, or write source.

Core owns archive/descriptor validation. These projections bind its already packed,
native-tested inputs to release policy and reconcile them with the committed locks.
"""

import copy
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import zipfile


# Explicit runtime identities; project/NuGet names and the agent/v* tag are separate.
# Package Maker intentionally belongs to this family without the AI Agent prefix.
FAMILY_PACKAGE_IDS = (
    "younics.ai.agent",
    "younics.sunder.package.maker",
    "younics.ai.agent.execution.docker",
    "younics.ai.agent.execution.local",
    "younics.ai.agent.mcp",
    "younics.ai.agent.memory.semantic",
    "younics.ai.agent.provider.anthropic",
    "younics.ai.agent.provider.gemini",
    "younics.ai.agent.provider.lmstudio",
    "younics.ai.agent.provider.openai",
    "younics.ai.agent.skills",
    "younics.ai.agent.subagents",
    "younics.ai.agent.tools.files",
    "younics.ai.agent.tools.shell",
    "younics.ai.agent.tools.web",
)


class Invalid(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Invalid(message)


def unique(pairs):
    result = {}
    names = set()
    for key, value in pairs:
        require(key.casefold() not in names, "Duplicate/case-colliding JSON member")
        names.add(key.casefold())
        result[key] = value
    return result


def decode(data):
    require(len(data) <= 16 * 1024 * 1024, "Release JSON exceeds its bound")
    return json.loads(data.decode("utf-8"), object_pairs_hook=unique,
                      parse_constant=lambda _: require(False, "Non-finite JSON number"))


def regular(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink(), f"Not a regular input: {path}")
    return path


def load(path):
    with regular(path).open("rb") as stream:
        return decode(stream.read(16 * 1024 * 1024 + 1))


def encoded(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")


def canonical(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, sort_keys=True,
                      separators=(",", ":")).encode("utf-8")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def file_sha(path):
    digest = hashlib.sha256()
    with regular(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def closed(value, fields):
    require(type(value) is dict and set(value) == set(fields.split()), "Missing/unknown release fields")


def digest(value, length=64):
    require(type(value) is str and re.fullmatch(f"[0-9a-f]{{{length}}}", value), "Invalid identity digest")
    return value


def positive(value):
    require(type(value) is int and 0 < value <= 9007199254740991, "Invalid positive safe integer")
    return value


def origin(value):
    require(type(value) is str, "Expected a canonical pinned HTTPS Registry origin")
    match = re.fullmatch(r"https://([a-z0-9.-]+)(?::([0-9]+))?/?", value)
    require(match, "Expected a canonical pinned HTTPS Registry origin")
    host, port = match.groups()
    require(len(host) <= 253 and re.search(r"[a-z]", host)
            and all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label)
                    and not label.startswith("xn--") for label in host.split(".")), "Noncanonical Registry DNS host")
    require(port is None or (not port.startswith("0") and 1 <= int(port) <= 65535 and int(port) != 443),
            "Noncanonical Registry port")
    return value.removesuffix("/")


def authoring_origin(value):
    # Core owned/lock documents use canonical AbsoluteUri, including exactly '/'.
    require(type(value) is str and value.endswith("/"), "Authoring origin requires its canonical trailing slash")
    return origin(value) + "/"


def timestamp(value):
    require(type(value) is str and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?(?:Z|[+-]\d\d:\d\d)", value),
            "Invalid UTC/offset timestamp")
    # Python 3.9 accepts only three/six fractional digits; Core's DateTimeOffset
    # and owned-baseline wire forms can retain more. Validate the calendar/offset
    # without changing the exact stored text or its hash.
    parsed = re.sub(r"\.(\d+)", lambda match: "." + (match[1] + "000000")[:6], value)
    return datetime.fromisoformat(parsed.replace("Z", "+00:00"))


def safe_relative(value):
    require(type(value) is str and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]*", value)
            and all(part not in ("", ".", "..") for part in value.split("/")), "Unsafe relative path")
    return value


def source_file(root, relative, *, optional=False):
    path = Path(root)
    for part in safe_relative(relative).split("/"):
        path /= part
        require(not path.is_symlink(), "Source input contains a symlink")
    if optional and not path.exists():
        return None
    return regular(path)


def version(value):
    require(type(value) is str and len(value) <= 256, "Invalid SemVer")
    match = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
                         r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
                         r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?", value)
    require(match, "Invalid strict SemVer")
    pre = match[4]
    parts = []
    for item in pre.split(".") if pre else []:
        require(not item.isdigit() or item == "0" or not item.startswith("0"), "Noncanonical prerelease")
        parts.append((0, int(item)) if item.isdigit() else (1, item))
    return tuple(int(match[i]) for i in (1, 2, 3)), pre is None, tuple(parts)


def satisfies(selected, constraint):
    # PackageVersionRange's complete V1 grammar: exact or space-conjoined comparisons.
    require(type(constraint) is str and 0 < len(constraint) <= 1024
            and constraint == constraint.strip() and not re.search(r"[^\S ]", constraint), "Invalid reviewed range")
    parts = [part for part in constraint.split(" ") if part]
    actual = version(selected)
    comparisons = []
    for part in parts:
        match = re.fullmatch(r"(>=|<=|>|<|=)?(.+)", part)
        op, text = match.groups()
        require(op or len(parts) == 1, "Unqualified version in compound range")
        bound = version(text)
        comparisons.append({None: actual == bound, "=": actual == bound, ">": actual > bound,
                            ">=": actual >= bound, "<": actual < bound, "<=": actual <= bound}[op])
    return all(comparisons)


def inventory(source):
    value = load(source_file(source, "packages.json"))
    version(value["releaseVersion"])
    packages = value["packages"]
    require(len(packages) == 15 and all(p["artifactType"] == "sunderpkg" for p in packages), "Expected 15 archives")
    require(len({p["packageId"] for p in packages}) == 15, "Duplicate family identity")
    require({p["packageId"] for p in packages} == set(FAMILY_PACKAGE_IDS), "Expected the exact AI Agent runtime family")
    for package in packages:
        require(re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)*", package["packageId"]), "Invalid package ID")
        safe_relative(package["projectPath"])
    return value


def locks(source):
    records = []
    for package in inventory(source)["packages"]:
        relative = str(PurePosixPath(package["projectPath"]).parent / "sunder-contracts.lock.json")
        records.append({"path": relative, "sha256": file_sha(source_file(source, relative))})
    require(len({r["path"] for r in records}) == 15, "Each production project must own one lock")
    records.sort(key=lambda r: r["path"])
    # Canonical UTF-8, sorted object keys, no BOM or final newline (Workstream H).
    return sha(canonical(records))


def package_imports(lock):
    """Read reviewed role intent, never the legacy missing-field migration shape."""
    imports = lock["packageImports"]
    require(type(imports) is dict, "Invalid packageImports")
    for package_id, entry in imports.items():
        require(type(entry) is dict, "Invalid package API selection")
        require("consumerRoles" in entry,
                f"{package_id}: package-api.consumer-roles-migration-required; run MigrateSunderPackageApiConsumerRoles explicitly")
        closed(entry, "localName presence requiredContracts consumerRoles")
        require(entry["consumerRoles"] in (["app"], ["runtime"], ["app", "runtime"]),
                f"{package_id}: package-api.consumer-roles-invalid")
        require(type(entry["localName"]) is str and re.fullmatch(r"[a-z][A-Za-z0-9]{0,127}", entry["localName"]),
                "Invalid package API local name")
        require(entry["presence"] in ("required", "optional"), "Invalid package API presence")
        required = entry["requiredContracts"]
        require(type(required) is list and all(type(key) is str for key in required)
                and required == sorted(set(required)), "Invalid required-contract readiness")
        require(package_id in lock["packageArtifacts"], "Package API has no selected artifact")
        contracts = lock["packageArtifacts"][package_id]["contracts"]
        require(set(required) <= {key for key, record in contracts.items() if record["authorityKind"] == "package"},
                "Required contract is outside the selected consumer subset")
    return imports


def use_policy(value):
    actions = value.get("actions")
    require(type(value.get("required")) is bool and type(actions) is list and actions
            and actions == [action for action in ("discover", "invoke", "subscribe") if action in actions],
            "Invalid contract use policy")
    return value["required"], tuple(actions)


def validate_role_uses(role_uses):
    require(type(role_uses) in (list, tuple) and len(role_uses) <= 128, "Invalid source role-use evidence")
    keys = []
    for use in role_uses:
        closed(use, "role contractId contractVersion descriptorSha256 abiSha256 serviceId serviceType required actions")
        require(use["role"] == "runtime", "Source role uses grant Runtime only")
        require(all(type(use[field]) is str and use[field] for field in ("contractId", "serviceId", "serviceType")),
                "Incomplete source service identity")
        version(use["contractVersion"])
        digest(use["descriptorSha256"])
        digest(use["abiSha256"])
        use_policy(use)
        keys.append((use["contractId"], use["serviceId"]))
    require(keys == sorted(set(keys)), "Source role uses must be unique and ordered")


def source_role_use_evidence(qualification):
    """Project Core's source-backed Worker receipt, never infer grants from a manifest.

    The caller authenticates the qualified role-input bytes/source frame. Native
    capture and transport bind this projection to those exact qualification bytes;
    Core's prebuilt Pack remains the source/receipt validation authority.
    """
    frame, plan = qualification["inputs"], qualification["rolePlan"]
    workers = [role for role in plan["roles"] if role["role"] == "worker"]
    require(len(workers) == 1, "Source use evidence requires one qualified Worker receipt")
    receipt = decode(workers[0]["receipt"].encode("utf-8"))
    require(type(receipt["schemaVersion"]) is int and receipt["schemaVersion"] == 1
            and receipt["producer"]["name"] == "Sunder.Package.Build" and receipt["producer"]["language"] == "csharp"
            and "role" not in receipt and receipt["packageId"] == frame["packageId"]
            and receipt["packageVersion"] == frame["packageVersion"]
            and receipt["lockSha256"] == frame["contractLockSha256"]
            and receipt["inputFingerprint"] == plan["inputFingerprint"], "Source role-use receipt identity differs from qualification")
    digest(receipt["lockSha256"])
    digest(receipt["inputFingerprint"])
    role_uses = receipt.get("roleUses", [])
    validate_role_uses(role_uses)
    return {"lockSha256": receipt["lockSha256"], "roleUses": role_uses}


def qualified_owned_baseline(source_root, frame):
    """Read Core's exact baseline-presence proof without inventing an empty file."""
    relative = frame["ownedBaseline"]
    path = source_file(source_root, relative, optional=True)
    markers = [item for item in frame["externalInputs"] if item["path"] == "baseline-absent:" + relative]
    if path is None:
        require(markers == [dict(path="baseline-absent:" + relative, bytes=0, sha256=sha(b""))]
                and frame["ownedBaselineSha256"] == sha(b""), "Missing owned baseline lacks exact qualified absence proof")
    else:
        require(not markers, "Qualified absent owned baseline appeared")
    return path


def source_use_policies(lock, manifest, bundles, role_uses):
    validate_role_uses(role_uses)
    policies = {}
    if not role_uses:
        return policies

    def matches(record, bundle):
        return (bundle.get("source") == "imported" and bundle.get("authorityKind") == "package"
                and bundle.get("exposure") == "public" and bundle["version"] == record["resolvedContractVersion"]
                and all(bundle.get(field) == record.get(field) for field in
                        ("authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity", "descriptorSha256", "abiSha256")))

    # A provider selection authenticates metadata closure, not consumer grants.
    # Require a reviewed, actually bound Runtime provider before using its closure.
    provider_artifacts = set()
    for group in manifest.get("providerImports", []):
        reviewed = lock.get("providerImports", {}).get(group["key"])
        if reviewed is None or any(group[field] != reviewed[field] for field in ("packageId", "contractId")):
            continue
        artifact = lock["packageArtifacts"].get(group["packageId"], {})
        record = artifact.get("contracts", {}).get(group["contractId"])
        root = bundles.get((group["contractId"], group["contractVersion"]))
        if record is None or root is None or not matches(record, root):
            continue
        if group["bundleRef"] != {"contractId": group["contractId"], "contractVersion": group["contractVersion"]}:
            continue
        if any(reference.get("role") == "runtime" and reference.get("contractId") == group["contractId"]
               and reference.get("contractVersion") == group["contractVersion"]
               and any(all(provider.get(field) == reference.get(field) for field in
                           ("role", "providerId", "contractId", "contractVersion")) for provider in manifest.get("provides", []))
               for reference in group.get("providerRefs", [])):
            provider_artifacts.add(group["packageId"])
    for use in role_uses:
        identity = (use["role"], use["contractId"], use["contractVersion"])
        bundle = bundles.get(identity[1:])
        require(bundle is not None and any(
            (record := lock["packageArtifacts"][package_id]["contracts"].get(use["contractId"])) is not None
            and matches(record, bundle) for package_id in provider_artifacts),
            "Source role use is outside the reviewed provider artifact closure")
        require(all(use[field] == bundle[field] for field in ("descriptorSha256", "abiSha256")),
                "Source role use differs from its exact imported descriptor")
        policy = use_policy(use)
        require(identity not in policies or policies[identity] == policy, "Conflicting source role-use policies")
        policies[identity] = policy
    return policies


def verify_consumer_projection(lock, manifest, *, role_uses=()):
    imports = package_imports(lock)
    groups = {item["packageId"]: item for item in manifest.get("packageApiImports", [])}
    require(len(groups) == len(manifest.get("packageApiImports", [])) and set(groups) == set(imports),
            "Packed package API import set differs from committed lock")
    present = {target["role"] for target in manifest.get("targets", [])}
    readiness = {}
    for package_id, entry in imports.items():
        group = groups[package_id]
        roles = set(entry["consumerRoles"]) & present
        require(roles, f"{package_id}: package-api.consumer-roles-unavailable")
        contracts = lock["packageArtifacts"][package_id]["contracts"]
        bundles = sorted((key, record["resolvedContractVersion"]) for key, record in contracts.items()
                         if record["authorityKind"] == "package")
        expected_bundles = [{"contractId": key, "contractVersion": version} for key, version in bundles]
        expected_uses = [{"role": role, **bundle} for role in sorted(roles) for bundle in expected_bundles]
        require(group["presence"] == entry["presence"] and group["bundleRefs"] == expected_bundles
                and group["useRefs"] == expected_uses, "Packed consumer role/bundle projection differs from reviewed intent")
        for role in roles:
            for key, version in bundles:
                identity = (role, key, version)
                readiness[identity] = readiness.get(identity, False) or key in entry["requiredContracts"]
    bundles = {(bundle["contractId"], bundle["version"]): bundle for bundle in manifest.get("contractBundles", [])}
    require(len(bundles) == len(manifest.get("contractBundles", [])), "Duplicate contract bundle identity")
    source_policies = source_use_policies(lock, manifest, bundles, role_uses)
    actual = {}
    for use in manifest.get("usesContracts", []):
        bundle = bundles.get((use["contractId"], use["contractVersion"]))
        require(bundle is not None and use["role"] in present, "Orphan contract use or unavailable role")
        if bundle["source"] != "imported":
            continue
        identity = (use["role"], use["contractId"], use["contractVersion"])
        require(identity not in actual and bundle.get("authorityKind") != "platform", "Duplicate or platform consumer use")
        require("abiSha256" not in bundle or use.get("abiSha256") == bundle["abiSha256"], "Imported use ABI differs from its bundle")
        actual[identity] = use_policy(use)
    require(set(actual) == set(readiness) | set(source_policies),
            "Packed imported uses/readiness differ from role-scoped reviewed intent")
    for identity, (required, actions) in actual.items():
        # Package imports still own required-wins readiness; source evidence is
        # a separate exact policy and cannot broaden/narrow a group's role set.
        if identity in readiness:
            require(required == readiness[identity], "Packed imported uses/readiness differ from role-scoped reviewed intent")
        if identity in source_policies:
            require((required, actions) == source_policies[identity], "Packed use differs from reviewed source role-use policy")


def snapshots(contract):
    return ([contract["current"]] if "current" in contract else []) + contract["history"]


def has_public_evidence(contract):
    return any(snapshot["exposure"] == "public" for snapshot in snapshots(contract))


def owned_contracts(baseline, package_id):
    fields = "schemaVersion authorityKind authorityId packageId contracts"
    closed(baseline, fields + (" retiredPrivateContracts" if "retiredPrivateContracts" in baseline else ""))
    require(type(baseline["schemaVersion"]) is int and baseline["schemaVersion"] == 1
            and baseline["authorityKind"] == "package"
            and baseline["authorityId"] == baseline["packageId"] == package_id, "Mixed owned baseline")
    active, retired = baseline["contracts"], baseline.get("retiredPrivateContracts", {})
    require(type(active) is dict and type(retired) is dict and len(active) + len(retired) <= 128,
            "Invalid combined active/retired baseline maps")
    for contracts in (active, retired):
        for record in contracts.values():
            closed(record, "contractId history" + (" current" if "current" in record else ""))
            require(type(record["history"]) is list and len(record["history"]) <= 256
                    and ("current" in record or record["history"]), "Invalid owned contract history")
            for snapshot in snapshots(record):
                require(type(snapshot) is dict and snapshot["exposure"] in ("public", "package")
                        and snapshot["state"] in ("draft", "release-frozen", "published"), "Invalid owned snapshot")
                require(contracts is not retired or snapshot["exposure"] == "package", "Retired private record contains public evidence")
    return active


def archive_metadata(path):
    with zipfile.ZipFile(regular(path)) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "Duplicate archive member")

        def read(name, limit=1048576):
            info = archive.getinfo(name)
            require(0 <= info.file_size <= limit and not info.is_dir(), "Metadata exceeds its bound")
            return archive.read(info)

        manifest_bytes = read("manifest/sunder-package.json")
        index_bytes = read("manifest/content-index.json")
        manifest = decode(manifest_bytes)
        identity = sha(hashlib.sha256(manifest_bytes).digest() + hashlib.sha256(index_bytes).digest())
        contracts = []
        for bundle in sorted(manifest.get("contractBundles", []), key=lambda b: b["contractId"]):
            if bundle["exposure"] != "public":
                continue
            descriptor = read("payload/shared/" + safe_relative(bundle["descriptorPath"]), 4194304)
            require(sha(descriptor) == bundle["descriptorSha256"], "Descriptor bytes differ from inspected identity")
            owned = bundle["source"] == "owned"
            contracts.append({
                "contractId": bundle["contractId"], "contractVersion": bundle["version"],
                "descriptorBytes": len(descriptor), "descriptorSha256": bundle["descriptorSha256"],
                "abiSha256": bundle["abiSha256"], "artifactSource": bundle["source"],
                "authorityKind": bundle["authorityKind"], "authorityId": bundle["authorityId"],
                "selectedAuthorityRelease": manifest["version"] if owned else bundle["selectedAuthorityRelease"],
                "selectedAuthorityContentIdentity": identity if owned else bundle["selectedAuthorityContentIdentity"],
            })
        public = {"schemaVersion": 1, "packageId": manifest["id"], "packageVersion": manifest["version"],
                  "packageContentIdentity": identity, "publicContractCount": len(contracts), "contracts": contracts}
        public_sha = sha(b"sunder.package-public-api-inventory.v1\0" + encoded(public))
        return manifest, public, public_sha


def artifact_records(source, assets):
    family = inventory(source)
    result = []
    for package in family["packages"]:
        name = f"{package['packageId']}.{family['releaseVersion']}.sunderpkg"
        path = Path(assets) / name
        manifest, public, public_sha = archive_metadata(path)
        require(manifest["id"] == package["packageId"] and manifest["version"] == family["releaseVersion"],
                "Retained archive is a different package/version")
        result.append({"path": name, "packageId": package["packageId"], "version": family["releaseVersion"],
                       "size": path.stat().st_size, "sha256": file_sha(path),
                       "packageContentIdentity": public["packageContentIdentity"], "publicInventorySha256": public_sha})
    return sorted(result, key=lambda r: r["path"])


def publication(record, owner):
    digest(owner, 32)
    require(owner != "0" * 32, "Missing approved Registry owner")
    return {"package": {k: record[k] for k in ("packageId", "version", "packageContentIdentity")},
            "ownerPrincipalId": owner, "publicInventorySha256": record["publicInventorySha256"],
            "initialAvailability": "Yanked"}


def native_source_role_uses(assets, records):
    """Read the projection retained from each qualified native Worker receipt."""
    path = Path(assets) / "native-test-evidence.json"
    if not path.exists():
        return {}
    from native_release_inputs import RIDS, json_file
    regular(path)
    evidence = json_file(path.parent.resolve() / path.name, 256 * 1024 * 1024, 4 * 1024 * 1024)
    require(type(evidence["schemaVersion"]) is int and evidence["schemaVersion"] == 3, "Expected native source-use evidence schema 3")
    expected = {item["path"]: {field: item[field] for field in ("path", "size", "sha256")} for item in records}
    final = evidence["finalArtifacts"]
    require(len(final) == len(expected) and {item["path"]: item for item in final} == expected,
            "Source role-use evidence belongs to different final archives")
    runs = evidence["nativeRuns"]
    require(len(runs) == len(RIDS) and {run["rid"] for run in runs} == set(RIDS), "Incomplete native source-use RID evidence")
    package_ids = {item["packageId"] for item in records}
    result = {}
    for run in runs:
        require(len(run["packages"]) == len(package_ids) and {item["packageId"] for item in run["packages"]} == package_ids,
                "Incomplete native source-use package evidence")
        for package in run["packages"]:
            package_id, reviewed = package["packageId"], package.get("sourceRoleUses")
            require(package_id not in result or result[package_id] == reviewed, "Source role-use evidence differs across native RIDs")
            result[package_id] = reviewed
    return {key: value for key, value in result.items() if value is not None}


def verify_family(source, assets, registry=None, *, source_role_uses=None):
    """Verify retained metadata without publication. None means local unpublished authority.

    Explicit source_role_uses must come from authenticated Core qualifications;
    release callers normally use the projection in retained native evidence.
    """
    if registry is not None:
        origin(registry)
    family = inventory(source)
    records = artifact_records(source, assets)
    reviewed_uses = native_source_role_uses(assets, records) if source_role_uses is None else source_role_uses
    require(type(reviewed_uses) is dict and set(reviewed_uses) <= {item["packageId"] for item in records}, "Unknown source role-use package")
    metadata = {r["packageId"]: archive_metadata(Path(assets) / r["path"]) for r in records}
    graph = {}
    for package in family["packages"]:
        package_id = package["packageId"]
        manifest, _, _ = metadata[package_id]
        dependencies = manifest.get("dependsOn", [])
        graph[package_id] = {d["packageId"] for d in dependencies}
        require(len(graph[package_id]) == len(dependencies), "Duplicate ordinary dependency")
        for dependency in dependencies:
            target = dependency["packageId"]
            require(target in metadata, "Dependency outside retained Agent cohort requires separately reviewed candidates")
            require(satisfies(metadata[target][0]["version"], dependency["versionRange"]), "Retained dependency fails ordinary range")
        relative = str(PurePosixPath(package["projectPath"]).parent / "sunder-contracts.lock.json")
        lock_path = source_file(source, relative)
        lock = load(lock_path)
        evidence = reviewed_uses.get(package_id)
        if evidence is not None:
            closed(evidence, "lockSha256 roleUses")
            require(evidence["lockSha256"] == file_sha(lock_path), "Source role-use evidence differs from the committed lock")
        verify_consumer_projection(lock, manifest, role_uses=evidence["roleUses"] if evidence is not None else ())
        imports = {i["packageId"]: i for i in manifest.get("packageArtifactImports", [])}
        require(len(imports) == len(manifest.get("packageArtifactImports", [])), "Duplicate artifact import")
        require(set(imports) == set(lock["packageArtifacts"]), "Archive/committed lock import set differs")
        for selected_id, entry in lock["packageArtifacts"].items():
            require(selected_id in graph[package_id], "An API selection is missing its ordinary dependency")
            require("registryOrigin" not in entry or registry is not None and authoring_origin(entry["registryOrigin"]) == origin(registry) + "/", "Lock origin differs from selected authority scope")
            selected, public, public_sha = metadata[selected_id]
            require(entry["selectedPackageVersion"] == selected["version"]
                    and entry["selectedPackageContentIdentity"] == public["packageContentIdentity"]
                    and entry["publicInventorySha256"] == public_sha
                    and entry["publicContractCount"] == public["publicContractCount"], "Complete selected artifact identity differs")
            require(satisfies(selected["version"], entry["requestedVersionRange"]), "Selected package fails reviewed range")
            require(type(entry["includePrerelease"]) is bool and (version(selected["version"])[1] or entry["includePrerelease"]),
                    "Prerelease was not reviewed")
            projected = {}
            for contract in public["contracts"]:
                projected[contract["contractId"]] = {
                    **{k: contract[k] for k in ("artifactSource", "authorityKind", "authorityId", "selectedAuthorityRelease",
                                                "selectedAuthorityContentIdentity")},
                    "resolvedContractVersion": contract["contractVersion"], "descriptorBytes": contract["descriptorBytes"],
                    "descriptorSha256": contract["descriptorSha256"], "abiSha256": contract["abiSha256"],
                    "etag": '"' + contract["descriptorSha256"] + '"',
                }
            require(canonical(entry["contracts"]) == canonical(projected), "Full committed public inventory does not reproduce exactly")
            for field in ("requestedVersionRange", "includePrerelease", "selectedPackageVersion", "selectedPackageContentIdentity",
                          "publicContractCount", "publicInventorySha256"):
                require(imports[selected_id][field] == entry[field], "Packed artifact import differs from committed lock")
        platform = {i["key"]: i for i in manifest.get("platformImports", [])}
        require(set(platform) == set(lock["platformImports"]), "Packed platform import set differs")
        for key, entry in lock["platformImports"].items():
            require("registryOrigin" not in entry or registry is not None and authoring_origin(entry["registryOrigin"]) == origin(registry) + "/", "Platform origin differs from selected authority scope")
            for field in ("authorityKind", "authorityId", "authorityReleaseRange", "includePrerelease",
                          "selectedAuthorityRelease", "selectedAuthorityContentIdentity", "contractId"):
                require(platform[key][field] == entry[field], "Packed platform selection differs")
            require(platform[key]["contractVersion"] == entry["resolvedContractVersion"], "Packed platform version differs")
    order = []
    while len(order) < len(graph):
        ready = sorted(p for p, deps in graph.items() if p not in order and deps <= set(order))
        require(ready, "Family contains a dependency cycle")
        order.append(ready[0])
    require(order[0] == "younics.ai.agent", "The retained base owner must publish first")
    return {"lockSetSha256": locks(source), "order": order, "artifacts": records}


def family_plan(source, assets, registry, owner):
    origin(registry)
    verified = verify_family(source, assets, registry)
    return {"lockSetSha256": verified["lockSetSha256"], "order": verified["order"],
            "publications": [publication(record, owner) for record in verified["artifacts"]]}


def validate_provenance(source, assets):
    path = Path(assets) / "release-provenance.json"
    value = load(path)
    closed(value, "schemaVersion buildType invocation sources materials artifactSourceCommit lockSetSha256 controlPlane publication artifacts")
    require(path.read_bytes() == canonical(value) + b"\n", "Noncanonical release provenance")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1
            and value["buildType"] == "https://sunderapp.io/provenance/github-actions/v1", "Unknown provenance contract")
    inv = value["invocation"]
    closed(inv, "repository workflow workflowRef runId runAttempt releaseId ref refName sha")
    digest(value["artifactSourceCommit"], 40)
    require(inv["sha"] == value["artifactSourceCommit"], "Artifact source and invocation differ")
    for field in ("runId", "runAttempt"):
        require(type(inv[field]) is str and re.fullmatch(r"[1-9][0-9]*", inv[field]), "Invalid run coordinate")
    positive(inv["releaseId"])
    control = value["controlPlane"]
    closed(control, "repositoryId runId workflowPath workflowSha event")
    positive(control["repositoryId"])
    positive(control["runId"])
    require(str(control["runId"]) == inv["runId"] and control["event"] == "push"
            and control["workflowPath"] == ".github/workflows/sunder-package-release.yml"
            and control["workflowSha"] == value["artifactSourceCommit"], "Control-plane source/run differs")
    require(inv["repository"] == "Younics/sunder-agent-package" and inv["workflow"] == "AI Agent Family Release"
            and inv["refName"] == "agent/v" + inventory(source)["releaseVersion"]
            and inv["ref"] == "refs/tags/" + inv["refName"]
            and inv["workflowRef"] == inv["repository"] + "/" + control["workflowPath"] + "@" + inv["ref"], "Workflow identity differs")
    require(len(value["sources"]) == 2 and len(value["materials"]) == 1, "Source/material set differs")
    for item in value["sources"]:
        closed(item, "name sha")
        digest(item["sha"], 40)
    material = value["materials"][0]
    closed(material, "name tag sha packageVersion")
    require(material["name"] == "sunder-core-sdk" and material["tag"] == "sdk/v" + material["packageVersion"], "Core material differs")
    require(value["sources"] == [{"name": "sunder-agent-package", "sha": inv["sha"]},
                                 {"name": "sunder-core", "sha": material["sha"]}], "Source/material identities differ")
    closed(value["publication"], "registryOrigin ownerPrincipalId")
    origin(value["publication"]["registryOrigin"])
    digest(value["publication"]["ownerPrincipalId"], 32)
    require(value["publication"]["ownerPrincipalId"] != "0" * 32, "Missing approved owner")
    require(value["lockSetSha256"] == locks(source), "Aggregate production lock set differs")
    require(type(value["artifacts"]) is list and len(value["artifacts"]) == 15, "Expected exactly 15 primary artifacts")
    for record in value["artifacts"]:
        closed(record, "path packageId version size sha256 packageContentIdentity publicInventorySha256")
        positive(record["size"])
        for field in ("sha256", "packageContentIdentity", "publicInventorySha256"):
            digest(record[field])
    require(value["artifacts"] == artifact_records(source, assets), "Inspected primary artifact set differs")
    return value


def git(source, *args):
    return subprocess.check_output(["git", "-C", str(source), *args])


def baseline_at(source, commit, relative):
    entry = git(source, "ls-tree", "--format=%(objecttype) %(objectmode)", commit, "--", relative).decode().strip()
    require(entry in ("", "blob 100644"), "Owned baseline tree entry is not a regular file")
    return git(source, "show", f"{commit}:{relative}") if entry else None


def verify_evidence_commit(source, evidence_commit, provenance):
    artifact = provenance["artifactSourceCommit"]
    digest(evidence_commit, 40)
    parents = git(source, "rev-list", "--parents", "-n", "1", evidence_commit).decode().split()
    require(parents == [evidence_commit, artifact], "Publication evidence must be an unmerged direct child of artifact source")
    changes = git(source, "diff-tree", "--no-commit-id", "--raw", "-r", "--no-renames", artifact, evidence_commit).decode().splitlines()
    family = inventory(source)
    allowed = {str(PurePosixPath(p["projectPath"]).parent / "sunder-contracts.owned.json"): p["packageId"]
               for p in family["packages"]}
    require(changes, "Publication evidence commit contains no evidence")
    for line in changes:
        metadata, relative = line.split("\t")
        fields = metadata.split()
        require(len(fields) == 5 and fields[:2] == [":100644", "100644"] and fields[4] == "M" and relative in allowed,
                "Evidence commit changes source, locks, paths, modes, or unapproved receipts")
    records = {r["packageId"]: r for r in provenance["artifacts"]}
    confirmed = 0
    for relative, package_id in allowed.items():
        original_bytes = baseline_at(source, artifact, relative)
        actual_bytes = baseline_at(source, evidence_commit, relative)
        path = source_file(source, relative, optional=True)
        require((path.read_bytes() if path else None) == original_bytes, "Baseline checkout differs from artifact-source tree")
        if original_bytes is None:
            require(actual_bytes is None, "Evidence cannot create a private-only baseline")
            continue
        require(actual_bytes is not None, "Evidence cannot remove an owned baseline")
        before = decode(original_bytes)
        after = decode(actual_bytes)
        owned_contracts(before, package_id)
        owned_contracts(after, package_id)
        stripped = copy.deepcopy(after)
        require(set(before["contracts"]) == set(after["contracts"]), "Evidence commit changes owned contract set")
        for key, original in before["contracts"].items():
            actual = after["contracts"][key]
            require(len(original["history"]) == len(actual["history"]), "Evidence commit changes history")
            require(("current" in original) == ("current" in actual), "Evidence cannot add/remove a current contract")
            if not has_public_evidence(original):
                require(canonical(actual) == canonical(original), "Private baseline history is immutable publication evidence")
                continue
            for old, new, reset in zip(snapshots(original), snapshots(actual), snapshots(stripped["contracts"][key])):
                if old["state"] == "published" or old.get("packageRelease", {}).get("packageVersion") != records[package_id]["version"]:
                    require(old["state"] != "draft", "Release source contains an unfrozen contract")
                    require(canonical(new) == canonical(old), "Previously published evidence is immutable")
                    continue
                require(old["state"] == "release-frozen" and new["state"] == "published", "Every frozen tuple needs publication evidence")
                pub = new["publication"]
                closed(pub, "registryOrigin packageId packageVersion packageContentIdentity publishedAtUtc etag")
                require(authoring_origin(pub["registryOrigin"]) == origin(provenance["publication"]["registryOrigin"]) + "/"
                        and pub["packageId"] == package_id and pub["packageVersion"] == records[package_id]["version"]
                        and pub["packageContentIdentity"] == records[package_id]["packageContentIdentity"]
                        and pub["etag"] == '"' + old["descriptorSha256"] + '"'
                        and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?Z", pub["publishedAtUtc"]), "Publication fields differ from retained artifact")
                timestamp(pub["publishedAtUtc"])
                confirmed += 1
                reset["state"] = old["state"]
                del reset["publication"]
        require(canonical(stripped) == canonical(before), "Evidence commit modifies descriptor/ABI/source or frozen release fields")
    require(confirmed > 0, "Evidence child contains no verified publication fields")


def main():
    command, *args = sys.argv[1:]
    if command == "lock-set":
        print(locks(*args))
    elif command == "plan":
        print(encoded(family_plan(*args)).decode())
    elif command == "verify-provenance":
        validate_provenance(*args)
    elif command == "complete-provenance":
        source, assets = args
        path = Path(assets) / "release-provenance.json"
        value = load(path)
        value.update(artifactSourceCommit=value["invocation"]["sha"], lockSetSha256=locks(source),
                     controlPlane={"repositoryId": int(os.environ["GITHUB_REPOSITORY_ID"]),
                                   "runId": int(value["invocation"]["runId"]),
                                   "workflowPath": ".github/workflows/sunder-package-release.yml",
                                   "workflowSha": os.environ["GITHUB_WORKFLOW_SHA"], "event": os.environ["GITHUB_EVENT_NAME"]},
                     publication={"registryOrigin": origin(os.environ["REGISTRY_API_URL"].rstrip("/")),
                                  "ownerPrincipalId": digest(os.environ["SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID"], 32)},
                     artifacts=artifact_records(source, assets))
        path.write_bytes(canonical(value) + b"\n")
        validate_provenance(source, assets)
    elif command == "verify-evidence-commit":
        source, assets, commit = args
        verify_evidence_commit(source, commit, validate_provenance(source, assets))
    elif command == "expected-publication":
        provenance, package_id = args
        value = load(provenance)
        records = [r for r in value["artifacts"] if r["packageId"] == package_id]
        require(len(records) == 1, "Missing exact primary artifact")
        print(encoded(publication(records[0], value["publication"]["ownerPrincipalId"])).decode())
    elif command == "publication-time":
        source, commit, package_id = args
        digest(commit, 40)
        family = inventory(source)
        package = next(p for p in family["packages"] if p["packageId"] == package_id)
        relative = str(PurePosixPath(package["projectPath"]).parent / "sunder-contracts.owned.json")
        data = baseline_at(source, commit, relative)
        contracts = owned_contracts(decode(data), package_id) if data is not None else {}
        times = {s["publication"]["publishedAtUtc"] for c in contracts.values() if has_public_evidence(c)
                 for s in snapshots(c) if s["state"] == "published"
                 and s["publication"]["packageVersion"] == family["releaseVersion"]}
        require(len(times) <= 1, "Owned publication timestamps disagree")
        print(next(iter(times), ""))
    elif command == "check-json":
        load(args[0])
    elif command == "inspect":
        manifest, public, inventory_sha = archive_metadata(args[0])
        print(encoded({"archiveSha256": file_sha(args[0]), "inventory": public,
                       "publicInventorySha256": inventory_sha, "targets": manifest["targets"],
                       "dependsOn": manifest.get("dependsOn", []), "platformImports": manifest.get("platformImports", [])}).decode())
    else:
        raise Invalid("Unknown Agent release command")


if __name__ == "__main__":
    try:
        main()
    except (Invalid, ValueError, KeyError, TypeError, OSError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        sys.exit(f"Agent release evidence rejected: {error}")
