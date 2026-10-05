#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 8 ]]; then
  printf 'Usage: assemble-native-family.sh <native-input-directory> <output-directory> <version> <agent-commit> <core-tag> <core-commit> <core-package-version> <repository-root>\n' >&2
  exit 2
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 -B "$script_dir/native-family.py" assemble "$@"
