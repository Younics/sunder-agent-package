#!/usr/bin/env python3
"""Phase-specific gates shared by trusted acceptance and promotion policy."""

from pathlib import Path
import subprocess
import sys

import agent_cohort as cohort
import agent_release as release


def verify(source, assets, evidence, registry):
    value = release.load(evidence)
    provenance = release.validate_provenance(source, assets)
    release.require(value["schemaVersion"] == 4 and value["phase"] in ("pre-visibility", "post-visibility"),
                    "Acceptance must identify its visibility phase")
    binding = value["releaseEvidence"]
    release.closed(binding, "artifactSourceCommit publicationEvidenceCommit lockSetSha256")
    release.require(binding["artifactSourceCommit"] == provenance["artifactSourceCommit"] == value["sourceCommit"]
                    and binding["lockSetSha256"] == provenance["lockSetSha256"]
                    and provenance["publication"]["registryOrigin"] == release.origin(registry), "Acceptance release/Registry binding differs")
    release.verify_evidence_commit(source, binding["publicationEvidenceCommit"], provenance)
    release.closed(value["cohort"], "request result")
    proposed = cohort.request(value["cohort"]["request"])
    expected = [{"package": {k: r[k] for k in ("packageId", "version", "packageContentIdentity")},
                 "expectedAvailability": "Yanked", "availability": "Listed"} for r in provenance["artifacts"]]
    expected.sort(key=lambda v: v["package"]["packageId"] + "/" + v["package"]["version"])
    release.require(proposed["versions"] == expected, "Acceptance cohort does not cover the exact 15 retained identities")
    tag = "latest" if release.version(value["version"])[1] else "preview"
    release.require(proposed["distTags"] == [{"packageId": v["package"]["packageId"], "tag": tag, "target": v["package"]} for v in expected],
                    "Acceptance must atomically set every family release tag")
    if value["phase"] == "pre-visibility":
        release.require(value["cohort"]["result"] is None, "Pre-visibility acceptance cannot claim a committed cohort")
    else:
        cohort.result(value["cohort"]["result"], proposed)
    return value


if __name__ == "__main__":
    try:
        verify(*sys.argv[1:])
    except (release.Invalid, ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(f"Agent acceptance rejected: {error}")
