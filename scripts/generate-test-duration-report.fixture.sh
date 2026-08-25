#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-agent-test-evidence.XXXXXX")"
trap 'rm -rf "$temporary_root"' EXIT
unset SUNDER_AGENT_PACKAGE_ARCHIVE_DIR SUNDER_REQUIRE_TEST_PREREQUISITES

fixture_root="$temporary_root/repository"
tests_root="$fixture_root/tests/Fixture.Tests"
replacement_tests_root="$fixture_root/tests/Replacement.Tests"
results_root="$fixture_root/results/full"
mkdir -p "$tests_root" "$replacement_tests_root" "$results_root"

cat > "$tests_root/Fixture.Tests.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk" />
EOF
cat > "$tests_root/EvidenceTests.cs" <<'EOF'
namespace Fixture.Tests;

public sealed class EvidenceTests
{
    [PackageArtifactsFact]
    public void ExpectedArtifactSkip() { }

    [Fact]
    public void OrdinaryTest() { }

    [PlatformTheory(TestPlatform.Linux, "The seeded theory is Linux-specific.")]
    [InlineData(1)]
    [InlineData(2)]
    public void LinuxTheory(int value) { }
}
EOF
cat > "$replacement_tests_root/Replacement.Tests.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk" />
EOF
cat > "$replacement_tests_root/ReplacementTests.cs" <<'EOF'
namespace Replacement.Tests;

public sealed class ReplacementTests
{
    [Fact]
    public void OrdinaryTest() { }
}
EOF
cat > "$fixture_root/inventory.json" <<'EOF'
{
  "schemaVersion": 2,
  "projects": [
    {
      "path": "tests/Fixture.Tests/Fixture.Tests.csproj",
      "owner": "fixture-owner",
      "modes": ["source"],
      "lanes": [],
      "sourceOnlyTests": []
    },
    {
      "path": "tests/Replacement.Tests/Replacement.Tests.csproj",
      "owner": "replacement-owner",
      "modes": ["source"],
      "lanes": [],
      "sourceOnlyTests": []
    }
  ]
}
EOF

write_trx() {
  local path="$1"
  local expected_outcome="$2"
  local ordinary_outcome="$3"
  local expected_reason="$4"
  local ordinary_reason="$5"
  local expected_storage="$6"
  local theory_shape="$7"
  local theory_outcome="$8"
  local theory_reason="$9"
  local theory_results
  local theory_definitions

  if [[ "$theory_shape" == expanded ]]; then
    theory_results=$(cat <<EOF
    <UnitTestResult testId="linux-1" testName="Fixture.Tests.EvidenceTests.LinuxTheory(value: 1)" outcome="$theory_outcome" duration="00:00:00.001">
      <Output><ErrorInfo><Message>$theory_reason</Message></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testId="linux-2" testName="Fixture.Tests.EvidenceTests.LinuxTheory(value: 2)" outcome="$theory_outcome" duration="00:00:00.001">
      <Output><ErrorInfo><Message>$theory_reason</Message></ErrorInfo></Output>
    </UnitTestResult>
EOF
)
    theory_definitions=$(cat <<'EOF'
    <UnitTest id="linux-1" name="Fixture.Tests.EvidenceTests.LinuxTheory(value: 1)" storage="fixture.tests.dll">
      <TestMethod className="Fixture.Tests.EvidenceTests" name="LinuxTheory" />
    </UnitTest>
    <UnitTest id="linux-2" name="Fixture.Tests.EvidenceTests.LinuxTheory(value: 2)" storage="fixture.tests.dll">
      <TestMethod className="Fixture.Tests.EvidenceTests" name="LinuxTheory" />
    </UnitTest>
EOF
)
  elif [[ "$theory_shape" == collapsed ]]; then
    theory_results=$(cat <<EOF
    <UnitTestResult testId="linux" testName="Fixture.Tests.EvidenceTests.LinuxTheory" outcome="$theory_outcome" duration="00:00:00.001">
      <Output><ErrorInfo><Message>$theory_reason</Message></ErrorInfo></Output>
    </UnitTestResult>
EOF
)
    theory_definitions=$(cat <<'EOF'
    <UnitTest id="linux" name="Fixture.Tests.EvidenceTests.LinuxTheory" storage="fixture.tests.dll">
      <TestMethod className="Fixture.Tests.EvidenceTests" name="LinuxTheory" />
    </UnitTest>
EOF
)
  else
    printf 'Unknown theory fixture shape: %s\n' "$theory_shape" >&2
    exit 2
  fi

  cat > "$path" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" name="fixture">
  <Times start="2026-08-25T00:00:00+00:00" finish="2026-08-25T00:00:01+00:00" />
  <Results>
    <UnitTestResult testId="expected" testName="Fixture.Tests.EvidenceTests.ExpectedArtifactSkip" outcome="$expected_outcome" duration="00:00:00.001">
      <Output><ErrorInfo><Message>$expected_reason</Message></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testId="ordinary" testName="Fixture.Tests.EvidenceTests.OrdinaryTest" outcome="$ordinary_outcome" duration="00:00:00.001">
      <Output><ErrorInfo><Message>$ordinary_reason</Message></ErrorInfo></Output>
    </UnitTestResult>
$theory_results
    <UnitTestResult testId="replacement" testName="Replacement.Tests.ReplacementTests.OrdinaryTest" outcome="Passed" duration="00:00:00.001" />
  </Results>
  <TestDefinitions>
    <UnitTest id="expected" name="Fixture.Tests.EvidenceTests.ExpectedArtifactSkip" storage="$expected_storage">
      <TestMethod className="Fixture.Tests.EvidenceTests" name="ExpectedArtifactSkip" />
    </UnitTest>
    <UnitTest id="ordinary" name="Fixture.Tests.EvidenceTests.OrdinaryTest" storage="fixture.tests.dll">
      <TestMethod className="Fixture.Tests.EvidenceTests" name="OrdinaryTest" />
    </UnitTest>
$theory_definitions
    <UnitTest id="replacement" name="Replacement.Tests.ReplacementTests.OrdinaryTest" storage="replacement.tests.dll">
      <TestMethod className="Replacement.Tests.ReplacementTests" name="OrdinaryTest" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
EOF
}

expected_reason="Set SUNDER_AGENT_PACKAGE_ARCHIVE_DIR to the complete package archive directory."
linux_reason="The seeded theory is Linux-specific."
trx="$results_root/evidence.trx"
expectations="$fixture_root/tests/test-evidence-expectations.json"
write_trx "$trx" NotExecuted Passed "$expected_reason" "" fixture.tests.dll expanded Passed ""

python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform linux \
  --write-expectations "$expectations" >/dev/null
python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform linux \
  --expectations "$expectations" >/dev/null

# An unsupported platform intentionally collapses a theory to one skipped declaration.
write_trx "$trx" NotExecuted Passed "$expected_reason" "" fixture.tests.dll collapsed NotExecuted "$linux_reason"
python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform windows \
  --expectations "$expectations" > "$fixture_root/windows-report.json"
jq -e '
  .selection.platform == "windows"
  and .canonicalIdentityExpectation == {platform: "linux", enforced: false}
  and .projectSuiteEvidence == ["fixture.tests", "replacement.tests"]
  and .expectedSkipEvidence == .skippedTests
  and .evidenceViolations == []
' "$fixture_root/windows-report.json" >/dev/null

# Collapsing rows on the owning platform must still fail canonical identity enforcement.
write_trx "$trx" NotExecuted Passed "$expected_reason" "" fixture.tests.dll collapsed Passed ""
if python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform linux \
  --expectations "$expectations" > "$fixture_root/row-collapse-report.json"; then
  echo "Canonical Linux evidence accepted collapsed theory rows." >&2
  exit 1
fi
jq -e '.evidenceViolations | any(contains("canonical linux test identity hashes"))' \
  "$fixture_root/row-collapse-report.json" >/dev/null

write_trx "$trx" Passed NotExecuted "" "$expected_reason" fixture.tests.dll expanded Passed ""
if python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform linux \
  --expectations "$expectations" > "$fixture_root/identity-swap-report.json"; then
  echo "An unexpected skip traded places with the approved skip." >&2
  exit 1
fi
jq -e '.evidenceViolations | any(contains("exact identity, reason, or owner"))' \
  "$fixture_root/identity-swap-report.json" >/dev/null

write_trx "$trx" NotExecuted Passed "wrong prerequisite reason" "" fixture.tests.dll expanded Passed ""
if python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform linux \
  --expectations "$expectations" > "$fixture_root/reason-report.json"; then
  echo "A changed skip reason was accepted." >&2
  exit 1
fi
jq -e '.evidenceViolations | any(contains("exact identity, reason, or owner"))' \
  "$fixture_root/reason-report.json" >/dev/null

# Change only the observed test's project so its inventory-derived owner cannot match policy.
write_trx "$trx" NotExecuted Passed "$expected_reason" "" replacement.tests.dll expanded Passed ""
if python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform linux \
  --expectations "$expectations" > "$fixture_root/owner-report.json"; then
  echo "A changed observed skip owner was accepted." >&2
  exit 1
fi
jq -e '.evidenceViolations | any(contains("exact identity, reason, or owner"))' \
  "$fixture_root/owner-report.json" >/dev/null

# Windows must reject both unexpected skips and failed rows without Linux row hashes.
write_trx "$trx" NotExecuted NotExecuted "$expected_reason" "unexpected skip" fixture.tests.dll collapsed NotExecuted "$linux_reason"
if python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform windows \
  --expectations "$expectations" > "$fixture_root/windows-unexpected-skip-report.json"; then
  echo "Windows evidence accepted an unexpected skip." >&2
  exit 1
fi
jq -e '.evidenceViolations | any(contains("exact identity, reason, or owner"))' \
  "$fixture_root/windows-unexpected-skip-report.json" >/dev/null

write_trx "$trx" NotExecuted Failed "$expected_reason" "failed" fixture.tests.dll collapsed NotExecuted "$linux_reason"
if python3 "$repository_root/scripts/generate-test-duration-report.py" "$fixture_root/results" \
  --inventory "$fixture_root/inventory.json" --mode source --platform windows \
  --expectations "$expectations" > "$fixture_root/windows-failure-report.json"; then
  echo "Windows evidence accepted a failed row." >&2
  exit 1
fi
jq -e '.evidenceViolations | any(contains("zero primary-run failures"))' \
  "$fixture_root/windows-failure-report.json" >/dev/null

python3 - "$repository_root/scripts/generate-test-duration-report.py" <<'PY'
import importlib.util
import os
import pathlib
import sys

script = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("test_evidence", script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
policy = {
    "project": "fixture.tests",
    "owner": "fixture-owner",
    "fullyQualifiedName": "Fixture.Tests.EvidenceTests.FileSystemTest",
    "attribute": "FileSystemFact",
    "platform": None,
    "capability": "FileSymbolicLinks",
    "reason": "The test requires symbolic links.",
}
module.filesystem_capability_available = lambda capability: False
expected = module.expected_skip(policy)
assert expected["reason"] == (
    "The test requires symbolic links. "
    "Required filesystem capability 'FileSymbolicLinks' is unavailable."
)
os.environ["SUNDER_REQUIRE_TEST_PREREQUISITES"] = "1"
assert module.expected_skip(policy) is None
PY
