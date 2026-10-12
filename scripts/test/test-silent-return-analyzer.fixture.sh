#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_references="$(dotnet msbuild "$repository_root/tests/Sunder.Package.Agent.Provider.OpenAI.Tests/Sunder.Package.Agent.Provider.OpenAI.Tests.csproj" \
  -p:ExcludeRestorePackageImports=true -getProperty:IsTestProject -getItem:ProjectReference)"
if [[ "$project_references" != *'"IsTestProject": "true"'* || "$project_references" != *'Sunder.Package.Agent.TestAnalyzers.csproj'* || "$project_references" != *'"OutputItemType": "Analyzer"'* ]]; then
  printf 'Test analyzer reference is absent before the first NuGet restore.\n' >&2
  exit 1
fi
if [[ "$(dotnet msbuild "$repository_root/tests/Sunder.Package.Agent.TestAnalyzers/Sunder.Package.Agent.TestAnalyzers.csproj" \
  -p:ExcludeRestorePackageImports=true -getProperty:IsTestProject)" == 'true' ]]; then
  printf 'The analyzer must not be classified as a test project or reference itself.\n' >&2
  exit 1
fi
fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/sunder-silent-return.XXXXXX")"
trap 'rm -rf "$fixture_root"' EXIT

cat > "$fixture_root/SilentReturnFixture.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repository_root/tests/Sunder.Package.Agent.TestAnalyzers/Sunder.Package.Agent.TestAnalyzers.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
EOF

cat > "$fixture_root/SilentReturnTest.cs" <<'EOF'
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    internal sealed class FactAttribute : System.Attribute;
}

internal sealed class SilentReturnTest
{
    [Xunit.Fact]
    public void UnsupportedPrerequisiteAppearsToPass()
    {
        if (!System.OperatingSystem.IsLinux())
        {
            return;
        }
    }

    [Xunit.Fact]
    public System.Threading.Tasks.Task CompletedTaskReturnAppearsToPass()
    {
        return System.Threading.Tasks.Task.CompletedTask;
    }

    [Xunit.Fact]
    public System.Threading.Tasks.Task ExpressionBodyAppearsToPass()
        => System.Threading.Tasks.Task.CompletedTask;
}
EOF

build_log="$fixture_root/build.log"
if dotnet build "$fixture_root/SilentReturnFixture.csproj" --nologo > "$build_log" 2>&1; then
  printf 'Silent return analyzer fixture unexpectedly built successfully.\n' >&2
  exit 1
fi
if ! grep -Fq 'SUNDERTEST001' "$build_log"; then
  printf 'Silent return analyzer fixture failed without SUNDERTEST001.\n' >&2
  cat "$build_log" >&2
  exit 1
fi
