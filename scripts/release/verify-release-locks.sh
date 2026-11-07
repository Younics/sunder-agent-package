#!/usr/bin/env bash
set -euo pipefail
repository_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
feed="${2:-}"
python3 -B - "$repository_root" "$feed" <<'PY'
import base64, hashlib, json, pathlib, sys, xml.etree.ElementTree as ET
root = pathlib.Path(sys.argv[1]).resolve()
def key(text):
    core, _, pre = text.partition('-')
    return tuple(map(int, core.split('.'))), not pre, tuple((0, int(p)) if p.isdigit() else (1, p) for p in pre.split('.')) if pre else ()
def accepts(value, constraint):
    if constraint.startswith('[') and constraint.endswith(']') and ',' not in constraint:
        return value == constraint[1:-1]
    if constraint.startswith(('[', '(')) and ',' in constraint:
        low, high = constraint[1:-1].split(',')
        return (not low or (key(value) >= key(low) if constraint[0] == '[' else key(value) > key(low))) and (not high or (key(value) <= key(high) if constraint[-1] == ']' else key(value) < key(high)))
    return key(value) >= key(constraint)
versions = {item.attrib['Include']: item.attrib['Version'] for item in ET.parse(root / 'Directory.Packages.props').iter('PackageVersion')
            if item.attrib.get('Include', '').startswith('Sunder.')}
projects = [item.attrib['Path'] for item in ET.parse(root / 'Sunder.AgentPackage.slnx').iter('Project')]
expected = {root / pathlib.Path(path).parent / 'packages.release.lock.json' for path in projects}
actual = {path for area in ('src', 'tests') for path in (root / area).glob('*/packages.release.lock.json')}
if expected != actual: raise SystemExit('Release lock inventory does not exactly match the solution project graph')
for path in sorted(actual):
    lock = json.loads(path.read_text())
    if lock['version'] != 2: raise SystemExit('Invalid NuGet lock schema: ' + str(path))
    for framework in lock['dependencies'].values():
        for name, constraint in versions.items():
            entry = framework.get(name)
            if entry is None: continue
            if entry['type'] == 'Project' or not entry.get('contentHash') or not accepts(entry['resolved'], constraint):
                raise SystemExit(f'Release lock does not resolve {name} within declared {constraint}: {path}')
            if sys.argv[2]:
                package = pathlib.Path(sys.argv[2]) / f"{name}.{entry['resolved']}.nupkg"
                digest = base64.b64encode(hashlib.sha512(package.read_bytes()).digest()).decode()
                if digest != entry['contentHash']: raise SystemExit('Core candidate content hash differs: ' + str(path))
inventory = json.loads((root / 'packages.json').read_text())
reviewed = json.loads((root / 'compatibility/agent-current-inventory.json').read_text())['packages']
if len(inventory['packages']) != len(reviewed) or {row['packageId'] for row in inventory['packages']} != {row['packageId'] for row in reviewed}:
    raise SystemExit('Expected the complete reviewed canonical package family')
print(f"Verified {len(actual)} release locks and {len(reviewed)} primary package projects against their independently declared Core dependencies.")
PY
