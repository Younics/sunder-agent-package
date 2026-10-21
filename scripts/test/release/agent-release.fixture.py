#!/usr/bin/env python3
"""Release-boundary regressions; all files are synthetic and all HTTP/Git is mocked."""

import copy
import io
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_cohort as cohort
import agent_cohort_receipt as receipts
import agent_pipeline as pipeline
import agent_release as release
import reverse_agent_cohort as reversal


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(release.canonical(value) + b"\n")


def seed_source(source, ids):
    source.mkdir(parents=True, exist_ok=True)
    packages = [{"packageId": package_id, "artifactType": "sunderpkg",
                 "projectPath": f"src/Fixture{i}/Fixture{i}.csproj"} for i, package_id in enumerate(ids)]
    write(source / "packages.json", {"schemaVersion": 1, "releaseVersion": "2.0.0", "packages": packages})
    for package in packages:
        root = source / Path(package["projectPath"]).parent
        write(root / "sunder-contracts.lock.json", {"lockVersion": 1, "packageArtifacts": {}, "packageImports": {},
                                                   "providerImports": {}, "platformImports": {}})
        write(root / "sunder-contracts.owned.json", {"schemaVersion": 1, "authorityKind": "package",
                                                    "authorityId": package["packageId"], "packageId": package["packageId"], "contracts": {}})


def pack(path, manifest):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("manifest/sunder-package.json", release.canonical(manifest))
        archive.writestr("manifest/content-index.json", b'{"schemaVersion":1,"files":[]}')


def prepare_fixture(source, assets):
    source, assets = Path(source), Path(assets)
    if not (source / "packages.json").exists():
        ids = sorted(p.name.removesuffix(".2.0.0.sunderpkg") for p in assets.glob("*.sunderpkg"))
        seed_source(source, ids)
    for i, package in enumerate(release.inventory(source)["packages"]):
        root = source / Path(package["projectPath"]).parent
        write(root / "sunder-contracts.lock.json", {"lockVersion": 1, "packageArtifacts": {}, "packageImports": {},
                                                   "providerImports": {}, "platformImports": {}})
        contracts = {} if i else {"fixture": {"contractId": "sunder.fixture.owned", "current": {
            "state": "release-frozen", "exposure": "public", "descriptorSha256": "a" * 64,
            "packageRelease": {"packageId": package["packageId"], "packageVersion": "2.0.0"}}, "history": []}}
        write(root / "sunder-contracts.owned.json", {"schemaVersion": 1, "authorityKind": "package",
              "authorityId": package["packageId"], "packageId": package["packageId"], "contracts": contracts})
        path = assets / f"{package['packageId']}.2.0.0.sunderpkg"
        if not zipfile.is_zipfile(path):
            pack(path, {"id": package["packageId"], "version": "2.0.0", "contractBundles": []})


def complete_fixture(source, assets):
    source, assets = Path(source), Path(assets)
    path = assets / "release-provenance.json"
    value = release.load(path)
    source_sha = value["invocation"]["sha"]
    value.setdefault("sources", [{"name": "sunder-agent-package", "sha": source_sha}, {"name": "sunder-core", "sha": "2" * 40}])
    value.setdefault("materials", [{"name": "sunder-core-sdk", "tag": "sdk/v1.1.0", "sha": "2" * 40, "packageVersion": "1.1.0"}])
    value.update(artifactSourceCommit=source_sha, lockSetSha256=release.locks(source),
                 controlPlane={"repositoryId": 1234, "runId": int(value["invocation"]["runId"]),
                               "workflowPath": ".github/workflows/sunder-package-release.yml", "workflowSha": source_sha, "event": "push"},
                 publication={"registryOrigin": "https://registry.test", "ownerPrincipalId": "1" * 32},
                 artifacts=release.artifact_records(source, assets))
    write(path, value)


def fixture_release(source, assets, output, release_id, tag):
    assets = Path(assets)
    packages = sorted(release.inventory(source)["packages"], key=lambda p: p["packageId"])
    names = [p["packageId"] + ".2.0.0.sunderpkg" for p in packages]
    names += ["native-test-evidence.json", "package-output-audit.json", "SHA256SUMS", "release-provenance.json", "release-sbom.spdx.json", "toolchain.txt"]
    records = [{"id": 1001 + i, "name": name, "state": "uploaded", "digest": "sha256:" + release.file_sha(assets / name),
                "size": (assets / name).stat().st_size} for i, name in enumerate(names)]
    write(Path(output), {"id": int(release_id), "tag_name": tag, "draft": True, "assets": records})


def evidence_git(source, assets, relative):
    value = release.load(Path(source) / relative)
    records = {r["packageId"]: r for r in release.artifact_records(source, assets)}
    record = records[value["packageId"]]
    for item in value["contracts"].values():
        if not release.has_public_evidence(item):
            continue
        for snapshot in release.snapshots(item):
            if snapshot["state"] != "release-frozen" or snapshot["packageRelease"]["packageVersion"] != record["version"]:
                continue
            snapshot["state"] = "published"
            snapshot["publication"] = {
                "registryOrigin": "https://registry.test/", "packageId": record["packageId"], "packageVersion": record["version"],
                "packageContentIdentity": record["packageContentIdentity"], "publishedAtUtc": "2026-10-05T00:00:00Z",
                "etag": '"' + snapshot["descriptorSha256"] + '"'}
    print(release.encoded(value).decode())


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.assets = self.root / "assets"
        self.assets.mkdir()
        self.ids = list(release.FAMILY_PACKAGE_IDS)
        seed_source(self.source, self.ids)
        self.manifests = {p: {"id": p, "version": "2.0.0", "dependsOn": [] if i == 0 else [
            {"packageId": self.ids[0], "versionRange": ">=2.0.0 <3.0.0"}], "contractBundles": []} for i, p in enumerate(self.ids)}
        self.repack()

    def repack(self):
        for package, manifest in self.manifests.items():
            pack(self.assets / f"{package}.2.0.0.sunderpkg", manifest)

    def plan(self):
        return release.family_plan(self.source, self.assets, "https://registry.test", "1" * 32)

    def test_owner_first_and_complete_ordinary_dependency_order(self):
        self.manifests[self.ids[1]]["dependsOn"].append({"packageId": self.ids[-1], "versionRange": "=2.0.0"})
        self.repack()
        value = self.plan()
        self.assertEqual(self.ids[0], value["order"][0])
        self.assertIn("younics.sunder.package.maker", value["order"])
        self.assertLess(value["order"].index(self.ids[-1]), value["order"].index(self.ids[1]))
        self.assertTrue(all(p["initialAvailability"] == "Yanked" for p in value["publications"]))

    def test_release_family_is_explicit_and_matches_the_current_inventory(self):
        repository = Path(__file__).resolve().parents[3]
        expected = release.load(repository / "compatibility/agent-current-inventory.json")
        self.assertEqual({row["packageId"] for row in expected["packages"]}, set(release.FAMILY_PACKAGE_IDS))
        self.assertEqual({row["packageId"] for row in release.inventory(repository)["packages"]}, set(release.FAMILY_PACKAGE_IDS))
        for replacement in ("younics.ai.agent.builder", "younics.ai.agent.unknown"):
            value = release.load(self.source / "packages.json")
            value["packages"][1]["packageId"] = replacement
            write(self.source / "packages.json", value)
            with self.assertRaisesRegex(release.Invalid, "exact AI Agent runtime family"):
                release.inventory(self.source)

    def test_cycle_missing_dependency_and_unsatisfied_ordinary_range(self):
        self.manifests[self.ids[0]]["dependsOn"] = [{"packageId": self.ids[1], "versionRange": "2.0.0"}]
        self.repack()
        with self.assertRaisesRegex(release.Invalid, "cycle"):
            self.plan()
        self.manifests[self.ids[0]]["dependsOn"] = []
        for dependency in ({"packageId": "missing", "versionRange": "2.0.0"},
                           {"packageId": self.ids[0], "versionRange": ">=3.0.0"}):
            self.manifests[self.ids[1]]["dependsOn"] = [dependency]
            self.repack()
            with self.assertRaises(release.Invalid):
                self.plan()

    def test_complete_import_identity_and_origin_free_lock_remain_exact(self):
        _, public, inventory_sha = release.archive_metadata(self.assets / f"{self.ids[0]}.2.0.0.sunderpkg")
        entry = {"requestedVersionRange": ">=2.0.0 <3.0.0", "includePrerelease": False, "selectedPackageVersion": "2.0.0",
                 "selectedPackageContentIdentity": public["packageContentIdentity"], "publicContractCount": 0,
                 "publicInventorySha256": inventory_sha, "contracts": {}}
        lock = self.source / "src/Fixture1/sunder-contracts.lock.json"
        document = release.load(lock)
        document["packageArtifacts"] = {self.ids[0]: entry}
        write(lock, document)
        self.manifests[self.ids[1]]["packageArtifactImports"] = [{"packageId": self.ids[0], **{k: v for k, v in entry.items() if k != "contracts"}}]
        self.repack()
        before = lock.read_bytes()
        self.plan()
        self.assertEqual(before, lock.read_bytes())
        for field, changed in (("selectedPackageContentIdentity", "a" * 64), ("publicInventorySha256", "b" * 64),
                               ("publicContractCount", 1), ("registryOrigin", "https://other.test")):
            with self.subTest(field=field):
                bad = copy.deepcopy(document)
                bad["packageArtifacts"][self.ids[0]][field] = changed
                write(lock, bad)
                with self.assertRaises(release.Invalid):
                    self.plan()
        lock.write_bytes(before)

    def test_lock_digest_changes_for_exact_bytes_not_just_semantics(self):
        expected = release.locks(self.source)
        path = self.source / "src/Fixture0/sunder-contracts.lock.json"
        path.write_bytes(path.read_bytes() + b"\n")
        self.assertNotEqual(expected, release.locks(self.source))

    def test_consumer_roles_are_closed_and_readiness_is_coalesced_per_role(self):
        contract = {"authorityKind": "package", "resolvedContractVersion": "1.0.0"}
        bundle = {"contractId": "example.shared", "contractVersion": "1.0.0"}
        lock = {"packageArtifacts": {key: {"contracts": {"example.shared": contract}} for key in ("example.owner", "example.reexport")},
                "packageImports": {
                    "example.owner": {"localName": "owner", "presence": "required", "requiredContracts": ["example.shared"], "consumerRoles": ["app"]},
                    "example.reexport": {"localName": "reexport", "presence": "optional", "requiredContracts": [], "consumerRoles": ["runtime"]}}}
        manifest = {"targets": [{"role": role} for role in ("app", "runtime")],
                    "contractBundles": [{"contractId": "example.shared", "version": "1.0.0", "source": "imported"}],
                    "packageApiImports": [{"packageId": key, "presence": entry["presence"], "bundleRefs": [bundle],
                                           "useRefs": [{"role": entry["consumerRoles"][0], **bundle}]} for key, entry in lock["packageImports"].items()],
                    "usesContracts": [{"role": role, **bundle, "required": role == "app", "actions": ["discover", "invoke"]}
                                      for role in ("app", "runtime")]}
        original = release.canonical(lock)
        release.verify_consumer_projection(lock, manifest)
        unavailable = copy.deepcopy(manifest)
        unavailable["targets"] = [{"role": "runtime"}]
        with self.assertRaisesRegex(release.Invalid, "consumer-roles-unavailable"):
            release.verify_consumer_projection(lock, unavailable)
        both = copy.deepcopy(lock)
        both["packageImports"]["example.owner"]["consumerRoles"] = ["app", "runtime"]
        shared = copy.deepcopy(manifest)
        shared["packageApiImports"][0]["useRefs"].append({"role": "runtime", **bundle})
        shared["usesContracts"][1]["required"] = True
        release.verify_consumer_projection(both, shared)
        # Adding a present App does not grant it through a Runtime-only reexport.
        bad = copy.deepcopy(manifest)
        bad["packageApiImports"][1]["useRefs"].insert(0, {"role": "app", **bundle})
        with self.assertRaisesRegex(release.Invalid, "role/bundle"):
            release.verify_consumer_projection(lock, bad)
        bad = copy.deepcopy(manifest)
        bad["usesContracts"][1]["required"] = True
        with self.assertRaisesRegex(release.Invalid, "role-scoped"):
            release.verify_consumer_projection(lock, bad)
        for roles in (None, [], ["Runtime"], ["runtime", "app"], ["runtime", "runtime"], ["other"], "runtime"):
            bad = copy.deepcopy(lock)
            bad["packageImports"]["example.owner"]["consumerRoles"] = roles
            with self.subTest(roles=roles), self.assertRaisesRegex(release.Invalid, "consumer-roles-invalid"):
                release.package_imports(bad)
        bad = copy.deepcopy(lock)
        del bad["packageImports"]["example.owner"]["consumerRoles"]
        with self.assertRaisesRegex(release.Invalid, "consumer-roles-migration-required"):
            release.package_imports(bad)
        bad = copy.deepcopy(lock)
        bad["packageImports"]["example.owner"]["unknown"] = True
        with self.assertRaises(release.Invalid):
            release.package_imports(bad)
        for roles in (["app"], ["runtime"], ["app", "runtime"]):
            bad = copy.deepcopy(lock)
            bad["packageImports"]["example.owner"]["consumerRoles"] = roles
            release.package_imports(bad)
        self.assertEqual(original, release.canonical(lock))

    def typed_discovery(self):
        owner, consumer = "younics.ai.agent", "younics.ai.agent.tools.web"
        target, tools = owner + ".execution.target", owner + ".tool.source"
        contracts = {key: dict(authorityKind="package", authorityId=owner, selectedAuthorityRelease="2.0.0",
                               selectedAuthorityContentIdentity="a" * 64, resolvedContractVersion="1.0.0",
                               descriptorSha256=character * 64, abiSha256="d" * 64)
                     for key, character in ((target, "b"), (tools, "c"))}
        lock = dict(packageImports={}, providerImports={"tools": dict(packageId=owner, contractId=tools)},
                    packageArtifacts={owner: dict(contracts=contracts)})
        bundles = [dict(contractId=key, version=record["resolvedContractVersion"], source="imported", exposure="public",
                        **{name: record[name] for name in ("authorityKind", "authorityId", "selectedAuthorityRelease",
                                                          "selectedAuthorityContentIdentity", "descriptorSha256", "abiSha256")})
                   for key, record in contracts.items()]
        provider = dict(role="runtime", providerId="web.tools", contractId=tools, contractVersion="1.0.0")
        use = dict(role="runtime", contractId=target, contractVersion="1.0.0", abiSha256="d" * 64,
                   required=False, actions=["discover"])
        manifest = dict(id=consumer, version="2.0.0", targets=[dict(role="runtime"), dict(role="app")],
                        contractBundles=bundles, packageApiImports=[], usesContracts=[use], provides=[provider],
                        providerImports=[dict(key="tools", packageId=owner, contractId=tools, contractVersion="1.0.0",
                                              bundleRef=dict(contractId=tools, contractVersion="1.0.0"), providerRefs=[provider])])
        role_uses = [{**use, "descriptorSha256": "b" * 64, "serviceId": "execution-target",
                      "serviceType": "Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService"}]
        return lock, manifest, role_uses

    def test_provider_only_optional_typed_discovery_requires_exact_source_intent(self):
        lock, manifest, role_uses = self.typed_discovery()
        before = release.canonical([lock, manifest, role_uses])
        release.verify_consumer_projection(lock, manifest, role_uses=role_uses)
        self.assertEqual({}, lock["packageImports"])
        self.assertEqual([], manifest["packageApiImports"])
        self.assertEqual(before, release.canonical([lock, manifest, role_uses]))
        with self.assertRaisesRegex(release.Invalid, "reviewed intent"):
            release.verify_consumer_projection(lock, manifest)
        manifest["usesContracts"] = []
        with self.assertRaisesRegex(release.Invalid, "reviewed intent"):
            release.verify_consumer_projection(lock, manifest, role_uses=role_uses)

    def test_typed_discovery_rejects_orphan_overbroad_and_unreviewed_uses(self):
        lock, manifest, role_uses = self.typed_discovery()
        mutations = {
            "invocation": lambda l, m, u: m["usesContracts"][0].update(actions=["discover", "invoke"]),
            "subscription": lambda l, m, u: m["usesContracts"][0].update(actions=["discover", "subscribe"]),
            "required": lambda l, m, u: m["usesContracts"][0].update(required=True),
            "app": lambda l, m, u: m["usesContracts"][0].update(role="app"),
            "version": lambda l, m, u: m["usesContracts"][0].update(contractVersion="2.0.0"),
            "abi": lambda l, m, u: m["usesContracts"][0].update(abiSha256="e" * 64),
            "duplicate": lambda l, m, u: m["usesContracts"].append(copy.deepcopy(m["usesContracts"][0])),
            "orphan bundle": lambda l, m, u: m["contractBundles"].pop(0),
            "unreviewed provider": lambda l, m, u: l["providerImports"].clear(),
            "absent provider": lambda l, m, u: m["provides"].clear(),
            "unselected contract": lambda l, m, u: l["packageArtifacts"]["younics.ai.agent"]["contracts"].pop(u[0]["contractId"]),
            "wrong authority": lambda l, m, u: m["contractBundles"][0].update(authorityId="example.foreign"),
            "platform": lambda l, m, u: m["contractBundles"][0].update(authorityKind="platform"),
            "source app grant": lambda l, m, u: u[0].update(role="app"),
            "source descriptor": lambda l, m, u: u[0].update(descriptorSha256="e" * 64),
            "unreviewed extra use": lambda l, m, u: m["usesContracts"].append(
                {**m["usesContracts"][0], "contractId": "younics.ai.agent.tool.source"}),
        }
        for name, mutate in mutations.items():
            changed = copy.deepcopy([lock, manifest, role_uses])
            mutate(*changed)
            with self.subTest(case=name), self.assertRaises(release.Invalid):
                release.verify_consumer_projection(changed[0], changed[1], role_uses=changed[2])

    def test_source_intent_cannot_override_package_consumer_readiness_or_group_roles(self):
        lock, manifest, role_uses = self.typed_discovery()
        owner = "younics.ai.agent"
        target = role_uses[0]["contractId"]
        lock["packageImports"][owner] = dict(localName="agent", presence="required", requiredContracts=[target], consumerRoles=["runtime"])
        refs = [dict(contractId=key, contractVersion="1.0.0") for key in sorted(lock["packageArtifacts"][owner]["contracts"])]
        manifest["packageApiImports"] = [dict(packageId=owner, presence="required", bundleRefs=refs,
                                              useRefs=[dict(role="runtime", **ref) for ref in refs])]
        manifest["usesContracts"].append(dict(role="runtime", **refs[1], abiSha256="d" * 64, required=False, actions=["discover"]))
        with self.assertRaisesRegex(release.Invalid, "reviewed intent"):
            release.verify_consumer_projection(lock, manifest, role_uses=role_uses)
        # An explicit optional typed use cannot weaken required-wins readiness.
        manifest["usesContracts"][0]["required"] = True
        with self.assertRaises(release.Invalid):
            release.verify_consumer_projection(lock, manifest, role_uses=role_uses)
        manifest["packageApiImports"][0]["useRefs"].append(dict(role="app", **refs[0]))
        with self.assertRaisesRegex(release.Invalid, "role/bundle"):
            release.verify_consumer_projection(lock, manifest, role_uses=role_uses)

    def test_source_role_uses_are_projected_only_from_the_bound_worker_receipt(self):
        _, manifest, role_uses = self.typed_discovery()
        frame = dict(packageId=manifest["id"], packageVersion=manifest["version"], contractLockSha256="e" * 64)
        receipt = dict(schemaVersion=1, producer=dict(name="Sunder.Package.Build", language="csharp", version="1.1.0"),
                       packageId=frame["packageId"], packageVersion=frame["packageVersion"], lockSha256=frame["contractLockSha256"],
                       inputFingerprint="f" * 64, roleUses=role_uses)
        def qualification(value):
            return dict(inputs=frame, rolePlan=dict(inputFingerprint="f" * 64,
                        roles=[dict(role="worker", receipt=release.encoded(value).decode())]))
        self.assertEqual(dict(lockSha256="e" * 64, roleUses=role_uses), release.source_role_use_evidence(qualification(receipt)))
        for field, value in (("role", "app"), ("packageId", "example.foreign"), ("packageVersion", "2.1.0"),
                             ("lockSha256", "b" * 64), ("inputFingerprint", "a" * 64), ("schemaVersion", True)):
            with self.subTest(field=field), self.assertRaises(release.Invalid):
                release.source_role_use_evidence(qualification({**receipt, field: value}))

    def test_retained_source_use_evidence_binds_final_archives_current_locks_and_all_rids(self):
        from native_release_inputs import RIDS
        records = release.artifact_records(self.source, self.assets)
        packages = [dict(packageId=row["packageId"], sourceRoleUses=dict(
            lockSha256=release.file_sha(self.source / Path(row["projectPath"]).parent / "sunder-contracts.lock.json"), roleUses=[]))
                    for row in release.inventory(self.source)["packages"]]
        evidence = dict(schemaVersion=3, finalArtifacts=[{k: row[k] for k in ("path", "size", "sha256")} for row in records],
                        nativeRuns=[dict(rid=rid, packages=copy.deepcopy(packages)) for rid in RIDS])
        path = self.assets / "native-test-evidence.json"
        write(path, evidence)
        self.assertEqual(self.ids[0], self.plan()["order"][0])
        mutations = [lambda v: v["nativeRuns"].pop(),
                     lambda v: v["nativeRuns"][0]["packages"].pop(),
                     lambda v: v["nativeRuns"][0]["packages"][0]["sourceRoleUses"].update(lockSha256="a" * 64),
                     lambda v: [run["packages"][0]["sourceRoleUses"].update(lockSha256="a" * 64) for run in v["nativeRuns"]],
                     lambda v: v["finalArtifacts"][0].update(sha256="a" * 64)]
        for mutate in mutations:
            changed = copy.deepcopy(evidence)
            mutate(changed)
            write(path, changed)
            with self.assertRaises(release.Invalid):
                self.plan()

    def test_duplicate_fields_symlinks_and_prerelease_ranges_fail(self):
        with self.assertRaises(release.Invalid):
            release.decode(b'{"a":1,"A":2}')
        self.assertFalse(release.satisfies("2.0.0-rc.1", ">=2.0.0 <3.0.0"))
        self.assertTrue(release.satisfies("2.0.0+build", ">=2.0.0 <3.0.0"))
        self.assertTrue(release.satisfies("2.0.0+build", ">=2.0.0  <3.0.0"))
        with self.assertRaises(release.Invalid):
            release.satisfies("2.0.0", ">=3.0.0 invalid")
        path = self.source / "src/Fixture0/sunder-contracts.lock.json"
        target = self.root / "lock.json"
        path.rename(target)
        path.symlink_to(target)
        with self.assertRaises(release.Invalid):
            release.locks(self.source)

    def test_core_authoring_origins_preserve_the_trailing_slash(self):
        self.assertEqual("https://hub.sunderapp.io/", release.authoring_origin("https://hub.sunderapp.io/"))
        self.assertEqual("https://hub.sunderapp.io", release.origin("https://hub.sunderapp.io/"))
        for invalid in ("https://127.0.0.1/", "https://hub.sunderapp.io:443/", "https://hub.sunderapp.io:0444/",
                        "https://hub.sunderapp.io:65536/", "https://hub.sunderapp.io./", "https://xn--abc.test/", "https://UPPER.test/"):
            with self.subTest(origin=invalid), self.assertRaises(release.Invalid):
                release.authoring_origin(invalid)
        with self.assertRaises(release.Invalid):
            release.authoring_origin("https://hub.sunderapp.io")
        self.assertEqual(123456, release.timestamp("2026-10-05T01:02:03.1234567+00:00").microsecond)
        self.assertEqual(100000, release.timestamp("2026-10-05T01:02:03.1Z").microsecond)

    def provenance(self):
        value = {"schemaVersion": 1, "buildType": "https://sunderapp.io/provenance/github-actions/v1", "invocation": {
            "repository": "Younics/sunder-agent-package", "workflow": "AI Agent Family Release",
            "workflowRef": "Younics/sunder-agent-package/.github/workflows/sunder-package-release.yml@refs/tags/agent/v2.0.0",
            "runId": "8001", "runAttempt": "1", "releaseId": 424242, "ref": "refs/tags/agent/v2.0.0", "refName": "agent/v2.0.0", "sha": "1" * 40}}
        write(self.assets / "release-provenance.json", value)
        complete_fixture(self.source, self.assets)
        return release.validate_provenance(self.source, self.assets)

    def test_closed_provenance_binds_lock_bytes_primary_identity_and_control_plane(self):
        valid = self.provenance()
        mutations = [lambda v: v.update(extra=True), lambda v: v.pop("lockSetSha256"),
                     lambda v: v.update(lockSetSha256="0" * 64), lambda v: v.update(schemaVersion=True),
                     lambda v: v["controlPlane"].update(workflowSha="9" * 40),
                     lambda v: v["controlPlane"].update(repositoryId=0),
                     lambda v: v["artifacts"][0].update(packageContentIdentity="f" * 64),
                     lambda v: v["artifacts"][0].update(size=float(v["artifacts"][0]["size"])),
                     lambda v: v["artifacts"].append({"path": "native-test-evidence.json"})]
        for mutate in mutations:
            bad = copy.deepcopy(valid)
            mutate(bad)
            write(self.assets / "release-provenance.json", bad)
            with self.assertRaises(release.Invalid):
                release.validate_provenance(self.source, self.assets)
        (self.assets / "release-provenance.json").write_bytes(release.encoded(valid))
        with self.assertRaisesRegex(release.Invalid, "Noncanonical"):
            release.validate_provenance(self.source, self.assets)

    def test_evidence_child_preserves_source_locks_descriptor_history_and_modes(self):
        provenance = self.provenance()
        relative = "src/Fixture0/sunder-contracts.owned.json"
        before = release.load(self.source / relative)
        before["contracts"] = {"owned": {"contractId": "sunder.fixture.owned", "current": {
            "state": "release-frozen", "exposure": "public", "descriptorSha256": "a" * 64,
            "packageRelease": {"packageId": self.ids[0], "packageVersion": "2.0.0"}}, "history": []}}
        before["contracts"]["historyOnly"] = {"contractId": "sunder.fixture.history", "history": [{
            "state": "published", "exposure": "public", "packageRelease": {"packageVersion": "1.0.0"}}]}
        before["contracts"]["privateHistory"] = {"contractId": "sunder.fixture.private", "history": [{
            "state": "release-frozen", "exposure": "package", "contractVersion": "0.1.0", "packageRelease": {"packageVersion": "2.0.0"}}]}
        before["retiredPrivateContracts"] = {"private": {"contractId": "sunder.fixture.retired", "current": {
            "state": "draft", "exposure": "package", "contractVersion": "0.2.0+Old.Build"}, "history": []}}
        (self.source / "src/Fixture14/sunder-contracts.owned.json").unlink()
        write(self.source / relative, before)
        after = copy.deepcopy(before)
        after["contracts"]["owned"]["current"].update(state="published", publication={
            "registryOrigin": "https://registry.test/", "packageId": self.ids[0], "packageVersion": "2.0.0",
            "packageContentIdentity": provenance["artifacts"][0]["packageContentIdentity"],
            "publishedAtUtc": "2026-10-05T00:00:00Z", "etag": '"' + "a" * 64 + '"'})
        parents = "6" * 40 + " " + "1" * 40
        diff = ":100644 100644 aaa bbb M\t" + relative
        def git(_source, *args):
            if args[0] == "rev-list":
                return parents.encode()
            if args[0] == "diff-tree":
                return diff.encode()
            if args[0] == "ls-tree":
                return b"blob 100644\n" if (self.source / args[-1]).exists() else b""
            commit, path = args[1].split(":", 1)
            if commit == "6" * 40 and path == relative:
                return release.encoded(after)
            return (self.source / path).read_bytes()
        with patch.object(release, "git", side_effect=git):
            release.verify_evidence_commit(self.source, "6" * 40, provenance)
            with patch.object(sys, "argv", ["agent_release.py", "publication-time", str(self.source), "6" * 40, self.ids[14]]), \
                    patch("sys.stdout", new_callable=io.StringIO) as output:
                release.main()
                self.assertEqual("\n", output.getvalue())
            original = (self.source / relative).read_bytes()
            for invalid in ("6" * 40 + " " + "2" * 40, parents + " " + "3" * 40):
                parents = invalid
                with self.assertRaises(release.Invalid):
                    release.verify_evidence_commit(self.source, "6" * 40, provenance)
            parents = "6" * 40 + " " + "1" * 40
            for invalid in (":100644 100755 aaa bbb M\t" + relative, ":100644 100644 aaa bbb M\tsrc/Source.cs"):
                diff = invalid
                with self.assertRaises(release.Invalid):
                    release.verify_evidence_commit(self.source, "6" * 40, provenance)
            diff = ":100644 100644 aaa bbb M\t" + relative
            valid = copy.deepcopy(after)
            for mutate in (
                    lambda v: v["retiredPrivateContracts"]["private"]["current"].update(state="published"),
                    lambda v: v.pop("retiredPrivateContracts"),
                    lambda v: v["contracts"]["privateHistory"]["history"][0].update(state="published"),
                    lambda v: v["contracts"]["historyOnly"].update(current=v["contracts"]["owned"]["current"])):
                after = copy.deepcopy(valid)
                mutate(after)
                with self.assertRaises(release.Invalid):
                    release.verify_evidence_commit(self.source, "6" * 40, provenance)
            after = copy.deepcopy(valid)
            after["contracts"]["owned"]["current"]["exposure"] = "package"
            with self.assertRaises(release.Invalid):
                release.verify_evidence_commit(self.source, "6" * 40, provenance)
            self.assertEqual(original, (self.source / relative).read_bytes())


class CohortTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.identities = [{"packageId": package_id, "version": "2.0.0", "packageContentIdentity": f"{i:064x}"}
                           for i, package_id in enumerate(sorted(release.FAMILY_PACKAGE_IDS, key=lambda value: value + "/2.0.0"))]
        self.request = {"idempotencyKey": "fixture-1", "expectedPackagePolicyRevision": 42,
                        "versions": [{"package": p, "expectedAvailability": "Yanked", "availability": "Listed"} for p in self.identities],
                        "distTags": [{"packageId": p["packageId"], "tag": "latest", "target": p}
                                     for p in self.identities]}
        self.result = {"idempotencyKey": "fixture-1", "requestSha256": cohort.request_hash(self.request),
                       "packagePolicy": {"revision": 43, "fingerprint": "a" * 64},
                       "committedAtUtc": "2026-10-05T00:00:00Z", "versions": self.request["versions"],
                       "previousTags": [{"packageId": p["packageId"], "tags": []}
                                        for p in sorted(self.identities, key=lambda value: value["packageId"])],
                       "resultingTags": [{"packageId": t["packageId"], "tags": [t]}
                                         for t in sorted(self.request["distTags"], key=lambda value: value["packageId"])]}
        self.req = self.root / "request.json"
        self.res = self.root / "result.json"
        write(self.req, self.request)

    def policy_response(self, synchronization, availability="Listed"):
        return {"nonce": synchronization["nonce"], "registryOrigin": "https://registry.test",
                "packagePolicy": copy.deepcopy(self.result["packagePolicy"]),
                "observedAtUtc": "2026-10-05T00:00:00.1234567Z", "validUntilUtc": "2026-10-06T00:00:00.1234567+00:00",
                "packages": [{"package": p, "packageState": "Active", "versionAvailability": availability,
                              "disposition": "Allowed", "errorCode": None, "publicReason": None,
                              "platformDependencies": synchronization["platformCatalogs"]} for p in synchronization["packages"]],
                "platformCatalogs": [{"catalog": c, "lifecycleState": "Supported", "disposition": "Allowed",
                                      "errorCode": None, "revokedAtUtc": None} for c in synchronization["platformCatalogs"]]}

    def test_request_hash_matches_actual_core_dto_serialization(self):
        # Captured from the compiled Sunder.Registry.Contracts DTO with Registry's
        # JsonSerializer options; protects property order, domain and '+' escaping.
        # Preserve the authenticated DTO input together with its captured hash.
        package = {"packageId": "sunder.package.agent", "version": "2.0.0+test", "packageContentIdentity": "a" * 64}
        value = {"idempotencyKey": "fixture", "expectedPackagePolicyRevision": 42,
                 "versions": [{"package": package, "expectedAvailability": "Yanked", "availability": "Listed"}],
                 "distTags": [{"packageId": package["packageId"], "tag": "latest", "target": package}]}
        self.assertEqual("275a0573c2c566cfcf3683d5f43c25ed3b8f34e03026e73b6bf0e6a987059d8b", cohort.request_hash(value))

    def test_parent_package_uses_registry_slash_key_order(self):
        parent = {"packageId": "younics.ai.agent", "version": "2.0.0", "packageContentIdentity": "a" * 64}
        child = {**parent, "packageId": "younics.ai.agent.tools"}
        value = {"idempotencyKey": "order", "expectedPackagePolicyRevision": 1,
                 "versions": [{"package": p, "expectedAvailability": "Yanked", "availability": "Listed"} for p in (parent, child)], "distTags": []}
        with self.assertRaises(release.Invalid):
            cohort.request(value)
        value["versions"].reverse()
        cohort.request(value)

    def test_result_requires_complete_snapshots_revision_and_exact_request(self):
        cohort.result(self.result, self.request)
        mutations = [lambda v: v.update(requestSha256="0" * 64), lambda v: v["packagePolicy"].update(revision=44),
                     lambda v: v["resultingTags"].pop(), lambda v: v["resultingTags"][0]["tags"].clear(),
                     lambda v: v["previousTags"][0]["tags"].append({"packageId": self.identities[0]["packageId"],
                                                               "tag": "preview", "target": self.identities[0]}),
                     lambda v: v.update(extra=True), lambda v: v["packagePolicy"].update(fingerprint="invalid"),
                     lambda v: v.update(checkpoint=None), lambda v: v.update(committedAtUtc="invalid")]
        for mutate in mutations:
            bad = copy.deepcopy(self.result)
            mutate(bad)
            with self.assertRaises(release.Invalid):
                cohort.result(bad, self.request)

    def test_lost_response_retries_byte_identical_request_and_retains_result(self):
        sent = []
        def http(_registry, route, payload, token):
            self.assertEqual("/api/v1/me/package-cohorts/availability", route)
            sent.append(payload)
            return (0, b"", 28) if len(sent) == 1 else (200, release.encoded(self.result), 0)
        with patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                patch.object(cohort, "current_tags", return_value=self.result["previousTags"]), \
                patch.object(cohort, "guard"), patch.object(cohort, "verify_current"), patch.object(cohort, "http", side_effect=http):
            cohort.apply("https://registry.test", self.req, self.res)
            cohort.apply("https://registry.test", self.req, self.res)
        self.assertEqual(2, len(sent))
        self.assertEqual(sent[0], sent[1])
        self.assertEqual(self.result, release.load(self.res))

    def test_unavailable_stale_and_conflicting_success_never_fallback(self):
        for response, attempts in [((503, b'{"code":"registry.v1.internal"}', 0), 2),
                                   ((409, b'{"code":"package.policy-stale"}', 0), 1),
                                   ((200, release.encoded({**self.result, "requestSha256": "f" * 64}), 0), 1)]:
            with patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                    patch.object(cohort, "current_tags", return_value=self.result["previousTags"]), \
                    patch.object(cohort, "guard"), patch.object(cohort, "http", return_value=response) as send:
                with self.assertRaises(release.Invalid):
                    cohort.apply("https://registry.test", self.req, self.res)
                self.assertEqual(attempts, send.call_count)
                self.assertTrue(all(call.args[2] == cohort.request_bytes(self.request) for call in send.call_args_list))
                self.assertEqual(self.request, release.load(self.req))
                self.assertFalse(self.res.exists())

    def test_request_tamper_during_guard_stops_before_post(self):
        def tamper():
            write(self.req, {**self.request, "expectedPackagePolicyRevision": 43})
        with patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                patch.object(cohort, "current_tags", return_value=self.result["previousTags"]), \
                patch.object(cohort, "guard", side_effect=tamper), patch.object(cohort, "http") as send:
            with self.assertRaises(release.Invalid):
                cohort.apply("https://registry.test", self.req, self.res)
            send.assert_not_called()

    def test_forward_downgrade_stable_and_numeric_preview_stops_before_mutation(self):
        for tag, target, newer in (("latest", "2.0.0", "2.1.0"), ("preview", "2.0.0-beta.2", "2.0.0-beta.10")):
            proposed = copy.deepcopy(self.request)
            for change in proposed["versions"]:
                change["package"]["version"] = target
            for change in proposed["distTags"]:
                change["tag"] = tag
                change["target"]["version"] = target
            write(self.req, proposed)
            observed = copy.deepcopy(self.result["previousTags"])
            prior = {**proposed["distTags"][0], "target": {**proposed["distTags"][0]["target"], "version": newer}}
            next(item for item in observed if item["packageId"] == prior["packageId"])["tags"] = [prior]
            with self.subTest(tag=tag), patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                    patch.object(cohort, "current_tags", return_value=observed), \
                    patch.object(cohort, "guard") as guard, patch.object(cohort, "http") as send:
                with self.assertRaisesRegex(release.Invalid, "cannot regress"):
                    cohort.apply("https://registry.test", self.req, self.res)
                guard.assert_not_called()
                send.assert_not_called()

    def test_concurrent_policy_change_during_guard_keeps_original_fence_and_never_compensates(self):
        concurrent = []
        def guard():
            concurrent.append({"version": "2.1.0", "revision": 43})
        def http(_registry, route, payload, _token):
            self.assertEqual([{ "version": "2.1.0", "revision": 43}], concurrent)
            self.assertEqual("/api/v1/me/package-cohorts/availability", route)
            self.assertEqual(cohort.request_bytes(self.request), payload)
            return 409, b'{"code":"package.policy-stale"}', 0
        with patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                patch.object(cohort, "current_tags", return_value=self.result["previousTags"]), \
                patch.object(cohort, "guard", side_effect=guard), patch.object(cohort, "http", side_effect=http) as send:
            with self.assertRaises(release.Invalid):
                cohort.apply("https://registry.test", self.req, self.res)
            self.assertEqual(1, send.call_count)
            self.assertEqual(self.request, release.load(self.req))
            self.assertFalse(self.res.exists())

    def test_guard_failure_stops_before_post_and_guard_receives_no_registry_credentials(self):
        credentials = {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43,
                       "SUNDER_REGISTRY_PUBLISH_TOKEN": "publish-fixture",
                       "SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON": '["fixture-guard"]'}
        def reject(command, *, env, check):
            self.assertEqual(["fixture-guard"], command)
            self.assertNotIn("SUNDER_REGISTRY_CLI_TOKEN", env)
            self.assertNotIn("SUNDER_REGISTRY_PUBLISH_TOKEN", env)
            self.assertTrue(check)
            raise subprocess.CalledProcessError(1, command)
        with patch.dict(os.environ, credentials), patch.object(cohort, "current_tags", return_value=self.result["previousTags"]), \
                patch.object(cohort.subprocess, "run", side_effect=reject), patch.object(cohort, "http") as send:
            with self.assertRaises(subprocess.CalledProcessError):
                cohort.apply("https://registry.test", self.req, self.res)
            send.assert_not_called()

    def test_missing_or_malformed_owner_credential_never_falls_back_to_publish_token(self):
        for token in ("", "sunder_cli_invalid"):
            with self.subTest(token=token), patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": token,
                    "SUNDER_REGISTRY_PUBLISH_TOKEN": "publish-fixture"}), patch.object(cohort, "http") as send:
                with self.assertRaisesRegex(release.Invalid, "owner credential"):
                    cohort.apply("https://registry.test", self.req, self.res)
                send.assert_not_called()

    def test_post_commit_verification_failure_retains_result_without_compensation(self):
        with patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                patch.object(cohort, "current_tags", return_value=self.result["previousTags"]), patch.object(cohort, "guard"), \
                patch.object(cohort, "http", return_value=(200, release.encoded(self.result), 0)) as send, \
                patch.object(cohort, "verify_current", side_effect=release.Invalid("concurrent map drift")):
            for _ in range(2):
                with self.assertRaisesRegex(release.Invalid, "map drift"):
                    cohort.apply("https://registry.test", self.req, self.res)
            self.assertEqual(1, send.call_count)
            self.assertEqual(self.result, release.load(self.res))

    def test_read_only_verification_rejects_partial_family_and_never_sends_credentials(self):
        def get(_registry, route):
            package = next(p for p in self.identities if "/" + p["packageId"] + "/" in route)
            return {"publication": {"package": package}, "isYanked": False}
        with patch.object(cohort, "current_tags", return_value=self.result["resultingTags"]) as tags, \
                patch.object(cohort, "get", side_effect=get), patch.object(cohort, "http") as send:
            cohort.verify_current("https://registry.test", self.request, self.result)
            partial = copy.deepcopy(self.result["resultingTags"])
            partial[0]["tags"] = []
            tags.return_value = partial
            with self.assertRaisesRegex(release.Invalid, "tag map drifted"):
                cohort.verify_current("https://registry.test", self.request, self.result)
            send.assert_not_called()

    def test_announcement_confirms_retained_result_only_after_fresh_policy_floor(self):
        write(self.res, self.result)
        authority = {"authorityKind": "platform", "authorityId": "sunder.core", "selectedAuthorityRelease": "1.1.0",
                     "selectedAuthorityContentIdentity": "f" * 64}
        content = {p["packageId"]: p["packageContentIdentity"] for p in self.identities}
        def metadata(path):
            return ({"platformImports": [authority]}, {"packageContentIdentity": content[path.name.removesuffix(".2.0.0.sunderpkg")]}, "unused")
        sent = []
        mutate = lambda value: None
        def http(registry, route, payload=None, token=None):
            sent.append(route)
            if route == "/api/v1/policy/synchronize":
                self.assertIsNone(token)
                sync = release.decode(payload)
                self.assertEqual({"nonce", "packages", "platformCatalogs", "minimumPackagePolicy"}, set(sync))
                self.assertRegex(sync["nonce"], r"^[0-9a-f]{32}$")
                self.assertEqual(self.result["packagePolicy"], sync["minimumPackagePolicy"])
                self.assertEqual([{"authorityId": "sunder.core", "releaseVersion": "1.1.0", "catalogSha256": "f" * 64}], sync["platformCatalogs"])
                observed = copy.deepcopy(self.policy_response(sync))
                mutate(observed)
                return 200, release.encoded(observed), 0
            self.assertEqual("/api/v1/me/package-cohorts/availability", route)
            self.assertEqual(cohort.request_bytes(self.request), payload)
            self.assertEqual("sunder_cli_" + "a" * 43, token)
            return 200, release.encoded(self.result), 0
        with patch.dict(os.environ, {"SUNDER_REGISTRY_CLI_TOKEN": "sunder_cli_" + "a" * 43}), \
                patch.object(cohort, "archive_metadata", side_effect=metadata), patch.object(cohort, "verify_current"), \
                patch.object(cohort, "http", side_effect=http):
            for mutate in (lambda v: None, lambda v: v["packagePolicy"].update(revision=44, fingerprint="b" * 64)):
                sent.clear()
                cohort.confirm("https://registry.test", self.req, self.res, self.root)
                self.assertEqual(["/api/v1/policy/synchronize", "/api/v1/me/package-cohorts/availability"], sent)
            mutations = {
                "older revision": lambda v: v["packagePolicy"].update(revision=42),
                "same revision conflict": lambda v: v["packagePolicy"].update(fingerprint="b" * 64),
                "malformed newer fingerprint": lambda v: v["packagePolicy"].update(revision=44, fingerprint="invalid"),
                "nonce": lambda v: v.update(nonce="0" * 32),
                "origin": lambda v: v.update(registryOrigin="https://other.test"),
                "missing package": lambda v: v["packages"].pop(),
                "package identity": lambda v: v["packages"][0]["package"].update(packageContentIdentity="f" * 64),
                "package block": lambda v: v["packages"][0].update(packageState="SecurityBlocked"),
                "package error": lambda v: v["packages"][0].update(errorCode="package.security-blocked"),
                "missing platform": lambda v: v["platformCatalogs"].clear(),
                "revoked platform": lambda v: v["platformCatalogs"][0].update(lifecycleState="Revoked"),
                "platform error": lambda v: v["platformCatalogs"][0].update(errorCode="contract-authority.revoked"),
                "missing time": lambda v: v.pop("observedAtUtc"),
                "timezone": lambda v: v.update(observedAtUtc="2026-10-05T01:00:00.1234567+01:00"),
                "lease interval": lambda v: v.update(validUntilUtc="2026-10-06T00:00:01.1234567Z"),
                "sub-microsecond interval": lambda v: v.update(validUntilUtc="2026-10-06T00:00:00.1234568Z"),
                "removed checkpoint": lambda v: v.update(checkpoint=None),
            }
            for name, mutate in mutations.items():
                sent.clear()
                with self.subTest(case=name), self.assertRaises(release.Invalid):
                    cohort.confirm("https://registry.test", self.req, self.res, self.root)
                self.assertEqual(["/api/v1/policy/synchronize"], sent)

    def test_pipeline_retains_current_policy_request_before_cohort_mutation(self):
        provenance = {"invocation": {"releaseId": 424242}, "publication": {"registryOrigin": "https://registry.test"},
                      "artifactSourceCommit": "1" * 40, "artifacts": self.identities}
        store = Mock(tag="agent/v2.0.0")
        retained = {}
        def http(registry, route, payload, token=None):
            self.assertEqual("https://registry.test", registry)
            self.assertEqual("/api/v1/policy/synchronize", route)
            self.assertIsNone(token)
            synchronization = release.decode(payload)
            self.assertEqual({"nonce", "packages", "platformCatalogs", "minimumPackagePolicy"}, set(synchronization))
            self.assertIsNone(synchronization["minimumPackagePolicy"])
            self.assertEqual([], synchronization["platformCatalogs"])
            self.assertEqual(sorted(self.identities, key=lambda p: (p["packageId"], p["version"], p["packageContentIdentity"])), synchronization["packages"])
            return 200, release.encoded(self.policy_response(synchronization, "Yanked")), 0
        def apply(registry, request_path, result_path):
            store.upload.assert_called_once_with(424242, [request_path])
            proposed = release.load(request_path)
            self.assertEqual(pipeline.cohort_request(provenance, 43), proposed)
            committed = {**self.result, "idempotencyKey": proposed["idempotencyKey"], "requestSha256": cohort.request_hash(proposed),
                         "packagePolicy": {"revision": 44, "fingerprint": "b" * 64}, "versions": proposed["versions"]}
            cohort.result(committed, proposed)
            cohort.store(result_path, committed)
            retained.update(schemaVersion=2, registryOrigin=registry, artifactSourceCommit=provenance["artifactSourceCommit"],
                            releaseId=424242, request=proposed, result=committed)
        with patch.dict(os.environ), patch.object(pipeline, "verified", return_value=(store, {"draft": True}, provenance)), \
                patch.object(pipeline, "registry_versions") as versions, patch.object(cohort, "http", side_effect=http), \
                patch.object(cohort, "apply", side_effect=apply) as mutate, patch.object(receipts, "upload"), \
                patch.object(receipts, "download_bytes", side_effect=lambda *args: retained), patch.object(cohort, "confirm") as confirm:
            pipeline.visibility(self.root / "core", self.root, 424242)
            mutate.assert_called_once()
            confirm.assert_called_once()
            self.assertEqual(["Yanked", "Listed"], [call.args[2] for call in versions.call_args_list])
        self.assertEqual(pipeline.cohort_request(provenance, 43), release.load(self.root / pipeline.REQUEST))
        self.assertEqual(retained, release.load(self.root / ("agent-family-cohort-" + cohort.request_hash(retained["request"]) + ".json")))

    def test_pipeline_invalid_policy_never_retains_or_dispatches_a_cohort(self):
        provenance = {"invocation": {"releaseId": 424242}, "publication": {"registryOrigin": "https://registry.test"},
                      "artifacts": self.identities}
        store = Mock(tag="agent/v2.0.0")
        mutations = {
            "nonce": lambda v: v.update(nonce="0" * 32),
            "origin": lambda v: v.update(registryOrigin="https://other.test"),
            "fingerprint": lambda v: v["packagePolicy"].update(fingerprint="invalid"),
            "revision": lambda v: v["packagePolicy"].update(revision=0),
            "package state": lambda v: v["packages"][0].update(packageState="SecurityBlocked"),
            "availability": lambda v: v["packages"][0].update(versionAvailability="Listed"),
            "identity": lambda v: v["packages"][0]["package"].update(version="9.0.0"),
            "unexpected platform": lambda v: v["platformCatalogs"].append({"catalog": {}, "lifecycleState": "Unknown",
                                                                           "disposition": "PolicyRequired", "errorCode": None, "revokedAtUtc": None}),
            "time": lambda v: v.update(validUntilUtc="2026-10-05T00:00:00.1234567Z"),
            "removed checkpoint": lambda v: v.update(checkpoint=None),
        }
        def http(registry, route, payload):
            value = self.policy_response(release.decode(payload), "Yanked")
            mutate(value)
            return 200, release.encoded(value), 0
        with patch.object(pipeline, "verified", return_value=(store, {"draft": True}, provenance)), \
                patch.object(pipeline, "registry_versions"), patch.object(cohort, "http", side_effect=http), \
                patch.object(cohort, "apply") as apply:
            for name, mutate in mutations.items():
                with self.subTest(case=name), self.assertRaises(release.Invalid):
                    pipeline.visibility(self.root / "core", self.root, 424242)
                self.assertFalse((self.root / pipeline.REQUEST).exists())
            store.upload.assert_not_called()
            apply.assert_not_called()

    def test_explicit_reverse_restores_old_tag_and_deletes_only_new_tag(self):
        old = {**self.identities[0], "version": "1.0.0", "packageContentIdentity": "d" * 64}
        next(item for item in self.result["previousTags"] if item["packageId"] == old["packageId"])["tags"] = [
            {"packageId": old["packageId"], "tag": "latest", "target": old}]
        reverse = cohort.reverse(self.request, self.result, "reviewed-reverse", 43)
        self.assertEqual(old, reverse["distTags"][0]["target"])
        self.assertTrue(all(t["target"] is None for t in reverse["distTags"][1:]))
        self.assertTrue(all(v["availability"] == "Yanked" for v in reverse["versions"]))
        with self.assertRaises(release.Invalid):
            cohort.reverse(self.request, self.result, "fixture-1", 43)


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        CohortTests.setUp(self)
        self.value = {"schemaVersion": 2, "registryOrigin": "https://registry.test", "artifactSourceCommit": "1" * 40, "releaseId": 424242,
                      "request": self.request, "result": self.result}
        self.request_sha = receipts.check(self.value)
        self.data = release.canonical(self.value) + b"\n"
        self.asset = {"id": 1001, "name": "agent-family-cohort-" + self.request_sha + ".json", "state": "uploaded",
                      "size": len(self.data), "digest": "sha256:" + release.sha(self.data),
                      "url": "https://api.github.com/repos/Younics/sunder-agent-package/releases/assets/1001"}
        self.info = {"id": 424242, "tag_name": "agent/v2.0.0", "draft": True, "target_commitish": "1" * 40, "assets": [self.asset]}

    def test_numeric_asset_byte_binding_and_tamper_rejection(self):
        def download():
            return receipts.download_bytes("Younics/sunder-agent-package", "424242", "agent/v2.0.0", self.request_sha)
        with patch.object(receipts, "gh", side_effect=lambda _: release.encoded(self.info)), \
                patch.object(receipts.subprocess, "check_output", return_value=self.data):
            self.assertEqual(self.value, download())
            for field, bad in (("digest", None), ("size", 1), ("url", "https://other.test/asset"), ("state", "new")):
                old = self.asset[field]
                self.asset[field] = bad
                with self.assertRaises(release.Invalid):
                    download()
                self.asset[field] = old
            self.info["assets"].append(self.asset)
            with self.assertRaises(release.Invalid):
                download()

    def test_ambiguous_receipt_upload_recovers_exact_bytes_without_replacement(self):
        write(self.req, self.request)
        write(self.res, self.result)
        self.data = release.canonical(self.value) + b"\n"
        self.asset.update(size=len(self.data), digest="sha256:" + release.sha(self.data))
        self.info["assets"] = []
        def upload(_command, **kwargs):
            self.assertEqual(self.data, kwargs["input"])
            self.info["assets"] = [self.asset]
            return subprocess.CompletedProcess([], 28)
        with patch.dict(os.environ, {"GH_TOKEN": "mock-token"}), \
                patch("sys.stdout", new_callable=io.StringIO), \
                patch.object(receipts, "gh", side_effect=lambda _: release.encoded(self.info)), \
                patch.object(receipts.subprocess, "run", side_effect=upload) as send, \
                patch.object(receipts.subprocess, "check_output", return_value=self.data):
            for _ in range(2):
                receipts.upload("Younics/sunder-agent-package", "424242", "agent/v2.0.0", "https://registry.test", "1" * 40, self.req, self.res)
            self.assertEqual(1, send.call_count)


class ReverseTests(unittest.TestCase):
    def setUp(self):
        CohortTests.setUp(self)
        self.source = self.root / "source"
        seed_source(self.source, [p["packageId"] for p in self.identities])
        self.forward = {"schemaVersion": 2, "registryOrigin": "https://registry.test", "artifactSourceCommit": "1" * 40, "releaseId": 424242,
                        "request": self.request, "result": self.result}
        self.forward_path = self.root / "forward.json"
        write(self.forward_path, self.forward)
        proposed = cohort.reverse(self.request, self.result, "reviewed-reverse", 43)
        write(self.req, proposed)
        self.info = {"id": 424242, "tag_name": "agent/v2.0.0", "draft": True, "target_commitish": "1" * 40}
        self.tag = {"ref": "refs/tags/agent/v2.0.0", "object": {"type": "commit", "sha": "1" * 40}}
        self.args = ["Younics/sunder-agent-package", "424242", "agent/v2.0.0", str(self.source), "1" * 40,
                     str(self.forward_path), str(self.req)]
        patches = [patch.dict(os.environ, {"GITHUB_REF": "refs/heads/main", "GITHUB_SHA": "9" * 40, "REGISTRY_API_URL": "https://registry.test"}),
                   patch.object(reversal, "git", side_effect=lambda source, *args: (("1" if Path(source) == self.source else "9") * 40).encode()),
                   patch.object(receipts, "release_info", side_effect=lambda *args: self.info),
                   patch.object(receipts, "gh", side_effect=lambda _: release.encoded(self.tag)),
                   patch.object(receipts, "download_bytes", side_effect=lambda *args: self.forward),
                   patch.object(cohort, "current_tags", return_value=self.result["resultingTags"])]
        for item in patches:
            mocked = item.start()
            self.addCleanup(item.stop)
        self.tags = mocked

    def test_reverse_guard_accepts_only_exact_unannounced_source_and_captured_maps(self):
        reversal.guard(*self.args)
        self.tags.return_value = self.result["previousTags"]  # Exact lost-response retry is allowed.
        reversal.guard(*self.args)
        self.tags.return_value = copy.deepcopy(self.result["resultingTags"])
        self.tags.return_value[0]["tags"] = []
        with self.assertRaisesRegex(release.Invalid, "Concurrent tag"):
            reversal.guard(*self.args)
        self.tags.return_value = self.result["resultingTags"]
        self.info["draft"] = False
        with self.assertRaises(release.Invalid):
            reversal.guard(*self.args)
        self.info["draft"] = True
        self.tag["object"]["sha"] = "2" * 40
        with self.assertRaises(release.Invalid):
            reversal.guard(*self.args)

    def test_reverse_cannot_send_owner_credential_to_an_unapproved_origin(self):
        self.forward["registryOrigin"] = "https://unapproved.test"
        write(self.forward_path, self.forward)
        with self.assertRaisesRegex(release.Invalid, "protected configuration"):
            reversal.guard(*self.args)

    def test_reverse_orchestration_retains_one_request_and_exact_restored_result(self):
        def apply(registry, request_path, result_path):
            self.assertEqual("https://registry.test", registry)
            proposed = release.load(request_path)
            self.assertEqual(cohort.reverse(self.request, self.result, "reviewed-reverse", 43), proposed)
            committed = {**self.result, "idempotencyKey": proposed["idempotencyKey"], "requestSha256": cohort.request_hash(proposed),
                         "packagePolicy": {"revision": 44, "fingerprint": "f" * 64}, "versions": proposed["versions"],
                         "previousTags": self.result["resultingTags"], "resultingTags": self.result["previousTags"]}
            cohort.result(committed, proposed)
            cohort.store(result_path, committed)
        output = self.root / "reverse-output"
        with patch.object(cohort, "apply", side_effect=apply) as send, patch("sys.stdout", new_callable=io.StringIO):
            reversal.run(*self.args[:6], "reviewed-reverse", "43", str(output))
            self.assertEqual(1, send.call_count)
        self.assertEqual({"request.json", "result.json"}, {p.name for p in output.iterdir()})
        self.assertEqual(self.result["previousTags"], release.load(output / "result.json")["resultingTags"])


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "prepare":
        prepare_fixture(*sys.argv[2:])
    elif len(sys.argv) > 1 and sys.argv[1] == "complete":
        complete_fixture(*sys.argv[2:])
    elif len(sys.argv) > 1 and sys.argv[1] == "release":
        fixture_release(*sys.argv[2:])
    elif len(sys.argv) > 1 and sys.argv[1] == "evidence-git":
        evidence_git(*sys.argv[2:])
    else:
        unittest.main()
