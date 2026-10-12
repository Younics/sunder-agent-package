#!/usr/bin/env python3
"""Check or explicitly write CURRENT service expectations from owning evidence.

This never accepts/freezes contracts, builds packages, updates locks, or writes
historical evidence. Public tuples come from the frozen owning baseline and must
agree with its existing receipt. Selected private tuples come from owning receipts;
their reviewed identities, versions and method sets cannot change in this operation.
The C# current-descriptor readers remain responsible for full SDK schema/ABI parsing.
"""

import argparse
import base64
import hashlib
import json
import re
import sys
from pathlib import Path


PUBLIC = "compatibility/agent-service-public-successors.json"
PRIVATE = "compatibility/agent-ordinary-private-successors.json"
HISTORY = "compatibility/agent-rpc-migration-baseline.json"
HISTORY_SHA256 = "a9f4563d6095b57d66ca915821218ef596a0845ab17839d3e5bba5e394a899eb"
OWNER = "sunder.package.agent"
SOURCE = "src/Sunder.Package.Agent"
ABI_DOMAIN = b"sunder.rpc.abi.v1\0"
PURPOSE = "Reviewed current public services from owning Accept/Freeze evidence; historical migration evidence remains immutable."


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON member: {key}")
        result[key] = value
    return result


def parse(text):
    return json.loads(text, object_pairs_hook=unique_object)


def digest(value):
    return hashlib.sha256(value).hexdigest()


def tuple_from_snapshot(package_id, source, snapshot):
    raw = snapshot["canonicalDescriptor"].encode("utf-8")
    descriptor = parse(raw)
    require(descriptor["descriptorVersion"] == 1
            and descriptor["schemaProfile"] == "sunder.rpc.values.v1", "Unsupported descriptor profile")
    require(len(raw) == snapshot["descriptorBytes"], "Descriptor byte count mismatch")
    require(digest(raw) == snapshot["descriptorSha256"], "Descriptor digest mismatch")
    require(descriptor["version"] == snapshot["contractVersion"], "Descriptor version mismatch")
    require(re.fullmatch(r"[0-9a-f]{64}", snapshot["abiSha256"]) is not None, "Invalid ABI digest")
    if "canonicalAbiProjection" in snapshot:
        abi = snapshot["canonicalAbiProjection"].encode("utf-8")
        require(len(abi) == snapshot["abiProjectionBytes"], "ABI byte count mismatch")
        require(digest(ABI_DOMAIN + abi) == snapshot["abiSha256"], "ABI digest mismatch")
        require(parse(abi)["contractId"] == descriptor["contractId"], "ABI contract identity mismatch")
    methods = {key: sorted(value["methods"]) for key, value in sorted(descriptor["services"].items())}
    require(methods and all(methods.values()), "Empty service/method inventory")
    return dict(packageId=package_id, sourceDirectory=source, contractId=descriptor["contractId"],
                version=descriptor["version"], descriptorSha256=snapshot["descriptorSha256"],
                abiSha256=snapshot["abiSha256"], methods=methods)


def reconcile(root, configuration, rid, private_ids=(), write=False):
    root = root.resolve()
    inputs = {}

    def read(relative, optional=False):
        path = root / relative
        require(path.resolve().is_relative_to(root), "Evidence path escapes repository")
        if path not in inputs:
            inputs[path] = path.read_bytes() if path.exists() else None
        raw = inputs[path]
        require(optional or raw is not None, f"Missing owning evidence: {relative}")
        return None if raw is None else parse(raw)

    def receipt(source, package_id):
        require(source.startswith("src/") and ".." not in Path(source).parts, "Invalid source directory")
        relative = f"{source}/obj/{configuration}/net10.0/sunder-analysis/{rid}/sunder-rpc/authoring-receipt.v1.json"
        value = read(relative)
        require(value["schemaVersion"] == 1 and value["packageId"] == package_id, "Receipt owner mismatch")
        contracts = {item["contractId"]: item for item in value["contracts"]}
        require(len(contracts) == len(value["contracts"]), "Duplicate receipt contract")
        return value, contracts

    history = read(HISTORY)
    require(digest(inputs[root / HISTORY]) == HISTORY_SHA256, "Historical oracle has changed")
    historical = {item["contractId"]: item for item in history["descriptors"] if item["exposure"] == "public"}
    require(len(historical) == 19, "Historical public inventory must contain 19 identities")
    owned = read(f"{SOURCE}/sunder-contracts.owned.json")
    require(owned["schemaVersion"] == 1 and owned["authorityKind"] == "package"
            and owned["authorityId"] == owned["packageId"] == OWNER, "Public baseline owner mismatch")
    owner_receipt, emitted = receipt(SOURCE, OWNER)
    public = []
    for record in owned["contracts"].values():
        current = record.get("current")
        if current is None or current["exposure"] != "public":
            continue
        contract_id = record["contractId"]
        require(contract_id in historical, f"Unreviewed public identity: {contract_id}")
        require(current["state"] == "release-frozen", f"Current public contract is not frozen: {contract_id}")
        require(current["packageRelease"] == dict(packageId=OWNER, packageVersion=owner_receipt["packageVersion"]),
                f"Frozen release does not match owning receipt: {contract_id}")
        original = historical[contract_id]
        retained = [current, *record["history"]]
        require(any(item["contractVersion"] == original["version"]
                    and item["exposure"] == "public"
                    and item["canonicalDescriptor"].encode("utf-8") == base64.b64decode(original["canonicalDescriptorBase64"], validate=True)
                    and item["canonicalAbiProjection"].encode("utf-8") == base64.b64decode(original["canonicalAbiProjectionBase64"], validate=True)
                    and item["descriptorSha256"] == original["descriptorSha256"]
                    and item["abiSha256"] == original["abiSha256"] for item in retained),
                f"Original public snapshot is not retained: {contract_id}")
        expected = tuple_from_snapshot(OWNER, SOURCE, current)
        require(expected["contractId"] == contract_id, "Public record/descriptor identity mismatch")
        generated = emitted[contract_id]
        require(generated["exposure"] == "public" and generated["canonicalDescriptor"] == current["canonicalDescriptor"],
                f"Owning receipt differs from current frozen source: {contract_id}")
        require(expected == tuple_from_snapshot(OWNER, SOURCE, generated), f"Receipt tuple mismatch: {contract_id}")
        public.append(expected)
    require(len(public) == 19 and {item["contractId"] for item in public} == historical.keys(),
            "Current public inventory differs from reviewed 19 identities")
    old_public = read(PUBLIC, optional=True)
    if old_public is not None:
        require(set(old_public) == {"schemaVersion", "purpose", "contracts"} and old_public["schemaVersion"] == 1
                and isinstance(old_public["purpose"], str) and old_public["purpose"].strip(), "Invalid current public fixture")
    proposed_public = dict(schemaVersion=1, purpose=old_public["purpose"] if old_public else PURPOSE,
                           contracts=sorted(public, key=lambda item: item["contractId"]))

    private = read(PRIVATE)
    require(private["schemaVersion"] == 1 and len(private["contracts"]) == 13, "Expected 13 current private records")
    private_entries = {item["contractId"]: item for item in private["contracts"]}
    require(len(private_entries) == 13, "Duplicate private identity")
    require(len(set(private_ids)) == len(private_ids), "Duplicate private selection")
    private_text = inputs[root / PRIVATE].decode("utf-8")
    for contract_id in private_ids:
        require(contract_id in private_entries, f"Unknown current private identity: {contract_id}")
        entry = private_entries[contract_id]
        owner, contracts = receipt(entry["sourceDirectory"], entry["packageId"])
        snapshot = contracts[contract_id]
        require(snapshot["exposure"] == "package" and snapshot["contractVersion"] == owner["packageVersion"],
                f"Private contract must belong to its current package version: {contract_id}")
        proposed = tuple_from_snapshot(entry["packageId"], entry["sourceDirectory"], snapshot)
        for field in ("contractId", "version", "methods"):
            require(proposed[field] == entry[field], f"Private {field} change requires separate review: {contract_id}")
        for field in ("descriptorSha256", "abiSha256"):
            before, after = json.dumps(entry[field]), json.dumps(proposed[field])
            require(private_text.count(before) == 1, f"Ambiguous private digest occurrence: {contract_id}/{field}")
            private_text = private_text.replace(before, after, 1)
            entry[field] = proposed[field]
    require(parse(private_text) == private, "Private update altered unselected evidence")

    proposals = {
        root / PUBLIC: (json.dumps(proposed_public, indent=2, ensure_ascii=False) + "\n").encode("utf-8"),
        root / PRIVATE: private_text.encode("utf-8"),
    }
    changed = [path for path, value in proposals.items() if value != inputs[path]]
    for path, expected in inputs.items():
        require((path.read_bytes() if path.exists() else None) == expected, f"Evidence changed during reconciliation: {path}")
    if not write:
        require(not changed, "Current successor evidence needs explicit --write: " + ", ".join(path.name for path in changed))
    else:
        for path in changed:
            path.write_bytes(proposals[path])
    return [path.relative_to(root).as_posix() for path in changed]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", default="Debug")
    parser.add_argument("--rid", required=True)
    parser.add_argument("--private-contract", action="append", default=[])
    parser.add_argument("--write", action="store_true", help="Explicitly update current fixtures; default is read-only check")
    args = parser.parse_args()
    try:
        changed = reconcile(Path(__file__).resolve().parents[2], args.configuration, args.rid, args.private_contract, args.write)
        print(json.dumps(dict(publicContracts=19, privateContracts=13, reconciledPrivateContracts=args.private_contract,
                              written=changed if args.write else [], mode="write" if args.write else "check"), indent=2))
        return 0
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(f"Current service evidence reconciliation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
