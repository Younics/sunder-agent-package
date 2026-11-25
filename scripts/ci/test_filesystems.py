"""Provision the case-sensitive macOS prerequisite without changing the default volume."""
from contextlib import contextmanager
import errno
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
import time


EJECT_TIMEOUT = 30
EJECT_RETRY_DELAY = 1


def require_case_sensitive(root):
    probe = root / "case-probe"
    probe.write_text("probe", encoding="utf-8")
    if (root / "CASE-PROBE").exists():
        raise ValueError("The required test volume is not case-sensitive")
    probe.unlink()


@contextmanager
def case_sensitive_volume(log):
    # Keep the mount out of the prepared state's recursively cleaned temp tree.
    work = Path(tempfile.mkdtemp(prefix="case-sensitive-tests-", dir=os.environ.get("RUNNER_TEMP"))).resolve()
    mount = work / "mount"
    mount.mkdir()
    image = work / "tests.sparseimage"
    attach_requested = False

    def record(text):
        with Path(log).open("a", encoding="utf-8") as stream:
            stream.write(text)

    def command(*arguments, timeout=None, check=True, log_stdout=True):
        try:
            result = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            record(f"Timed out running {arguments!r} after {timeout:.1f}s\n")
            raise
        record(json.dumps(dict(command=arguments, exitCode=result.returncode)) + "\n"
               + (result.stdout if log_stdout else "") + result.stderr)
        if check:
            result.check_returncode()
        return result

    def image_attachment(timeout):
        # Only this invocation's private image can authorize a teardown command.
        info = command("hdiutil", "info", "-plist", timeout=min(5, timeout), log_stdout=False)
        rows = [row for row in plistlib.loads(info.stdout.encode("utf-8"))["images"]
                if row.get("image-path") == str(image)]
        if len(rows) > 1:
            raise ValueError(f"Ambiguous attachment for the case-sensitive test image: {image}")
        return rows[0] if rows else None

    def entities(attachment):
        return [] if attachment is None else attachment["system-entities"]

    def mounted_paths(attachment):
        return [row["mount-point"] for row in entities(attachment) if row.get("mount-point")]

    def detach():
        deadline = time.monotonic() + EJECT_TIMEOUT

        def remaining():
            seconds = deadline - time.monotonic()
            if seconds <= 0:
                raise TimeoutError(f"Timed out waiting for the case-sensitive test image to detach: {image}")
            return seconds

        attachment = image_attachment(remaining())
        # Unmount the verified APFS volume separately. Combining unmount and
        # eject can leave its backing disk busy even after the mount disappears.
        if mount.is_mount() or mounted_paths(attachment):
            if not mount.is_mount() or mounted_paths(attachment) != [str(mount)]:
                raise ValueError(f"Cannot verify the case-sensitive test mount: {mount}")
            command("diskutil", "unmount", str(mount), timeout=remaining())
            attachment = image_attachment(remaining())

        while True:
            if mount.is_mount() or mounted_paths(attachment):
                raise ValueError(f"The case-sensitive test image is still mounted: {image}")
            if attachment is None:
                return

            devices = [row["dev-entry"] for row in entities(attachment)
                       if row.get("content-hint") == "GUID_partition_scheme"
                       and re.fullmatch(r"/dev/disk[0-9]+", row.get("dev-entry", ""))]
            if len(devices) != 1:
                raise ValueError(f"Cannot verify the backing device for the case-sensitive test image: {image}")
            try:
                command("hdiutil", "detach", devices[0], timeout=remaining())
            except subprocess.CalledProcessError as error:
                if error.returncode != errno.EBUSY or deadline - time.monotonic() <= EJECT_RETRY_DELAY:
                    raise
                # Allow the APFS container to release the backing disk, but
                # rebind the image each time rather than caching a device number.
                time.sleep(EJECT_RETRY_DELAY)
                attachment = image_attachment(remaining())
            else:
                if mount.is_mount() or image_attachment(remaining()) is not None:
                    raise ValueError(f"The case-sensitive test image is still attached: {image}")
                return

    try:
        command("hdiutil", "create", "-size", "512m", "-type", "SPARSE",
                "-fs", "Case-sensitive APFS", "-volname", "Sunder Test", str(image))
        attach_requested = True
        command("hdiutil", "attach", "-nobrowse", "-mountpoint", str(mount), str(image))
        require_case_sensitive(mount)
        yield mount
    finally:
        # Never traverse a still-mounted image on failed detach, even when the
        # test process itself failed. Retain it and fail the prerequisite instead.
        if attach_requested:
            try:
                detach()
            except (OSError, ValueError, subprocess.SubprocessError):
                target = ("+f", "--", str(mount)) if mount.is_mount() else ("--", str(image))
                try:
                    command("/usr/sbin/lsof", "-nP", *target, timeout=5, check=False)
                except (OSError, subprocess.TimeoutExpired) as diagnostic:
                    record(f"Could not capture image or mount holders: {diagnostic}\n")
                raise
        if mount.is_mount():
            raise ValueError(f"The case-sensitive test volume is still mounted: {mount}")
        shutil.rmtree(work)
