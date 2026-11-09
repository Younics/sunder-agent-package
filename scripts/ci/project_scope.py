"""Evaluate the real project dependencies needed by a component's test projects."""
import json
from pathlib import Path
import subprocess

import release_inputs as inputs


def projects(source, selected, inventory, mode):
    """Restore/build roots are explicit; project references are followed by MSBuild."""
    rows = inputs.inventory(source)["packages"]
    if selected is None:
        return ["Sunder.AgentPackage.slnx"]
    roots = {row["projectPath"] for row in rows if row["packageId"] in selected}
    roots.update(row["path"] for row in inventory["projects"] if mode in row["modes"])
    return sorted(roots)


def packages(source, roots, properties, env):
    """Hydrate only canonical packages reachable through evaluated references.

    This runs no targets, restores or builds. Core's own graph is restored by the
    ordinary project restore; only Agent package selections need hydration here.
    """
    import agent_ci
    source = Path(source).resolve()
    rows = inputs.inventory(source)["packages"]
    if roots == ["Sunder.AgentPackage.slnx"]:
        return sorted(row["packageId"] for row in rows)
    pending = [source / path for path in roots]
    seen = set()
    while pending:
        project = pending.pop().resolve(strict=True)
        if project in seen or not project.is_relative_to(source):
            continue
        inputs.source_file(source, project.relative_to(source).as_posix())
        seen.add(project)
        result = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-getItem:ProjectReference",
                                 *agent_ci.arguments(properties)], cwd=source, env=env, check=True,
                                text=True, stdout=subprocess.PIPE)
        references = json.loads(result.stdout)["Items"]["ProjectReference"]
        for reference in references:
            path = Path(reference["FullPath"])
            inputs.require(path.is_absolute(), "Project reference must have an evaluated absolute path")
            pending.append(path)
    return sorted(row["packageId"] for row in rows if (source / row["projectPath"]).resolve() in seen)
