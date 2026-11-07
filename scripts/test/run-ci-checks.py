#!/usr/bin/env python3
"""Discover fast script tests; keep native/cold acceptance with its explicit owners."""
import argparse
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


def commands(group):
    if group == "source":
        yield ["node", "--test", *map(str, sorted(ROOT.glob("scripts/compatibility/*.test.mjs")))]
        yield ["node", "scripts/compatibility/check-package-boundaries.mjs"]
        for pattern in ("scripts/test/test_*.py", "scripts/test/*.fixture.py", "scripts/test/*.fixture.sh", "scripts/test/release/test_agent_ci.py"):
            for path in sorted(ROOT.glob(pattern)):
                yield ([sys.executable, "-B"] if path.suffix == ".py" else ["bash"]) + [str(path)]
        yield [sys.executable, "-B", "scripts/test/verify-current-contracts.py", "--source-only"]
        yield [sys.executable, "-B", "scripts/test/release/release-inputs.fixture.py"]
    else:
        for path in sorted(ROOT.glob("scripts/test/release/test_*.py")):
            if path.name != "test_agent_ci.py": yield [sys.executable, "-B", str(path)]
        for file in ("agent-release.fixture.py", "native-release.fixture.sh", "publish-registry-package.fixture.sh", "verify-nuget-package.fixture.sh"):
            yield ([sys.executable, "-B"] if file.endswith(".py") else ["bash"]) + ["scripts/test/release/" + file]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("group", choices=("source", "release"))
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args()
    for command in commands(args.group):
        print("+ " + " ".join(command), flush=True)
        if not args.list: subprocess.run(command, cwd=ROOT, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"}, check=True)
