"""One immutable Core tool handoff per platform, shared by component jobs."""
import os
from pathlib import Path
import platform
import shutil
import subprocess

import release_inputs as inputs

MANIFEST = "core-tools.json"
DRIVER = "Sunder.Package.Build.Tests.dll"


def files(root):
    result = []
    for path in sorted(root.rglob("*")):
        inputs.require(not path.is_symlink(), "Linked Core tool input")
        if path.is_dir() or path == root / MANIFEST:
            continue
        inputs.regular(path)
        result.append(dict(path=path.relative_to(root).as_posix(), size=path.stat().st_size, sha256=inputs.file_sha(path)))
    return result


def verify(core, root):
    import agent_ci
    root = inputs.directory(root)
    value = inputs.load(root / MANIFEST)
    inputs.closed(value, "schemaVersion coreCommit platform architecture files")
    inputs.require(value["schemaVersion"] == 1 and value["coreCommit"] == agent_ci.head(core), "Core tool source differs")
    inputs.require(value["platform"] == os.sys.platform and value["architecture"] == platform.machine().lower(),
                   "Core tools were prepared for another platform")
    inputs.require(value["files"] and value["files"] == files(root), "Core tool bytes changed or handoff is incomplete")
    for name in (*inputs.TOOL_FILES, DRIVER):
        inputs.regular(root / name)
    return root


def prepare(core, output):
    import agent_ci
    core = Path(core).resolve(strict=True)
    output = Path(output).resolve()
    inputs.require(not output.is_relative_to(core), "Keep Core tools outside their source checkout")
    project = core / "languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj"
    inputs.require(not output.exists(), "Core tool output must be a new directory")
    properties = ["-p:Configuration=Release", "-p:ContinuousIntegrationBuild=true", "-p:GeneratePackageOnBuild=false"]
    env = inputs.isolated_env(output.parent / "core-tool-home", output.parent / "core-tool-home/contracts/v1/sha256")
    subprocess.run(["dotnet", "restore", str(project), "--locked-mode", *properties], env=env, check=True)
    subprocess.run(["dotnet", "build", str(project), "--no-restore", *properties], env=env, check=True)
    shutil.copytree(agent_ci.output_directory(project, env), output)
    value = dict(schemaVersion=1, coreCommit=agent_ci.head(core), platform=os.sys.platform,
                 architecture=platform.machine().lower(), files=files(output))
    (output / MANIFEST).write_bytes(inputs.canonical(value) + b"\n")
    verify(core, output)
    return output
