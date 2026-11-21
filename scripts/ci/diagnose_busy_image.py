"""Isolated native experiment. Hold only the APFS image created by this probe."""
from pathlib import Path
import errno
import json
import subprocess
import sys
from unittest.mock import patch

import test_filesystems


def exercise(report, temporary_holder):
    original_run = subprocess.run
    holder, mount = None, None
    logs = report / ("transient.log" if temporary_holder else "persistent.log")

    def release():
        if holder is not None and holder.poll() is None:
            holder.stdin.close()
            assert holder.wait(timeout=5) == 0

    def observed(arguments, **kwargs):
        result = original_run(arguments, **kwargs)
        if arguments[0] == "/usr/sbin/lsof":
            assert any(line.split()[1:2] == [str(holder.pid)] for line in result.stdout.splitlines()), result
            if temporary_holder:
                release()
        return result

    try:
        with patch.object(test_filesystems.subprocess, "run", side_effect=observed):
            try:
                with test_filesystems.case_sensitive_volume(logs) as mount:
                    assert mount.is_mount()
                    (mount / "marker").write_text("Must remain if the busy image cannot detach.")
                    holder = subprocess.Popen(
                        [sys.executable, "-u", "-c", "import sys; print('holding'); sys.stdin.readline()"],
                        cwd=mount, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
                    assert holder.stdout.readline() == "holding\n"
            except subprocess.CalledProcessError as error:
                assert not temporary_holder and error.returncode == errno.EBUSY, logs.read_text()
            else:
                assert temporary_holder, logs.read_text()
        detach = [record for line in logs.read_text().splitlines() if line.startswith("{")
                  if (record := json.loads(line))["command"][:2] == ["hdiutil", "detach"]]
        assert [row["exitCode"] for row in detach] == [errno.EBUSY, 0 if temporary_holder else errno.EBUSY]
        assert detach[0]["command"][2] == str(mount) and detach[1]["command"][2].startswith("/dev/disk")
        if temporary_holder:
            assert not mount.parent.exists()
        else:
            assert mount.is_mount()
            assert (mount.parent / "tests.sparseimage").is_file() and (mount / "marker").is_file()
        print(logs.name, detach)
    finally:
        release()
        if mount is not None and mount.is_mount():
            original_run(["hdiutil", "detach", str(mount)], timeout=30, check=True)
            assert not mount.is_mount()


if __name__ == "__main__":
    output = Path("artifacts/filesystem-probe")
    output.mkdir(parents=True, exist_ok=True)
    exercise(output, True)
    exercise(output, False)
