"""Retain bounded shutdown phases and metadata, never copy surviving payloads."""
from hashlib import file_digest
import json
import os
from pathlib import Path
import re
import stat
import sys
from xml.etree import ElementTree as ET

metrics, expected = Path(sys.argv[1]), Path(sys.argv[2])
exit_code = int(sys.argv[3])
case = "Sunder.Runtime.Host.Tests.AgentNativeReleaseSmokeTests.CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost"
log = metrics / "test.log"
lines = log.read_text(errors="replace").splitlines()
trace = [line for line in lines if line.startswith("shutdown-probe ")]
(metrics / "shutdown-trace.log").write_text("\n".join(trace) + "\n")

temp = Path((metrics / "test-temp-root.txt").read_text().strip()) / "sunder-runtime-worker"
survivors = []
if temp.is_dir() and not temp.is_symlink():
    for entry in sorted(temp.iterdir()):
        if not re.fullmatch(r"[0-9a-f]{32}", entry.name):
            continue
        info = entry.lstat()
        children = []
        if stat.S_ISDIR(info.st_mode):
            with os.scandir(entry) as entries:
                for child in entries:
                    child_stat = child.stat(follow_symlinks=False)
                    children.append({"mode": oct(child_stat.st_mode), "size": child_stat.st_size})
                    if len(children) == 64:
                        break
        survivors.append({
            "activation": entry.name, "mode": oct(info.st_mode), "size": info.st_size,
            "mtimeNs": info.st_mtime_ns, "topLevelMetadata": children,
        })

trx = metrics / "native-family.trx"
rows = ET.parse(trx).findall(".//{*}UnitTestResult") if trx.exists() else []
scope = metrics / "scope" / "qualification.json"
exact = scope.exists() and scope.read_bytes() == expected.read_bytes()
evidence = dict(diagnosticOnly=True, case=case, exitCode=exit_code,
    cases=[dict(name=row.get("testName"), outcome=row.get("outcome"), duration=row.get("duration"),
                message=row.findtext(".//{*}ErrorInfo/{*}Message")) for row in rows],
    exactScopeWritten=exact, survivingDirectories=survivors,
    stageSweepFault=any("PackageStageCleanupService.RunSweepAsync" in line for line in lines),
    sessionDrainDeadline=any("Previous package session cleanup exceeded the drain deadline" in line for line in lines),
    traceLines=len(trace))
if trx.exists():
    with trx.open("rb") as stream:
        evidence["trxSha256"] = file_digest(stream, "sha256").hexdigest()
(metrics / "verdict.json").write_text(json.dumps(evidence, indent=2) + "\n")
print(json.dumps({key: value for key, value in evidence.items() if key != "survivingDirectories"}, indent=2))
if exit_code == 0:
    assert len(rows) == 1 and (rows[0].get("testName"), rows[0].get("outcome")) == (case, "Passed")
    assert exact and trace
