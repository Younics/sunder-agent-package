# Shared test source

These are current test collaborators, not executable suites or package source.
No source here reads or reconstructs the protected historical captures.

- `Contracts/`: descriptor-only assertions imported by `CurrentContractFixtures.props`.
- `Rpc/`: generated-role-aware loopback broker, adapters, and local fake ports,
  imported by `RegressionTestHarness.props`.
- `Packages/`: package-context wrappers imported by `PackageContextFixtures.props`.
- `Consumers/` and `Execution/`: opt-in, package-specific generated-service probes.
- `Presentation/`: App-role composition/gateway collaborators for Main and the
  rendered presentation suite. MCP includes only the Agent transcript fixture,
  without taking a Subagents role dependency.
- `Repository/`: current repository inventory readers.
- `Canonical/`: opt-in canonical role/manifest assertions for Main and Integration.
- `Composition/`: generated composition negotiation, controlled host collaborators,
  and role aliases for Main and Integration. No ordinary test borrows these from
  an executable artifact suite.
- `Platform/`: xUnit 2 platform/filesystem attributes shared by Main, Maker and MCP.

Framework-neutral reusable code belongs in `Sunder.Package.Agent.TestSupport` (or
`TestSupport.Avalonia` when it actually requires Avalonia), and compiles once.
The RPC and descriptor assertions remain source-shared intentionally: their
generated service types belong to each selected role, and rendered tests use
Avalonia's required xUnit 3 ABI while the ordinary suites use xUnit 2. Do not erase
these identities or mix test-framework dependencies just to remove source links.

Keep each inclusion narrow. Test projects must not link source from sibling
executable test projects. Current generated-provider fixture input text remains in
`Sunder.Package.Agent.Tests/Fixtures`; its exact hashes are validated by the existing
preparation/loader pipeline. The source-only Maker and MCP projects do not participate
in the separately selected locked-package compatibility lane.
