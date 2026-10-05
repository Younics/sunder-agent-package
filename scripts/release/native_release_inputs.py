"""Bounded transport for Core V1 role inputs. Core remains the qualification authority.

No receipts are synthesized or rewritten here. The transport reads only the identity
and file closure needed to safely retain/extract inputs; Pack checks the complete
canonical document, current source/restore/toolset frame and Worker/App receipts.
"""

import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tarfile
import zipfile

RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
ROLE_INPUTS = "role-inputs.v1.json"
MAX_DOCUMENT = 64 * 1024 * 1024
MAX_FILES = 16384
MAX_LEAF = 2 * 1024 * 1024 * 1024
MAX_RUN = 15 * (MAX_LEAF + MAX_DOCUMENT + 16 * 1024 * 1024)
METADATA = {"manifest/sunder-package.json", "manifest/content-index.json"}
RESERVED = {"CON", "PRN", "AUX", "NUL"} | {
    prefix + str(i) for prefix in ("COM", "LPT") for i in range(1, 10)
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def keys(value, names):
    require(isinstance(value, dict) and set(value) == set(names.split()),
            "Unexpected/missing document members: " + names)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sha(value):
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value),
            "Expected an exact lowercase SHA-256")


def integer(value, maximum, minimum=0):
    require(type(value) is int and minimum <= value <= maximum, "Invalid bounded byte/count value")


def relative(value, length=2048, depth=36):
    require(isinstance(value, str) and 0 < len(value) <= length, "Invalid path length")
    require(all(32 <= ord(c) <= 126 and c not in '\\<>:"|?*' for c in value),
            "Unsafe/nonportable path: " + value)
    parts = value.split("/")
    require(len(parts) <= depth and all(
        p not in ("", ".", "..") and not p.endswith((" ", "."))
        and p.split(".")[0].upper() not in RESERVED for p in parts), "Unsafe path: " + value)
    return value


def regular(path, directory=False):
    # Canonicalize system aliases at the caller's root, never untrusted descendants.
    path = Path(os.path.abspath(path))
    for ancestor in reversed((path,) + tuple(path.parents)):
        info = ancestor.lstat()
        require(not stat.S_ISLNK(info.st_mode)
                and not getattr(info, "st_file_attributes", 0) & 0x400,
                "Links/reparse points are not inputs: " + str(ancestor))
    info = path.stat()
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode),
            "Expected a regular input: " + str(path))
    return info


def stream_hash(stream, size, sink=None):
    result = hashlib.sha256()
    remaining = size
    while remaining:
        block = stream.read(min(65536, remaining))
        require(bool(block), "Truncated input")
        result.update(block)
        if sink is not None:
            sink.write(block)
        remaining -= len(block)
    return result.hexdigest()


def file_record(path, name=None, maximum=MAX_LEAF):
    before = regular(path)
    integer(before.st_size, maximum)
    with Path(path).open("rb") as stream:
        value = stream_hash(stream, before.st_size)
        require(not stream.read(1), "Input grew during capture: " + str(path))
    after = regular(path)
    require((before.st_size, before.st_mtime_ns, before.st_ino) ==
            (after.st_size, after.st_mtime_ns, after.st_ino), "Input changed during capture: " + str(path))
    return {"path": name or Path(path).name, "sha256": value, "size": before.st_size}


def read_bytes(path, maximum=MAX_DOCUMENT):
    integer(regular(path).st_size, maximum)
    with Path(path).open("rb") as stream:
        value = stream.read(maximum + 1)
    require(len(value) <= maximum, "Input exceeds byte budget")
    return value


def parse(data, maximum=MAX_DOCUMENT, budget=262144):
    require(len(data) <= maximum, "JSON exceeds byte budget")
    # Preflight before allocating the JSON object graph (including hostile arrays
    # and nesting). Strings/receipts remain opaque and count as a single value.
    depth = tokens = 0
    quoted = escaped = scalar = False
    for byte in data:
        if quoted:
            if escaped:
                escaped = False
            elif byte == 92:
                escaped = True
            elif byte == 34:
                quoted = False
        elif byte == 34:
            quoted = True
            scalar = False
            tokens += 1
        elif byte in (91, 123):
            depth += 1
            tokens += 1
            scalar = False
        elif byte in (93, 125):
            depth -= 1
            scalar = False
        elif byte in (9, 10, 13, 32, 44, 58):
            scalar = False
        elif not scalar:
            scalar = True
            tokens += 1
        require(0 <= depth <= 16 and tokens <= budget, "JSON exceeds depth/structural budget")
    count = 0

    def pairs(items):
        nonlocal count
        count += len(items)
        require(count <= budget, "JSON exceeds structural budget")
        folded = [key.lower() for key, _ in items]
        require(len(folded) == len(set(folded)), "Duplicate/case-colliding JSON members")
        return dict(items)

    def invalid_constant(value):
        raise ValueError("Nonfinite JSON number: " + value)

    value = json.loads(data.decode("utf-8"), object_pairs_hook=pairs, parse_constant=invalid_constant)
    pending = [(value, 0)]
    while pending:
        item, depth = pending.pop()
        count += 1
        require(depth <= 16 and count <= budget, "JSON exceeds depth/structural budget")
        if isinstance(item, dict):
            pending.extend((child, depth + 1) for child in item.values())
        elif isinstance(item, list):
            pending.extend((child, depth + 1) for child in item)
    return value


def json_file(path, maximum=MAX_DOCUMENT, budget=262144):
    return parse(read_bytes(path, maximum), maximum, budget)


def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=True, allow_nan=False).encode("utf-8")


def file_set(files, core=False):
    require(isinstance(files, list) and 0 < len(files) <= MAX_FILES, "Invalid leaf file count")
    result = {}
    folded = set()
    parents = {}
    total = 0
    for record in files:
        keys(record, "path bytes sha256" if core else "path size sha256")
        name = relative(record["path"], 1024, 32)
        sha(record["sha256"])
        size = record["bytes" if core else "size"]
        integer(size, MAX_LEAF)
        total += size
        require(total <= MAX_LEAF, "Leaf exceeds 2 GiB")
        require(name.lower() not in folded, "Duplicate/case-colliding file path: " + name)
        folded.add(name.lower())
        segments = name.split("/")
        for i in range(1, len(segments)):
            parent = "/".join(segments[:i])
            require(parents.setdefault(parent.lower(), parent) == parent,
                    "Case-colliding directory: " + parent)
        result[name] = {"path": name, "sha256": record["sha256"], "size": size}
    require(list(result) == sorted(result), "Leaf file inventory is not ordinal sorted")
    require(not folded.intersection(parents), "File/directory collision")
    require(METADATA <= result.keys(), "Missing manifest/content index")
    return result


def tree(root):
    regular(root, directory=True)
    result = []
    pending = [(Path(root), 0)]
    discovered = 0
    total = 0
    while pending:
        directory, depth = pending.pop()
        require(depth <= 32, "Leaf discovery depth exceeded")
        with os.scandir(directory) as entries:
            for entry in entries:
                discovered += 1
                require(discovered <= 65536, "Leaf discovery budget exceeded")
                info = entry.stat(follow_symlinks=False)
                require(not stat.S_ISLNK(info.st_mode)
                        and not getattr(info, "st_file_attributes", 0) & 0x400, "Linked leaf input")
                if stat.S_ISDIR(info.st_mode):
                    pending.append((Path(entry.path), depth + 1))
                else:
                    require(len(result) < MAX_FILES, "Leaf file budget exceeded")
                    total += info.st_size
                    require(total <= MAX_LEAF, "Leaf exceeds 2 GiB")
                    result.append(file_record(entry.path, Path(entry.path).relative_to(root).as_posix()))
    return file_set(sorted(result, key=lambda value: value["path"]))


def qualification(data, package_id, project, rid, version):
    value = parse(data)
    keys(value, "schemaVersion inputs rolePlan leaf files")
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1
            and value["leaf"] == "sunder-dev", "Expected Core role-input evidence V1")
    frame = value["inputs"]
    keys(frame, "packageId packageVersion project projectIdentity configuration targetFramework runtimeIdentifier "
         "assemblyName ownedBaseline ownedBaselineSha256 contractLock contractLockSha256 properties items sources toolset externalInputs")
    require(frame["packageId"] == package_id and frame["packageVersion"] == version
            and frame["project"] == project and frame["runtimeIdentifier"] == rid
            and frame["configuration"] == "Release" and frame["targetFramework"] == "net10.0"
            and frame["projectIdentity"] == Path(project).stem
            and frame["assemblyName"] == Path(project).stem, "Mixed/stale package/project/configuration/RID qualification")
    for field in ("ownedBaseline", "contractLock"):
        relative(frame[field], 1024, 32)
        sha(frame[field + "Sha256"])
    plan = value["rolePlan"]
    keys(plan, "inputFingerprint nativePlanSha256 hasApp roles")
    sha(plan["inputFingerprint"])
    sha(plan["nativePlanSha256"])
    require(type(plan["hasApp"]) is bool and isinstance(plan["roles"], list), "Invalid native role plan")
    require([role["role"] for role in plan["roles"]] ==
            (["worker", "app"] if plan["hasApp"] else ["worker"]), "Mixed/incomplete Worker/App roles")
    for role in plan["roles"]:
        keys(role, "role libraries generatedInputs references runtimeReferences receipt")
        require(isinstance(role["receipt"], str) and role["receipt"], "Missing opaque Core role receipt")
    return value, file_set(value["files"], core=True)


def validate_manifest(data, package_id, version, rids, has_app=None):
    manifest = parse(data)
    require(manifest["id"] == package_id and manifest["version"] == version, "Mixed manifest identity")
    targets = manifest["targets"]
    runtime = [target for target in targets if target["role"] == "runtime"]
    apps = [target for target in targets if target["role"] == "app"]
    require(len(targets) == len(runtime) + len(apps)
            and sorted(t["rid"] for t in runtime) == sorted(rids)
            and (not apps or sorted(t["rid"] for t in apps) == sorted(rids)), "Invalid manifest role/RID closure")
    if has_app is not None:
        require(bool(apps) == has_app, "Manifest and qualified App role disagree")
    for target in targets:
        require(target["targetFramework"] == "net10.0", "Invalid native framework")
    for target in runtime:
        require(target["kind"] == "worker" and re.fullmatch(r"lib/[A-Za-z0-9._-]+\.dll", target["entryPoint"])
                and target["executionRuntime"] == {"id": "sunder.runtime.dotnet", "versionRange": ">=10.0.0 <11.0.0",
                                                   "moduleAbi": "sunder.dotnet-worker-module.v1"},
                "Expected a framework-dependent Worker V1 module")
    return manifest


def projection(files, role, rid):
    """Core's package-shared / role-shared / exact-target logical projection."""
    prefixes = ("payload/shared/", "payload/" + role + "/shared/", "payload/" + role + "/" + rid + "/")
    result = {}
    folded = set()
    for name, record in files.items():
        for prefix in prefixes:
            if name.startswith(prefix):
                logical = name[len(prefix):]
                require(logical and logical.lower() not in folded, "Ambiguous logical payload projection: " + logical)
                result[logical] = record
                folded.add(logical.lower())
    return result


def verify_archive(path, packages, version):
    """Match every native file through Core's shared/role/RID storage projection."""
    integer(regular(path).st_size, MAX_LEAF, 1)
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        require(0 < len(entries) <= MAX_FILES * len(RIDS), "Archive entry budget exceeded")
        names = {}
        total = 0
        for entry in entries:
            relative(entry.filename)
            require(entry.filename.lower() not in names and not entry.is_dir()
                    and not stat.S_ISLNK(entry.external_attr >> 16), "Duplicate/linked archive entry")
            integer(entry.file_size, MAX_LEAF)
            total += entry.file_size
            require(total <= MAX_LEAF * len(RIDS), "Archive byte budget exceeded")
            names[entry.filename.lower()] = entry.filename
        require(METADATA <= set(names.values()), "Archive is missing metadata")
        manifest_entry = archive.getinfo("manifest/sunder-package.json")
        integer(manifest_entry.file_size, MAX_DOCUMENT)
        manifest = validate_manifest(archive.read(manifest_entry), packages[0][1]["packageId"], version,
                                     [rid for rid, _ in packages])
        actual = {}
        for entry in entries:
            with archive.open(entry) as stream:
                actual[entry.filename] = {"path": entry.filename, "size": entry.file_size, "sha256": stream_hash(stream, entry.file_size)}
        used = set(METADATA)
        for rid, package in packages:
            roles = ["runtime", "app"] if package["leaf"]["hasApp"] else ["runtime"]
            require(sorted(t["role"] for t in manifest["targets"] if t["rid"] == rid) == sorted(roles), "Archive changed native role closure")
            native = {record["path"]: record for record in package["leaf"]["files"]}
            native_used = set(METADATA)
            for role in roles:
                before = projection(native, role, rid)
                after = projection(actual, role, rid)
                values = lambda files: {name: (record["size"], record["sha256"]) for name, record in files.items()}
                require(values(before) == values(after), "Archive changed tested native bytes: " + role + "/" + rid)
                native_used.update(record["path"] for record in before.values())
                used.update(record["path"] for record in after.values())
            # Core composes only these two documents. Retain any other package-wide
            # non-payload file exactly; reject files orphaned from every role/RID.
            for name, record in native.items():
                if name not in METADATA and not name.startswith("payload/"):
                    require(actual.get(name) == record, "Archive changed package-wide bytes: " + name)
                    used.add(name)
                    native_used.add(name)
            require(native_used == set(native), "Orphaned native payload file")
        require(used == set(actual), "Archive contains untested extra files")
        return manifest


def write_tar(path, sources):
    # A deliberately small tar profile: ordinary files and bounded POSIX path
    # extension records only. No links, sparse files, directories or host metadata.
    with Path(path).open("wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w|", format=tarfile.PAX_FORMAT) as archive:
            for name, source in sorted(sources.items()):
                relative(name)
                info = regular(source)
                header = tarfile.TarInfo(name)
                header.size = info.st_size
                header.mode = 0o755 if info.st_mode & 0o111 else 0o644
                with Path(source).open("rb") as stream:
                    archive.addfile(header, stream)


def scan_tar(path, expected, consume, destination=None, capture_names=()):
    """Full preflight pass, then optional private extraction pass; never extractall.

    Parse fixed headers before asking for payload bytes. tarfile's automatic PAX/
    GNU extension processing is intentionally not used on transferred inputs.
    """
    integer(regular(path).st_size, MAX_RUN, 1)
    seen = set()
    extended_path = None
    with gzip.open(path, "rb") as stream:
        while True:
            block = stream.read(512)
            require(len(block) == 512, "Truncated tar header")
            if block == bytes(512):
                require(extended_path is None and seen == set(expected), "Missing native bundle entries")
                # At most the conventional 10 KiB tar blocking, no appended archive/data.
                trailer = stream.read(10241)
                require(512 <= len(trailer) <= 10240 and len(trailer) % 512 == 0 and not any(trailer),
                        "Invalid/oversized tar trailer")
                break
            header = tarfile.TarInfo.frombuf(block, "ascii", "strict")
            require(header.size >= 0 and not header.linkname, "Linked/invalid tar entry")
            if header.type == tarfile.XHDTYPE:
                require(extended_path is None and 0 < header.size <= 4096, "Invalid/oversized PAX header")
                data = stream.read(header.size)
                require(len(data) == header.size, "Truncated PAX header")
                # The writer needs only a single path record for names >100 bytes.
                match = re.fullmatch(rb"([1-9][0-9]*) path=([^\n]+)\n", data)
                require(match is not None and int(match[1]) == len(data), "Unsupported PAX metadata")
                extended_path = relative(match[2].decode("ascii"))
                padding = stream.read((-header.size) % 512)
                require(len(padding) == (-header.size) % 512 and not any(padding), "Invalid PAX padding")
                continue
            require(header.type in (tarfile.REGTYPE, tarfile.AREGTYPE), "Only regular tar files are permitted")
            name = relative(extended_path or header.name)
            extended_path = None
            require(name in expected and name not in seen, "Extra/duplicate native bundle entry: " + name)
            record = expected[name]
            require(header.size == record["size"], "Native entry size differs from evidence: " + name)
            seen.add(name)
            # Only qualification/manifest/metric records need an in-memory body.
            capture = name in capture_names
            if capture:
                integer(header.size, MAX_DOCUMENT)
                data = stream.read(header.size)
                require(len(data) == header.size and digest(data) == record["sha256"], "Native metadata hash mismatch: " + name)
                consume(name, data)
            else:
                data = None
            if destination is not None:
                output = Path(destination) / name
                output.parent.mkdir(parents=True, exist_ok=True)
                regular(output.parent, directory=True)
                with output.open("xb") as sink:
                    if data is not None:
                        sink.write(data)
                    else:
                        require(stream_hash(stream, header.size, sink) == record["sha256"], "Native payload hash mismatch: " + name)
                output.chmod(0o755 if header.mode & 0o111 else 0o644)
            elif data is None:
                require(stream_hash(stream, header.size) == record["sha256"], "Native payload hash mismatch: " + name)
            padding = stream.read((-header.size) % 512)
            require(len(padding) == (-header.size) % 512 and not any(padding), "Invalid tar padding")
