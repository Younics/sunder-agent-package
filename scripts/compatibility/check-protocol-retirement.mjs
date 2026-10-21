#!/usr/bin/env node
// Current dependency/artifact boundary. No feed observations or historical input.
import { execFile } from "node:child_process";
import { readdir, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { promisify } from "node:util";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const exec = promisify(execFile);
const excluded = new Set([".git", ".sunder", "artifacts", "bin", "compatibility", "Fixtures", "node_modules", "obj"]);
const protocol = /Sunder\.Package\.Agent\.(?:Protocol|Contracts)(?:\b|\/)/iu;
const nugetRelease = /\bdotnet\s+pack\b|\bnuget\s+(?:pack|push)\b|\bNUGET_API_KEY\b|[/\-](?:t|target):["']?(?:[A-Za-z0-9_.-]+[,;])*pack(?=[,;\s"'`]|$)|[/\-]p(?:roperty)?:["']?IsPackable\s*=\s*["']?true(?=["'\s]|$)/imu;

async function files(directory, result = []) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory() && !excluded.has(entry.name)) await files(path, result);
    else if (entry.isFile()) result.push(path);
  }
  return result;
}

async function surfaces() {
  const texts = {};
  for (const path of await files(root)) {
    const relative = path.slice(root.length + 1).replaceAll("\\", "/");
    if (/\.(?:csproj|props|targets)$/u.test(path) || relative.startsWith(".github/workflows/")
      || relative.startsWith("scripts/release/") && !relative.includes(".fixture.")) {
      texts[relative] = await readFile(path, "utf8");
    }
  }
  return texts;
}

async function evaluations() {
  const result = [];
  for (const path of (await files(root)).filter((path) => path.endsWith(".csproj")).sort()) {
    const { stdout } = await exec(process.env.DOTNET_HOST_PATH ?? "dotnet", ["msbuild", path, "-nologo",
      "-getProperty:PackageId,SunderPackageFamilyId,IsPackable", "-p:Configuration=Release"], {
      cwd: root, timeout: 30_000, maxBuffer: 1024 * 1024,
      env: { ...process.env, DOTNET_CLI_USE_MSBUILD_SERVER: "0", MSBUILDDISABLENODEREUSE: "1" },
    });
    const properties = JSON.parse(stdout.slice(stdout.indexOf("{"), stdout.lastIndexOf("}") + 1)).Properties;
    result.push({ projectPath: path.slice(root.length + 1).replaceAll("\\", "/"),
      packageId: properties.PackageId, familyId: properties.SunderPackageFamilyId, isPackable: properties.IsPackable });
  }
  return result;
}

export async function checkProtocolRetirement({ inventoryText, expectedText, retirementSurfaceTexts,
  collectProjectEvaluations = evaluations } = {}) {
  const failures = [];
  try {
    const expected = JSON.parse(expectedText ?? await readFile(resolve(root, "compatibility/agent-current-inventory.json"), "utf8"));
    const inventory = JSON.parse(inventoryText ?? await readFile(resolve(root, "packages.json"), "utf8"));
    const coordinates = (entries) => entries.map(({ key, packageId, projectPath }) => [key, packageId, projectPath]).sort();
    if (expected.packages.length !== 15 || !Array.isArray(inventory.packages)
      || JSON.stringify(coordinates(inventory.packages)) !== JSON.stringify(coordinates(expected.packages))
      || inventory.packages.some((value) => value.artifactType !== "sunderpkg")) {
      return { mode: "invalid", failures: ["Inventory must contain the exact 15 current project/package pairs and only sunderpkg artifacts."] };
    }
    if (inventory.releaseVersion !== expected.packageVersion) failures.push("Current package version differs.");
    for (const [path, text] of Object.entries(retirementSurfaceTexts ?? await surfaces())) {
      const decoded = text.replace(/&quot;|&#0*34;|&#x0*22;/giu, '"').replace(/&apos;|&#0*39;|&#x0*27;/giu, "'");
      if (protocol.test(decoded) || /agent-protocol|verify-packed-protocol|\bprotocol_(?:count|name|path|url|bytes)\b/iu.test(decoded))
        failures.push(`${path} retains a Protocol dependency or artifact/release path.`);
      if (nugetRelease.test(decoded) || /<IsPackable\b[^>]*>(?!\s*false\s*<\/IsPackable>)[\s\S]*?<\/IsPackable>/iu.test(decoded))
        failures.push(`${path} retains a NuGet pack/push/credential path.`);
    }
    const projects = await collectProjectEvaluations();
    if (!Array.isArray(projects) || projects.length === 0) throw new Error("Release project evaluations are unavailable");
    const byPath = new Map();
    for (const project of projects) {
      if (typeof project?.projectPath !== "string" || !project.projectPath || byPath.has(project.projectPath)
        || typeof project.packageId !== "string" || !project.packageId || !["true", "false"].includes(project.isPackable?.toLowerCase()))
        throw new Error("Malformed/duplicate Release project evaluation");
      byPath.set(project.projectPath, project);
      if (project.isPackable.toLowerCase() !== "false" || protocol.test(project.packageId))
        failures.push(`${project.projectPath} is packable or retains a Protocol identity.`);
    }
    for (const expectedPackage of expected.packages) {
      if (byPath.get(expectedPackage.projectPath)?.familyId !== expectedPackage.packageId)
        failures.push(`${expectedPackage.projectPath} has a missing/incorrect SunderPackageFamilyId evaluation.`);
    }
  } catch (error) {
    failures.push(`Current package boundary failed closed: ${error.message ?? error}`);
  }
  return { mode: failures.length ? "invalid" : "current", failures: failures.sort() };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const result = await checkProtocolRetirement();
  if (result.failures.length) {
    for (const failure of result.failures) process.stderr.write(failure + "\n");
    process.exitCode = 1;
  } else process.stdout.write("Current 15-package dependency/artifact boundary passed.\n");
}
