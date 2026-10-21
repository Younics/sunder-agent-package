import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { checkProtocolRetirement } from "./check-protocol-retirement.mjs";

const expected = JSON.parse(await readFile(new URL("../../compatibility/agent-current-inventory.json", import.meta.url), "utf8"));
const inventory = JSON.parse(await readFile(new URL("../../packages.json", import.meta.url), "utf8"));
const projects = () => expected.packages.map((value) => ({ projectPath: value.projectPath,
  packageId: value.projectPath.split("/").at(-1).slice(0, -7), familyId: value.packageId, isPackable: "false" }));
const check = (overrides = {}) => checkProtocolRetirement({ inventoryText: JSON.stringify(inventory),
  expectedText: JSON.stringify(expected), retirementSurfaceTexts: {}, collectProjectEvaluations: async () => projects(), ...overrides });

test("current inventory has reviewed 15 pairs, 12 App / 15 Worker, 18 public / 13 private and exact providers", async () => {
  assert.equal(expected.packages.length, 15);
  assert.equal(expected.packages.filter((value) => value.hasApp).length, 12);
  assert.equal(expected.contracts.filter((value) => value.exposure === "public").length, 18);
  assert.equal(expected.contracts.filter((value) => value.exposure === "package").length, 13);
  for (const value of inventory.packages) {
    const reviewed = expected.packages.find((item) => item.key === value.key);
    assert.deepEqual(value.providerIds, Object.keys(reviewed.providers).sort());
    assert.equal(value.artifactName, `sunder-${value.key}-package`);
  }
  assert.deepEqual(Object.keys(inventory.capabilities).sort(), expected.packages.map((value) => value.packageId).sort());
  assert.deepEqual(await check(), { mode: "current", failures: [] });
});

test("runtime identity remains independent of C# project/assembly identity", async () => {
  const evaluated = projects();
  const maker = evaluated.find((value) => value.familyId === "younics.sunder.package.maker");
  assert.equal(maker.packageId, "Sunder.Package.Agent.Builder");
  maker.familyId = "younics.ai.agent.builder";
  assert((await check({ collectProjectEvaluations: async () => evaluated })).failures.some((value) => value.includes("SunderPackageFamilyId")));
});

for (const [name, mutate] of [
  ["missing package", (value) => value.packages.pop()],
  ["duplicate package", (value) => value.packages.push(value.packages[0])],
  ["wrong project", (value) => { value.packages[0].projectPath = "src/Wrong.csproj"; }],
  ["Protocol artifact", (value) => value.packages.push({ key: "agent-protocol", packageId: "Sunder.Package.Agent.Protocol", artifactType: "nuget" })],
]) test(`rejects ${name}`, async () => {
  const value = structuredClone(inventory); mutate(value);
  assert.equal((await check({ inventoryText: JSON.stringify(value) })).mode, "invalid");
});

for (const text of [
  '<PackageReference Include="Sunder.Package.Agent.Protocol" />',
  '<ProjectReference Include="../Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj" />',
  '<IsPackable>true</IsPackable>', 'dotnet pack project.csproj', 'dotnet msbuild -t:Clean,Pack', 'NUGET_API_KEY',
]) test(`rejects active dependency/release path: ${text}`, async () => {
  assert.equal((await check({ retirementSurfaceTexts: { "input.props": text } })).mode, "invalid");
});

test("missing or duplicate evaluated projects fail closed", async () => {
  assert.equal((await check({ collectProjectEvaluations: async () => [] })).mode, "invalid");
  assert.equal((await check({ collectProjectEvaluations: async () => [...projects(), projects()[0]] })).mode, "invalid");
});

test("remote availability is not consulted", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = () => { throw new Error("unexpected network access"); };
  try { assert.equal((await check()).mode, "current"); }
  finally { globalThis.fetch = original; }
});
