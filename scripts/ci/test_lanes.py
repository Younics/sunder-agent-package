"""Agent CI test selection and evidence, shared by workflows and local commands."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
POLICY = json.loads((ROOT / "scripts/test/ci-scenarios.json").read_text())


def run(*args, cwd=ROOT, env=None):
    subprocess.run([str(arg) for arg in args], cwd=cwd, env=env, check=True)


def discovered_tests(output):
    marker = "The following Tests are available:"
    if marker not in output:
        raise ValueError("Test adapter produced no discovery header; see discovery log")
    return [line[4:] for line in output.split(marker, 1)[1].splitlines() if line.startswith("    ")]


def run_project(state_path, source, project, common, filter_value, output):
    import agent_ci
    name = Path(project).stem
    base = [*common[:2], project, *common[3:], "--filter", filter_value]
    log = output / (name + ".discovery.log")
    command = [*base, "--list-tests", "--verbosity", "quiet"]
    status = agent_ci.execute(state_path, command, log=log)
    if status: raise subprocess.CalledProcessError(status, command)
    cases = discovered_tests(log.read_text(encoding="utf-8"))
    _, environment = agent_ci.context(state_path)
    (output / (name + ".discovery.json")).write_text(json.dumps({
        "schemaVersion": 1, "project": name, "filter": filter_value,
        "report": name + ".trx", "tests": cases,
        "prerequisites": {key: environment.get(key, "") for key in (
            "SUNDER_REQUIRE_TEST_PREREQUISITES", "SUNDER_DOCKER_INTEGRATION",
            "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR", "SUNDER_AGENT_NATIVE_ARCHIVE_DIR")},
    }, indent=2) + "\n", encoding="utf-8")
    if not cases:
        return 0
    # The all-family case owns bounded child builds and the existing CI job budget;
    # one ordinary-case watchdog must not truncate its fifteen-family production.
    diagnostics = ["--blame"] if any(POLICY["artifactProducer"] in case for case in cases) else ["--blame-hang", "--blame-hang-timeout", "15m"]
    command = [*base, "--logger", f"trx;LogFileName={name}.trx", "--results-directory", str(output),
               "--diag", str(output / (name + ".vstest.log")), *diagnostics]
    status = agent_ci.execute(state_path, command, log=output / (name + ".run.log"))
    if status: raise subprocess.CalledProcessError(status, command)
    return len(cases)


def tests(state_path, lane):
    import agent_ci
    state, _ = agent_ci.context(state_path)
    source = Path(state["source"])
    common = ["dotnet", "test", "Sunder.AgentPackage.slnx", "-c", "Release", "--no-build", "--no-restore"]
    inventory = json.loads((source / "artifacts/reports/test-inventory.json").read_text())
    mode = "package" if lane == "package" else "source"
    projects = [row["path"] for row in inventory["projects"] if mode in row["modes"]
                and (lane != "package" or "PackageCompatibility" in row.get("lanes", []))]
    artifact = POLICY["artifactProducer"]
    directory = source / "artifacts/test-results" / ("artifact" if lane == "artifact" else "required" if lane == "required-linux" else "full")
    if lane == "source":
        projects = [row["path"] for row in inventory["projects"] if mode in row["modes"] and not row.get("requiredOnly")]
        filter_value = "FullyQualifiedName!~" + artifact
        if sys.platform == "linux":
            filter_value += "".join("&FullyQualifiedName!~" + name for name in POLICY["requiredLinux"])
    elif lane == "artifact":
        projects = ["tests/Sunder.Package.Agent.Integration.Tests/Sunder.Package.Agent.Integration.Tests.csproj"]
        filter_value = "FullyQualifiedName~" + artifact
    elif lane == "package": filter_value = "Lane=PackageCompatibility&Category!=Artifact"
    elif lane == "required-linux": filter_value = "|".join("FullyQualifiedName~" + name for name in POLICY["requiredLinux"])
    elif lane == "stress": filter_value = POLICY["stress"]["filter"]
    else: raise ValueError("Unknown Agent test lane")
    repetitions = POLICY["stress"]["repetitions"] if lane == "stress" else 1
    if type(repetitions) is not int or repetitions < 1: raise ValueError("Empty stress selection")
    for attempt in range(repetitions):
        output = source / "artifacts/stress" / str(attempt + 1) if lane == "stress" else directory
        if output.exists(): shutil.rmtree(output)
        output.mkdir(parents=True)
        count = sum(run_project(state_path, source, project, common, filter_value, output) for project in projects)
        if not count: raise ValueError(f"{lane} selected zero tests")
    if lane in ("required-linux", "stress"):
        output = source / "artifacts/stress" if lane == "stress" else directory
        run(sys.executable, "-B", source / "scripts/test/generate-test-duration-report.py", output, "--expected-skips", "0", cwd=source)
        if lane == "required-linux":
            observed = set()
            for path in output.rglob("*.trx"):
                document = ET.parse(path)
                passed = {row.get("testId") for row in document.findall(".//{*}UnitTestResult") if row.get("outcome") == "Passed"}
                for definition in document.findall(".//{*}UnitTest"):
                    if definition.get("id") in passed:
                        observed.add(definition.find("./{*}TestMethod").get("name"))
            if not set(POLICY["requiredLinux"]) <= observed:
                raise ValueError("Required Linux test identity missing; passing counts alone are insufficient")


def validate(state_path, mode, stress=False):
    import agent_ci
    state, _ = agent_ci.context(state_path)
    source = Path(state["source"])
    previous = dict(os.environ)
    platform = "windows" if os.name == "nt" else "macos" if sys.platform == "darwin" else "linux"
    try:
        if mode == "source":
            os.environ.update(SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION="1",
                SUNDER_AGENT_SOURCE_ARTIFACT_EVIDENCE=str(source / "artifacts/source-family"),
                SUNDER_AGENT_SOURCE_ARTIFACT_COMMAND_EVIDENCE=str(source / "artifacts/source-commands"))
            for directory in ("test-results", "stress", "source-family", "source-commands"):
                path = source / "artifacts" / directory
                if path.exists(): shutil.rmtree(path)
        else:
            path = source / "artifacts/test-results"
            if path.exists(): shutil.rmtree(path)
        try:
            tests(state_path, mode)
            if mode == "source":
                tests(state_path, "artifact")
                if platform == "linux":
                    os.environ.update(SUNDER_DOCKER_INTEGRATION="1", SUNDER_REQUIRE_TEST_PREREQUISITES="1")
                    tests(state_path, "required-linux")
                    if stress: tests(state_path, "stress")
        finally:
            report(mode, platform)
    finally:
        os.environ.clear()
        os.environ.update(previous)


def report(mode, platform):
    command = [sys.executable, "-B", ROOT / "scripts/test/generate-test-duration-report.py", "artifacts/test-results",
               "--inventory", "artifacts/reports/test-inventory.json", "--mode", mode, "--platform", platform,
                "--verify"]
    if mode == "package": command += ["--lane", "PackageCompatibility", "--exclude-category", "Artifact"]
    with (ROOT / "artifacts/reports/results.json").open("w", encoding="utf-8") as stream:
        subprocess.run([str(arg) for arg in command], cwd=ROOT, stdout=stream, check=True)


def retain_verifier():
    import agent_ci
    project = ROOT / "tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj"
    output = agent_ci.output_directory(project, os.environ)
    destination = ROOT / "artifacts/package-test-binaries"
    if destination.exists(): shutil.rmtree(destination)
    shutil.copytree(output, destination)


def family(core, directory):
    core, directory = Path(core).resolve(strict=True), Path(directory).resolve(strict=True)
    env = {**os.environ, "SUNDER_REQUIRE_AGENT_PACKAGE_RELEASE": "1", "SUNDER_REQUIRE_AGENT_UI_SNAPSHOT": "1",
           "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR": str(directory), "SUNDER_AGENT_UI_SNAPSHOT_DIR": str(ROOT / "artifacts/agent-ui-snapshot")}
    cli = core / "languages/csharp/dotnet/host/Sunder.Cli/Sunder.Cli.csproj"
    target = subprocess.check_output(["dotnet", "msbuild", str(cli), "-p:Configuration=Release", "-getProperty:TargetPath"], cwd=core, text=True).strip()
    archives = list(directory.glob("*.sunderpkg"))
    if not archives: raise ValueError("No family archives")
    for archive in archives: run("dotnet", target, "dev", "package", "validate", archive, env=env)
    results = ROOT / "artifacts/family-results/core"
    for project, required in POLICY["coreFamily"].items():
        path = core / f"languages/csharp/dotnet/tests/{project}/{project}.csproj"
        run("dotnet", "test", path, "-c", "Release", "--no-build", "--no-restore", "--filter", "|".join("FullyQualifiedName=" + name for name in required),
            "--logger", f"trx;LogFileName={project}.trx", "--results-directory", results, env=env)
    required = [arg for names in POLICY["coreFamily"].values() for name in names for arg in ("--require-test", name)]
    run("node", core / "scripts/test-oracles/summarize-core-test-results.mjs", results, "--allow-partial", *required)
