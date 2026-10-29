#!/usr/bin/env python3
"""Offline negative controls for the independent current contract verifier."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("current_contracts", Path(__file__).with_name("verify-current-contracts.py"))
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
ROOT = Path(__file__).resolve().parents[2]


class CurrentContractsTests(unittest.TestCase):
    def setUp(self):
        self.expected, self.inventory = verifier.expected_inventory(ROOT)
        self.contract = next(c for c in self.expected["contracts"] if c["contractId"] == "younics.ai.agent.chat.provider")
        self.version = verifier.contract_version(self.contract, self.expected)
        self.descriptor = dict(descriptorVersion=1, schemaProfile="sunder.rpc.values.v1", contractId=self.contract["contractId"], version=self.version,
                               services={self.contract["service"]: dict(methods={m: dict(kind="server-stream" if m in self.contract["streams"] else "unary") for m in self.contract["methods"]})})

    def snapshot(self):
        raw = json.dumps(self.descriptor, separators=(",", ":"))
        return dict(contractId=self.contract["contractId"], contractVersion=self.version, exposure="public",
                    canonicalDescriptor=raw, descriptorBytes=len(raw.encode()), descriptorSha256=verifier.release.sha(raw.encode()), abiSha256="a" * 64)

    def test_complete_source_review_inventory_is_separate_from_observed_records(self):
        self.assertEqual(64, sum(len(p["providers"]) for p in self.expected["packages"]))
        self.assertFalse(any("path.migrator" in c["contractId"] for c in self.expected["contracts"]))
        self.assertFalse(any("descriptorSha256" in c for c in self.expected["contracts"]))
        self.assertEqual(31, len(self.expected["contracts"]))

    def test_matching_snapshot_is_a_proposal_not_an_expectation_write(self):
        before = copy.deepcopy(self.expected)
        value = verifier.contract_record(self.snapshot(), self.contract, self.expected)
        self.assertEqual(self.contract["contractId"], value["contractId"])
        self.assertEqual(before, self.expected)

    def test_coordinated_method_and_hash_replacement_cannot_rebase_expectations(self):
        methods = self.descriptor["services"][self.contract["service"]]["methods"]
        methods["unexpected"] = methods.pop("describe")
        with self.assertRaisesRegex(ValueError, "Method inventory"):
            verifier.contract_record(self.snapshot(), self.contract, self.expected)

    def test_stream_downgrade_is_rejected_even_with_recomputed_hash(self):
        self.descriptor["services"][self.contract["service"]]["methods"]["stream-chat"]["kind"] = "unary"
        with self.assertRaisesRegex(ValueError, "Stream inventory"):
            verifier.contract_record(self.snapshot(), self.contract, self.expected)

    def test_identity_exposure_version_and_hash_tampering_fail(self):
        for field, value in (("contractId", "unknown"), ("exposure", "package"), ("contractVersion", "99.0.0"),
                             ("descriptorBytes", 0), ("descriptorSha256", "0" * 64)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                verifier.contract_record(dict(self.snapshot(), **{field: value}), self.contract, self.expected)

    def test_current_required_and_app_authority_are_explicit(self):
        agent = next(p for p in self.expected["packages"] if p["key"] == "agent")
        policies = verifier.uses(agent, self.expected)
        self.assertEqual(15, sum(role == "runtime" for role, _ in policies))
        self.assertEqual(5, sum(role == "app" for role, _ in policies))
        self.assertEqual({"younics.ai.agent.behavior.loop", "younics.ai.agent.run.control", "younics.ai.agent.runtime.catalog"},
                         {cid for (role, cid), (required, _) in policies.items() if role == "runtime" and required})
        web = next(p for p in self.expected["packages"] if p["key"] == "agent-tools-web")
        self.assertEqual({("runtime", "younics.ai.agent.execution.target"): (False, ["discover"])}, verifier.uses(web, self.expected))
        maker = next(p for p in self.expected["packages"] if p["key"] == "agent-builder")
        maker_policies = verifier.uses(maker, self.expected)
        self.assertEqual(18, sum(role == "runtime" for role, _ in maker_policies))
        self.assertFalse(any(required for (role, _), (required, _) in maker_policies.items() if role == "runtime"))
        self.assertEqual({("app", "younics.sunder.package.maker.runtime"): (True, ["discover", "invoke"])},
                         {key: policy for key, policy in maker_policies.items() if key[0] == "app"})

    def test_duplicate_and_case_colliding_inputs_fail(self):
        with self.assertRaises(ValueError): verifier.index([{"id": "a"}, {"id": "a"}], "id")
        with self.assertRaises(ValueError): verifier.release.decode(b'{"contracts":[],"Contracts":[]}')

    def test_manifest_authority_actions_imports_and_caps_are_independent_expectations(self):
        package = next(p for p in self.expected["packages"] if p["key"] == "agent-tools-web")
        tool, target = "younics.ai.agent.tool.source", "younics.ai.agent.execution.target"
        contracts = {contract["contractId"]: contract for contract in self.expected["contracts"]}
        manifest = dict(id=package["packageId"], version="2.0.0",
                        contractBundles=[dict(contractId=cid, authorityId=verifier.AGENT, authorityKind="package", source="imported",
                                              exposure="public", version=verifier.contract_version(contracts[cid], self.expected), descriptorSha256="a" * 64, abiSha256="b" * 64) for cid in (tool, target)],
                        provides=[dict(providerId="web.tools", contractId=tool, role="runtime", audience="public", contractVersion=verifier.contract_version(contracts[tool], self.expected),
                                       descriptorSha256="a" * 64, abiSha256="b" * 64)],
                        usesContracts=[dict(role="runtime", contractId=target, required=False, actions=["discover"], contractVersion=verifier.contract_version(contracts[target], self.expected), abiSha256="b" * 64)],
                        dependsOn=[dict(packageId=verifier.AGENT, versionRange=">=2.0.0 <3.0.0", presence="required")],
                        providerImports=[dict(packageId=verifier.AGENT, contractId=tool)],
                        targets=[dict(role="runtime", kind="worker", requiredHostCapabilities=self.inventory["capabilities"][package["packageId"]])])
        verifier.verify_manifest(manifest, package, self.expected, self.inventory)
        changes = [
            lambda m: m["provides"][0].update(audience="owner"),
            lambda m: m["contractBundles"][0].update(authorityId="other.owner"),
            lambda m: m["usesContracts"][0].update(actions=["discover", "invoke"]),
            lambda m: m["usesContracts"][0].update(required=True),
            lambda m: m["providerImports"].clear(),
            lambda m: m["dependsOn"][0].update(presence="optional"),
            lambda m: m["targets"][0].update(role="app"),
            lambda m: m["targets"][0]["requiredHostCapabilities"].append("extra-capability"),
        ]
        for change in changes:
            bad = copy.deepcopy(manifest)
            change(bad)
            with self.assertRaises(ValueError): verifier.verify_manifest(bad, package, self.expected, self.inventory)

    def test_maker_optional_dependency_api_and_provider_presence_cannot_be_promoted_to_required(self):
        package = next(p for p in self.expected["packages"] if p["key"] == "agent-builder")
        policies = verifier.uses(package, self.expected)
        contracts = {c["contractId"]: c for c in self.expected["contracts"]}
        ids = {cid for _, cid in policies} | set(package["providers"].values())
        manifest = dict(id=package["packageId"], version="2.0.0",
                        contractBundles=[dict(contractId=cid, authorityId=contracts[cid]["packageId"], authorityKind="package",
                                              source="owned" if contracts[cid]["packageId"] == package["packageId"] else "imported",
                                              exposure=contracts[cid]["exposure"], version=verifier.contract_version(contracts[cid], self.expected),
                                              descriptorSha256="a" * 64, abiSha256="b" * 64) for cid in sorted(ids)],
                        provides=[dict(providerId=pid, contractId=cid, role="runtime", audience="owner" if cid.endswith("maker.runtime") else "public",
                                       contractVersion=verifier.contract_version(contracts[cid], self.expected), descriptorSha256="a" * 64, abiSha256="b" * 64)
                                  for pid, cid in package["providers"].items()],
                        usesContracts=[dict(role=role, contractId=cid, required=required, actions=actions,
                                            contractVersion=verifier.contract_version(contracts[cid], self.expected), abiSha256="b" * 64)
                                       for (role, cid), (required, actions) in policies.items()],
                        dependsOn=[dict(packageId=verifier.AGENT, versionRange=">=2.0.0 <3.0.0", presence="optional")],
                        packageApiImports=[dict(packageId=verifier.AGENT, presence="optional",
                                                bundleRefs=[dict(contractId=cid) for cid in sorted(ids) if contracts[cid]["exposure"] == "public"],
                                                useRefs=[dict(role="runtime", contractId=cid) for cid in sorted(ids) if contracts[cid]["exposure"] == "public"])],
                        providerImports=[dict(packageId=verifier.AGENT, contractId=cid, presence="optional") for cid in package["providers"].values() if contracts[cid]["exposure"] == "public"],
                        targets=[dict(role=role, kind="avalonia" if role == "app" else "worker",
                                      requiredHostCapabilities=self.inventory["capabilities"][package["packageId"]]) for role in ("app", "runtime")])
        verifier.verify_manifest(manifest, package, self.expected, self.inventory)
        for section in ("dependsOn", "packageApiImports", "providerImports"):
            with self.subTest(section=section):
                bad = copy.deepcopy(manifest)
                bad[section][0]["presence"] = "required"
                with self.assertRaises(ValueError): verifier.verify_manifest(bad, package, self.expected, self.inventory)

    def test_missing_family_or_stale_inventory_evidence_fails_before_artifacts(self):
        for inventory_hash in ("0" * 64, verifier.release.file_sha(ROOT / verifier.INVENTORY)):
            with tempfile.TemporaryDirectory() as directory:
                evidence = Path(directory) / "evidence.json"
                evidence.write_text(json.dumps(dict(schemaVersion=1, scope="local-unpublished-current-RID", inventorySha256=inventory_hash, packages=[])))
                with self.assertRaises(ValueError): verifier.propose(ROOT, evidence, self.expected, self.inventory)

    def test_qualification_source_paths_cannot_follow_links_or_escape(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "real").write_text("source")
            (root / "link").symlink_to(root / "real")
            for name in ("../real", "link"):
                with self.assertRaises(ValueError): verifier.release.source_file(root, name)

    def test_private_baseline_absence_requires_core_marker_and_rejects_reappearance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            relative = "sunder-contracts.owned.json"
            marker = dict(path="baseline-absent:" + relative, bytes=0, sha256=verifier.release.sha(b""))
            frame = dict(ownedBaseline=relative, ownedBaselineSha256=verifier.release.sha(b""), externalInputs=[marker])
            self.assertIsNone(verifier.release.qualified_owned_baseline(root, frame))
            for changes in (dict(externalInputs=[]), dict(ownedBaselineSha256="0" * 64), dict(externalInputs=[marker, marker])):
                with self.assertRaises(ValueError): verifier.release.qualified_owned_baseline(root, dict(frame, **changes))
            (root / relative).write_text("{}")
            with self.assertRaisesRegex(ValueError, "appeared"): verifier.release.qualified_owned_baseline(root, frame)


if __name__ == "__main__":
    unittest.main()
