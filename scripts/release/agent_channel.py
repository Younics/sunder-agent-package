"""Promote one already-Listed version after installed acceptance. Never change availability."""
import os
import re

import agent_cohort as cohort
import agent_release as r

REQUEST = "hub-channel-request.json"
RECEIPT = "hub-channel-receipt.json"


def route(package):
    return "/api/v1/packages/" + package["packageId"] + "/dist-tags"


def snapshot(registry, package):
    value = cohort.get(registry, route(package))
    r.closed(value, "packageId distTags packagePolicy")
    r.require(value["packageId"] == package["packageId"] and type(value["distTags"]) is list
              and len(value["distTags"]) <= 1024, "Invalid dist-tag readback")
    policy = value["packagePolicy"]
    r.closed(policy, "revision fingerprint")
    r.positive(policy["revision"])
    r.digest(policy["fingerprint"])
    tags = {}
    for item in value["distTags"]:
        r.closed(item, "tag version updatedAtUtc")
        tag = item["tag"]
        r.require(re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,63}", tag) and tag not in tags, "Invalid or ambiguous dist tag")
        r.version(item["version"])
        r.timestamp(item["updatedAtUtc"])
        tags[tag] = item["version"]
    return {"packagePolicy": policy, "tags": tags}


def request(plan, value, before):
    record, = [row for row in value["artifacts"] if row["packageId"] == plan["releasePackage"]]
    subject = cohort.identity({k: record[k] for k in ("packageId", "version", "packageContentIdentity")})
    r.closed(before, "packagePolicy tags")
    policy, tags = before["packagePolicy"], before["tags"]
    r.closed(policy, "revision fingerprint")
    r.positive(policy["revision"])
    r.digest(policy["fingerprint"])
    r.require(type(tags) is dict and len(tags) <= 1024, "Invalid prior channel map")
    for tag, version in tags.items():
        r.require(re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,63}", tag), "Invalid retained channel")
        r.version(version)
    version = subject["version"]
    tag = "latest" if r.version(version)[1] else "preview"
    current = tags.get(tag)
    # Equal precedence but different build metadata is not an advancing version.
    advance = current is None or r.version(version) > r.version(current)
    return dict(schemaVersion=1, registryOrigin=r.origin(plan["registryOrigin"]), package=subject,
                artifactSourceCommit=r.digest(plan["artifactSourceCommit"], 40),
                idempotencyKey="agent-channel-" + str(r.positive(value["invocation"]["releaseId"])),
                tag=tag, action="advance" if advance else "retain", before=before)


def put(proposed):
    token = os.environ.get("SUNDER_REGISTRY_API_KEY", "")
    r.require(re.fullmatch(r"sunder_pub_v1_[A-Za-z0-9_-]{43}", token), "Missing scoped channel API key")
    before = proposed["before"]["packagePolicy"]["revision"]
    headers = ["X-Sunder-Idempotency-Key: " + proposed["idempotencyKey"], "X-Sunder-Package-Policy-Revision: " + str(before)]
    status, body, failed = cohort.http(proposed["registryOrigin"], route(proposed["package"]) + "/" + proposed["tag"],
        r.encoded(dict(version=proposed["package"]["version"])), token, method="PUT", headers=headers)
    if not failed and status == 200:
        response = r.decode(body)
        r.closed(response, "success message errors forbidden notFound")
        r.require(response["success"] is True and response["errors"] == [] and response["forbidden"] is False
                  and response["notFound"] is False, "Hub did not confirm the retained channel request")
        return True
    r.require(failed or status >= 500, f"Hub rejected the original channel request ({status}); do not change the fence or key")
    return False


def verify(proposed, directory, *, minimum=None):
    subject, before = proposed["package"], proposed["before"]
    expected = dict(before["tags"])
    if proposed["action"] == "advance":
        expected[proposed["tag"]] = subject["version"]
    after = snapshot(proposed["registryOrigin"], subject)
    r.require(after["tags"] == expected and after["packagePolicy"]["revision"] >= before["packagePolicy"]["revision"]
              + (proposed["action"] == "advance"), "Complete channel map or policy revision differs")
    floor = minimum if minimum is not None else before["packagePolicy"]
    r.require(after["packagePolicy"]["revision"] >= floor["revision"]
              and (after["packagePolicy"]["revision"] != floor["revision"] or after["packagePolicy"] == floor),
              "Channel policy fell below its retained identity")
    cohort.confirm_listed(proposed["registryOrigin"], [subject], directory, after["packagePolicy"])
    return after


def promote(plan, value, directory, release, store, guard):
    def retain(path, document):
        cohort.store(path, document)
        store.upload(release["id"], [path])
    origin = plan["registryOrigin"]
    exact, = [v for v in value["artifacts"] if v["packageId"] == plan["releasePackage"]]
    path, proof = directory / REQUEST, directory / RECEIPT
    if not path.exists():
        r.require(release["draft"] and not proof.exists(), "Missing original channel request")
        before = snapshot(origin, exact)
        proposed = request(plan, value, before)
        cohort.confirm_listed(origin, [proposed["package"]], directory, before["packagePolicy"])
        retain(path, proposed)
    proposed = r.load(path)
    r.require(proposed == request(plan, value, proposed["before"]), "Channel request does not match this release")
    request_sha = r.file_sha(path)
    if proof.exists():
        receipt = r.load(proof)
        r.closed(receipt, "schemaVersion requestSha256 observed")
        r.require(type(receipt["schemaVersion"]) is int and receipt["schemaVersion"] == 1
                  and receipt["requestSha256"] == request_sha, "Wrong channel receipt")
        r.closed(receipt["observed"], "packagePolicy tags")
        after = verify(proposed, directory, minimum=receipt["observed"]["packagePolicy"])
        r.require(after["tags"] == receipt["observed"]["tags"], "Retained channel result changed")
        if proposed["action"] == "advance":
            # verify() established the original revision is stale: this can only
            # confirm the immutable operation, never create a different tag change.
            r.require(put(proposed), "Hub did not confirm the original channel operation")
        return
    r.require(release["draft"], "An announced release lacks its channel receipt")
    if proposed["action"] == "advance":
        for _ in range(2):
            guard()
            r.require(r.file_sha(path) == request_sha, "Retained channel request changed during guard")
            if put(proposed): break
        else: raise r.Invalid("Ambiguous Hub channel response; keep the exact request for recovery")
    after = verify(proposed, directory)
    retain(proof, dict(schemaVersion=1, requestSha256=request_sha, observed=after))
