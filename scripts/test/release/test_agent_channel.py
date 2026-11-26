"""Already-Listed releases: retain the exact single-channel request and reconcile ambiguous replies."""
from pathlib import Path
import sys
import os
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import agent_channel as channel
import agent_pipeline as pipeline
import agent_release as r


class ChannelTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory()
        self.addCleanup(scope.cleanup)
        self.directory = Path(scope.name)
        self.package = dict(packageId="example.package", version="2.0.0", packageContentIdentity="1"*64)
        self.plan = dict(releasePackage=self.package["packageId"], schemaVersion=3, registryOrigin="https://registry.test",
                         artifactSourceCommit="a"*40)
        self.value = dict(artifacts=[self.package], invocation=dict(releaseId=21))
        self.store, self.guard = Mock(), Mock()
        self.before = dict(packagePolicy=dict(revision=5, fingerprint="a"*64), tags={"stable": "1.0.0"})
        self.after = dict(packagePolicy=dict(revision=6, fingerprint="b"*64), tags=dict(self.before["tags"], latest="2.0.0"))
        self.release = dict(id=21,draft=True)

    def promote(self):
        channel.promote(self.plan, self.value, self.directory, self.release, self.store, self.guard)

    def test_lost_put_replays_the_original_fence_preserves_other_tags_and_never_changes_availability(self):
        sent = []
        def http(origin, route, body, token, *, method, headers):
            self.assertTrue((self.directory / channel.REQUEST).is_file())
            self.assertEqual("PUT", method)
            self.assertEqual("/api/v1/packages/example.package/dist-tags/latest", route)
            self.assertEqual({"version":"2.0.0"}, r.decode(body))
            self.assertEqual(["X-Sunder-Idempotency-Key: agent-channel-21", "X-Sunder-Package-Policy-Revision: 5"], headers)
            sent.append((route, body, headers))
            return ((0, b"", 28) if len(sent) == 1 else (200, r.encoded(dict(success=True, message="OK", errors=[], forbidden=False, notFound=False)), 0))
        with patch.dict(os.environ,SUNDER_REGISTRY_API_KEY="sunder_pub_v1_"+"a"*43), \
                patch.object(channel.cohort,"http",side_effect=http), patch.object(channel.cohort,"confirm_listed") as auth, \
                patch.object(channel,"snapshot",side_effect=[self.before,self.after,self.after]):
            self.promote()
            proof = (self.directory / channel.RECEIPT).read_bytes()
            self.release["draft"] = False
            self.promote()
            self.assertEqual(proof,(self.directory / channel.RECEIPT).read_bytes())
            self.assertEqual([sent[0]]*3,sent)
            self.assertEqual(2,self.guard.call_count)
            self.assertEqual(3,auth.call_count)
            self.assertEqual([channel.REQUEST,channel.RECEIPT],[call.args[1][0].name for call in self.store.upload.call_args_list])

    def test_older_major_and_equal_version_are_observation_only(self):
        for previous in ("2.0.0","3.0.0"):
            with self.subTest(previous=previous), tempfile.TemporaryDirectory() as temp:
                self.directory = Path(temp)
                self.before["tags"]["latest"] = previous
                with patch.object(channel,"snapshot",return_value=self.before), patch.object(channel.cohort,"confirm_listed"), \
                        patch.object(channel,"put") as mutate:
                    self.promote()
                    self.promote()
                    mutate.assert_not_called()
                    self.guard.assert_not_called()
                    self.assertEqual("retain",r.load(self.directory/channel.REQUEST)["action"])

    def test_stale_or_denied_request_is_preserved_not_refenced(self):
        with patch.dict(os.environ,SUNDER_REGISTRY_API_KEY="sunder_pub_v1_"+"a"*43), \
                patch.object(channel.cohort,"http",return_value=(409,b"",0)) as send, \
                patch.object(channel.cohort,"confirm_listed"), patch.object(channel,"snapshot",return_value=self.before) as get:
            for _ in range(2):
                with self.assertRaisesRegex(r.Invalid,"original channel request"):
                    self.promote()
            self.assertEqual(1,get.call_count)
            self.assertEqual(send.call_args_list[0],send.call_args_list[1])
            self.assertFalse((self.directory/channel.RECEIPT).exists())
            self.assertEqual(1,self.store.upload.call_count)

    def test_failed_admission_or_drift_does_not_grant_announcement(self):
        with patch.object(channel,"snapshot",return_value=self.before), \
                patch.object(channel.cohort,"confirm_listed",side_effect=r.Invalid("not listed")), \
                patch.object(channel,"put") as mutate, self.assertRaises(r.Invalid):
            self.promote()
        mutate.assert_not_called()
        self.store.upload.assert_not_called()
        with patch.object(channel,"snapshot",side_effect=[self.before,dict(self.after,tags={"latest":"2.0.0"})]), \
                patch.object(channel.cohort,"confirm_listed"), patch.object(channel,"put",return_value=True), self.assertRaises(r.Invalid):
            self.promote()
        self.assertFalse((self.directory/channel.RECEIPT).exists())

    def test_new_prerelease_targets_preview_and_real_install_gate_precedes_any_read_or_write(self):
        self.package["version"] = "2.1.0-rc.1"
        proposed = channel.request(self.plan,self.value,self.before)
        self.assertEqual("preview",proposed["tag"])
        with patch.object(pipeline,"verified",return_value=([(self.package,self.store)],[self.release],self.value)), \
                patch.object(r,"read_publications",return_value={}), patch.object(channel,"promote") as promote, \
                patch.object(pipeline,"registry_versions") as registry, self.assertRaisesRegex(r.Invalid,"six-RID"):
            pipeline.visibility(None,self.directory,self.plan)
        promote.assert_not_called()
        registry.assert_not_called()


if __name__ == "__main__":
    unittest.main()
