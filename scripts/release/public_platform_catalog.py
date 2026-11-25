"""Read the exact platform catalog from the public Sunder Registry, never Core source."""
from pathlib import Path
import subprocess
from urllib.parse import quote

import release_inputs as inputs

REGISTRY = "https://hub.sunderapp.io"
CATALOG = "sunder-platform-contracts.v1.json"
MAX_BYTES = 64 * 1024 * 1024


def download(url):
    return subprocess.check_output([
        "curl", "--disable", "--proto", "=https", "--silent", "--show-error", "--fail",
        "--max-time", "45", "--max-filesize", str(MAX_BYTES), url,
    ], timeout=50)


def fetch(source, destination, packages=None):
    selections = {(item["authorityKind"], item["authorityId"], item["selectedAuthorityRelease"],
                   item["selectedAuthorityContentIdentity"])
                  for _, lock in inputs.lock_documents(source, packages) for item in inputs.catalog_requirements(lock)}
    inputs.require(len(selections) == 1, "Expected one exact platform catalog selection")
    kind, authority, version, sha = next(iter(selections))
    inputs.require((kind, authority) == ("platform", "sunder.core"), "Unexpected platform catalog authority")
    inputs.version(version)
    inputs.digest(sha)
    route = f"/api/v1/platform-contracts/sunder.core/releases/{quote(version, safe='')}/catalog"
    path = Path(destination) / CATALOG
    inputs.require(not path.exists() and not path.is_symlink(), "Catalog download destination must be new")
    response = download(REGISTRY + route)
    inputs.require(len(response) <= MAX_BYTES and inputs.sha(response) == sha,
                   "Public Registry catalog bytes differ from the authored platform selection")
    catalog = inputs.decode(response)
    inputs.require((catalog["authorityKind"], catalog["authorityId"], catalog["releaseVersion"]) ==
                   (kind, authority, version), "Public Registry catalog identity differs")
    with path.open("xb") as stream:
        stream.write(response)
    return path
