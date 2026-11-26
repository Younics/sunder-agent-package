"""Verify the two Host-confirmed Hub operations, independently of archive download."""
from uuid import UUID

from native_release_inputs import integer, keys, require, sha


def verify(proof, rid, reinstall_id):
    subjects = {row["package"]["packageId"]: row["package"] for row in proof["policies"]}
    archives = {row["packageId"]: row["archiveSha256"] for row in proof["downloads"]}
    origin = proof["registryOrigin"]
    previous = None
    installations = {}
    for phase, ids, action in (("install", set(subjects), "package-install"), ("reinstall", {reinstall_id}, "package-update")):
        operation = proof[phase]
        keys(operation, "resolutionFingerprint expectedResolutionFingerprint sourceFingerprint rid runtimeSessionApplied committedStamp consents packages")
        sha(operation["resolutionFingerprint"]); sha(operation["sourceFingerprint"])
        require(operation["resolutionFingerprint"] == operation["expectedResolutionFingerprint"] and operation["rid"] == rid
                and operation["runtimeSessionApplied"] is True, "Hub apply does not confirm its exact preview and RID")
        stamp = operation["committedStamp"]
        keys(stamp, "runtimeInstanceId sessionGeneration")
        require(UUID(stamp["runtimeInstanceId"]).int != 0, "Missing Host instance")
        integer(stamp["sessionGeneration"], 2**63 - 1, 1)
        if previous is not None:
            require(stamp["runtimeInstanceId"] == previous["runtimeInstanceId"] and stamp["sessionGeneration"] > previous["sessionGeneration"],
                    "Reinstall did not commit a later generation on the same Host")
        previous = stamp
        rows = operation["packages"]
        require(sorted(row["packageId"] for row in rows) == sorted(ids), "Hub operation changed the wrong package set")
        manifests = {}
        for row in rows:
            keys(row, "packageId version actionKind sourcePackageContentIdentity provenance targets projections")
            key = row["packageId"]
            subject = subjects[key]
            version, identity = subject["version"], subject["packageContentIdentity"]
            require((row["version"], row["sourcePackageContentIdentity"], row["actionKind"]) == (version, identity, action),
                    "Hub operation changed the wrong version, source identity or action")
            require(row["provenance"] == dict(source="Registry", policy="ExplicitVersion", registryOrigin=origin + "/",
                                              sourcePackageId=key, requestedVersion=version, sourceIdentity=identity),
                    "Expected installed exact-version Registry provenance")
            targets = row["targets"]
            roles = [t["role"] for t in targets]
            require(sorted(roles) in (["runtime"], ["app", "runtime"]) and all(t == dict(role=t["role"], rid=rid) for t in targets),
                    "Installed Hub targets differ from the native platform")
            projections = row["projections"]
            require(sorted(p["kind"] for p in projections) == sorted([*roles, "shared"]), "Missing or extra Hub projection")
            for p in projections:
                keys(p, "kind rid sha256 size manifestSha256 sourceArchiveSha256 projectionContentIdentity")
                require(p["sourceArchiveSha256"] == archives[key] and p["rid"] == (None if p["kind"] == "shared" else rid),
                        "Hub projection is not from the qualified canonical archive/RID")
                sha(p["sha256"]); sha(p["manifestSha256"]); sha(p["projectionContentIdentity"]); integer(p["size"], 384 * 1024**2, 1)
            require(len({p["manifestSha256"] for p in projections}) == 1, "Mixed source manifests in Hub projections")
            manifests[key] = projections[0]["manifestSha256"]
            content = {p["kind"]: p for p in projections}
            if phase == "install": installations[key] = content
            else: require(content == installations[key], "Reinstall selected different immutable projection bytes")
        consents = operation["consents"]
        require(type(consents) is list and len({c["packageId"] for c in consents}) == len(consents)
                and (phase == "install" or not consents), "Unexpected Hub consent set")
        for consent in consents:
            keys(consent, "packageId packageVersion manifestSha256 capabilities")
            key = consent["packageId"]
            require(key in ids and consent["packageVersion"] == subjects[key]["version"] and consent["manifestSha256"] == manifests[key]
                    and type(consent["capabilities"]) is list and bool(consent["capabilities"])
                    and all(isinstance(c, str) and c for c in consent["capabilities"]), "Unbound Hub capability consent")
