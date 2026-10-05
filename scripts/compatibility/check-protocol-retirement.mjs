#!/usr/bin/env node

import { execFile } from "node:child_process";
import { readdir, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { promisify } from "node:util";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const execFileAsync = promisify(execFile);
const packageId = "Sunder.Package.Agent.Protocol";
const normalizedPackageId = packageId.toLowerCase();
const protocolProjectPath = "src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj";
const runtimePackages = Object.freeze([
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
]);
const nugetReleasePattern = /\bdotnet\s+pack\b|\bnuget\s+(?:pack|push)\b|(?:^|[\s"'`,])[/\-](?:t|target):["']?(?:[A-Za-z0-9_.-]+[,;])*pack(?=[,;\s"'`]|$)|\bNUGET_API_KEY\b|[/\-]p(?:roperty)?:["']?IsPackable\s*=\s*["']?true(?=["'\s]|$)/imu;
const maximumBodyBytes = 4 * 1024 * 1024;
const releaseMarkers = Object.freeze([
  normalizedPackageId,
  "agent protocol",
  "agent-protocol",
  protocolProjectPath.toLowerCase(),
  "verify-packed-protocol.sh",
  "max_protocol_",
  "protocol_count",
  "protocol_name",
  "protocol_path",
  "protocol_url",
  "protocol_bytes",
]);

const endpoints = Object.freeze([
  ["flat-container", `https://api.nuget.org/v3-flatcontainer/${normalizedPackageId}/index.json`, "application/json"],
  ["registration", `https://api.nuget.org/v3/registration5-gz-semver2/${normalizedPackageId}/index.json`, "application/json"],
  ["search", `https://azuresearch-usnc.nuget.org/query?q=packageid%3A${packageId}&prerelease=true&semVerLevel=2.0.0`, "application/json"],
  ["package-page", `https://www.nuget.org/packages/${packageId}`, "text/html"],
]);

export async function checkProtocolRetirement({
  inventoryText,
  retirementSurfaceTexts,
  collectProjectEvaluations = evaluateReleaseProjects,
  fetchImpl = globalThis.fetch,
  clock = () => new Date(),
} = {}) {
  const failures = [];
  let inventory;
  try {
    inventory = JSON.parse(inventoryText ?? await readFile(resolve(repositoryRoot, "packages.json"), "utf8"));
  } catch (error) {
    return { mode: "invalid", failures: [`Agent package inventory cannot be read: ${message(error)}`] };
  }

  if (!Array.isArray(inventory?.packages)) {
    return { mode: "invalid", failures: ["Agent package inventory must contain a packages array."] };
  }
  const entries = inventory.packages.filter((entry) => typeof entry?.packageId === "string"
    && entry.packageId.toLowerCase() === normalizedPackageId);
  const runtimeEntries = inventory.packages.filter((entry) => entry?.artifactType === "sunderpkg");
  const nugetEntries = inventory.packages.filter((entry) => entry?.artifactType === "nuget");
  validateRuntimeInventory(runtimeEntries, failures);
  if (entries.length > 1) failures.push("Agent package inventory contains duplicate Protocol identities.");
  if (entries.length === 1) {
    const entry = entries[0];
    if (entry.packageId !== packageId || entry.artifactType !== "nuget") {
      failures.push("The retained Agent Protocol inventory entry must use its exact NuGet identity.");
    }
    if (entry.projectPath !== protocolProjectPath) {
      failures.push("The retained Agent Protocol inventory entry must use its exact project path.");
    }
    if (inventory.packages.length !== 16 || runtimeEntries.length !== 15 || nugetEntries.length !== 1) {
      failures.push("The retained Agent inventory must contain exactly 15 runtime packages and one Protocol NuGet package.");
    }
    return { mode: "retained", failures };
  }
  if (entries.length !== 0 || failures.length !== 0) return { mode: "invalid", failures };
  if (inventory.packages.length !== 15 || runtimeEntries.length !== 15 || nugetEntries.length !== 0) {
    failures.push("The retired Agent inventory must contain exactly 15 runtime packages and no NuGet packages.");
  }
  const aliases = inventory.packages.filter((entry) => {
    const serialized = JSON.stringify(entry);
    return typeof serialized !== "string"
      || releaseMarkers.some((marker) => serialized.toLowerCase().includes(marker));
  });
  if (aliases.length !== 0) {
    failures.push("Agent package inventory retains Protocol identity or release metadata under an alias.");
  }
  try {
    retirementSurfaceTexts ??= await readRetirementSurfaceTexts();
    validateRetirementSurfaces(retirementSurfaceTexts, failures);
  } catch (error) {
    failures.push(`Agent retirement release surfaces cannot be read: ${message(error)}`);
  }
  try {
    validateProjectEvaluations(await collectProjectEvaluations(), runtimeEntries, failures);
  } catch (error) {
    failures.push(`Agent Release project evaluation failed closed: ${message(error)}`);
  }
  if (failures.length !== 0) return { mode: "invalid", failures: failures.sort() };
  if (typeof fetchImpl !== "function") return { mode: "retired", failures: ["Fetch is unavailable for the Protocol retirement audit."] };

  const startedAtUtc = clock().toISOString();
  for (const [surface, url, accept] of endpoints) {
    try {
      const response = await fetchImpl(url, {
        method: "GET",
        headers: { accept, "user-agent": "sunder-agent-protocol-retirement/1.0" },
        credentials: "omit",
        redirect: "error",
        signal: AbortSignal.timeout(15_000),
      });
      const body = await readBoundedBody(response);
      if (response.redirected || response.url !== url) {
        failures.push(`${surface} redirected away from its exact audited URL.`);
      } else if (surface === "search") {
        validateSearch(response, body, failures);
      } else {
        validateNotFound(surface, response, body, failures);
      }
    } catch (error) {
      failures.push(`${surface} audit failed closed: ${message(error)}`);
    }
  }

  const auditedAtUtc = clock().toISOString();
  const elapsed = Date.parse(auditedAtUtc) - Date.parse(startedAtUtc);
  if (!Number.isFinite(elapsed) || elapsed < 0 || elapsed > 5 * 60_000) {
    failures.push("Protocol retirement observation window is invalid or exceeds five minutes.");
  }
  return { mode: "retired", startedAtUtc, auditedAtUtc, failures: failures.sort() };
}

async function readBoundedBody(response) {
  const declaredLength = response.headers.get("content-length");
  if (declaredLength !== null) {
    if (!/^(?:0|[1-9][0-9]*)$/u.test(declaredLength)) throw new Error("response has an invalid Content-Length");
    if (Number(declaredLength) > maximumBodyBytes) throw new Error("response exceeds the audit body limit");
  }
  if (response.body === null) return new Uint8Array();
  const reader = response.body.getReader();
  const chunks = [];
  let length = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > maximumBodyBytes) {
      await reader.cancel();
      throw new Error("response exceeds the audit body limit");
    }
    chunks.push(value);
  }
  const body = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    body.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return body;
}

function validateSearch(response, body, failures) {
  if (response.status !== 200) {
    failures.push(`search returned ${response.status} instead of a conclusive result.`);
    return;
  }
  if (!hasMediaType(response, "application/json")) {
    failures.push("search response does not have the expected JSON media type.");
    return;
  }
  let result;
  try {
    result = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(body));
  } catch (error) {
    failures.push(`search returned invalid JSON: ${message(error)}`);
    return;
  }
  if (!Number.isInteger(result?.totalHits) || result.totalHits < 0 || !Array.isArray(result?.data)) {
    failures.push("search response does not contain valid totalHits and data members.");
    return;
  }
  if (result.data.some((entry) => typeof entry?.id === "string" && entry.id.toLowerCase() === normalizedPackageId)) {
    failures.push("search reports that the Agent Protocol NuGet identity is published.");
  } else if (result.totalHits !== 0 || result.data.length !== 0) {
    failures.push("search response is not a conclusive zero-result absence observation.");
  }
}

function validateNotFound(surface, response, body, failures) {
  const text = new TextDecoder("utf-8", { fatal: true }).decode(body);
  const conclusive = surface === "package-page"
    ? response.status === 404
      && hasMediaType(response, "text/html")
      && /<title>[\s\S]*?NuGet Gallery[\s\S]*?\| Page Not Found[\s\S]*?<\/title>/iu.test(text)
      && /class="error-title">Error 404 - Page Not Found<\/p>/u.test(text)
    : response.status === 404
      && hasMediaType(response, "application/xml")
      && /<Code>BlobNotFound<\/Code>/u.test(text)
      && /<Message>The specified blob does not exist\./u.test(text);
  if (!conclusive) failures.push(`${surface} did not return its conclusive NuGet.org absence signature.`);
}

function hasMediaType(response, expected) {
  return (response.headers.get("content-type") ?? "").split(";", 1)[0].trim().toLowerCase() === expected;
}

async function readRetirementSurfaceTexts() {
  const texts = {};
  await readRetirementFiles(".github/workflows", texts, () => true, false);
  await readRetirementFiles("scripts", texts);
  await readMsBuildFiles(repositoryRoot, "", texts);
  return texts;
}

async function readRetirementFiles(relativeDirectory, texts, include = () => true, excludeTestFiles = true) {
  const directory = resolve(repositoryRoot, relativeDirectory);
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relativePath = `${relativeDirectory}/${entry.name}`;
    if (entry.isDirectory()) {
      if (entry.name === "bin" || entry.name === "obj" || entry.name === "artifacts") continue;
      await readRetirementFiles(relativePath, texts, include, excludeTestFiles);
    } else if (entry.isFile()
      && (!excludeTestFiles || !entry.name.includes(".fixture.") && !entry.name.includes(".test."))
      && relativePath !== "scripts/compatibility/check-protocol-retirement.mjs"
      && include(relativePath)) {
      texts[relativePath] = await readFile(resolve(repositoryRoot, relativePath), "utf8");
    }
  }
}

async function readMsBuildFiles(directory, relativeDirectory, texts) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relativePath = relativeDirectory.length === 0 ? entry.name : `${relativeDirectory}/${entry.name}`;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      if (![".git", ".sunder", "artifacts", "bin", "node_modules", "obj"].includes(entry.name)) {
        await readMsBuildFiles(path, relativePath, texts);
      }
    } else if (entry.isFile() && [".csproj", ".props", ".targets"].some((extension) => entry.name.toLowerCase().endsWith(extension))) {
      texts[relativePath] = await readFile(path, "utf8");
    }
  }
}

function validateRetirementSurfaces(texts, failures) {
  if (texts === null || typeof texts !== "object" || Array.isArray(texts)) {
    failures.push("Agent retirement release surfaces are unavailable.");
    return;
  }
  for (const [path, text] of Object.entries(texts)) {
    if (typeof text !== "string") {
      failures.push(`Agent retirement release surface '${path}' is unreadable.`);
      continue;
    }
    const msbuild = [".csproj", ".props", ".targets"].some((extension) => path.toLowerCase().endsWith(extension));
    const searchableText = msbuild
      ? text.replace(/&quot;|&#0*34;|&#x0*22;/giu, '"').replace(/&apos;|&#0*39;|&#x0*27;/giu, "'")
      : text;
    if (msbuild) {
      if (/<IsPackable\b[^>]*>(?!\s*false\s*<\/IsPackable>)[\s\S]*?<\/IsPackable>/iu.test(text)
        || new RegExp(`<PackageId>\\s*${packageId.replaceAll(".", "\\.")}\\s*</PackageId>`, "iu").test(text)) {
        failures.push(`${path} remains configured to publish the retired Protocol package.`);
      }
    }
    const releaseCapable = msbuild
      || path.startsWith(".github/workflows/")
      || path.startsWith("scripts/release/")
      || nugetReleasePattern.test(searchableText);
    if (!releaseCapable) continue;
    const lower = searchableText.toLowerCase();
    for (const marker of releaseMarkers) {
      if (lower.includes(marker)) failures.push(`${path} retains Protocol release metadata: ${marker}`);
    }
    if (nugetReleasePattern.test(searchableText)) {
      failures.push(`${path} retains a NuGet pack, push, or credential path after Protocol retirement.`);
    }
  }
}

async function evaluateReleaseProjects() {
  const paths = [];
  await collectProjectPaths(repositoryRoot, paths);
  const evaluations = [];
  for (const path of paths.sort()) {
    const { stdout } = await execFileAsync(
      process.env.DOTNET_HOST_PATH ?? "dotnet",
      ["msbuild", path, "-nologo", "-getProperty:PackageId,IsPackable", "-p:Configuration=Release"],
      {
        cwd: repositoryRoot,
        timeout: 30_000,
        maxBuffer: 1024 * 1024,
        env: { ...process.env, DOTNET_CLI_USE_MSBUILD_SERVER: "0", MSBUILDDISABLENODEREUSE: "1" },
      },
    );
    const start = stdout.indexOf("{");
    const end = stdout.lastIndexOf("}");
    if (start < 0 || end <= start) throw new Error(`MSBuild did not return project properties for ${path}`);
    const properties = JSON.parse(stdout.slice(start, end + 1)).Properties;
    evaluations.push({
      projectPath: path.slice(repositoryRoot.length + 1).replaceAll("\\", "/"),
      packageId: properties?.PackageId,
      isPackable: properties?.IsPackable,
    });
  }
  return evaluations;
}

async function collectProjectPaths(directory, paths) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      if (![".git", ".sunder", "artifacts", "bin", "node_modules", "obj"].includes(entry.name)) {
        await collectProjectPaths(path, paths);
      }
    } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".csproj")) {
      paths.push(path);
    }
  }
}

function validateProjectEvaluations(evaluations, runtimeEntries, failures) {
  if (!Array.isArray(evaluations) || evaluations.length === 0) {
    failures.push("Agent Release project evaluations are unavailable.");
    return;
  }
  const byPath = new Map();
  for (const project of evaluations) {
    if (typeof project?.projectPath !== "string" || project.projectPath.length === 0
      || typeof project?.packageId !== "string" || project.packageId.length === 0
      || typeof project?.isPackable !== "string"
      || !["true", "false"].includes(project.isPackable.toLowerCase())
      || byPath.has(project.projectPath)) {
      failures.push("Agent Release project evaluation is malformed.");
      continue;
    }
    byPath.set(project.projectPath, project);
    if (project.isPackable.toLowerCase() === "true"
      || project.packageId.toLowerCase() === normalizedPackageId) {
      failures.push(`${project.projectPath ?? "Agent project"} remains Release-packable as '${project.packageId}'.`);
    }
  }
  for (const entry of runtimeEntries) {
    const project = byPath.get(entry.projectPath);
    if (project === undefined) {
      failures.push(`Runtime package project was not evaluated in Release: ${entry.projectPath}`);
    } else if (project.packageId.toLowerCase() !== entry.packageId.toLowerCase()) {
      failures.push(`${entry.projectPath} evaluates PackageId '${project.packageId}' instead of '${entry.packageId}'.`);
    }
  }
}

function validateRuntimeInventory(runtimeEntries, failures) {
  const records = [];
  for (const entry of runtimeEntries) {
    if (typeof entry?.packageId !== "string" || typeof entry?.key !== "string" || typeof entry?.projectPath !== "string") {
      failures.push("Agent runtime inventory entries must have string key, packageId, and projectPath members.");
      continue;
    }
    records.push([entry.key, entry.packageId, entry.projectPath]);
  }
  records.sort((left, right) => left[1].localeCompare(right[1]));
  if (JSON.stringify(records) !== JSON.stringify(runtimePackages)) {
    failures.push("Agent runtime inventory must contain the exact 15 canonical key, package, and project records.");
  }
}

function message(error) {
  return error instanceof Error ? error.message : String(error);
}

async function main() {
  const result = await checkProtocolRetirement();
  if (result.failures.length !== 0) {
    for (const failure of result.failures) process.stderr.write(`Protocol retirement gate failed: ${failure}\n`);
    process.exitCode = 1;
    return;
  }
  if (result.mode === "retained") {
    process.stdout.write("Agent Protocol remains in the exact NuGet inventory; online retirement audit skipped.\n");
  } else {
    process.stdout.write(`Agent Protocol retirement feed audit passed at ${result.auditedAtUtc}.\n`);
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) await main();
