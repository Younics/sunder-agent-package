"""The unfinished public 2.0.0 cutover does not repeat one Windows test group."""
import hashlib

import agent_release as r

OWNER = "younics.ai.agent.execution.local"
SUITE = "Sunder.Package.Agent.Execution.Local.Tests"
CLASS = f"{SUITE}.WindowsSecureMutationTests"
PROJECT = f"tests/{SUITE}/{SUITE}.csproj"
RIDS = {"win-x64", "win-arm64"}
DISPOSITION = "cutoverWindowsCasesNotRun"
COUNT = 34

# Reviewed cutover scope from 906e2b9. These are source objects, not old test
# results. Validate against the current checkout, with no history fetch or lookup.
LOCKS = "d5e75d939ece1ce2795600d1f174a84111f3af9c8c70c14b13c6b2288d5f39cb"
SCOPE_SHA256 = "3e7d827a46b46c23ef161bb7bc41825c5021bfaaf8ca5a1967863f8215445ec5"
# The only additional reviewed tree changes BuilderToolchainService{,.Updates}.cs.
# Both Windows Local compile closures were byte-identical across the two trees.
MAKER_UPDATER_SCOPE_SHA256 = "acd7e6a50d858bde23109841e86988770a5863b4bc3ae1006500e23322ed25ba"
SCOPE = (
    "src", f"tests/{SUITE}", "tests/Shared", "tests/Sunder.Package.Agent.TestSupport",
    "tests/Sunder.Package.Agent.TestSupport.Avalonia", "tests/RegressionTestHarness.props",
    "tests/PackageContextFixtures.props", "scripts/test/required-platform-cases.json", ".gitattributes",
    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config",
    "global.json", "Sunder.Agent.Worker.props",
)


def applies(plan):
    """Recognize only the reviewed first publication, never a partial or later release."""
    return (plan["schemaVersion"] == 2 and plan["registryOrigin"] == "https://hub.sunderapp.io"
            and len(plan["packages"]) == len(r.FAMILY_PACKAGE_IDS)
            and {row["packageId"] for row in plan["packages"]} == set(r.FAMILY_PACKAGE_IDS)
            and all(row["selected"] and row["version"] == "2.0.0" for row in plan["packages"]))


def disposition(source, plan_path, rid, packages, mode):
    import agent_selection as selection

    head = r.git(source, "rev-parse", "HEAD").decode("ascii").strip()
    plan = selection.load(plan_path, source, head)
    if plan.get("schemaVersion") == 3:
        r.require(plan["releasePackage"] == OWNER and plan["cutoverPlan"] is not None, "Missing initial Windows Local selection")
        plan = plan["cutoverPlan"]
    r.require(applies(plan) and rid in RIDS and packages == [OWNER] and mode == "source",
              "The 2.0.0 cutover disposition only applies to Windows Local behavior on the initial release")
    objects = r.git(source, "rev-parse", *(f"HEAD:{name}" for name in SCOPE)).splitlines()
    r.require(plan["lockSetSha256"] == LOCKS and len(objects) == len(SCOPE)
              and hashlib.sha256(b"".join(objects)).hexdigest() in (SCOPE_SHA256, MAKER_UPDATER_SCOPE_SHA256)
              and not r.git(source, "status", "--porcelain", "--untracked-files=all", "--", *SCOPE),
              "The reviewed 2.0.0 Windows scope changed; stop for review rather than omitting or repeating cases")
    return dict(className=CLASS, rid=rid, planSha256=r.file_sha(plan_path), caseCount=COUNT, status="not-selected")
