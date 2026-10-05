import assert from "node:assert/strict";
import test from "node:test";
import { checkProtocolRetirement } from "./check-protocol-retirement.mjs";

const exactEntry = {
  artifactType: "nuget",
  packageId: "Sunder.Package.Agent.Protocol",
  projectPath: "src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj",
};
const runtimePackages = [
  ["agent", "sunder.package.agent", "src/Sunder.Package.Agent/Sunder.Package.Agent.csproj"],
  ["agent-builder", "sunder.package.agent.builder", "src/Sunder.Package.Agent.Builder/Sunder.Package.Agent.Builder.csproj"],
  ["agent-execution-docker", "sunder.package.agent.execution.docker", "src/Sunder.Package.Agent.Execution.Docker/Sunder.Package.Agent.Execution.Docker.csproj"],
  ["agent-execution-local", "sunder.package.agent.execution.local", "src/Sunder.Package.Agent.Execution.Local/Sunder.Package.Agent.Execution.Local.csproj"],
  ["agent-mcp", "sunder.package.agent.mcp", "src/Sunder.Package.Agent.Mcp/Sunder.Package.Agent.Mcp.csproj"],
  ["agent-memory-semantic", "sunder.package.agent.memory.semantic", "src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj"],
  ["agent-provider-anthropic", "sunder.package.agent.provider.anthropic", "src/Sunder.Package.Agent.Provider.Anthropic/Sunder.Package.Agent.Provider.Anthropic.csproj"],
  ["agent-provider-gemini", "sunder.package.agent.provider.gemini", "src/Sunder.Package.Agent.Provider.Gemini/Sunder.Package.Agent.Provider.Gemini.csproj"],
  ["agent-provider-lmstudio", "sunder.package.agent.provider.lmstudio", "src/Sunder.Package.Agent.Provider.LMStudio/Sunder.Package.Agent.Provider.LMStudio.csproj"],
  ["agent-provider-openai", "sunder.package.agent.provider.openai", "src/Sunder.Package.Agent.Provider.OpenAI/Sunder.Package.Agent.Provider.OpenAI.csproj"],
  ["agent-skills", "sunder.package.agent.skills", "src/Sunder.Package.Agent.Skills/Sunder.Package.Agent.Skills.csproj"],
  ["agent-subagents", "sunder.package.agent.subagents", "src/Sunder.Package.Agent.Subagents/Sunder.Package.Agent.Subagents.csproj"],
  ["agent-tools-files", "sunder.package.agent.tools.files", "src/Sunder.Package.Agent.Tools.Files/Sunder.Package.Agent.Tools.Files.csproj"],
  ["agent-tools-shell", "sunder.package.agent.tools.shell", "src/Sunder.Package.Agent.Tools.Shell/Sunder.Package.Agent.Tools.Shell.csproj"],
  ["agent-tools-web", "sunder.package.agent.tools.web", "src/Sunder.Package.Agent.Tools.Web/Sunder.Package.Agent.Tools.Web.csproj"],
];

test("retained exact Protocol inventory does not use the network", async () => {
  let calls = 0;
  const result = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retainedInventory()),
    fetchImpl: async () => { calls++; throw new Error("unexpected network call"); },
  });

  assert.deepEqual(result, { mode: "retained", failures: [] });
  assert.equal(calls, 0);
});

test("case or artifact-type aliases cannot impersonate the retained NuGet entry", async () => {
  const result = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retainedInventory({
      ...exactEntry,
      artifactType: "sunderpkg",
      packageId: exactEntry.packageId.toLowerCase(),
    })),
  });

  assert.equal(result.mode, "retained");
  assert(result.failures.includes("The retained Agent Protocol inventory entry must use its exact NuGet identity."));
});

test("malformed inventory cannot be interpreted as retired", async () => {
  const result = await checkProtocolRetirement({ inventoryText: "{}" });
  assert.deepEqual(result, {
    mode: "invalid",
    failures: ["Agent package inventory must contain a packages array."],
  });
});

test("zero inventory requires four conclusive anonymous absence observations", async () => {
  const calls = [];
  const result = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => projectEvaluations(),
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      return url.includes("azuresearch-usnc.nuget.org")
        ? response(url, 200, '{"totalHits":0,"data":[]}')
        : url.includes("www.nuget.org")
          ? response(url, 404, '<title>NuGet Gallery | Page Not Found</title><p class="error-title">Error 404 - Page Not Found</p>', "text/html")
          : response(url, 404, "<Error><Code>BlobNotFound</Code><Message>The specified blob does not exist.</Message></Error>", "application/xml");
    },
    clock: sequenceClock("2026-09-07T10:00:00.000Z", "2026-09-07T10:00:01.000Z"),
  });

  assert.deepEqual(result.failures, []);
  assert.equal(calls.length, 4);
  assert(calls.every((call) => call.options.method === "GET" && call.options.credentials === "omit"));
});

test("zero inventory fails closed when search reports the exact package", async () => {
  const result = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => projectEvaluations(),
    fetchImpl: async (url) => url.includes("azuresearch-usnc.nuget.org")
      ? response(url, 200, '{"totalHits":1,"data":[{"id":"sunder.package.agent.protocol"}]}')
      : url.includes("www.nuget.org")
        ? response(url, 404, '<title>NuGet Gallery | Page Not Found</title><p class="error-title">Error 404 - Page Not Found</p>', "text/html")
        : response(url, 404, "<Error><Code>BlobNotFound</Code><Message>The specified blob does not exist.</Message></Error>", "application/xml"),
    clock: sequenceClock("2026-09-07T10:00:00.000Z", "2026-09-07T10:00:01.000Z"),
  });

  assert(result.failures.includes("search reports that the Agent Protocol NuGet identity is published."));
});

test("generic 404s and inconsistent search totals are not absence evidence", async () => {
  const result = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => projectEvaluations(),
    fetchImpl: async (url) => url.includes("azuresearch-usnc.nuget.org")
      ? response(url, 200, '{"totalHits":7,"data":[]}')
      : response(url, 404, "not found", "text/plain"),
    clock: sequenceClock("2026-09-07T10:00:00.000Z", "2026-09-07T10:00:01.000Z"),
  });

  assert.equal(result.failures.length, 4);
  assert(result.failures.some((failure) => failure.includes("zero-result")));
  assert(result.failures.some((failure) => failure.includes("absence signature")));
});

test("zero inventory rejects Protocol aliases and live release metadata before network access", async () => {
  let calls = 0;
  const alias = await checkProtocolRetirement({
    inventoryText: JSON.stringify({
      ...retiredInventory(),
      packages: [...retiredInventory().packages, { ...exactEntry, packageId: "example.alias" }],
    }),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => projectEvaluations(),
    fetchImpl: async () => { calls++; throw new Error("unexpected network call"); },
  });
  const surface = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: { "scripts/release/publish.sh": "protocol_name=Sunder.Package.Agent.Protocol.1.0.0.nupkg" },
    collectProjectEvaluations: async () => projectEvaluations(),
    fetchImpl: async () => { calls++; throw new Error("unexpected network call"); },
  });

  assert(alias.failures.some((failure) => failure.includes("under an alias")));
  assert(surface.failures.some((failure) => failure.includes("release metadata")));
  assert.equal(calls, 0);
});

test("zero inventory rejects neutral NuGet aliases and evaluated Release-packable projects", async () => {
  const neutralNuget = retiredInventory();
  neutralNuget.packages.push({
    artifactType: "nuget",
    packageId: "Sunder.Package.Agent.Contracts",
    projectPath: "src/ReleaseShim/ReleaseShim.csproj",
  });
  const inventoryResult = await checkProtocolRetirement({
    inventoryText: JSON.stringify(neutralNuget),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => projectEvaluations(),
  });
  const projectResult = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => [...projectEvaluations(), {
      projectPath: "eng/ReleaseShim/ReleaseShim.csproj",
      packageId: "Sunder.Package.Agent.Contracts",
      isPackable: "true",
    }],
  });

  assert(inventoryResult.failures.some((failure) => failure.includes("no NuGet packages")));
  assert(projectResult.failures.some((failure) => failure.includes("remains Release-packable")));
});

test("zero inventory requires distinct canonical runtimes and well-formed project evaluations", async () => {
  const duplicate = retiredInventory();
  duplicate.packages[14].packageId = duplicate.packages[13].packageId;
  const inventoryResult = await checkProtocolRetirement({
    inventoryText: JSON.stringify(duplicate),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => projectEvaluations(),
  });
  const evaluationResult = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: cleanRetirementSurfaces(),
    collectProjectEvaluations: async () => [{
      projectPath: "src/Runtime0/Runtime0.csproj",
      packageId: "sunder.package.agent",
      isPackable: "not-a-boolean",
    }],
  });

  assert(inventoryResult.failures.some((failure) => failure.includes("exact 15 canonical key, package, and project records")));
  assert(evaluationResult.failures.includes("Agent Release project evaluation is malformed."));
  assert(evaluationResult.failures.some((failure) => failure.includes("was not evaluated in Release")));
});

test("zero inventory permits read-only Core NuGet verification but rejects conditional packability", async () => {
  const allowed = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: {
      ...cleanRetirementSurfaces(),
      "scripts/release/verify-core.sh": "test -f artifacts/Sunder.Sdk.1.1.0.nupkg\n",
    },
    collectProjectEvaluations: async () => projectEvaluations(),
    fetchImpl: conclusiveAbsenceResponse,
    clock: sequenceClock("2026-09-07T10:00:00.000Z", "2026-09-07T10:00:01.000Z"),
  });
  const conditional = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: {
      "eng/ReleaseShim.csproj": "<Project><PropertyGroup><IsPackable Condition=\"'$(Configuration)' == 'Hidden'\">true</IsPackable></PropertyGroup></Project>\n",
    },
    collectProjectEvaluations: async () => projectEvaluations(),
  });
  const targetCommand = await checkProtocolRetirement({
    inventoryText: JSON.stringify(retiredInventory()),
    retirementSurfaceTexts: {
      "eng/Release.targets": "<Project><Target Name=\"Publish\"><Exec Command=\"dotnet msbuild Neutral.csproj /t:&quot;Build,Pack&quot; -p:&quot;IsPackable=true&quot;\" /></Target></Project>\n",
    },
    collectProjectEvaluations: async () => projectEvaluations(),
  });

  assert.deepEqual(allowed.failures, []);
  assert(conditional.failures.some((failure) => failure.includes("remains configured to publish")));
  assert(targetCommand.failures.some((failure) => failure.includes("NuGet pack, push, or credential path")));
});

function response(url, status, body, contentType = "application/json") {
  const value = new Response(body, {
    status,
    headers: { "content-type": contentType },
  });
  Object.defineProperty(value, "url", { value: url });
  return value;
}

function conclusiveAbsenceResponse(url) {
  return url.includes("azuresearch-usnc.nuget.org")
    ? response(url, 200, '{"totalHits":0,"data":[]}')
    : url.includes("www.nuget.org")
      ? response(url, 404, '<title>NuGet Gallery | Page Not Found</title><p class="error-title">Error 404 - Page Not Found</p>', "text/html")
      : response(url, 404, "<Error><Code>BlobNotFound</Code><Message>The specified blob does not exist.</Message></Error>", "application/xml");
}

function sequenceClock(...values) {
  return () => new Date(values.shift());
}

function cleanRetirementSurfaces() {
  return {
    ".github/workflows/release.yml": "name: Runtime release\n",
    "src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj": "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n",
  };
}

function retiredInventory() {
  return {
    packages: runtimePackages.map(([key, packageId, projectPath]) => ({
      artifactType: "sunderpkg",
      key,
      packageId,
      projectPath,
    })),
  };
}

function projectEvaluations() {
  return retiredInventory().packages.map((entry) => ({
    projectPath: entry.projectPath,
    packageId: entry.packageId,
    isPackable: "false",
  }));
}

function retainedInventory(protocol = exactEntry) {
  const inventory = retiredInventory();
  inventory.packages.push(protocol);
  return inventory;
}
