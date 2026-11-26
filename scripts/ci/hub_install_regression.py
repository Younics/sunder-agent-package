"""Exercise the release installer on a Listed reference, with no draft or publication access."""
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/release"))
import bash_runtime
import release_inputs as inputs

# A fixed published reference independent of unshipped PR package versions.
ORIGIN = "https://hub.sunderapp.io"
OWNER = "younics.ai.agent"
VERSION = "2.0.0"
SIZE = 32803281
SHA256 = "53af0bfe971d4d0ed8110eb5a0df929e3748629af193d5d4ff3c6964502aacda"


def main():
    with tempfile.TemporaryDirectory(prefix="agent-hub-check-") as temporary:
        root = Path(temporary).resolve()
        archives = root / "archives"
        archives.mkdir()
        path = archives / f"{OWNER}.{VERSION}.sunderpkg"
        env = dict(inputs.without_credentials(), SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN=ORIGIN)
        subprocess.run(["curl", "--disable", "--fail", "--silent", "--show-error", "--proto", "=https",
                        "--max-time", "180", "--max-filesize", str(SIZE), "--output", str(path),
                        f"{ORIGIN}/api/v1/packages/{OWNER}/versions/{VERSION}/canonical"], env=env, check=True)
        inputs.require(inputs.regular(path).stat().st_size == SIZE and inputs.file_sha(path) == SHA256,
                       "Public Hub install reference differs from its immutable archive")
        subprocess.run(bash_runtime.command(ROOT / "scripts/release/run-native-runtime-smoke.sh", archives,
                       root / "metrics", "published", OWNER), env=env, cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
