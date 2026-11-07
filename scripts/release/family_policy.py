"""Release expectations come from reviewed source policy, never produced artifacts."""
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
REVIEWED = json.loads((ROOT / "compatibility/agent-current-inventory.json").read_text())["packages"]
FAMILY_PROJECTS = {row["packageId"]: row["projectPath"] for row in REVIEWED}
if not FAMILY_PROJECTS or len(FAMILY_PROJECTS) != len(REVIEWED) or len(set(FAMILY_PROJECTS.values())) != len(REVIEWED):
    raise ValueError("Reviewed package/project inventory must be nonempty and unique")
FAMILY_PACKAGE_IDS = tuple(FAMILY_PROJECTS)


def target_framework(project=None, root=ROOT):
    paths = [Path(root) / project] if project else []
    paths.append(Path(root) / "Directory.Build.props")
    for path in paths:
        if not path.is_file(): continue
        values = ET.parse(path).findall("./PropertyGroup/TargetFramework")
        if not values: continue
        if len(values) != 1 or not re.fullmatch(r"net[1-9][0-9]*\.0", values[0].text or ""):
            raise ValueError("Native release requires one declared framework: " + str(path))
        return values[0].text
    raise ValueError("Missing native target framework")


def runtime_requirement(framework):
    major = int(re.fullmatch(r"net([1-9][0-9]*)\.0", framework)[1])
    return {"id": "sunder.runtime.dotnet", "versionRange": f">={major}.0.0 <{major + 1}.0.0",
            "moduleAbi": "sunder.dotnet-worker-module.v1"}


if __name__ == "__main__":
    from ci_platforms import RIDS
    frameworks = {key: target_framework(project) for key, project in FAMILY_PROJECTS.items()}
    print(json.dumps(dict(packageIds=sorted(FAMILY_PACKAGE_IDS), rids=RIDS, targetFrameworks=frameworks,
                         runtimeRequirements={key: runtime_requirement(value) for key, value in frameworks.items()})))
