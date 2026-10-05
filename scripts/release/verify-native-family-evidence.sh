#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 8 ]]; then
  printf 'Usage: verify-native-family-evidence.sh <native-test-evidence.json> <artifact-directory> <packages.json> <version> <agent-commit> <core-tag> <core-commit> <core-package-version>\n' >&2
  exit 2
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 -B "$script_dir/native-family.py" verify "$@"
