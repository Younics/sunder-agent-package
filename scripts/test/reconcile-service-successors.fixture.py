#!/usr/bin/env python3
"""Exercise current-evidence writes against isolated copies of owning snapshots."""

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("reconcile-service-successors.py")
SPEC = importlib.util.spec_from_file_location("service_successors", SCRIPT)
TOOL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TOOL)
REPOSITORY = SCRIPT.resolve().parents[2]
RECEIPT = f"{TOOL.SOURCE}/obj/Debug/net10.0/sunder-analysis/osx-arm64/sunder-rpc/authoring-receipt.v1.json"
BASELINE = f"{TOOL.SOURCE}/sunder-contracts.owned.json"
PRIVATE_IDS = ["sunder.agent.owner.runtime", "sunder.package.agent.agent.profile"]


class CurrentSuccessorEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="sunder-current-service-evidence-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.originals = {}
        for relative in (TOOL.HISTORY, BASELINE, RECEIPT, TOOL.PRIVATE):
            raw = (REPOSITORY / relative).read_bytes()
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(raw)
            self.originals[relative] = raw

    def reconcile(self, write=False):
        return TOOL.reconcile(self.root, "Debug", "osx-arm64", PRIVATE_IDS, write)

    def mutate(self, relative, edit):
        value = json.loads((self.root / relative).read_bytes())
        edit(value)
        (self.root / relative).write_text(json.dumps(value), encoding="utf-8")

    def test_explicit_write_retains_history_and_unselected_private_records_then_is_a_noop(self):
        def previous_current_tuples(value):
            for item in value["contracts"]:
                if item["contractId"] in PRIVATE_IDS:
                    for field in ("descriptorSha256", "abiSha256"):
                        item[field] = TOOL.digest((item["contractId"] + "/previous/" + field).encode("utf-8"))
        self.mutate(TOOL.PRIVATE, previous_current_tuples)
        private_before = (self.root / TOOL.PRIVATE).read_bytes()
        with self.assertRaisesRegex(ValueError, "explicit --write"):
            self.reconcile()
        self.assertFalse((self.root / TOOL.PUBLIC).exists())
        self.assertEqual(private_before, (self.root / TOOL.PRIVATE).read_bytes())

        self.reconcile(write=True)
        public = json.loads((self.root / TOOL.PUBLIC).read_bytes())
        self.assertEqual(19, len(public["contracts"]))
        embedding = next(item for item in public["contracts"] if item["contractId"] == "sunder.agent.embedding.provider")
        self.assertEqual("2.0.0", embedding["version"])
        before = json.loads(private_before)["contracts"]
        after = json.loads((self.root / TOOL.PRIVATE).read_bytes())["contracts"]
        emitted = {item["contractId"]: item for item in json.loads(self.originals[RECEIPT])["contracts"]}
        self.assertEqual(13, len(after))
        for old, new in zip(before, after):
            if old["contractId"] not in PRIVATE_IDS:
                self.assertEqual(old, new)
            else:
                for field in ("descriptorSha256", "abiSha256"):
                    self.assertNotEqual(old[field], new[field])
                    self.assertEqual(emitted[new["contractId"]][field], new[field])
        for relative in (TOOL.HISTORY, BASELINE, RECEIPT):
            self.assertEqual(self.originals[relative], (self.root / relative).read_bytes())
        stamps = {relative: (self.root / relative).stat().st_mtime_ns for relative in (TOOL.PUBLIC, TOOL.PRIVATE)}
        self.assertEqual([], self.reconcile())
        self.assertEqual([], self.reconcile(write=True))
        self.assertEqual(stamps, {relative: (self.root / relative).stat().st_mtime_ns for relative in stamps})

    def test_unfrozen_public_snapshot_is_rejected_before_any_fixture_write(self):
        self.mutate(BASELINE, lambda value: value["contracts"]["embedding-provider"]["current"].update(state="draft"))
        with self.assertRaisesRegex(ValueError, "not frozen"):
            self.reconcile(write=True)
        self.assertFalse((self.root / TOOL.PUBLIC).exists())
        self.assertEqual(self.originals[TOOL.PRIVATE], (self.root / TOOL.PRIVATE).read_bytes())

    def test_lost_original_public_history_is_rejected(self):
        self.mutate(BASELINE, lambda value: value["contracts"]["embedding-provider"].update(history=[]))
        with self.assertRaisesRegex(ValueError, "Original public snapshot is not retained"):
            self.reconcile(write=True)
        self.assertFalse((self.root / TOOL.PUBLIC).exists())

    def test_corrupt_private_receipt_cannot_partially_write_public_fixture(self):
        def corrupt(value):
            next(item for item in value["contracts"] if item["contractId"] == PRIVATE_IDS[0])["descriptorSha256"] = "0" * 64
        self.mutate(RECEIPT, corrupt)
        with self.assertRaisesRegex(ValueError, "Descriptor digest mismatch"):
            self.reconcile(write=True)
        self.assertFalse((self.root / TOOL.PUBLIC).exists())
        self.assertEqual(self.originals[TOOL.PRIVATE], (self.root / TOOL.PRIVATE).read_bytes())


if __name__ == "__main__":
    unittest.main()
